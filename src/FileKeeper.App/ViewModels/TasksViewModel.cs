using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileKeeper.App.Services;
using FileKeeper.Core.Configuration;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;
using FileKeeper.Core.Scheduling;

namespace FileKeeper.App.ViewModels;

/// <summary>
/// 任务总览。压缩 / 清理 / 同步 / 程序四个分区统一在此管理。
/// 左侧导航可以只选中其中一个分区，此时页面仅展示对应分区（见 <see cref="ActiveFilter"/>）。
/// </summary>
public sealed partial class TasksViewModel : ObservableObject, IDisposable
{
    private readonly ConfigStore _config;
    private readonly IAppLogger _logger;
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;

    public TasksViewModel(
        ConfigStore config,
        IAppLogger logger,
        INavigationService navigation,
        IDialogService dialogs)
    {
        _config = config;
        _logger = logger;
        _navigation = navigation;
        _dialogs = dialogs;

        _config.ExternalChanged += OnExternalChanged;
        Refresh();
    }

    public ObservableCollection<TaskItemViewModel> CompressionTasks { get; } = new();

    public ObservableCollection<TaskItemViewModel> CleanupTasks { get; } = new();

    public ObservableCollection<TaskItemViewModel> SyncTasks { get; } = new();

    public ObservableCollection<TaskItemViewModel> ProgramTasks { get; } = new();

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    /// <summary>正在执行的任务 Id。用于热重载重建卡片后恢复「执行中」标记</summary>
    private readonly HashSet<Guid> _runningIds = new();

    /// <summary>每个执行中任务对应的取消源，供卡片「终止」按 Id 定点取消</summary>
    private readonly Dictionary<Guid, CancellationTokenSource> _taskCancellers = new();

    /// <summary>当前分区筛选：null 表示四个分区全部展示</summary>
    [ObservableProperty]
    private TaskKind? _activeFilter;

    public bool HasActiveFilter => ActiveFilter is not null;

    /// <summary>筛选提示条文案</summary>
    public string FilterText => ActiveFilter switch
    {
        TaskKind.Compression => "仅显示压缩任务",
        TaskKind.Cleanup => "仅显示清理任务",
        TaskKind.Sync => "仅显示同步任务",
        TaskKind.Program => "仅显示程序任务",
        _ => string.Empty
    };

    public bool ShowCompressionSection => ActiveFilter is null or TaskKind.Compression;

    public bool ShowCleanupSection => ActiveFilter is null or TaskKind.Cleanup;

    public bool ShowSyncSection => ActiveFilter is null or TaskKind.Sync;

    public bool ShowProgramSection => ActiveFilter is null or TaskKind.Program;

    /// <summary>执行按钮文案：选中单个分区时只执行该分区</summary>
    public string RunAllButtonText => ActiveFilter is null ? "立即执行全部" : "立即执行本类";

    /// <summary>由导航驱动分区筛选</summary>
    public void SetActiveFilter(TaskKind? filter) => ActiveFilter = filter;

    partial void OnActiveFilterChanged(TaskKind? value)
    {
        OnPropertyChanged(nameof(HasActiveFilter));
        OnPropertyChanged(nameof(FilterText));
        OnPropertyChanged(nameof(ShowCompressionSection));
        OnPropertyChanged(nameof(ShowCleanupSection));
        OnPropertyChanged(nameof(ShowSyncSection));
        OnPropertyChanged(nameof(ShowProgramSection));
        OnPropertyChanged(nameof(RunAllButtonText));
    }

    [RelayCommand]
    private void ClearFilter() => _navigation.GoToTasks(null);

    [RelayCommand]
    private void NewCompression()
    {
        var task = new CompressionTask
        {
            OutputDirectory = _config.Current.Settings.DefaultOutputDirectory ?? string.Empty
        };

        _navigation.OpenEditor(new CompressionTaskEditViewModel(
            _config, _logger, _navigation, _dialogs, task, isNew: true));
    }

    [RelayCommand]
    private void NewCleanup()
    {
        _navigation.OpenEditor(new CleanupTaskEditViewModel(
            _config, _logger, _navigation, _dialogs, new CleanupTask(), isNew: true));
    }

    [RelayCommand]
    private void NewSync()
    {
        _navigation.OpenEditor(new SyncTaskEditViewModel(
            _config, _logger, _navigation, _dialogs, new SyncTask(), isNew: true));
    }

    [RelayCommand]
    private void NewProgram()
    {
        _navigation.OpenEditor(new ProgramTaskEditViewModel(
            _config, _logger, _navigation, _dialogs, new ProgramTask(), isNew: true));
    }

