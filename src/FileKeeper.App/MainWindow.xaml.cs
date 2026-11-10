using System.ComponentModel;
using System.Windows;
using FileKeeper.App.ViewModels;

namespace FileKeeper.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _exitRequested;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>托盘菜单「退出」专用：绕过最小化到托盘，真正关闭整个程序。</summary>
    public void CloseForExit()
    {
        _exitRequested = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 默认关闭行为是缩到托盘：后台服务独立运行，界面缩到托盘不影响定时任务
        if (!_exitRequested && _viewModel.Settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
        Application.Current.Shutdown();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
