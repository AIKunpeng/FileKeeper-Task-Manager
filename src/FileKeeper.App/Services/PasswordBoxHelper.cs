using System.Windows;
using System.Windows.Controls;

namespace FileKeeper.App.Services;

/// <summary>
/// 让 PasswordBox 支持数据绑定。
/// PasswordBox.Password 不是依赖属性，WPF 出于安全考虑不允许直接绑定，
/// 这里用附加属性做双向桥接：外界绑 Password 附加属性，内部与 Password 属性互相同步。
/// </summary>
public static class PasswordBoxHelper
{
    private static bool _isUpdating;

    public static readonly DependencyProperty PasswordProperty =
        DependencyProperty.RegisterAttached(
            "Password",
            typeof(string),
            typeof(PasswordBoxHelper),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnPasswordChanged));

    public static string GetPassword(DependencyObject obj) => (string)obj.GetValue(PasswordProperty);

    public static void SetPassword(DependencyObject obj, string value) => obj.SetValue(PasswordProperty, value);

    public static readonly DependencyProperty AttachProperty =
        DependencyProperty.RegisterAttached(
            "Attach",
            typeof(bool),
            typeof(PasswordBoxHelper),
            new PropertyMetadata(false, OnAttachChanged));

    public static bool GetAttach(DependencyObject obj) => (bool)obj.GetValue(AttachProperty);

    public static void SetAttach(DependencyObject obj, bool value) => obj.SetValue(AttachProperty, value);

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (_isUpdating || d is not PasswordBox box)
        {
            return;
        }

        _isUpdating = true;

        try
        {
            box.Password = e.NewValue as string ?? string.Empty;
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private static void OnAttachChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
        {
            return;
        }

        if (e.OldValue is true)
        {
            box.PasswordChanged -= OnBoxPasswordChanged;
        }

        if (e.NewValue is true)
        {
            box.PasswordChanged += OnBoxPasswordChanged;

            // 附加属性先于 Password 生效，需要立刻把已保存的密码推给 PasswordBox
            SyncFromBox(box);
        }
    }

    private static void OnBoxPasswordChanged(object sender, RoutedEventArgs e) =>
        SyncFromBox((PasswordBox)sender);

    private static void SyncFromBox(PasswordBox box)
    {
        if (_isUpdating)
        {
            return;
        }

        _isUpdating = true;

        try
        {
            SetPassword(box, box.Password);
        }
        finally
        {
            _isUpdating = false;
        }
    }
}
