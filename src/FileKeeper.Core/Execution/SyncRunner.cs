using FileKeeper.Core.Engines;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;

namespace FileKeeper.Core.Execution;

/// <summary>
/// 同步任务执行器（本地 → 远程）。
///
/// 流程：校验配置 → 枚举本地文件 → 连接 → 按需建远端目录 →
/// 【增量比对】→ 原子上传（失败重试）→ 可选删除本地源文件。
///
/// 三条关键约定：
/// 1. <b>增量判定用「本地更晚」而非「两者不等」</b>：远端与本地时钟、时区未必对齐，
///    用「不等」会导致每次全量重传。因此只在本地修改时间比远端晚 2 秒以上时才重传。
/// 2. <b>连接中断要尽早放弃</b>：连续失败达到阈值即中止整个任务，
///    否则 1000 个文件 × 60 秒超时会让任务跑上十几小时。
/// 3. <b>先确认上传成功再删本地源文件</b>：删除是最后一步，且不因删除失败而判定文件传输失败。
/// </summary>
public sealed class SyncRunner
{
    /// <summary>连续失败达到此数量即认为连接已中断，提前中止任务</summary>
    private const int MaxConsecutiveFailures = 3;

    /// <summary>时间容差（秒）：本地比远端晚超过此值才判定为已变更</summary>
    private const double ModifiedToleranceSeconds = 2;

    private readonly IAppLogger _logger;

    /// <summary>客户端工厂。默认走真实协议；冒烟测试注入内存替身以验证比对与重试逻辑</summary>
    private readonly Func<SyncTask, ISyncClient> _clientFactory;

    public SyncRunner(IAppLogger logger) : this(logger, SyncClientFactory.Create)
    {
    }

    /// <summary>测试专用构造：允许替换同步客户端实现</summary>
    public SyncRunner(IAppLogger logger, Func<SyncTask, ISyncClient> clientFactory)
    {
        _logger = logger;
        _clientFactory = clientFactory;
    }

    public async Task<TaskRunResult> RunAsync(
        SyncTask task,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = TaskRunResult.Create(task, TaskKind.Sync);

        try
        {
            task.Normalize();
            cancellationToken.ThrowIfCancellationRequested();

            var configurationError = SyncClientFactory.Validate(task);
            if (configurationError is not null)
            {
                return Fail(result, configurationError);
            }

            if (!Directory.Exists(task.LocalDirectory))
            {
                return Fail(result, $"本地目录不存在或不可访问：{task.LocalDirectory}");
            }

            var files = FileSelector.SelectFiles(
                task.LocalDirectory,
                task.IncludeSubDirectories,
                task.FilterMode,
                task.Extensions,
                task.ExcludedDirectories,
                cancellationToken);

            if (files.Count == 0)
            {
                result.Skip("没有符合过滤条件的文件");
                _logger.TaskResult(result);
                return result;
            }

            await using var client = _clientFactory(task);

            _logger.Info($"开始同步到 {task.Host}:{task.Port}（{client.Protocol}）", task.Name);

            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(0);

            // 远端根目录提前建好，让权限/路径问题在开工前就暴露
            if (!string.IsNullOrWhiteSpace(task.RemoteDirectory))
            {
                await client.EnsureDirectoryAsync(task.RemoteDirectory, cancellationToken).ConfigureAwait(false);
            }

            var consecutiveFailures = 0;

            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (consecutiveFailures >= MaxConsecutiveFailures)
                {
                    result.ErrorMessage =
                        $"连续 {consecutiveFailures} 个文件上传失败，连接可能已中断，任务提前中止";
                    _logger.Error(result.ErrorMessage, task.Name);
                    break;
                }

                var file = files[index];
                var remotePath = BuildRemotePath(task, file);

                IProgress<double>? fileProgress = progress is null
                    ? null
                    : new Progress<double>(value => progress.Report((index + value / 100.0) * 100.0 / files.Count));

                var outcome = await ProcessFileAsync(client, task, file, remotePath, fileProgress, result, cancellationToken)
                    .ConfigureAwait(false);

                consecutiveFailures = outcome == FileOutcome.Failed ? consecutiveFailures + 1 : 0;
            }

            result.Complete();
            progress?.Report(100);
            _logger.TaskResult(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Skip("任务已取消（服务停止或手动中断）");
            _logger.Warn("任务已取消", task.Name);
            _logger.TaskResult(result);
            return result;
        }
        catch (Exception ex)
        {
            return Fail(result, $"同步失败：{ex.Message}", ex);
        }
    }

