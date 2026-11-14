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
///
/// 两种视图：运行记录（按任务运行汇总，默认）与原始日志（逐行）。
/// 读取全部走线程池；连续触发（自动刷新、按键、切日期）用生成号守卫，只让最新一次的结果生效。
/// </summary>
public sealed partial class LogViewModel : ObservableObject, IDisposable
{
    private readonly IDialogService _dialogs;
    private readonly ConfigStore _config;
    private readonly DispatcherTimer? _autoRefreshTimer;
    private readonly DispatcherTimer _keywordDebounceTimer;

    /// <summary>加载代数。发起时递增，结果返回时比对，过期结果直接丢弃</summary>
    private int _loadGeneration;

    /// <summary>用户是否手动选过日期。选过之后跨天就不再自动跳到最新一天</summary>
    private bool _dateTouched;

    /// <summary>自动跟随最新日期的内部赋值标记，避免误判成用户操作</summary>
    private bool _autoSelecting;

    /// <summary>两种视图的统计缓存，切换视图时无需重读文件</summary>
    private string _runsStatistics = string.Empty;
    private string _entriesStatistics = string.Empty;

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
                _ = LoadSafeAsync();
            }
        };

        // 定时器常开，由 AutoRefresh 开关决定是否真正重载
        _autoRefreshTimer.Start();

        // 关键字过滤防抖：每敲一个字符就读整个日志文件代价太高，
        // 停止输入 300ms 后才真正查询
        _keywordDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _keywordDebounceTimer.Tick += (_, _) =>
        {
            _keywordDebounceTimer.Stop();
            _ = LoadSafeAsync();
        };

        // 放在最后赋值：LogsDirectory 一变就会触发下面的重载钩子，
        // 构造函数里再显式调一次就是重复读一遍磁盘
        LogsDirectory = logsDirectory;
    }

    /// <summary>任务运行记录（默认视图）</summary>
    public ObservableCollection<TaskRunRecord> Runs { get; } = new();

    /// <summary>原始日志行</summary>
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
    private string _viewMode = RunView;

    [ObservableProperty]
    private bool _autoRefresh;

    [ObservableProperty]
    private string _statisticsText = string.Empty;

    public const string RunView = "运行记录";

    public const string RawView = "原始日志";

    public IReadOnlyList<string> ViewModes { get; } = new[] { RunView, RawView };

    public IReadOnlyList<string> LevelFilters { get; } = new[] { "全部", "信息", "警告", "错误" };

    [RelayCommand]
    private async Task LoadAsync() => await LoadSafeAsync().ConfigureAwait(true);

    private async Task LoadSafeAsync()
    {
        var generation = ++_loadGeneration;

        try
        {
            await LoadCoreAsync(generation).ConfigureAwait(true);
        }
        catch
        {
            // 日志读取失败（文件被锁、目录暂时不可见）保持现状即可，下次刷新再试
        }
    }

    private async Task LoadCoreAsync(int generation)
    {
        var logsDirectory = LogsDirectory;
        var selectedDate = SelectedDate;
        var keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();
        var levels = LevelFilter switch
        {
            "信息" => new List<LogLevel> { LogLevel.Info },
            "警告" => new List<LogLevel> { LogLevel.Warn },
            "错误" => new List<LogLevel> { LogLevel.Error },
            _ => null
        };

        // 日期下拉必须每次刷新都重新枚举：App 常驻托盘可能一连开好几天，
        // 只在构造时枚举一次的话，新一天的日志文件永远出现在下拉里
        var datesTask = Task.Run(() => FileLogger.GetAvailableDates(logsDirectory));
        var entriesTask = Task.Run(() => FileLogger.Read(selectedDate, keyword, null, levels, logsDirectory));
        await Task.WhenAll(datesTask, entriesTask).ConfigureAwait(true);

        // 过期结果：期间用户又触发了新的加载，或目录已被切换
        if (generation != _loadGeneration ||
            !string.Equals(logsDirectory, LogsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var dates = datesTask.Result.OrderByDescending(d => d).ToList();

        // 当天无论有没有日志都应可选，否则服务还没跑过时日期下拉是空的
        if (!dates.Contains(DateTime.Today))
        {
            dates.Insert(0, DateTime.Today);
        }

        AvailableDates.Clear();

        foreach (var date in dates)
        {
            AvailableDates.Add(date);
        }

        // 跨天自动跟随：页面挂着不动（App 托盘常驻，一开好几天），
        // 新的一天应自动展示最新日志。用户手动选过日期后不再代选
        if (!_dateTouched && dates[0] != SelectedDate && !_autoSelecting)
        {
            _autoSelecting = true;
            SelectedDate = dates[0];
            _autoSelecting = false;
            _ = LoadSafeAsync();
            return;
        }

        ApplyEntries(entriesTask.Result);
        ApplyRuns(await Task.Run(() => RunRecordParser.Parse(entriesTask.Result, SelectedDate)).ConfigureAwait(true), keyword);
    }

    private void ApplyEntries(IReadOnlyList<LogEntry> entries)
    {
        Entries.Clear();

        // 最新的排在最上面，符合查看日志的直觉；
        // RUN 行是给运行记录视图用的机器可读行，原始视图里隐藏
        foreach (var entry in entries.Where(e => !e.Message.StartsWith("RUN|", StringComparison.Ordinal))
                     .OrderByDescending(e => e.Timestamp))
        {
            Entries.Add(entry);
        }

        var info = Entries.Count(e => e.Level == LogLevel.Info);
        var warn = Entries.Count(e => e.Level == LogLevel.Warn);
        var error = Entries.Count(e => e.Level == LogLevel.Error);

        _entriesStatistics = $"共 {Entries.Count} 条 · 信息 {info} · 警告 {warn} · 错误 {error}";
        RefreshStatistics();
    }

    private void ApplyRuns(IReadOnlyList<TaskRunRecord> runs, string? keyword)
    {
        Runs.Clear();

        foreach (var run in runs.Where(r =>
                     string.IsNullOrEmpty(keyword) || r.TaskName.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            Runs.Add(run);
        }

        var success = Runs.Count(r => r.StatusText == "成功");
        var failed = Runs.Count(r => r.IsFailure);
        var others = Runs.Count - success - failed;

        _runsStatistics = $"共 {Runs.Count} 次运行 · 成功 {success} · 失败/部分 {failed}" +
                          (others > 0 ? $" · 其他 {others}" : string.Empty);
        RefreshStatistics();
    }

    private void RefreshStatistics() =>
        StatisticsText = ViewMode == RunView ? _runsStatistics : _entriesStatistics;

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
            _ = LoadSafeAsync();
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
        _dateTouched = false;
        _ = LoadSafeAsync();
    }

    // 筛选条件一变就重新加载，省掉一个「查询」按钮
    partial void OnSelectedDateChanged(DateTime value)
    {
        if (_autoSelecting)
        {
            return;
        }

        _dateTouched = true;
        _ = LoadSafeAsync();
    }

    partial void OnLevelFilterChanged(string value) => _ = LoadSafeAsync();

    partial void OnViewModeChanged(string value) => RefreshStatistics();

    partial void OnKeywordChanged(string value)
    {
        // 输入防抖：停顿 300ms 才查询
        _keywordDebounceTimer.Stop();
        _keywordDebounceTimer.Start();
    }

    public void Dispose()
    {
        _autoRefreshTimer?.Stop();
        _keywordDebounceTimer.Stop();
    }
}
