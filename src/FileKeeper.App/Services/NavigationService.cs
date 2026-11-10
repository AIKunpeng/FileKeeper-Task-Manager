using FileKeeper.App.ViewModels;
using FileKeeper.Core.Execution;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;

namespace FileKeeper.App.Services;

public enum PageKind
{
    Tasks,
    Logs,
    Settings
}

/// <summary>
/// 左侧导航的选中项。任务总览下的四个分区各占一项，
/// 与 <see cref="PageKind"/> 的区别是它带上了「任务页的哪个分区」这一层。
/// </summary>
public enum NavItem
{
    TasksAll,
    TasksCompression,
    TasksCleanup,
    TasksSync,
    TasksProgram,
    Logs,
    Settings
}

/// <summary>「打开任务页并只显示某个分区」的导航请求，Filter 为 null 表示显示全部</summary>
public sealed record TasksPageRequest(TaskKind? Filter);

/// <summary>
/// 页面导航。ViewModel 之间不互相持有引用，全部经由此处跳转，
/// 避免出现「任务列表直接依赖主窗口」的环形依赖。
/// </summary>
public interface INavigationService
{
    void GoToTasks();

    /// <summary>跳到任务页并只显示指定分区（null = 全部），同时让左侧导航的选中项跟着走</summary>
    void GoToTasks(TaskKind? filter);

    void GoToLogs();

    void GoToSettings();

    /// <summary>打开某个编辑页</summary>
    void OpenEditor(object editor);

    /// <summary>关闭编辑页，回到任务总览</summary>
    void CloseEditor();
}

public sealed class NavigationService : INavigationService
{
    private Action<object>? _navigate;
    private Action? _goBack;

    public void Attach(Action<object> navigate, Action goBack)
    {
        _navigate = navigate;
        _goBack = goBack;
    }

    public void GoToTasks() => _navigate?.Invoke(PageKind.Tasks);

    public void GoToTasks(TaskKind? filter) => _navigate?.Invoke(new TasksPageRequest(filter));

    public void GoToLogs() => _navigate?.Invoke(PageKind.Logs);

    public void GoToSettings() => _navigate?.Invoke(PageKind.Settings);

    public void OpenEditor(object editor) => _navigate?.Invoke(editor);

    public void CloseEditor() => _goBack?.Invoke();
}

/// <summary>
/// 三类任务执行器的统一入口。
/// Core 的 TaskRunnerFactory 是按具体类型重载的，这里收敛成一次类型分发，
/// 界面端其余地方都只面向 IScheduledTask。
/// </summary>
public static class TaskExecutionService
{
    public static Task<TaskRunResult> RunAsync(
        IScheduledTask task,
        IAppLogger logger,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => task switch
        {
            CompressionTask compression => TaskRunnerFactory.RunAsync(compression, logger, progress, cancellationToken),
            CleanupTask cleanup => TaskRunnerFactory.RunAsync(cleanup, logger, progress, cancellationToken),
            SyncTask sync => TaskRunnerFactory.RunAsync(sync, logger, progress, cancellationToken),
            ProgramTask program => TaskRunnerFactory.RunAsync(program, logger, progress, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(task), $"未知任务类型：{task.GetType().Name}")
        };
}
