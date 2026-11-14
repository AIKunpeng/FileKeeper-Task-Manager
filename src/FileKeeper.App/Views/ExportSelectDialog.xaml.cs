using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FileKeeper.App.Views;

/// <summary>多选列表里的一行。Group 相同的行渲染为一个分组块。</summary>
public sealed partial class SelectionItem : ObservableObject
{
    public required string Group { get; init; }

    public required string Title { get; init; }

    public string SubText { get; init; } = string.Empty;

    public required object Tag { get; init; }

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>
/// 导出任务时挑任务的多选弹窗。行由调用方构建并预勾选，
/// 确定后逐行检查 <see cref="SelectionItem.IsChecked"/> 取走所选。
/// </summary>
public partial class ExportSelectDialog : Window
{
    public ExportSelectDialog()
    {
        InitializeComponent();
    }

    public static bool Show(
        Window? owner,
        string title,
        string message,
        string confirmText,
        ObservableCollection<SelectionItem> items)
    {
        var dialog = new ExportSelectDialog
        {
            Owner = owner,
            TitleText = { Text = title },
            MessageText = { Text = message },
            ConfirmButton = { Content = confirmText }
        };

        dialog.BuildContent(items);
        return dialog.ShowDialog() == true;
    }

    private void BuildContent(IReadOnlyList<SelectionItem> items)
    {
        var panel = new StackPanel();
        string? currentGroup = null;

        foreach (var item in items)
        {
            if (!string.Equals(currentGroup, item.Group, StringComparison.Ordinal))
            {
                currentGroup = item.Group;
                panel.Children.Add(new TextBlock
                {
                    Text = currentGroup,
                    Margin = new Thickness(6, 12, 0, 4),
                    Style = (Style)Application.Current.Resources["Caption"]
                });
            }

            var row = new CheckBox
            {
                IsChecked = item.IsChecked,
                Margin = new Thickness(6, 3, 0, 3),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var text = new StackPanel { Orientation = Orientation.Vertical };
            text.Children.Add(new TextBlock
            {
                Text = item.Title,
                Style = (Style)Application.Current.Resources["Body"]
            });

            if (!string.IsNullOrEmpty(item.SubText))
            {
                text.Children.Add(new TextBlock
                {
                    Text = item.SubText,
                    Style = (Style)Application.Current.Resources["Caption"],
                    Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"]
                });
            }

            row.Content = text;
            row.Checked += (_, _) => item.IsChecked = true;
            row.Unchecked += (_, _) => item.IsChecked = false;
            panel.Children.Add(row);
        }

        ItemsHost.Content = panel;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetAll(true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        if (ItemsHost.Content is StackPanel panel)
        {
            foreach (var checkBox in panel.Children.OfType<CheckBox>())
            {
                checkBox.IsChecked = value;
            }
        }
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
