namespace FileKeeper.Core.Models;

/// <summary>
/// 调度配置：按天/周/月 + 具体时刻。
/// </summary>
public sealed class ScheduleConfig
{
    /// <summary>频率：每天 / 每周 / 每月</summary>
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Daily;

    /// <summary>
    /// 周模式下选中的周几，取值 0=周日 .. 6=周六。
    /// 为空时回退为「当天周几」。
    /// </summary>
    public List<int> Weekdays { get; set; } = new();

    /// <summary>
    /// 月模式下选中的日期，取值 1..31；若某月不足该天数则取当月最后一天。
    /// </summary>
    public List<int> MonthDays { get; set; } = new();

    /// <summary>执行时刻（本地时间）</summary>
    public TimeOnly TimeOfDay { get; set; } = new(2, 0);

    /// <summary>服务重启后是否补跑上一次错过的执行</summary>
    public bool RunIfMissed { get; set; } = true;

    /// <summary>上次执行时间（UTC），由执行端回写</summary>
    public DateTimeOffset? LastRunUtc { get; set; }

    /// <summary>下次执行时间（UTC），由执行端回写，界面展示用</summary>
    public DateTimeOffset? NextRunUtc { get; set; }

    public ScheduleConfig Clone() => new()
    {
        Frequency = Frequency,
        Weekdays = new List<int>(Weekdays),
        MonthDays = new List<int>(MonthDays),
        TimeOfDay = TimeOfDay,
        RunIfMissed = RunIfMissed,
        LastRunUtc = LastRunUtc,
        NextRunUtc = NextRunUtc
    };

    /// <summary>
    /// 归一化：过滤非法值、去重排序，避免脏配置导致调度计算异常。
    /// </summary>
    public void Normalize()
    {
        Weekdays = Weekdays.Where(d => d is >= 0 and <= 6).Distinct().OrderBy(d => d).ToList();
        MonthDays = MonthDays.Where(d => d is >= 1 and <= 31).Distinct().OrderBy(d => d).ToList();

        if (Frequency == ScheduleFrequency.Weekly && Weekdays.Count == 0)
        {
            Weekdays.Add((int)DateTime.Today.DayOfWeek);
        }

        if (Frequency == ScheduleFrequency.Monthly && MonthDays.Count == 0)
        {
            MonthDays.Add(DateTime.Today.Day);
        }
    }

    /// <summary>
    /// 保留运行时调度状态（LastRunUtc / NextRunUtc），调度配置内容以本对象为准。
    ///
    /// 编辑页基于打开时的配置快照工作，保存时若整对象替换，会把服务端在编辑期间
    /// 推进的 LastRunUtc / NextRunUtc 一并回滚到过去，任务会被立刻重跑一次。
    /// 调度配置被用户改过时，旧的 NextRunUtc 已不适用，按新配置重新推算。
    /// </summary>
    public void RestoreRuntimeStateFrom(ScheduleConfig source)
    {
        LastRunUtc = source.LastRunUtc;

        var configChanged =
            Frequency != source.Frequency ||
            TimeOfDay != source.TimeOfDay ||
            RunIfMissed != source.RunIfMissed ||
            !Weekdays.SequenceEqual(source.Weekdays) ||
            !MonthDays.SequenceEqual(source.MonthDays);

        NextRunUtc = configChanged
            ? Scheduling.ScheduleCalculator.ComputeNextRunUtc(this, DateTimeOffset.Now)
            : source.NextRunUtc;
    }
}
