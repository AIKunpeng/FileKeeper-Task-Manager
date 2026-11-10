using FileKeeper.Core.Models;

namespace FileKeeper.Core.Scheduling;

/// <summary>
/// 调度计算。全部为纯函数（不读写文件、不依赖时钟以外的状态），便于单独验证与界面预览。
///
/// 语义约定：
/// - <see cref="ScheduleConfig.TimeOfDay"/> 是<b>本地时间</b>，内部按本地时间推算后再转 UTC；
/// - 返回的下次执行时间<b>严格晚于</b>当前时刻，避免同一分钟内重复触发；
/// - 夏令时：遇到「不存在的时刻」（春季跳跃）顺延到跳跃后的第一刻；
///   遇到「重复的时刻」（秋季回拨）取<b>较早</b>的一次 —— 宁可早跑，不要漏跑。
/// </summary>
public static class ScheduleCalculator
{
    private static readonly string[] WeekdayNames = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    /// <summary>最多向后搜索的天数，覆盖跨年与「每月 31 号」落在短月份的情况</summary>
    private const int MaxSearchDays = 400;

    private static readonly TimeZoneInfo LocalZone = TimeZoneInfo.Local;

    /// <summary>
    /// 计算下一次执行时间（UTC）。找不到（配置非法）时返回 null。
    /// </summary>
    public static DateTimeOffset? ComputeNextRunUtc(ScheduleConfig schedule, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var localNow = TimeZoneInfo.ConvertTime(now, LocalZone).DateTime;

        for (var offset = 0; offset <= MaxSearchDays; offset++)
        {
            var date = localNow.Date.AddDays(offset);

            if (!IsRunDay(schedule, date))
            {
                continue;
            }

            var candidate = date + schedule.TimeOfDay.ToTimeSpan();

            if (candidate <= localNow)
            {
                continue;
            }

            candidate = AdjustForDaylightSaving(candidate);

            if (candidate <= localNow)
            {
                continue;
            }

            return ToDateTimeOffset(candidate);
        }

        return null;
    }

    /// <summary>
    /// 判断该日期是否为配置中的执行日。
    /// </summary>
    public static bool IsRunDay(ScheduleConfig schedule, DateTime date)
    {
        switch (schedule.Frequency)
        {
            case ScheduleFrequency.Daily:
                return true;

            case ScheduleFrequency.Weekly:
            {
                var weekday = (int)date.DayOfWeek;
                var days = schedule.Weekdays.Count > 0 ? schedule.Weekdays : new List<int> { weekday };
                return days.Contains(weekday);
            }

            case ScheduleFrequency.Monthly:
            {
                var lastDay = DateTime.DaysInMonth(date.Year, date.Month);
                var days = schedule.MonthDays.Count > 0 ? schedule.MonthDays : new List<int> { date.Day };

                // 某月不足该天数时取当月最后一天（例如「31 号」在 2 月落到 28/29 号）
                return days.Any(d => date.Day == Math.Min(d, lastDay));
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// 判定本轮是否执行。
    /// </summary>
    /// <param name="now">当前时刻</param>
    /// <param name="windowStart">
    /// 上一轮观测时刻（服务启动时取启动时刻）。
    /// 这是区分「正常到期」与「关机期间错过」的唯一依据：
    /// 执行点落在 (windowStart, now] 内，说明服务当时在运行，属于正常到期；
    /// 早于 windowStart，说明服务当时没在跑，属于错过。
    /// </param>
    public static ScheduleDecision Decide(ScheduleConfig schedule, DateTimeOffset now, DateTimeOffset windowStart)
    {
        if (schedule.NextRunUtc is not { } next)
        {
            return ScheduleDecision.NotDue;
        }

        if (next > now)
        {
            return ScheduleDecision.NotDue;
        }

        if (next > windowStart)
        {
            return ScheduleDecision.Due;
        }

        // 已错过。补跑只对「曾经跑过」的任务有意义：
        // 从未执行过的任务直接排到下一次即可，不该当成一次错过的执行而立刻补跑。
        return schedule.RunIfMissed && schedule.LastRunUtc is not null
            ? ScheduleDecision.Missed
            : ScheduleDecision.ExpiredSkipped;
    }

    /// <summary>
    /// 跳过一次错过的执行：只推进下次执行时间，不记录本次执行（因为确实没跑）。
    /// 不推进的话 NextRunUtc 会一直停留在过去，导致每轮都被重复判定。
    /// </summary>
    public static void SkipMissed(ScheduleConfig schedule, DateTimeOffset now)
    {
        schedule.NextRunUtc = ComputeNextRunUtc(schedule, now);
    }

    /// <summary>
    /// 尚未排定下次时间时先补算一次（例如新建任务、或服务首次启动）。
    /// 返回 true 表示配置被修改，需要回写。
    /// </summary>
    public static bool InitializeIfNeeded(ScheduleConfig schedule, DateTimeOffset now)
    {
        if (schedule.NextRunUtc is not null)
        {
            return false;
        }

        schedule.NextRunUtc = ComputeNextRunUtc(schedule, now);
        return true;
    }

    /// <summary>
    /// 执行完成后推进调度：记录本次执行时间并算出下一次。
    /// </summary>
    public static void Advance(ScheduleConfig schedule, DateTimeOffset now)
    {
        schedule.LastRunUtc = now;
        schedule.NextRunUtc = ComputeNextRunUtc(schedule, now);
    }

    /// <summary>
    /// 人类可读的频率描述，供界面展示。
    /// </summary>
    public static string Describe(ScheduleConfig schedule)
    {
        var time = schedule.TimeOfDay.ToString("HH:mm");

        return schedule.Frequency switch
        {
            ScheduleFrequency.Daily => $"每天 {time}",

            ScheduleFrequency.Weekly => schedule.Weekdays.Count == 0
                ? $"每周 {time}"
                : $"每周 {string.Join("、", schedule.Weekdays.Select(d => WeekdayNames[d]))} {time}",

            ScheduleFrequency.Monthly => schedule.MonthDays.Count == 0
                ? $"每月 {time}"
                : $"每月 {string.Join("、", schedule.MonthDays.Select(d => $"{d} 号"))} {time}",

            _ => time
        };
    }

    private static DateTime AdjustForDaylightSaving(DateTime local)
    {
        var candidate = local;

        // 春季跳跃时该时刻不存在（如 02:00 直接跳到 03:00），顺延到跳跃后的第一刻
        for (var i = 0; i < 3 && LocalZone.IsInvalidTime(candidate); i++)
        {
            candidate = candidate.AddHours(1);
        }

        return candidate;
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime local)
    {
        if (LocalZone.IsAmbiguousTime(local))
        {
            // 秋季回拨时该时刻出现两次，取较早的一次
            var offsets = LocalZone.GetAmbiguousTimeOffsets(local);
            return new DateTimeOffset(local, offsets[0]);
        }

        return new DateTimeOffset(local, LocalZone.GetUtcOffset(local));
    }
}
