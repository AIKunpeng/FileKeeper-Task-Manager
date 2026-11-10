using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileKeeper.App.Services;
using FileKeeper.Core.Configuration;
using FileKeeper.Core;
using FileKeeper.Core.Engines;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Services;

namespace FileKeeper.App.ViewModels;

/// <summary>
/// 设置页：服务管理、引擎可用性、通用设置与路径信息。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(ConfigStore config, IDialogService dialogs, ServiceStatusMonitor statusMonitor, IAppLogger logger)
    {
        _config = config;
        _dialogs = dialogs;
        _statusMonitor = statusMonitor;
        _logger = logger;

        LoadGeneral();
        LoadEngines();

        ConfigPath = config.ConfigFilePath;
        Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
    }

    private readonly ConfigStore _config;
    private readonly IDialogService _dialogs;
    private readonly ServiceStatusMonitor _statusMonitor;
    private readonly IAppLogger _logger;

    public ServiceStatusMonitor ServiceStatus => _statusMonitor;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = string.Empty;

    [ObservableProperty]
    private string _embeddedLibraryText = string.Empty;

    [ObservableProperty]
    private string _cliText = string.Empty;

    [ObservableProperty]
    private string _winRarText = string.Empty;

    [ObservableProperty]
    private bool _canCreateRar;

    [ObservableProperty]
    private int _logRetentionDays = 30;

    [ObservableProperty]
    private bool _minimizeToTrayOnClose = true;

    /// <summary>配置里的日志目录；空串表示「用默认位置」</summary>
    [ObservableProperty]
    private string _logDirectory = string.Empty;

    /// <summary>当前真正生效的日志目录（配置值为空时显示默认位置）</summary>
    [ObservableProperty]
    private string _logsPath = string.Empty;

    public string ConfigPath { get; }

    public string Version { get; }

    /// <summary>「关于」里的版权声明</summary>
    public string Copyright => ProductInfo.Copyright;

    /// <summary>输入框留空时的说明：把「留空代表哪个位置」写实，而不是让用户猜</summary>
    public string LogDirectoryHint => $"留空时使用默认位置 {LogPaths.Resolve(null)}";

    /// <summary>是否已以管理员运行。未提权时操作服务会走 UAC 提权。</summary>
    public bool IsElevated => ServiceAdmin.IsElevated();

    public string ElevationHint => IsElevated
        ? "当前以管理员权限运行，可直接管理服务。"
        : "当前为普通权限运行。安装、卸载、启动、停止服务时会弹出 Windows 权限授权窗口。";

    [RelayCommand]
    private async Task InstallAsync()
    {
        await RunServiceOperationAsync("正在安装服务", () => ServiceAdmin.Install());
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        var confirmed = _dialogs.Confirm(
            "卸载服务",
            "卸载后定时任务将不再自动执行，配置与日志会保留。",
            "卸载",
            detail: "已生成的压缩包与已上传到服务器的文件不受影响。",
            isDanger: true);

        if (!confirmed)
        {
            return;
        }

        await RunServiceOperationAsync("正在卸载服务", () => ServiceAdmin.Uninstall());
    }

    [RelayCommand]
    private async Task StartServiceAsync()
    {
        await RunServiceOperationAsync("正在启动服务", () => ServiceAdmin.Start());
    }

    [RelayCommand]
    private async Task StopServiceAsync()
    {
        await RunServiceOperationAsync("正在停止服务", () => ServiceAdmin.Stop());
    }

    [RelayCommand]
    private void BrowseLogDirectory()
    {
        // 初始目录取当前生效位置，而不是配置原值：配置为空时从系统目录起步，
        // 用户要一路点到自己的目标目录；从默认位置起步至少不会白跑一趟
        var picked = _dialogs.BrowseFolder(LogsPath);

        if (!string.IsNullOrWhiteSpace(picked))
        {
            LogDirectory = picked;
        }
    }

    [RelayCommand]
    private void SaveGeneral()
    {
        var requested = string.IsNullOrWhiteSpace(LogDirectory) ? null : LogDirectory.Trim();

        _config.Update(config =>
        {
            config.Settings.LogRetentionDays = LogRetentionDays;
            config.Settings.MinimizeToTrayOnClose = MinimizeToTrayOnClose;

            // 同理于安装器：来源取自「解析后的旧目录」。
            // 第一次从默认位置改走时配置值还是 null，若按原值判断就会漏掉标记，
            // 结果历史日志永远留在旧目录里，界面看起来像「改目录把日志弄丢了」
            var previous = LogPaths.Resolve(config.Settings.LogDirectory);

            if (string.Equals(previous, LogPaths.Resolve(requested), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // 换了目录就把旧位置登记成「待迁移」：
            // 历史日志要跟着走，用户才能在新的目录里继续看到以前的记录
            config.Settings.PendingLogMigrationFrom = previous;
            config.Settings.LogDirectory = requested;
        });

        _dialogs.ShowMessage("已保存", "设置已保存。");

        // 本进程写配置不会触发热重载（自写抑制），保留天数要在这里就地生效；
        // 服务端那份会经配置变更事件跟上
        if (_logger is FileLogger fileLogger)
        {
            fileLogger.ApplyRetention(LogRetentionDays);
        }
    }

    /// <summary>
    /// 从配置重新同步显示值。目录被另一个进程改掉后，界面不能还停在旧位置上。
    /// </summary>
    public void RefreshGeneral()
    {
        LogDirectory = _config.Current.Settings.LogDirectory ?? string.Empty;
        LogsPath = LogPaths.Resolve(_config.Current.Settings.LogDirectory);
    }

    [RelayCommand]
    private void OpenConfigFolder()
    {
        OpenFolder(Path.GetDirectoryName(ConfigPath));
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        OpenFolder(LogsPath);
    }

    private void OpenFolder(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage("无法打开", $"打开目录失败：{ex.Message}");
        }
    }

    private async Task RunServiceOperationAsync(string busyText, Func<(bool Ok, string Output)> operation)
    {
        IsBusy = true;
        BusyText = busyText;

        try
        {
            // 服务操作会阻塞（等待 UAC 或系统响应），放到后台线程以免卡住界面。
            // 但续体必须回到 UI 线程：finally 里的状态刷新和下面的弹窗都直接驱动界面。
            var (ok, output) = await Task.Run(operation);

            if (!ok)
            {
                _dialogs.ShowMessage("操作未完成", output);
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            _statusMonitor.Refresh();
        }
    }

    private void LoadGeneral()
    {
        var settings = _config.Current.Settings;
        LogRetentionDays = settings.LogRetentionDays;
        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;

        // 显示给人的是配置原值（空表示默认），旁边另有生效位置的提示
        LogDirectory = settings.LogDirectory ?? string.Empty;
        LogsPath = LogPaths.Resolve(settings.LogDirectory);
    }

    private void LoadEngines()
    {
        var diagnostics = ArchiveEngineFactory.Diagnose();

        EmbeddedLibraryText = diagnostics.EmbeddedLibraryAvailable
            ? $"可用 · {diagnostics.EmbeddedLibraryPath}"
            : $"不可用 · {diagnostics.EmbeddedLibraryError}";

        CliText = diagnostics.CliAvailable
            ? $"可用 · {diagnostics.CliPath}"
            : "不可用 · 未找到自带的 7z.exe";

        WinRarText = diagnostics.WinRarAvailable
            ? $"已检测到 · {diagnostics.WinRarPath}"
            : $"未检测到 · {diagnostics.RarUnavailableReason}";

        CanCreateRar = diagnostics.CanCreateRar;
    }
}
