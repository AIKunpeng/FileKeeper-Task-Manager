namespace FileKeeper.Core.Models;

/// <summary>
/// 全局设置。
/// </summary>
public sealed class GlobalSettings
{
    /// <summary>日志保留天数，超出后自动清理</summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>新建压缩任务时的默认输出目录</summary>
    public string? DefaultOutputDirectory { get; set; }

    /// <summary>关闭主窗口时最小化到托盘</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>调度循环扫描间隔（秒），仅服务重启后生效</summary>
    public int SchedulerIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// 日志目录。为 null 时使用默认位置 %ProgramData%\FileKeeper\logs。
    /// 配置文件本身始终留在 %ProgramData%\FileKeeper 下——它必须能被 LocalSystem 与普通用户双方找到，
    /// 这里只让日志的存放位置可改。
    /// </summary>
    public string? LogDirectory { get; set; }

    /// <summary>
    /// 待搬迁的历史日志来源目录。用户改了日志目录后写入，搬迁成功的一方负责把它清空。
    /// 之所以要在配置里留状态而不是「当场搬完」：界面端通常没有 %ProgramData% 的写权限，
    /// 搬不动时必须留给后台服务（LocalSystem）重试，否则历史日志就丢了。
    /// </summary>
    public string? PendingLogMigrationFrom { get; set; }

    public void Normalize()
    {
        LogRetentionDays = Math.Clamp(LogRetentionDays, 1, 3650);
        SchedulerIntervalSeconds = Math.Clamp(SchedulerIntervalSeconds, 5, 3600);
        DefaultOutputDirectory = NormalizeDirectory(DefaultOutputDirectory);
        LogDirectory = NormalizeDirectory(LogDirectory);
        PendingLogMigrationFrom = NormalizeDirectory(PendingLogMigrationFrom);
    }

    /// <summary>
    /// 归一化用户手填的路径：去掉拖拽或粘贴带来的引号、多余空格与多余的路径分隔符。
    /// 长度判断刻意短路：-3 是盘符根目录的长度，不能把 D:\ 削成 D:，
    /// 那会让它从盘符根变成「当前目录下的同名项」。
    /// </summary>
    private static string? NormalizeDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().Trim('"').Trim();

        while (text.Length > 3 &&
               (text[^1] == Path.DirectorySeparatorChar || text[^1] == Path.AltDirectorySeparatorChar))
        {
            text = text[..^1];
        }

        return text.Length == 0 ? null : text;
    }
}

/// <summary>
/// 配置根对象，序列化为 %ProgramData%\FileKeeper\config.json。
/// </summary>
public sealed class AppConfig
{
    /// <summary>配置文件结构版本，用于后续升级迁移</summary>
    public int Version { get; set; } = 1;

    public List<CompressionTask> CompressionTasks { get; set; } = new();

    public List<CleanupTask> CleanupTasks { get; set; } = new();

    public List<SyncTask> SyncTasks { get; set; } = new();

    public List<ProgramTask> ProgramTasks { get; set; } = new();

    public GlobalSettings Settings { get; set; } = new();

    /// <summary>
    /// 归一化并剔除无效任务，保证调度端拿到的数据始终可用。
    /// </summary>
    public void Normalize()
    {
        Settings ??= new GlobalSettings();
        Settings.Normalize();

        CompressionTasks ??= new List<CompressionTask>();
        CleanupTasks ??= new List<CleanupTask>();
        SyncTasks ??= new List<SyncTask>();
        ProgramTasks ??= new List<ProgramTask>();

        foreach (var task in CompressionTasks)
        {
            task.Normalize();
        }

        foreach (var task in CleanupTasks)
        {
            task.Normalize();
        }

        foreach (var task in SyncTasks)
        {
            task.Normalize();
        }

        foreach (var task in ProgramTasks)
        {
            task.Normalize();
        }

        // 剔除重复 Id，避免调度与界面定位错乱
        CompressionTasks = DeduplicateById(CompressionTasks);
        CleanupTasks = DeduplicateById(CleanupTasks);
        SyncTasks = DeduplicateById(SyncTasks);
        ProgramTasks = DeduplicateById(ProgramTasks);
    }

    private static List<T> DeduplicateById<T>(List<T> tasks) where T : class
    {
        var result = new List<T>(tasks.Count);
        var seen = new HashSet<Guid>();

        foreach (var task in tasks)
        {
            var id = (Guid)(task.GetType().GetProperty(nameof(CompressionTask.Id))?.GetValue(task) ?? Guid.Empty);
            if (id == Guid.Empty)
            {
                continue;
            }

            if (seen.Add(id))
            {
                result.Add(task);
            }
        }

        return result;
    }
}
