using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileKeeper.App.Services;
using FileKeeper.Core.Configuration;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;

namespace FileKeeper.App.ViewModels;

/// <summary>
/// 运行日志。日志由服务进程写入，界面端只读。
/// 服务与界面是两个进程，所以这里直接读文件而不是接收事件——这样无论服务是否在跑，
/// 界面都能看到历史日志。
/// </summary>
public sealed partial class LogViewModel : ObservableObject, IDisposable
{
    private readonly IDialogService _dialogs;
    private readonly ConfigStore _config;
    private readonly DispatcherTimer? _autoRefreshTimer;

    public LogViewModel(IDialogService dialogs, IAppLogger logger, ConfigStore config)
    {
        _dialogs = dialogs;
        _config = config;

        var logsDirectory = logger is FileLogger fileLogger
            ? fileLogger.LogsDirectory
            : LogPaths.Resolve(null);

        _autoRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(8)
        };

        _autoRefreshTimer.Tick += (_, _) =>
        {
            if (AutoRefresh)
            {
                Load();
            }
        };

        // 定时器常开，由 AutoRefresh 开关决定是否真正重载
        _autoRefreshTimer.Start();

        // 放在最后赋值：LogsDirectory 一变就会触发下面的重载钩子，
        // 构造函数里再显式调一次就是重复读一遍磁盘
        LogsDirectory = logsDirectory;
    }

    public ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>存在日志文件的日期，最新的排前面</summary>
    public ObservableCollection<DateTime> AvailableDates { get; } = new();

    [ObservableProperty]
    private string _logsDirectory = string.Empty;

    [ObservableProperty]
    private DateTime _selectedDate = DateTime.Today;

    [ObservableProperty]
    private string _keyword = string.Empty;

    [ObservableProperty]
    private string _levelFilter = "全部";

    [ObservableProperty]
    private bool _autoRefresh;

    [ObservableProperty]
    private string _statisticsText = string.Empty;

    public IReadOnlyList<string> LevelFilters { get; } = new[] { "全部", "信息", "警告", "错误" };

    private void LoadDates()
    {
        IReadOnlyList<DateTime> dates;

        try
        {
            dates = FileLogger.GetAvailableDates(LogsDirectory);
        }
        catch (Exception)
        {
            dates = Array.Empty<DateTime>();
        }

        var ordered = dates.OrderByDescending(d => d).ToList();

        // 当天无论有没有日志都应可选，否则服务还没跑过时日期下拉是空的
        if (!ordered.Contains(DateTime.Today))
        {
            ordered.Insert(0, DateTime.Today);
        }

        AvailableDates.Clear();

        foreach (var date in ordered)
        {
            AvailableDates.Add(date);
        }
    }

    [RelayCommand]
    private void Load()
    {
        var levels = LevelFilter switch
        {
            "信息" => new List<LogLevel> { LogLevel.Info },
            "警告" => new List<LogLevel> { LogLevel.Warn },
            "错误" => new List<LogLevel> { LogLevel.Error },
            _ => null
        };

        var keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();

        IReadOnlyList<LogEntry> entries;

        try
        {
            entries = FileLogger.Read(SelectedDate, keyword, null, levels, LogsDirectory);
        }
        catch (Exception)
        {
            entries = Array.Empty<LogEntry>();
        }

        Entries.Clear();

        // 最新的排在最上面，符合查看日志的直觉
        foreach (var entry in entries.OrderByDescending(e => e.Timestamp))
        {
            Entries.Add(entry);
        }

        var info = Entries.Count(e => e.Level == LogLevel.Info);
        var warn = Entries.Count(e => e.Level == LogLevel.Warn);
        var error = Entries.Count(e => e.Level == LogLevel.Error);

        StatisticsText = $"共 {Entries.Count} 条 · 信息 {info} · 警告 {warn} · 错误 {error}";
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(LogsDirectory);
            Process.Start(new ProcessStartInfo(LogsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage("无法打开", $"打开日志目录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 清理过期日志。不清空当天日志——那会连正在排查的现场一起抹掉。
    /// </summary>
    [RelayCommand]
    private void Purge()
    {
        var confirmed = _dialogs.Confirm(
            "清理过期日志",
            "将删除超过保留天数的历史日志文件，当天日志不受影响。",
            "清理",
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        try
        {
            // 必须带上配置里的保留天数：这个静态方法的默认值是 30，
            // 不传的话手动清理永远按 30 天算，设置页改的保留天数形同虚设
            var retentionDays = Math.Max(1, _config.Current.Settings.LogRetentionDays);
            FileLogger.PurgeOldLogs(LogsDirectory, retentionDays);
            Load();
            _dialogs.ShowMessage("清理完成", $"已按保留天数 {retentionDays} 天清理过期日志。");
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage("清理失败", ex.Message);
        }
    }

    /// <summary>
    /// 日志目录换了就整体重载。换目录通常意味着历史日志刚被搬到新的位置，
    /// 日期下拉若还停留在旧目录，用户会以为改目录把日志弄丢了。
    /// </summary>
    partial void OnLogsDirectoryChanged(string value)
    {
        LoadDates();
        Load();
    }

    // 筛选条件一变就重新加载，省掉一个「查询」按钮
    partial void OnSelectedDateChanged(DateTime value) => Load();

    partial void OnLevelFilterChanged(string value) => Load();

    partial void OnKeywordChanged(string value) => Load();

    public void Dispose() => _autoRefreshTimer?.Stop();
}
