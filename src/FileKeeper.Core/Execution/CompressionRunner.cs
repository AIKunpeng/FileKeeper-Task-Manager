using FileKeeper.Core.Engines;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;

namespace FileKeeper.Core.Execution;

/// <summary>
/// 压缩任务执行器。
///
/// 流程：校验配置 → 枚举文件 → 跳过被占用文件 → 渲染文件名 → 调用引擎 → 校验产物 → 可选删除源。
/// 任何一步失败都会体现在 <see cref="TaskRunResult"/> 中，不会向外抛异常（取消除外），
/// 以便调度循环可以稳定地继续处理下一个任务。
/// </summary>
public sealed class CompressionRunner
{
    private readonly IAppLogger _logger;

    public CompressionRunner(IAppLogger logger)
    {
        _logger = logger;
    }

    public async Task<TaskRunResult> RunAsync(
        CompressionTask task,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = TaskRunResult.Create(task, TaskKind.Compression);

        try
        {
            task.Normalize();
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(task.SourceDirectory) || !Directory.Exists(task.SourceDirectory))
            {
                return Fail(result, $"源目录不存在或不可访问：{task.SourceDirectory}");
            }

            if (string.IsNullOrWhiteSpace(task.OutputDirectory))
            {
                return Fail(result, "未设置压缩包存放目录");
            }

            // 提前校验引擎能力，避免枚举完文件才发现 RAR 不可用
            if (!ArchiveEngineFactory.CanCreate(task.Format))
            {
                return Fail(result, ArchiveEngineFactory.GetUnavailableReason(task.Format) ?? $"无法创建 {task.Format} 格式");
            }

            Directory.CreateDirectory(task.OutputDirectory);

            // 输出目录位于源目录之内时，必须排除，否则压缩包会把上一次的产物一起压进去
            var excludedDirectories = new List<string> { task.OutputDirectory };
            excludedDirectories.AddRange(task.ExcludedDirectories);

            var files = FileSelector.SelectFiles(
                task.SourceDirectory,
                task.IncludeSubDirectories,
                task.FilterMode,
                task.Extensions,
                excludedDirectories,
                cancellationToken);

            if (files.Count == 0)
            {
                result.Skip("没有符合过滤条件的文件");
                _logger.TaskResult(result);
                return result;
            }

            var compressible = new List<string>(files.Count);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (FileSelector.IsAccessible(file))
                {
                    compressible.Add(file);
                }
                else
                {
                    result.SkippedCount++;
                    result.AddDetail($"跳过（被占用或无权限）：{file}");
                    _logger.Warn($"跳过被占用的文件：{file}", task.Name);
                }
            }

            if (compressible.Count == 0)
            {
                result.Skip("所有候选文件都被其他进程占用");
                _logger.TaskResult(result);
                return result;
            }

            result.ProcessedBytes = SumFileSizes(compressible);

            var archivePath = FileNameTemplate.BuildArchivePath(
                task.OutputDirectory, task.FileNameTemplate, task.Format, DateTimeOffset.Now, task.Name);

            var engine = ArchiveEngineFactory.Create(task.Format, requiresPassword: !string.IsNullOrEmpty(task.Password));

            _logger.Info(
                $"开始压缩 {compressible.Count} 个文件（{engine.Name}）→ {Path.GetFileName(archivePath)}",
                task.Name);

            progress?.Report(0);

            await engine.CompressAsync(new CompressionRequest
            {
                ArchivePath = archivePath,
                Format = task.Format,
                Files = compressible,
                BaseDirectory = task.SourceDirectory,
                CompressionLevel = task.CompressionLevel,
                Password = task.Password,
                VolumeSizeBytes = task.VolumeSizeBytes
            }, progress, cancellationToken).ConfigureAwait(false);

            if (!ArchiveExists(archivePath))
            {
                return Fail(result, $"压缩已执行但未找到产物文件：{archivePath}");
            }

            result.OutputPath = archivePath;
            result.SuccessCount = compressible.Count;

            if (task.DeleteSourceAfterCompress)
            {
                DeleteSourceFiles(compressible, result, task.Name, cancellationToken);
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
            return Fail(result, $"压缩失败：{ex.Message}", ex);
        }
    }

    private void DeleteSourceFiles(
        IReadOnlyList<string> files,
        TaskRunResult result,
        string taskName,
        CancellationToken cancellationToken)
    {
        var deleted = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                result.AddDetail($"删除源文件失败：{file}（{ex.Message}）");
                _logger.Warn($"删除源文件失败：{file}（{ex.Message}）", taskName);
            }
        }

        if (deleted > 0)
        {
            _logger.Info($"压缩后删除源文件 {deleted} 个", taskName);
        }
    }

    private TaskRunResult Fail(TaskRunResult result, string message, Exception? exception = null)
    {
        result.Fail(message);
        _logger.Error(message, result.TaskName, exception);
        _logger.TaskResult(result);
        return result;
    }

    private static long SumFileSizes(IEnumerable<string> files)
    {
        long total = 0;

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists)
                {
                    total += info.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 取不到大小不影响任务本身
            }
        }

        return total;
    }

    /// <summary>
    /// 校验产物是否存在。分卷压缩时第一分卷带后缀（xxx.zip.001 / xxx.part1.rar），
    /// 因此主文件名不存在时需按前缀再找一次。
    /// </summary>
    private static bool ArchiveExists(string archivePath)
    {
        if (File.Exists(archivePath))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(archivePath);
        var prefix = Path.GetFileName(archivePath);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(prefix) || !Directory.Exists(directory))
        {
            return false;
        }

        return Directory.EnumerateFiles(directory, prefix + ".*").Any();
    }
}
