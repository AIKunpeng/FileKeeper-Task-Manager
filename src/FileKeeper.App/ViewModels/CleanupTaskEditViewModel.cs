using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileKeeper.App.Services;
using FileKeeper.Core.Configuration;
using FileKeeper.Core.Execution;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;

namespace FileKeeper.App.ViewModels;

/// <summary>
/// 清理任务编辑页。
/// 删除不可恢复，所以这里额外提供「预计影响」预览，让用户在保存前就能看到会被删掉什么。
/// </summary>
public sealed partial class CleanupTaskEditViewModel : TaskEditorViewModelBase
{
    private const int PreviewSampleCount = 8;

    private readonly CleanupTask _model;

    public CleanupTaskEditViewModel(
        ConfigStore config,
        IAppLogger logger,
        INavigationService navigation,
        IDialogService dialogs,
        CleanupTask source,
        bool isNew)
        : base(config, logger, navigation, dialogs, isNew)
    {
        _model = source.Clone();

        Name = _model.Name;
        Enabled = _model.Enabled;
        TargetDirectory = _model.TargetDirectory;
        IncludeSubDirectories = _model.IncludeSubDirectories;
        FilterMode = _model.FilterMode;
        Extensions = new List<string>(_model.Extensions);
        ExcludedDirectories = new List<string>(_model.ExcludedDirectories);
        ThresholdValue = _model.ThresholdValue;
        ThresholdUnit = _model.ThresholdUnit;
        TimeBasis = _model.TimeBasis;
        Schedule.LoadFrom(_model.Schedule);
    }

    public override string EditorTitle => IsNew ? "新建清理任务" : $"编辑「{_model.Name}」";

    [ObservableProperty]
    private string _targetDirectory = string.Empty;

    [ObservableProperty]
    private bool _includeSubDirectories = true;

    [ObservableProperty]
    private FileFilterMode _filterMode;

    [ObservableProperty]
    private List<string> _extensions = new();

    [ObservableProperty]
    private List<string> _excludedDirectories = new();

    [ObservableProperty]
    private int _thresholdValue = 30;

    [ObservableProperty]
    private CleanupThresholdUnit _thresholdUnit;

    [ObservableProperty]
    private AgeTimeBasis _timeBasis;

    [ObservableProperty]
    private string _previewText = string.Empty;

    [ObservableProperty]
    private bool _isPreviewing;

    public TimeSpan Threshold => ThresholdUnit switch
    {
        CleanupThresholdUnit.Hours => TimeSpan.FromHours(Math.Max(0, ThresholdValue)),
        CleanupThresholdUnit.Weeks => TimeSpan.FromDays(Math.Max(0, ThresholdValue) * 7),
        CleanupThresholdUnit.Months => TimeSpan.FromDays(Math.Max(0, ThresholdValue) * 30),
        _ => TimeSpan.FromDays(Math.Max(0, ThresholdValue))
    };

    /// <summary>
    /// 换算后的等价时长。用户按周 / 月输入时回显等价天数、按小时输入时回显小时，
    /// 避免对「30 天还是 30 个月」产生误判。
    /// </summary>
    public string ThresholdDaysText => ThresholdUnit switch
    {
        CleanupThresholdUnit.Hours => $"等价于 {Math.Max(0, ThresholdValue)} 小时",
        CleanupThresholdUnit.Weeks => $"等价于 {Math.Max(0, ThresholdValue) * 7} 天",
        CleanupThresholdUnit.Months => $"等价于 {Math.Max(0, ThresholdValue) * 30} 天",
        _ => $"等价于 {Math.Max(0, ThresholdValue)} 天"
    };

    public string CutoffText => $"将删除「{TimeBasisText}」早于 {DateTime.Now - Threshold:yyyy-MM-dd HH:mm} 的文件";

    public string TimeBasisText => TimeBasis == AgeTimeBasis.CreationTime ? "创建时间" : "最后修改时间";

    protected override IScheduledTask WorkingModel => _model;

