using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;

namespace FileKeeper.SmokeTest;

/// <summary>
/// 控制台日志器：冒烟输出直接打到 stdout，不写 ProgramData，
/// 保证测试全程不触碰真实业务数据与正式配置。
/// </summary>
public sealed class ConsoleLogger : IAppLogger
{
    public void Info(string message, string? context = null) => Write("INFO ", context, message);

    public void Warn(string message, string? context = null) => Write("WARN ", context, message);

    public void Error(string message, string? context = null, Exception? exception = null)
    {
        Write("ERROR", context, message);
        if (exception is not null)
        {
            Console.WriteLine("        " + exception);
        }
    }

    public void TaskResult(TaskRunResult result)
    {
        var status = result.Status switch
        {
            TaskRunStatus.Success => "成功",
            TaskRunStatus.PartialSuccess => "部分成功",
            TaskRunStatus.Skipped => "跳过",
            _ => "失败"
        };

        Write("RESULT", result.TaskName,
            $"{status}：成功 {result.SuccessCount} / 失败 {result.FailedCount} / 跳过 {result.SkippedCount}"
            + (result.ErrorMessage is null ? string.Empty : $"（{result.ErrorMessage}）"));

        foreach (var detail in result.Details)
        {
            Console.WriteLine("        " + detail);
        }
    }

    private static void Write(string level, string? context, string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {level} {(string.IsNullOrEmpty(context) ? string.Empty : $"[{context}] ")}{message}");
}