    [RelayCommand]
    private void Edit(TaskItemViewModel item)
    {
        var model = FindModel(item);

        if (model is null)
        {
            return;
        }

        switch (model)
        {
            case CompressionTask compression:
                _navigation.OpenEditor(new CompressionTaskEditViewModel(
                    _config, _logger, _navigation, _dialogs, compression, isNew: false));
                break;

            case CleanupTask cleanup:
                _navigation.OpenEditor(new CleanupTaskEditViewModel(
                    _config, _logger, _navigation, _dialogs, cleanup, isNew: false));
                break;

            case SyncTask sync:
                _navigation.OpenEditor(new SyncTaskEditViewModel(
                    _config, _logger, _navigation, _dialogs, sync, isNew: false));
                break;

            case ProgramTask program:
                _navigation.OpenEditor(new ProgramTaskEditViewModel(
                    _config, _logger, _navigation, _dialogs, program, isNew: false));
                break;
        }
    }

    [RelayCommand]
    private void ToggleEnabled(TaskItemViewModel item)
    {
        _config.Update(config => SetEnabled(config, item.Kind, item.Id, !item.Enabled));
        Refresh();
    }

    /// <summary>
    /// 启用开关。IScheduledTask.Enabled 是只读的（调度端只需要读），
    /// 改这一项必须回到具体类型上，所以在这里按类型分发。
    /// </summary>
    private static void SetEnabled(AppConfig config, TaskKind kind, Guid id, bool enabled)
    {
        switch (kind)
        {
            case TaskKind.Compression:
                if (config.CompressionTasks.FirstOrDefault(t => t.Id == id) is { } compression)
                {
                    compression.Enabled = enabled;
                }

                break;

            case TaskKind.Cleanup:
                if (config.CleanupTasks.FirstOrDefault(t => t.Id == id) is { } cleanup)
                {
                    cleanup.Enabled = enabled;
                }

                break;

            case TaskKind.Sync:
                if (config.SyncTasks.FirstOrDefault(t => t.Id == id) is { } sync)
                {
                    sync.Enabled = enabled;
                }

                break;

            case TaskKind.Program:
                if (config.ProgramTasks.FirstOrDefault(t => t.Id == id) is { } program)
                {
                    program.Enabled = enabled;
                }

                break;
        }
    }

