using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileKeeper.App.Services;
using FileKeeper.Core.Configuration;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;

namespace FileKeeper.App.ViewModels;

/// <summary>
/// 三类任务编辑页的公共部分。
/// 保存 / 取消 / 立即执行的流程完全一致，差别只在于「校验什么」和「写回什么」，
/// 所以把流程放在基类，把差异留给抽象成员。
/// </summary>
public abstract partial class TaskEditorViewModelBase : ObservableObject
{
    protected TaskEditorViewModelBase(
        ConfigStore config,
        IAppLogger logger,
        INavigationService navigation,
        IDialogService dialogs,
        bool isNew)
    {
        Config = config;
        Logger = logger;
        Navigation = navigation;
        Dialogs = dialogs;
        IsNew = isNew;
    }

    protected ConfigStore Config { get; }

    protected IAppLogger Logger { get; }

    protected INavigationService Navigation { get; }

    protected IDialogService Dialogs { get; }

    /// <summary>新建时为 true，用于显示「新建」还是「编辑」标题</summary>
    public bool IsNew { get; }

    public abstract string EditorTitle { get; }

    public ScheduleEditViewModel Schedule { get; } = new();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    /// <summary>校验界面输入，返回首条错误；无误返回 null</summary>
    protected abstract string? Validate();

    /// <summary>把界面值写回工作副本</summary>
    protected abstract void ApplyToModel();

    /// <summary>把工作副本落到配置中（新建为追加，编辑为替换）</summary>
    protected abstract void SaveToConfig();

    /// <summary>供试运行使用的模型</summary>
    protected abstract IScheduledTask WorkingModel { get; }

    [RelayCommand]
    private void Save()
    {
        var error = Validate();

        if (error is not null)
        {
            Dialogs.ShowMessage("无法保存", error);
            return;
        }

        ApplyToModel();
        SaveToConfig();
        Navigation.CloseEditor();
    }

    [RelayCommand]
    private void Cancel() => Navigation.CloseEditor();

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // 试运行必须先校验并把界面值写回，否则执行的会是上一次的旧配置，
        // 用户改了目录却还在跑旧目录，这种偏差最难排查。
        var error = Validate();

        if (error is not null)
        {
            Dialogs.ShowMessage("无法执行", error);
            return;
        }

        ApplyToModel();

        IsBusy = true;
        ProgressValue = 0;
        BusyText = "正在执行";

        var progress = new Progress<double>(value => ProgressValue = value);

        try
        {
            // 与总览页同理：结果弹窗与忙碌状态更新都要回 UI 线程
            var result = await TaskExecutionService.RunAsync(WorkingModel, Logger, progress, cancellationToken);
            ReportResult(result);
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消，不必提示
        }
        catch (Exception ex)
        {
            Dialogs.ShowMessage("执行失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
            BusyText = string.Empty;
        }
    }

    private void ReportResult(TaskRunResult result)
    {
        var status = result.Status switch
        {
            TaskRunStatus.Success => "成功",
            TaskRunStatus.PartialSuccess => "部分成功",
            TaskRunStatus.Skipped => "跳过",
            _ => "失败"
        };

        var text = $"结果：{status}\n"
                   + $"成功 {result.SuccessCount} 个，失败 {result.FailedCount} 个，跳过 {result.SkippedCount} 个";

        if (result.OutputPath is not null)
        {
            text += $"\n产物：{result.OutputPath}";
        }

        if (result.ErrorMessage is not null)
        {
            text += $"\n\n{result.ErrorMessage}";
        }

        Dialogs.ShowMessage("执行完成", text);
    }
}
