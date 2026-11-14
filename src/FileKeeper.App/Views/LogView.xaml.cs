using System.Windows;
using FileKeeper.App.ViewModels;

namespace FileKeeper.App.Views;

public partial class LogView
{
    public LogView()
    {
        InitializeComponent();

        // 页面可见即刷新：App 托盘常驻可能一连开好几天，日期下拉若只在构造时
        // 枚举一次，跨天后新日志文件永远进不了下拉（用户会以为当天没日志）。
        // IsVisibleChanged 同时覆盖「导航进入本页」与「主窗口从托盘恢复」两种场景
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && DataContext is LogViewModel viewModel)
            {
                viewModel.LoadCommand.Execute(null);
            }
        };
    }
}
