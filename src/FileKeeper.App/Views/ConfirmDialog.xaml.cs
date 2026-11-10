using System.Windows;
using System.Windows.Media;

namespace FileKeeper.App.Views;

/// <summary>
/// 统一确认弹窗。危险操作（删除源文件、永久删除、卸载服务、同步后删源）都走这里，
/// 而不是系统 MessageBox——后者的 Win32 外观会破坏整体观感。
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 显示确认框。
    /// </summary>
    /// <param name="title">标题</param>
    /// <param name="message">正文说明</param>
    /// <param name="confirmText">确认按钮文字，例如「删除」</param>
    /// <param name="detail">危险提示，非空时以红色条展示</param>
    /// <param name="isDanger">确认按钮是否用危险色</param>
    public static bool Show(
        Window? owner,
        string title,
        string message,
        string confirmText = "确定",
        string? detail = null,
        bool isDanger = false)
    {
        var dialog = new ConfirmDialog
        {
            Owner = owner,
            TitleText = { Text = title },
            MessageText = { Text = message },
            ConfirmButton = { Content = confirmText }
        };

        if (isDanger)
        {
            dialog.ConfirmButton.Background = (Brush)Application.Current.Resources["DangerBrush"];
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            dialog.DetailText.Text = detail;
            dialog.DetailBar.Visibility = Visibility.Visible;
        }

        return dialog.ShowDialog() == true;
    }

    /// <summary>仅提示，无取消按钮。</summary>
    public static void ShowMessage(Window? owner, string title, string message, string confirmText = "知道了")
    {
        var dialog = new ConfirmDialog
        {
            Owner = owner,
            TitleText = { Text = title },
            MessageText = { Text = message },
            ConfirmButton = { Content = confirmText }
        };

        dialog.CancelButton.Visibility = Visibility.Collapsed;
        dialog.ShowDialog();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
