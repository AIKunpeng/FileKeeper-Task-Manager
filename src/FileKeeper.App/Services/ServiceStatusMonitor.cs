using System.ServiceProcess;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FileKeeper.Core.Services;

namespace FileKeeper.App.Services;

public enum ServiceStateKind
{
    Unknown,
    NotInstalled,
    Stopped,
    Running,
    Busy
}

/// <summary>
/// 服务状态轮询。主窗口顶栏徽标与设置页的状态卡片共用同一个实例，
/// 避免两处各自轮询导致显示不一致。
/// </summary>
public sealed partial class ServiceStatusMonitor : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer;

    public ServiceStatusMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    [ObservableProperty]
    private string _statusText = "检测中";

    [ObservableProperty]
    private ServiceStateKind _state = ServiceStateKind.Unknown;

    public void Refresh()
    {
        var status = ServiceControl.GetStatus();

        StatusText = ServiceControl.GetStatusText();
        IsInstalled = ServiceControl.IsInstalled();

        State = status switch
        {
            null => ServiceStateKind.NotInstalled,
            ServiceControllerStatus.Running => ServiceStateKind.Running,
            ServiceControllerStatus.Stopped => ServiceStateKind.Stopped,
            ServiceControllerStatus.StartPending => ServiceStateKind.Busy,
            ServiceControllerStatus.StopPending => ServiceStateKind.Busy,
            _ => ServiceStateKind.Unknown
        };
    }

    /// <summary>便捷判断，供界面决定显示「安装」还是「卸载」</summary>
    [ObservableProperty]
    private bool _isInstalled;

    public void Dispose()
    {
        _timer.Stop();
    }
}