    [RelayCommand]
    private void Delete(TaskItemViewModel item)
    {
        var confirmed = _dialogs.Confirm(
            "删除任务",
            $"确定要删除任务「{item.Name}」吗？",
            "删除",
            detail: "删除后无法恢复。已经生成的压缩包、已上传到服务器的文件不受影响。",
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        _config.Update(config =>
        {
            switch (item.Kind)
            {
                case TaskKind.Compression:
                    config.CompressionTasks.RemoveAll(t => t.Id == item.Id);
                    break;

                case TaskKind.Cleanup:
                    config.CleanupTasks.RemoveAll(t => t.Id == item.Id);
                    break;

                case TaskKind.Sync:
                    config.SyncTasks.RemoveAll(t => t.Id == item.Id);
                    break;

                case TaskKind.Program:
                    config.ProgramTasks.RemoveAll(t => t.Id == item.Id);
                    break;
            }
        });

        Refresh();
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunAsync(TaskItemViewModel item, CancellationToken cancellationToken)
    {
        var model = FindModel(item);

        if (model is null)
        {
            return;
        }

        // 不写 ConfigureAwait(false)：await 之后要回到 UI 线程驱动列表刷新与结果弹窗，
        // 它是只适用于库代码的写法，用在界面工程里会让续体落到线程池线程
        await ExecuteAsync(() => new[] { model }, $"正在执行「{item.Name}」", cancellationToken);
    }

    /// <summary>终止正在执行的单个任务。全局取消（底部「取消」按钮）走各自的取消命令</summary>
    [RelayCommand]
    private void Stop(TaskItemViewModel item)
    {
        if (_taskCancellers.TryGetValue(item.Id, out var canceller))
        {
            canceller.Cancel();
        }
    }

    /// <summary>
    /// 页面按钮：执行当前视图里的任务——选中单个分区时只跑这一分区，
    /// 否则跑全部。与托盘菜单的「执行全部」区分开，避免在筛选状态下误跑别的分区。
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private Task RunVisibleAsync(CancellationToken cancellationToken) => RunTasksAsync(ActiveFilter, cancellationToken);

    /// <summary>
    /// 执行全部已启用任务。设为 public 是为了让托盘菜单「立即执行全部任务」
    /// 与界面按钮走同一条路径，避免两处逻辑分叉。
    /// </summary>
    public Task RunAllAsync(CancellationToken cancellationToken) => RunTasksAsync(null, cancellationToken);

    private async Task RunTasksAsync(TaskKind? scope, CancellationToken cancellationToken)
    {
        var tasks = CollectEnabled(_config.Current, scope).ToList();

        if (tasks.Count == 0)
        {
            var hint = scope is null
                ? "当前没有已启用的任务。请先新建任务并启用。"
                : $"当前没有已启用的{TaskKindText.For(scope.Value)}任务。请先新建任务并启用。";

            _dialogs.ShowMessage("没有可执行的任务", hint);
            return;
        }

        var label = scope is null ? "全部任务" : $"{TaskKindText.For(scope.Value)}任务";
        await ExecuteAsync(() => tasks, $"正在执行{label}（共 {tasks.Count} 个）", cancellationToken);
    }

    /// <summary>按分区收集已启用任务。scope 为 null 时收集全部四类</summary>
    private static IEnumerable<IScheduledTask> CollectEnabled(AppConfig config, TaskKind? scope) => scope switch
    {
        TaskKind.Compression => config.CompressionTasks.Where(t => t.Enabled).Cast<IScheduledTask>(),
        TaskKind.Cleanup => config.CleanupTasks.Where(t => t.Enabled).Cast<IScheduledTask>(),
        TaskKind.Sync => config.SyncTasks.Where(t => t.Enabled).Cast<IScheduledTask>(),
        TaskKind.Program => config.ProgramTasks.Where(t => t.Enabled).Cast<IScheduledTask>(),
        _ => config.CompressionTasks.Where(t => t.Enabled).Cast<IScheduledTask>()
            .Concat(config.CleanupTasks.Where(t => t.Enabled))
            .Concat(config.SyncTasks.Where(t => t.Enabled))
            .Concat(config.ProgramTasks.Where(t => t.Enabled))
    };

    private static string KindTextOf(TaskKind kind) => TaskKindText.For(kind);

    public void Dispose()
    {
        _config.ExternalChanged -= OnExternalChanged;
    }

    /// <summary>
    /// 界面端手动执行与服务端调度执行共用同一套执行器，
    /// 因此手动执行结束后也要推进调度时间，否则界面显示的「下次运行」会停在原地。
    /// 每个任务各自挂一个可单独取消的令牌：卡片上的「终止」只停当前这一个，
    /// 底部「取消」停整批（经 cancellationToken 传导）。
    /// </summary>
    private async Task ExecuteAsync(
        Func<IEnumerable<IScheduledTask>> taskSelector,
        string busyText,
        CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            _dialogs.ShowMessage("正在执行", "已有任务在执行中，请等待完成或先终止。");
            return;
        }

        IScheduledTask[] tasks;

        try
        {
            tasks = taskSelector().ToArray();
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage("无法执行", $"读取任务失败：{ex.Message}");
            return;
        }

        IsBusy = true;
        BusyText = busyText;
        ProgressValue = 0;

        var results = new List<TaskRunResult>();
        var progress = new Progress<double>(value => ProgressValue = value);

        try
        {
            foreach (var task in tasks)
            {
                BusyText = $"正在执行「{task.Name}」";
                ProgressValue = 0;

                using var canceller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _taskCancellers[task.Id] = canceller;
                SetRunning(task.Id, true);

                TaskRunResult result;

                try
                {
                    // 执行本体是异步 IO，不会占住 UI 线程；去掉 ConfigureAwait(false) 只改变续体恢复位置，
                    // 保证上面的 BusyText 更新与下面的 Refresh() 都发生在 UI 线程
                    result = await TaskExecutionService.RunAsync(task, _logger, progress, canceller.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 整批被取消（底部「取消」按钮）：放弃剩余任务
                    break;
                }
                catch (OperationCanceledException)
                {
                    // 仅该任务被终止（卡片上的「终止」）：记为已取消，继续后面的任务
                    result = TaskRunResult.Create(task, GetKind(task));
                    result.Skip("已手动终止");
                }
                catch (Exception ex)
                {
                    result = TaskRunResult.Create(task, GetKind(task));
                    result.Fail(ex.Message);
                }
                finally
                {
                    SetRunning(task.Id, false);
                    _taskCancellers.Remove(task.Id);
                }

                results.Add(result);

                // 手动执行后推进调度，保证界面与服务端的排期一致
                if (result.Status is TaskRunStatus.Success or TaskRunStatus.PartialSuccess)
                {
                    AdvanceSchedule(GetKind(task), task.Id, DateTimeOffset.Now);
                }
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            ProgressValue = 0;
            Refresh();
        }

        if (results.Count > 0)
        {
            ReportResults(results);
        }
    }

    private void ReportResults(IReadOnlyList<TaskRunResult> results)
    {
        var lines = results.Select(result =>
        {
            var status = result.Status switch
            {
                TaskRunStatus.Success => "成功",
                TaskRunStatus.PartialSuccess => "部分成功",
                TaskRunStatus.Skipped => "跳过",
                _ => "失败"
            };

            var extra = result.Kind == TaskKind.Cleanup
                ? $"删除 {result.SuccessCount} 个"
                : result.Kind == TaskKind.Sync
                    ? $"上传 {result.SuccessCount} 个"
                    : result.Kind == TaskKind.Program
                        ? $"退出码 {result.ExitCode}"
                        : $"{result.SuccessCount} 个文件";

            return $"【{status}】{result.TaskName}：{extra}";
        });

        var body = string.Join(Environment.NewLine, lines);
        var failed = results.Count(r => r.Status == TaskRunStatus.Failed);
        var errors = results.Where(r => r.ErrorMessage is not null).Select(r => r.ErrorMessage!);

        if (errors.Any())
        {
            body += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, errors);
        }

        _dialogs.ShowMessage(
            failed == 0 ? "执行完成" : $"执行完成（{failed} 个失败）",
            body);
    }

    private void OnExternalChanged(object? sender, EventArgs e) => UiDispatcher.Invoke(Refresh);

    public void Refresh()
    {
        var config = _config.Current;

        Rebuild(CompressionTasks, config.CompressionTasks.Select(TaskItemViewModel.FromModel));
        Rebuild(CleanupTasks, config.CleanupTasks.Select(TaskItemViewModel.FromModel));
        Rebuild(SyncTasks, config.SyncTasks.Select(TaskItemViewModel.FromModel));
        Rebuild(ProgramTasks, config.ProgramTasks.Select(TaskItemViewModel.FromModel));

        var total = CompressionTasks.Count + CleanupTasks.Count + SyncTasks.Count + ProgramTasks.Count;
        var enabled = CompressionTasks.Count(t => t.Enabled)
                      + CleanupTasks.Count(t => t.Enabled)
                      + SyncTasks.Count(t => t.Enabled)
                      + ProgramTasks.Count(t => t.Enabled);

        if (total == 0)
        {
            SummaryText = "还没有任何任务，点击分区标题旁的「+ 新建」开始";
            return;
        }

        // 最近一次执行时间：取所有已排定任务中最早的
        var next = new[] { CompressionTasks, CleanupTasks, SyncTasks, ProgramTasks }
            .SelectMany(list => list)
            .Where(item => item.Enabled && item.NextRunText != "尚未排定")
            .Select(item => item.NextRunText)
            .OrderBy(text => text)
            .FirstOrDefault();

        SummaryText = $"共 {total} 个任务（{enabled} 个已启用）"
                      + (next is null ? string.Empty : $" · 最近一次执行 {next}");
    }

    private void Rebuild(
        ObservableCollection<TaskItemViewModel> target,
        IEnumerable<TaskItemViewModel> source)
    {
        target.Clear();

        foreach (var item in source)
        {
            // 热重载可能在执行中途重建卡片，恢复「执行中」标记
            item.IsRunning = _runningIds.Contains(item.Id);
            target.Add(item);
        }
    }

    /// <summary>更新某个任务的「执行中」状态：记录 Id 并原地更新已生成的卡片</summary>
    private void SetRunning(Guid id, bool running)
    {
        _ = running ? _runningIds.Add(id) : _runningIds.Remove(id);

        foreach (var item in CompressionTasks.Concat(CleanupTasks).Concat(SyncTasks).Concat(ProgramTasks)
                     .Where(i => i.Id == id))
        {
            item.IsRunning = running;
        }
    }

    private IScheduledTask? FindModel(TaskItemViewModel item)
    {
        var config = _config.Current;
        return FindTask(config, item.Kind, item.Id);
    }

    private void AdvanceSchedule(TaskKind kind, Guid id, DateTimeOffset now)
    {
        _config.Update(config =>
        {
            var schedule = FindTask(config, kind, id)?.Schedule;
            if (schedule is not null)
            {
                ScheduleCalculator.Advance(schedule, now);
            }
        });
    }

    private static IScheduledTask? FindTask(AppConfig config, TaskKind kind, Guid id) => kind switch
    {
        TaskKind.Compression => config.CompressionTasks.FirstOrDefault(t => t.Id == id),
        TaskKind.Cleanup => config.CleanupTasks.FirstOrDefault(t => t.Id == id),
        TaskKind.Sync => config.SyncTasks.FirstOrDefault(t => t.Id == id),
        TaskKind.Program => config.ProgramTasks.FirstOrDefault(t => t.Id == id),
        _ => null
    };

    private static TaskKind GetKind(IScheduledTask task) => task switch
    {
        CompressionTask => TaskKind.Compression,
        CleanupTask => TaskKind.Cleanup,
        SyncTask => TaskKind.Sync,
        ProgramTask => TaskKind.Program,
        _ => TaskKind.Compression
    };
}
