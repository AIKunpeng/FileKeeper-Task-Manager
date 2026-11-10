using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FileKeeper.Core.Models;
using FileKeeper.Core.Scheduling;

namespace FileKeeper.App.ViewModels;

/// <summary>
/// 调度编辑。三类任务的调度配置完全一样，共用这一份 VM 与一份 XAML，
/// 避免同样的频率 / 时刻 / 周几 / 几号逻辑写三遍。
/// </summary>
public sealed partial class ScheduleEditViewModel : ObservableObject
{
    private static readonly (string Label, int Value)[] WeekdayNames =
    {
        ("周日", 0), ("周一", 1), ("周二", 2), ("周三", 3),
        ("周四", 4), ("周五", 5), ("周六", 6)
    };

    public ScheduleEditViewModel()
    {
        foreach (var (label, value) in WeekdayNames)
        {
            var item = new ToggleItemViewModel(label, value);
            item.PropertyChanged += OnToggleChanged;
            WeekdayItems.Add(item);
        }

        for (var day = 1; day <= 31; day++)
        {
            var item = new ToggleItemViewModel(day.ToString(), day);
            item.PropertyChanged += OnToggleChanged;
            MonthDayItems.Add(item);
        }
    }

    [ObservableProperty]
    private ScheduleFrequency _frequency;

    [ObservableProperty]
    private TimeOnly _timeOfDay = new(2, 0);

    [ObservableProperty]
    private bool _runIfMissed = true;

    public ObservableCollection<ToggleItemViewModel> WeekdayItems { get; } = new();

    public ObservableCollection<ToggleItemViewModel> MonthDayItems { get; } = new();

    public bool HasWeekdaySelected => WeekdayItems.Any(i => i.IsSelected);

    public bool HasMonthDaySelected => MonthDayItems.Any(i => i.IsSelected);

    /// <summary>人类可读的调度描述，例如「每周 周一、周三 02:00」</summary>
    public string DescribeText => ScheduleCalculator.Describe(BuildConfig());

    /// <summary>下次执行时间预览。让用户保存前就能确认排期是否符合预期。</summary>
    public string NextRunPreviewText
    {
        get
        {
            var next = ScheduleCalculator.ComputeNextRunUtc(BuildConfig(), DateTimeOffset.Now);
            return next is null ? "尚未排定" : $"下次 {next.Value.ToLocalTime():MM-dd HH:mm}";
        }
    }

    public void LoadFrom(ScheduleConfig schedule)
    {
        Frequency = schedule.Frequency;
        TimeOfDay = schedule.TimeOfDay;
        RunIfMissed = schedule.RunIfMissed;

        foreach (var item in WeekdayItems)
        {
            item.IsSelected = schedule.Weekdays.Contains(item.Value);
        }

        foreach (var item in MonthDayItems)
        {
            item.IsSelected = schedule.MonthDays.Contains(item.Value);
        }

        RaisePreviewChanged();
    }

    public void ApplyTo(ScheduleConfig schedule)
    {
        schedule.Frequency = Frequency;
        schedule.TimeOfDay = TimeOfDay;
        schedule.RunIfMissed = RunIfMissed;
        schedule.Weekdays = WeekdayItems.Where(i => i.IsSelected).Select(i => i.Value).ToList();
        schedule.MonthDays = MonthDayItems.Where(i => i.IsSelected).Select(i => i.Value).ToList();
    }

    public ScheduleConfig BuildConfig()
    {
        var config = new ScheduleConfig();
        ApplyTo(config);
        config.Normalize();
        return config;
    }

    /// <summary>校验：周 / 月模式下至少要选一个，否则调度无从计算。</summary>
    public string? Validate()
    {
        if (Frequency == ScheduleFrequency.Weekly && !HasWeekdaySelected)
        {
            return "每周模式请至少选择一个周几";
        }

        if (Frequency == ScheduleFrequency.Monthly && !HasMonthDaySelected)
        {
            return "每月模式请至少选择一个日期";
        }

        return null;
    }

    partial void OnFrequencyChanged(ScheduleFrequency value) => RaisePreviewChanged();

    partial void OnTimeOfDayChanged(TimeOnly value) => RaisePreviewChanged();

    private void OnToggleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToggleItemViewModel.IsSelected))
        {
            RaisePreviewChanged();
        }
    }

    private void RaisePreviewChanged()
    {
        OnPropertyChanged(nameof(DescribeText));
        OnPropertyChanged(nameof(NextRunPreviewText));
    }
}
