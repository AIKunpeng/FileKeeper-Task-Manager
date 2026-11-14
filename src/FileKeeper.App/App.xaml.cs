using System.Windows;
using System.Windows.Threading;
using FileKeeper.App.Services;
using FileKeeper.App.ViewModels;
using FileKeeper.Core.Configuration;
using FileKeeper.Core.Logging;

namespace FileKeeper.App;

/// <summary>
/// 界面端组合根。
/// 这里手工装配各 ViewModel 而不引入 DI 容器：依赖数量固定且很少，
/// 容器带来的收益不足以抵消额外的启动复杂度。
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;
    private MainViewModel? _mainViewModel;
    private ConfigStore? _configStore;
    private FileLogger? _logger;
    private LogViewModel? _logs;
    private SettingsViewModel? _settings;
    private TrayIconService? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 三处兜底：界面绝不应因为一次异常整个崩掉，至少要留下线索
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            AppPaths.EnsureCreated();
        }
        catch (Exception ex)
        {
            // 建不出目录也要让界面起来，否则用户连修配置的机会都没有
            FileLogger.WriteFatal($"初始化程序目录失败：{ex.Message}", ex);
        }

        var config = new ConfigStore();
        _configStore = config;

        try
        {
            config.Load();
            config.StartWatching();
            config.ExternalChanged += OnConfigChanged;
            // 设置页保存属于本进程自写，不触发 ExternalChanged（防回环抑制），
            // 但改日志目录必须立刻切本进程的写入与查询位置，所以补听自写事件
            config.SelfChanged += OnConfigChanged;
        }
        catch (Exception ex)
        {
            FileLogger.WriteFatal($"加载配置失败：{ex.Message}", ex);
        }

        // 日志位置取自配置：安装时或设置页都可能在别处，
        // 硬编码默认目录会让用户改了之后界面和服务各写一处
        var logger = new FileLogger(
            LogPaths.Resolve(config.Current.Settings.LogDirectory),
            Math.Max(1, config.Current.Settings.LogRetentionDays));
        _logger = logger;

        var navigation = new NavigationService();
        var statusMonitor = new ServiceStatusMonitor();
        var dialogs = new DialogService();

        var tasks = new TasksViewModel(config, logger, navigation, dialogs);

        var logs = new LogViewModel(dialogs, logger, config);
        var settings = new SettingsViewModel(config, dialogs, statusMonitor, logger);

        _logs = logs;
        _settings = settings;

        _mainViewModel = new MainViewModel(tasks, logs, settings, navigation, statusMonitor);

        var window = new MainWindow(_mainViewModel);
        _window = window;
        window.Show();

        var tray = new TrayIconService();
        _tray = tray;
        tray.ShowRequested += OnTrayShow;
        tray.RunAllRequested += OnTrayRunAll;
        tray.ExitRequested += OnTrayExit;
        tray.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null)
        {
            _tray.ShowRequested -= OnTrayShow;
            _tray.RunAllRequested -= OnTrayRunAll;
            _tray.ExitRequested -= OnTrayExit;
            _tray.Dispose();
        }

        _mainViewModel?.Dispose();

        if (_configStore is not null)
        {
            _configStore.ExternalChanged -= OnConfigChanged;
            _configStore.SelfChanged -= OnConfigChanged;
            _configStore.StopWatching();
            _configStore.Dispose();
        }

        _logger?.Dispose();

        base.OnExit(e);
    }

    /// <summary>
    /// 配置热重载时把日志目录同步到当前进程。设置页改了目录、或另一个进程改了它都会走到这里。
    /// </summary>
    private void OnConfigChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(SyncLogDirectory);

    /// <summary>
    /// 把本进程的日志写入切换到当前生效的目录。
    ///
    /// 顺序很关键：先把旧目录的历史日志搬过去，再切写句柄。
    /// 反过来会丢掉切换时刻正在写的那几行，用户眼看着今天的日志断了一截。
    /// </summary>
    private void SyncLogDirectory()
    {
        var config = _configStore;
        var logger = _logger;

        if (config is null || logger is null)
        {
            return;
        }

        var target = LogPaths.Resolve(config.Current.Settings.LogDirectory);

        if (string.Equals(target, logger.LogsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var pending = config.Current.Settings.PendingLogMigrationFrom;
        logger.Retarget(target, pending);

        // 旧目录已经没有 .log 了才算搬完，标记可以撤；还有剩说明搬不动
        // （典型是界面端没有 %ProgramData% 的写权限），保留标记交给后台服务重试
        if (!string.IsNullOrWhiteSpace(pending) && !HasLogFiles(pending))
        {
            try
            {
                config.Update(c => c.Settings.PendingLogMigrationFrom = null);
            }
            catch (Exception ex)
            {
                logger.Warn($"清理日志迁移标记失败：{ex.Message}", "日志");
            }
        }

        if (_logs is not null)
        {
            _logs.LogsDirectory = target;
        }

        _settings?.RefreshGeneral();
    }

    private static bool HasLogFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.log").Any();
        }
        catch
        {
            // 目录不可列举时按「还没搬完」处理，保留标记让服务端重试
            return true;
        }
    }

    private void OnTrayShow(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_window is null)
            {
                return;
            }

            _window.Show();

            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }

            _window.Activate();
        });
    }

    private async void OnTrayRunAll(object? sender, EventArgs e)
    {
        if (_mainViewModel is null)
        {
            return;
        }

        try
        {
            // 托盘执行走的是同一套 ViewModel 流程，续体必须回到 UI 线程才不会跨线程改集合
            await _mainViewModel.RunAllAsync();
        }
        catch (Exception ex)
        {
            FileLogger.WriteFatal($"托盘执行全部任务失败：{ex.Message}", ex);
        }
    }

    private void OnTrayExit(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _window?.CloseForExit();
            Shutdown();
        });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Handle(e.Exception);

        // 标记为已处理，避免异常冒泡导致整个界面进程退出
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Handle(exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Handle(e.Exception);
        e.SetObserved();
    }

    private void Handle(Exception exception)
    {
        FileLogger.WriteFatal($"界面异常：{exception.Message}", exception);

        try
        {
            Dispatcher.Invoke(() => Views.ConfirmDialog.ShowMessage(
                _window,
                "发生未处理的异常",
                $"{exception.Message}\n\n详细信息已写入日志，程序可继续使用。"));
        }
        catch (Exception)
        {
            // 弹窗本身失败时不能再抛，否则会形成异常循环
        }
    }
}
