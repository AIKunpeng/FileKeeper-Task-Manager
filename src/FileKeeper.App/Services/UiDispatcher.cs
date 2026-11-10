using System.Windows;

namespace FileKeeper.App.Services;

/// <summary>
/// 线程切换助手。配置文件是进程共享的，热重载回调来自 FileSystemWatcher 的工作线程，
/// 直接在那里改 ObservableCollection 会抛「调用线程无法访问此对象」。
/// </summary>
public static class UiDispatcher
{
    public static void Invoke(Action action)
    {
        if (Application.Current?.Dispatcher is null)
        {
            action();
            return;
        }

        var dispatcher = Application.Current.Dispatcher;

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.InvokeAsync(action);
    }
}