    protected override string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return "请填写任务名称";
        }

        if (string.IsNullOrWhiteSpace(TargetDirectory))
        {
            return "请选择要清理的目录";
        }

        if (!Directory.Exists(TargetDirectory))
        {
            return $"目录不存在：{TargetDirectory}";
        }

        if (ThresholdValue <= 0)
        {
            return "时间阈值必须大于 0，否则会删除全部文件";
        }

        return Schedule.Validate();
    }

    protected override void ApplyToModel() => ApplyTo(_model);

    protected override void SaveToConfig()
    {
        Config.Update(config =>
        {
            var existing = config.CleanupTasks.FirstOrDefault(t => t.Id == _model.Id);

            if (existing is null)
            {
                config.CleanupTasks.Add(_model.Clone());
            }
            else
            {
                var clone = _model.Clone();

                // 编辑基于打开时的快照，服务可能已推进调度状态；
                // 整对象替换会把 NextRunUtc 回滚到过去，任务会被立刻重跑
                clone.Schedule.RestoreRuntimeStateFrom(existing.Schedule);
                config.CleanupTasks[config.CleanupTasks.IndexOf(existing)] = clone;
            }
        });
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        var selected = Dialogs.BrowseFolder(TargetDirectory);
        if (selected is not null)
        {
            TargetDirectory = selected;
        }
    }

    /// <summary>
    /// 预览会被删除的文件。扫描可能很慢，所以做成显式按钮而不是自动执行，
    /// 并且与执行端使用同一个 FileSelector，保证筛选条件一致。
    /// </summary>
    [RelayCommand]
    private async Task PreviewAffectedAsync()
    {
        if (string.IsNullOrWhiteSpace(TargetDirectory) || !Directory.Exists(TargetDirectory))
        {
            PreviewText = "请先选择一个有效的目录";
            return;
        }

        IsPreviewing = true;
        PreviewText = "正在扫描...";

        var snapshot = _model.Clone();
        ApplyTo(snapshot);

        try
        {
            var result = await Task.Run(() =>
            {
                var files = FileSelector.SelectFiles(
                    snapshot.TargetDirectory,
                    snapshot.IncludeSubDirectories,
                    snapshot.FilterMode,
                    snapshot.Extensions,
                    snapshot.ExcludedDirectories);

                var cutoff = DateTime.Now - snapshot.Threshold;
                var matched = new List<string>();

                foreach (var file in files)
                {
                    try
                    {
                        var info = new FileInfo(file);
                        var basis = snapshot.TimeBasis == AgeTimeBasis.CreationTime
                            ? info.CreationTime
                            : info.LastWriteTime;

                        if (basis < cutoff)
                        {
                            matched.Add(file);
                        }
                    }
                    catch (Exception)
                    {
                        // 单个文件读不到时间戳就跳过，扫描过程不该因此中断
                    }
                }

                return matched;
            });

            if (result.Count == 0)
            {
                PreviewText = "当前配置下没有符合条件的文件，不会删除任何内容。";
                return;
            }

            var samples = result.Take(PreviewSampleCount).Select(Path.GetFileName);
            var text = $"预计删除 {result.Count} 个文件：" + string.Join("、", samples);

            if (result.Count > PreviewSampleCount)
            {
                text += $" 等 {result.Count} 个";
            }

            PreviewText = text;
        }
        catch (Exception ex)
        {
            PreviewText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsPreviewing = false;
        }
    }

    private void ApplyTo(CleanupTask target)
    {
        target.Name = Name.Trim();
        target.Enabled = Enabled;
        target.TargetDirectory = TargetDirectory.Trim();
        target.IncludeSubDirectories = IncludeSubDirectories;
        target.FilterMode = FilterMode;
        target.Extensions = new List<string>(Extensions);
        target.ExcludedDirectories = new List<string>(ExcludedDirectories);
        target.ThresholdValue = ThresholdValue;
        target.ThresholdUnit = ThresholdUnit;
        target.TimeBasis = TimeBasis;

        Schedule.ApplyTo(target.Schedule);
        target.Normalize();
    }

    partial void OnThresholdValueChanged(int value) => RaiseThresholdChanged();

    partial void OnThresholdUnitChanged(CleanupThresholdUnit value) => RaiseThresholdChanged();

    partial void OnTimeBasisChanged(AgeTimeBasis value) => RaiseThresholdChanged();

    private void RaiseThresholdChanged()
    {
        OnPropertyChanged(nameof(Threshold));
        OnPropertyChanged(nameof(ThresholdDaysText));
        OnPropertyChanged(nameof(CutoffText));
        OnPropertyChanged(nameof(TimeBasisText));
    }
}
