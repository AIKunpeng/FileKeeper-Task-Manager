using FileKeeper.Core.Models;

namespace FileKeeper.Core.Logging;

/// <summary>
/// 轻量日志接口。执行器只产出结构化结果，由实现统一落盘。
/// </summary>
public interface IAppLogger
{
    void Info(string message, string? context = null);

    void Warn(string message, string? context = null);

    void Error(string message, string? context = null, Exception? exception = null);

    /// <summary>记录一次任务执行结果</summary>
    void TaskResult(TaskRunResult result);
}

/// <summary>
/// 一条日志记录。
/// </summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message, string? Context)
{
    public string LevelText => Level switch
    {
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        _ => "ERROR"
    };
}
