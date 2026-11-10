using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileKeeper.App.Services;
using FileKeeper.Core;
using FileKeeper.Core.Models;

namespace FileKeeper.App.ViewModels;

/// <summary>
/// 主窗口外壳：左侧导航 + 内容区。
/// 编辑页也复用内容区，因此导航目标既可能是主页面，也可能是某个编辑页。
///
/// 导航只有一个真值来源——<see cref="SelectedNav"/>：
/// 左侧菜单的选中状态绑在它上面，页面切换与任务分区筛选都由它派生，
/// 避免「菜单高亮一个、内容显示另一个」这类错位。
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public MainViewModel(
        TasksViewModel tasks,
        LogViewModel logs,
        SettingsViewModel settings,
        NavigationService navigation,
        ServiceStatusMonitor serviceStatus)
    {
        Tasks = tasks;
        Logs = logs;
        Settings = settings;
        ServiceStatus = serviceStatus;

        navigation.Attach(NavigateTo, GoBack);

        CurrentPage = tasks;
        CurrentPageKind = PageKind.Tasks;
    }

    public TasksViewModel Tasks { get; }

    public LogViewModel Logs { get; }

    public SettingsViewModel Settings { get; }

    public ServiceStatusMonitor ServiceStatus { get; }

    /// <summary>当前内容区承载的 ViewModel。主页面或编辑页。</summary>
    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private PageKind _currentPageKind = PageKind.Tasks;

    /// <summary>左侧导航当前选中项。</summary>
    [ObservableProperty]
    private NavItem _selectedNav = NavItem.TasksAll;

    /// <summary>编辑页打开时隐藏左侧导航，避免用户在编辑中跳走丢失修改</summary>
    [ObservableProperty]
    private bool _isEditorOpen;

    public string WindowTitle => ProductInfo.Name;

    /// <summary>窗口底部版权栏的文案</summary>
    public string Copyright => ProductInfo.Copyright;

    /// <summary>
    /// 任务分区子菜单只在任务页展开：切到日志/设置时收起，
    /// 左侧导航就呈现「任务总览 → 四个分区」的两层结构。
    /// </summary>
    public bool ShowTaskSubNav => CurrentPageKind == PageKind.Tasks;

    partial void OnCurrentPageKindChanged(PageKind value) => OnPropertyChanged(nameof(ShowTaskSubNav));

    partial void OnSelectedNavChanged(NavItem value)
    {
        switch (value)
        {
            case NavItem.TasksAll:
                ApplyTasks(null);
                break;

            case NavItem.TasksCompression:
                ApplyTasks(TaskKind.Compression);
                break;

            case NavItem.TasksCleanup:
                ApplyTasks(TaskKind.Cleanup);
                break;

            case NavItem.TasksSync:
                ApplyTasks(TaskKind.Sync);
                break;

            case NavItem.TasksProgram:
                ApplyTasks(TaskKind.Program);
                break;

            case NavItem.Logs:
                ApplyPage(PageKind.Logs, Logs);
                break;

            case NavItem.Settings:
                ApplyPage(PageKind.Settings, Settings);
                break;
        }
    }

    /// <summary>
    /// 导航入口。目标既可能是主页面（PageKind）、带筛选的任务页请求，
    /// 也可能是某个编辑页（ViewModel），因此统一收口成 object，进来进行类型分流。
    /// </summary>
    private void NavigateTo(object target)
    {
        switch (target)
        {
            case PageKind kind:
                Navigate(kind);
                return;

            case TasksPageRequest request:
                ShowTasks(request.Filter);
                return;

            default:
                CurrentPage = target;
                IsEditorOpen = true;
                return;
        }
    }

    /// <summary>
    /// 切到任务页并指定分区筛选。这是给「导航服务」「清除筛选」这类
    /// 外部请求用的入口，内部保证选中项与内容同步。
    /// </summary>
    private void ShowTasks(TaskKind? filter)
    {
        var nav = ToNavItem(filter);

        if (SelectedNav == nav)
        {
            // 选中项没变化就不会触发 OnSelectedNavChanged，
            // 但页面状态仍要重置（例如从编辑页返回、或重复点同一个菜单项）
            ApplyTasks(filter);
            return;
        }

        SelectedNav = nav;
    }

    /// <summary>页面级导航（编辑页返回、托盘跳转等）。菜单选中态由 <see cref="SelectedNav"/> 承载，不经过这里</summary>
    private void Navigate(PageKind kind)
    {
        switch (kind)
        {
            case PageKind.Logs:
                Select(NavItem.Logs, () => ApplyPage(PageKind.Logs, Logs));
                break;

            case PageKind.Settings:
                Select(NavItem.Settings, () => ApplyPage(PageKind.Settings, Settings));
                break;

            default:
                // 返回任务页时保留当前分区筛选，用户从编辑页退回来不会丢失所在分区
                ShowTasks(Tasks.ActiveFilter);
                break;
        }
    }

    /// <summary>选中项未变化时 SelectedNav 不会通知，此时显式执行一次页面切换</summary>
    private void Select(NavItem nav, Action apply)
    {
        if (SelectedNav == nav)
        {
            apply();
            return;
        }

        SelectedNav = nav;
    }

    private void ApplyTasks(TaskKind? filter)
    {
        Tasks.SetActiveFilter(filter);
        CurrentPageKind = PageKind.Tasks;
        IsEditorOpen = false;
        CurrentPage = Tasks;

        // 从编辑页返回时刷新总览，让刚保存的改动立刻可见
        Tasks.Refresh();
    }

    private void ApplyPage(PageKind kind, object page)
    {
        CurrentPageKind = kind;
        IsEditorOpen = false;
        CurrentPage = page;
    }

    private static NavItem ToNavItem(TaskKind? filter) => filter switch
    {
        TaskKind.Compression => NavItem.TasksCompression,
        TaskKind.Cleanup => NavItem.TasksCleanup,
        TaskKind.Sync => NavItem.TasksSync,
        TaskKind.Program => NavItem.TasksProgram,
        _ => NavItem.TasksAll
    };

    [RelayCommand]
    private void GoBack() => Navigate(PageKind.Tasks);

    /// <summary>执行全部已启用任务。托盘菜单与界面按钮共用同一条路径。</summary>
    public async Task RunAllAsync()
    {
        Navigate(PageKind.Tasks);
        // 续体回到 UI 线程执行，因为后面的服务状态刷新会驱动界面绑定
        await Tasks.RunAllAsync(CancellationToken.None);
        ServiceStatus.Refresh();
    }

    public void Dispose()
    {
        Tasks.Dispose();
        Logs.Dispose();
        ServiceStatus.Dispose();
    }
}
