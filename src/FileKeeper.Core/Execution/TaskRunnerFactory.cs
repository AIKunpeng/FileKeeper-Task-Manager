using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;

namespace FileKeeper.Core.Execution;

/// <summary>
/// 按任务类型分发到对应执行器。调度循环只需面向本类，无需感知具体执行器。
/// </summary>
public static class TaskRunnerFactory
{
    public static Task<TaskRunResult> RunAsync(
        CompressionTask task,
        IAppLogger logger,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => new CompressionRunner(logger).RunAsync(task, progress, cancellationToken);

    public static Task<TaskRunResult> RunAsync(
        CleanupTask task,
        IAppLogger logger,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => new CleanupRunner(logger).RunAsync(task, progress, cancellationToken);

    public static Task<TaskRunResult> RunAsync(
        SyncTask task,
        IAppLogger logger,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => new SyncRunner(logger).RunAsync(task, progress, cancellationToken);

    public static Task<TaskRunResult> RunAsync(
        ProgramTask task,
        IAppLogger logger,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => new ProgramRunner(logger).RunAsync(task, progress, cancellationToken);

    /// <summary>任务是否启用（调度循环统一过滤用）</summary>
    public static bool IsEnabled(IScheduledTask task) => task.Enabled;
}