    private async Task<FileOutcome> ProcessFileAsync(
        ISyncClient client,
        SyncTask task,
        string file,
        string remotePath,
        IProgress<double>? progress,
        TaskRunResult result,
        CancellationToken cancellationToken)
    {
        long size;
        DateTime localModified;

        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                result.SkippedCount++;
                result.AddDetail($"跳过（文件已不存在）：{file}");
                return FileOutcome.Skipped;
            }

            size = info.Length;
            localModified = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.FailedCount++;
            result.AddDetail($"读取失败：{file}（{ex.Message}）");
            return FileOutcome.Failed;
        }

        // 正在被写入的文件（如活动日志）上传会得到半截内容，跳过等下次
        if (!FileSelector.IsAccessible(file))
        {
            result.SkippedCount++;
            result.AddDetail($"跳过（被占用或无权限）：{file}");
            _logger.Warn($"跳过被占用的文件：{file}", task.Name);
            return FileOutcome.Skipped;
        }

        if (task.IncrementalSync)
        {
            try
            {
                var remote = await client.GetFileInfoAsync(remotePath, cancellationToken).ConfigureAwait(false);

                if (remote is not null && !ShouldUpload(remote, size, localModified))
                {
                    result.SkippedCount++;
                    result.AddDetail($"跳过（远端已是最新）：{remotePath}");
                    return FileOutcome.Skipped;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 取远端信息失败不应直接放弃，按「需要上传」处理，让上传环节自己决定成败
                _logger.Warn($"获取远端信息失败，按需要上传处理：{remotePath}（{ex.Message}）", task.Name);
            }
        }

        if (!await UploadWithRetryAsync(client, task, remotePath, file, progress, cancellationToken).ConfigureAwait(false))
        {
            result.FailedCount++;
            result.AddDetail($"上传失败：{remotePath}");
            return FileOutcome.Failed;
        }

        result.SuccessCount++;
        result.ProcessedBytes += size;

        if (task.DeleteLocalAfterUpload)
        {
            DeleteLocalFile(file, result, task.Name);
        }

        return FileOutcome.Uploaded;
    }

    /// <summary>
    /// 增量判定：远端不存在 → 上传；大小不同 → 上传；
    /// 远端时间未知 → 保守重传；本地修改时间比远端晚超过容差 → 上传。
    /// </summary>
    private static bool ShouldUpload(RemoteFileInfo remote, long localSize, DateTime localModified)
    {
        if (remote.Size != localSize)
        {
            return true;
        }

        // 服务端不支持 MDTM 时无从判断内容是否变化，宁可重传也不要漏传
        if (remote.LastModified == DateTimeOffset.MinValue)
        {
            return true;
        }

        return (localModified - remote.LastModified.UtcDateTime).TotalSeconds > ModifiedToleranceSeconds;
    }

    /// <summary>
    /// 带重试的上传。重试针对瞬时网络抖动；持续失败由调用方的连续失败计数统一处理。
    /// </summary>
    private async Task<bool> UploadWithRetryAsync(
        ISyncClient client,
        SyncTask task,
        string remotePath,
        string localPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await client.UploadAsync(localPath, remotePath, progress, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt > task.RetryCount)
                {
                    _logger.Error(
                        $"上传失败（已重试 {task.RetryCount} 次）：{remotePath}（{ex.Message}）", task.Name, ex);
                    return false;
                }

                _logger.Warn($"上传失败，准备第 {attempt} 次重试：{remotePath}（{ex.Message}）", task.Name);

                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 2, 10)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private void DeleteLocalFile(string file, TaskRunResult result, string taskName)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex)
        {
            // 上传已经成功，删不掉本地文件不该判定本次上传失败
            result.AddDetail($"删除本地源文件失败：{file}（{ex.Message}）");
            _logger.Warn($"删除本地源文件失败：{file}（{ex.Message}）", taskName);
        }
    }

    private static string BuildRemotePath(SyncTask task, string file)
    {
        var relative = task.PreserveDirectoryStructure && task.IncludeSubDirectories
            ? Path.GetRelativePath(task.LocalDirectory, file)
            : Path.GetFileName(file);

        return RemotePath.Combine(task.RemoteDirectory, relative);
    }

    private TaskRunResult Fail(TaskRunResult result, string message, Exception? exception = null)
    {
        result.Fail(message);
        _logger.Error(message, result.TaskName, exception);
        _logger.TaskResult(result);
        return result;
    }

    private enum FileOutcome
    {
        Uploaded,
        Skipped,
        Failed
    }
}
