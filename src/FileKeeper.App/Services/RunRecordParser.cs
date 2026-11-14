using System.Text.RegularExpressions;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;

namespace FileKeeper.App.Services;

/// <summary>任务单次运行的展示记录，来源为日志文件的解析结果。</summary>
public sealed class TaskRunRecord
{
    public string TaskName { get; init; } = string.Empty;

    /// <summary>计划运行时间；手动触发或旧格式日志没有该信息时为 null</summary>
    public DateTimeOffset? ScheduledAt { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    /// <summary>尚未结束的运行（进行中或异常中断）为 null</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    public string ScheduledText { get; init; } = string.Empty;

    public string StartedText { get; init; } = string.Empty;

    public string FinishedText { get; init; } = string.Empty;

    public string DurationText { get; init; } = string.Empty;

    public string StatusText { get; init; } = string.Empty;

    public bool IsFailure { get; init; }

    public bool IsRunning { get; init; }
}

/// <summary>
/// 把一天的自然语言日志还原成「任务运行记录」列表。
///
/// 识别两种来源，避免升级过渡期重复计数：
/// 1. 结构化行（RUN|状态|计划时间|开始|结束）——精确，优先采用；
/// 2. 自然语言行（开始… / 任务完成：…）——旧格式日志的兜底。
/// 同一次运行两种行都出现时，以 RUN 行为准并丢弃启发式结果。
/// </summary>
public static partial class RunRecordParser
{
    [GeneratedRegex(@"成功 (\d+)，失败 (\d+)")]
    private static partial Regex CountRegex();

    [GeneratedRegex(@"耗时 ([\d.]+)s")]
    private static partial Regex DurationRegex();

    public static IReadOnlyList<TaskRunRecord> Parse(IReadOnlyList<LogEntry> entries, DateTime date)
    {
        // 每个任务名最多一条「进行中」的启发式记录，完成行到来时闭合
        var openRuns = new Dictionary<string, LogEntry>(StringComparer.Ordinal);
        var records = new List<TaskRunRecord>();

        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Context))
            {
                continue;
            }

            if (entry.Message.StartsWith("RUN|", StringComparison.Ordinal))
            {
                if (TryParseRunLine(entry, date, out var record))
                {
                    // RUN 行与启发式的「开始」行描述同一次运行，丢弃占位避免重复计数
                    openRuns.Remove(entry.Context);
                    records.Add(record);
                }

                continue;
            }

            if (entry.Message.StartsWith("开始", StringComparison.Ordinal))
            {
                openRuns[entry.Context] = entry;
                continue;
            }

            if (TryCloseBySummary(entry, openRuns, date, out var closed))
            {
                records.Add(closed);
            }
        }

        // 没等到完成行的运行：进行中，或进程异常中断（状态同样展示为运行中，由用户判断）
        foreach (var open in openRuns.Values)
        {
            records.Add(new TaskRunRecord
            {
                TaskName = open.Context ?? string.Empty,
                StartedAt = open.Timestamp,
                StartedText = FormatTime(open.Timestamp, date),
                FinishedText = "—",
                DurationText = "—",
                StatusText = "运行中",
                IsRunning = true
            });
        }

        return records
            .OrderByDescending(r => r.FinishedAt ?? r.StartedAt)
            .ToList();
    }

    private static bool TryParseRunLine(LogEntry entry, DateTime date, out TaskRunRecord record)
    {
        record = null!;

        // 格式：RUN|状态|计划时间|-|开始时间|结束时间
        var parts = entry.Message.Split('|');

        if (parts.Length < 5 ||
            !DateTimeOffset.TryParse(parts[^2], out var started) ||
            !DateTimeOffset.TryParse(parts[^1], out var finished))
        {
            return false;
        }

        DateTimeOffset? scheduled = parts[2] != "-" && DateTimeOffset.TryParse(parts[2], out var s) ? s : null;
        var statusText = parts[1] switch
        {
            nameof(TaskRunStatus.Success) => "成功",
            nameof(TaskRunStatus.PartialSuccess) => "部分成功",
            nameof(TaskRunStatus.Skipped) => "跳过",
            _ => "失败"
        };

        record = new TaskRunRecord
        {
            TaskName = entry.Context ?? string.Empty,
            ScheduledAt = scheduled,
            StartedAt = started,
            FinishedAt = finished,
            ScheduledText = scheduled is null ? "手动触发" : FormatTime(scheduled.Value, date),
            StartedText = FormatTime(started, date),
            FinishedText = FormatTime(finished, date),
            DurationText = FormatDuration(finished - started),
            StatusText = statusText,
            IsFailure = statusText is "失败" or "部分成功"
        };

        return true;
    }

    private static bool TryCloseBySummary(
        LogEntry entry,
        Dictionary<string, LogEntry> openRuns,
        DateTime date,
        out TaskRunRecord record)
    {
        record = null!;

        var message = entry.Message;

        // 旧格式日志没有 RUN 行，从结果摘要行（任务完成/失败/跳过/已取消）反推
        var isSummary = message.Contains("任务完成：") ||
                        message.Contains("任务失败：") ||
                        message.Contains("任务跳过：") ||
                        message.Contains("已手动终止") ||
                        message.Contains("任务已取消");

        if (!isSummary ||
            entry.Context is null ||
            !openRuns.Remove(entry.Context, out var open))
        {
            return false;
        }

        string statusText;

        if (message.Contains("任务跳过："))
        {
            statusText = "跳过";
        }
        else if (message.Contains("任务失败：") || message.Contains("已手动终止") || message.Contains("任务已取消"))
        {
            statusText = "失败";
        }
        else
        {
            // 「任务完成」细分为成功 / 部分成功：有失败项就不算完全成功
            var counts = CountRegex().Match(message);
            var success = counts.Success && int.TryParse(counts.Groups[1].Value, out var s) ? s : 0;
            var failed = counts.Success && int.TryParse(counts.Groups[2].Value, out var f) ? f : 0;
            statusText = failed == 0 ? "成功" : success > 0 ? "部分成功" : "失败";
        }

        var duration = DurationRegex().Match(message);
        var finishedAt = entry.Timestamp;

        record = new TaskRunRecord
        {
            TaskName = entry.Context,
            StartedAt = open.Timestamp,
            FinishedAt = finishedAt,
            ScheduledText = "—",
            StartedText = FormatTime(open.Timestamp, date),
            FinishedText = FormatTime(finishedAt, date),
            DurationText = duration.Success
                ? FormatDuration(TimeSpan.FromSeconds(double.Parse(duration.Groups[1].Value)))
                : FormatDuration(finishedAt - open.Timestamp),
            StatusText = statusText,
            IsFailure = statusText is "失败" or "部分成功"
        };

        return true;
    }

    private static string FormatTime(DateTimeOffset timestamp, DateTime date)
        => timestamp.LocalDateTime.Date == date
            ? timestamp.LocalDateTime.ToString("HH:mm:ss")
            : timestamp.LocalDateTime.ToString("MM-dd HH:mm:ss");

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalSeconds < 60
            ? $"{duration.TotalSeconds:F1} 秒"
            : $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒";
}
