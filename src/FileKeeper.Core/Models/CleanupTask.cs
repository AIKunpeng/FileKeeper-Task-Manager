namespace FileKeeper.Core.Models;

/// <summary>
/// 定时清理任务。
/// </summary>
public sealed class CleanupTask : IScheduledTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "新建清理任务";

    public bool Enabled { get; set; } = true;

    /// <summary>待清理目录</summary>
    public string TargetDirectory { get; set; } = string.Empty;

    /// <summary>是否包含子目录</summary>
    public bool IncludeSubDirectories { get; set; } = true;

    /// <summary>文件过滤方式</summary>
    public FileFilterMode FilterMode { get; set; } = FileFilterMode.All;

    /// <summary>指定扩展名，形如 ".log"；统一存小写带点形式。为空且为指定模式时视为不过滤</summary>
    public List<string> Extensions { get; set; } = new();

    /// <summary>排除的子目录名或完整路径</summary>
    public List<string> ExcludedDirectories { get; set; } = new();

    /// <summary>阈值数量，与 <see cref="ThresholdUnit"/> 相乘得到过期时长</summary>
    public int ThresholdValue { get; set; } = 30;

    /// <summary>阈值单位：小时 / 天 / 周 / 月</summary>
    public CleanupThresholdUnit ThresholdUnit { get; set; } = CleanupThresholdUnit.Days;

    /// <summary>时间基准：最后修改时间 / 创建时间</summary>
    public AgeTimeBasis TimeBasis { get; set; } = AgeTimeBasis.LastWriteTime;

    public ScheduleConfig Schedule { get; set; } = new();

    /// <summary>换算后的过期时长（周按 7 天、月按 30 天，小时保持小时精度）</summary>
    public TimeSpan Threshold
    {
        get
        {
            var value = Math.Max(0, ThresholdValue);
            return ThresholdUnit switch
            {
                CleanupThresholdUnit.Hours => TimeSpan.FromHours(value),
                CleanupThresholdUnit.Weeks => TimeSpan.FromDays(value * 7),
                CleanupThresholdUnit.Months => TimeSpan.FromDays(value * 30),
                _ => TimeSpan.FromDays(value)
            };
        }
    }

    /// <summary>
    /// 深拷贝。编辑页基于副本工作，取消时直接丢弃，不会污染原配置。
    /// </summary>
    public CleanupTask Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        TargetDirectory = TargetDirectory,
        IncludeSubDirectories = IncludeSubDirectories,
        FilterMode = FilterMode,
        Extensions = new List<string>(Extensions),
        ExcludedDirectories = new List<string>(ExcludedDirectories),
        ThresholdValue = ThresholdValue,
        ThresholdUnit = ThresholdUnit,
        TimeBasis = TimeBasis,
        Schedule = Schedule.Clone()
    };

    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "未命名清理任务" : Name.Trim();
        TargetDirectory = CompressionTask.NormalizePath(TargetDirectory);
        ThresholdValue = Math.Max(0, ThresholdValue);

        Extensions = Extensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().ToLowerInvariant())
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .Distinct()
            .ToList();

        ExcludedDirectories = ExcludedDirectories
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().Trim('"').Trim().TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Schedule.Normalize();
    }
}
