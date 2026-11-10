namespace FileKeeper.Core.Models;

/// <summary>
/// 任务单次执行结果，用于日志与界面展示。
/// </summary>
public sealed class TaskRunResult
{
    public Guid TaskId { get; set; }

    public string TaskName { get; set; } = string.Empty;

    public TaskKind Kind { get; set; }

    public TaskRunStatus Status { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public TimeSpan Duration => FinishedAt - StartedAt;

    public int SuccessCount { get; set; }

    public int FailedCount { get; set; }

    public int SkippedCount { get; set; }

    /// <summary>处理的字节数（压缩为源文件总大小，清理为释放的空间）</summary>
    public long ProcessedBytes { get; set; }

    /// <summary>压缩任务产出的压缩包路径</summary>
    public string? OutputPath { get; set; }

    /// <summary>程序任务的子进程退出码。仅程序任务填写，其余类型为 null</summary>
    public int? ExitCode { get; set; }

    /// <summary>整体失败时的异常信息</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>明细行（被跳过的文件、失败原因等），最多保留最近若干条</summary>
    public List<string> Details { get; set; } = new();

    public static TaskRunResult Create<T>(T task, TaskKind kind) where T : class
    {
        var id = (Guid)(task.GetType().GetProperty("Id")?.GetValue(task) ?? Guid.Empty);
        var name = (string)(task.GetType().GetProperty("Name")?.GetValue(task) ?? string.Empty);

        return new TaskRunResult
        {
            TaskId = id,
            TaskName = name,
            Kind = kind,
            Status = TaskRunStatus.Success,
            StartedAt = DateTimeOffset.Now
        };
    }

    public void AddDetail(string detail)
    {
        // 控制明细数量，避免超大目录把日志撑爆
        if (Details.Count < 200)
        {
            Details.Add(detail);
        }
    }

    public void Complete()
    {
        FinishedAt = DateTimeOffset.Now;

        Status = ErrorMessage is not null && SuccessCount == 0
            ? TaskRunStatus.Failed
            : FailedCount > 0
                ? TaskRunStatus.PartialSuccess
                : TaskRunStatus.Success;
    }

    public void Fail(string message)
    {
        ErrorMessage = message;
        Status = TaskRunStatus.Failed;
        FinishedAt = DateTimeOffset.Now;
    }

    public void Skip(string reason)
    {
        Status = TaskRunStatus.Skipped;
        ErrorMessage = reason;
        FinishedAt = DateTimeOffset.Now;
    }
}
