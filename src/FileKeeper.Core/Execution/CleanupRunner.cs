using FileKeeper.Core.Configuration;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;

namespace FileKeeper.Core.Execution;

/// <summary>
/// 清理任务执行器。
///
/// 删除为<b>永久删除</b>（不进回收站），每条删除都写入日志与结果明细。
/// 安全防线（三道，缺一不可）：
/// 1. 目录级：目标目录命中系统/程序受保护目录时直接拒绝执行；
/// 2. 路径级：删除前校验文件确实位于目标目录之下；
/// 3. 时间级：以文件时间属性与阈值比较，未来时间戳的文件永不删除。
/// </summary>
public sealed class CleanupRunner
{
    private readonly IAppLogger _logger;

    public CleanupRunner(IAppLogger logger)
    {
        _logger = logger;
    }

    public Task<TaskRunResult> RunAsync(
        CleanupTask task,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        DateTimeOffset? scheduledAt = null)
        // 目录枚举与逐文件删除全是同步 IO，直接跑在调用线程上会把
        // 界面端的 UI 线程整个挂住（进度条冻结、终止按钮排不上队），必须甩到线程池
        => Task.Run(() => RunCore(task, progress, cancellationToken, scheduledAt), cancellationToken);

    private TaskRunResult RunCore(
        CleanupTask task,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        DateTimeOffset? scheduledAt)
    {
        var result = TaskRunResult.Create(task, TaskKind.Cleanup);
        result.ScheduledAt = scheduledAt;

        try
        {
            task.Normalize();
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(task.TargetDirectory) || !Directory.Exists(task.TargetDirectory))
            {
                return Fail(result, $"清理目录不存在或不可访问：{task.TargetDirectory}");
            }

            if (AppPaths.IsProtectedDirectory(task.TargetDirectory))
            {
                return Fail(result, $"拒绝清理受保护的系统目录：{task.TargetDirectory}");
            }

            var cutoff = DateTimeOffset.Now - task.Threshold;
            var basisText = task.TimeBasis == AgeTimeBasis.CreationTime ? "创建时间" : "最后修改时间";

            _logger.Info(
                $"开始清理：{task.TargetDirectory}，删除「{basisText}」早于 {cutoff:yyyy-MM-dd HH:mm:ss} 的文件",
                task.Name);

            var files = FileSelector.SelectFiles(
                task.TargetDirectory,
                task.IncludeSubDirectories,
                task.FilterMode,
                task.Extensions,
                task.ExcludedDirectories,
                cancellationToken);

            if (files.Count == 0)
            {
                result.Skip("目录中没有文件");
                _logger.TaskResult(result);
                return result;
            }

            progress?.Report(0);

            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = files[index];
                var age = GetFileTime(file, task.TimeBasis);

                if (age is null || age > DateTimeOffset.Now)
                {
                    // 取不到时间或时间戳在未来，一律不删
                    result.SkippedCount++;
                    continue;
                }

                if (age >= cutoff)
                {
                    result.SkippedCount++;
                    continue;
                }

                DeleteFile(file, task, result);

                if (files.Count > 0)
                {
                    progress?.Report((index + 1) * 100.0 / files.Count);
                }
            }

            result.Complete();
            progress?.Report(100);
            _logger.TaskResult(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            // 取消必须上抛：整批取消要在调用方中断后续任务、单任务终止要走
            // 「已手动终止」分支，吞掉的话调用方无法区分自然完成与被取消
            throw;
        }
        catch (Exception ex)
        {
            return Fail(result, $"清理失败：{ex.Message}", ex);
        }
    }

    private void DeleteFile(string file, CleanupTask task, TaskRunResult result)
    {
        // 防线二：删除前再次确认文件确实位于目标目录之下
        if (!FileSelector.IsUnderDirectory(file, task.TargetDirectory))
        {
            result.SkippedCount++;
            result.AddDetail($"跳过（路径超出清理范围）：{file}");
            return;
        }

        long size = 0;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                result.SkippedCount++;
                return;
            }

            size = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.FailedCount++;
            result.AddDetail($"读取失败：{file}（{ex.Message}）");
            return;
        }

        try
        {
            // 只读文件直接删除会抛异常，这里清除只读属性后重试一次
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                result.AddDetail($"已清除只读属性后删除：{file}");
            }

            File.Delete(file);

            result.SuccessCount++;
            result.ProcessedBytes += size;
        }
        catch (Exception ex)
        {
            result.FailedCount++;
            result.AddDetail($"删除失败：{file}（{ex.Message}）");
            _logger.Warn($"删除文件失败：{file}（{ex.Message}）", task.Name);
        }
    }

    private static DateTimeOffset? GetFileTime(string file, AgeTimeBasis basis)
    {
        try
        {
            return basis == AgeTimeBasis.CreationTime
                ? new DateTimeOffset(File.GetCreationTime(file))
                : new DateTimeOffset(File.GetLastWriteTime(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private TaskRunResult Fail(TaskRunResult result, string message, Exception? exception = null)
    {
        result.Fail(message);
        _logger.Error(message, result.TaskName, exception);
        _logger.TaskResult(result);
        return result;
    }
}
