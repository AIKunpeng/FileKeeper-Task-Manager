using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace FileKeeper.App.ViewModels;

/// <summary>
/// 多选胶囊按钮的单项。用于周几（7 个）与每月几号（31 个）的选择。
/// </summary>
public sealed partial class ToggleItemViewModel : ObservableObject
{
    public ToggleItemViewModel(string label, int value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }

    public int Value { get; }

    [ObservableProperty]
    private bool _isSelected;
}
