using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileKeeper.App.Services;
using FileKeeper.Core.Configuration;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;

namespace FileKeeper.App.ViewModels;

/// <summary>
/// 程序任务编辑页。
/// 与清理任务不同，这里没有「预览影响」：执行的是外部程序，运行前无法穷举其副作用，
/// 所以把提示做在界面上（输出进日志、超时强杀），把验证交给「立即执行」试跑。
/// </summary>
public sealed partial class ProgramTaskEditViewModel : TaskEditorViewModelBase
{
    private readonly ProgramTask _model;

    public ProgramTaskEditViewModel(
        ConfigStore config,
        IAppLogger logger,
        INavigationService navigation,
        IDialogService dialogs,
        ProgramTask source,
        bool isNew)
        : base(config, logger, navigation, dialogs, isNew)
    {
        _model = source.Clone();

        Name = _model.Name;
        Enabled = _model.Enabled;
        FilePath = _model.FilePath;
        Arguments = _model.Arguments;
        WorkingDirectory = _model.WorkingDirectory ?? string.Empty;
        WindowMode = _model.WindowMode;
        WaitForExit = _model.WaitForExit;
        TimeoutSeconds = _model.TimeoutSeconds;
        Schedule.LoadFrom(_model.Schedule);
    }

    public override string EditorTitle => IsNew ? "新建程序任务" : $"编辑「{_model.Name}」";

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _arguments = string.Empty;

    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    [ObservableProperty]
    private ProcessWindow _windowMode;

    [ObservableProperty]
    private bool _waitForExit = true;

    [ObservableProperty]
    private int _timeoutSeconds = 3600;

    /// <summary>文件选择对话框过滤器。覆盖常见脚本形态，同时允许任意文件</summary>
    public string FileFilter =>
        "程序与脚本 (*.exe;*.bat;*.cmd;*.ps1)|*.exe;*.bat;*.cmd;*.ps1|所有文件 (*.*)|*.*";

    protected override IScheduledTask WorkingModel => _model;

    protected override string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return "请填写任务名称";
        }

        // 复用模型校验，保证编辑页与执行器的规则永远一致
        ApplyTo(_model);
        var error = _model.ValidateConfiguration();

        if (error is not null)
        {
            return error;
        }

        if (TimeoutSeconds < 1)
        {
            return "超时时间必须大于 0 秒";
        }

        return Schedule.Validate();
    }

    protected override void ApplyToModel() => ApplyTo(_model);

    protected override void SaveToConfig()
    {
        Config.Update(config =>
        {
            var existing = config.ProgramTasks.FirstOrDefault(t => t.Id == _model.Id);

            if (existing is null)
            {
                config.ProgramTasks.Add(_model.Clone());
            }
            else
            {
                config.ProgramTasks[config.ProgramTasks.IndexOf(existing)] = _model.Clone();
            }
        });
    }

    [RelayCommand]
    private void BrowseFile()
    {
        var selected = Dialogs.OpenFile(FileFilter, Path.GetDirectoryName(FilePath));

        if (selected is not null)
        {
            FilePath = selected;

            // 工作目录留空时执行端会用程序所在目录，这里顺带回显出来，
            // 让用户一眼看到「实际会在哪里执行」
            if (string.IsNullOrWhiteSpace(WorkingDirectory))
            {
                WorkingDirectory = Path.GetDirectoryName(selected) ?? string.Empty;
            }
        }
    }

    [RelayCommand]
    private void BrowseWorkingDirectory()
    {
        var selected = Dialogs.BrowseFolder(WorkingDirectory);

        if (selected is not null)
        {
            WorkingDirectory = selected;
        }
    }

    private void ApplyTo(ProgramTask target)
    {
        target.Name = Name.Trim();
        target.Enabled = Enabled;
        target.FilePath = FilePath.Trim();
        target.Arguments = Arguments;
        target.WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim();
        target.WindowMode = WindowMode;
        target.WaitForExit = WaitForExit;
        target.TimeoutSeconds = TimeoutSeconds;

        Schedule.ApplyTo(target.Schedule);
        target.Normalize();
    }
}
