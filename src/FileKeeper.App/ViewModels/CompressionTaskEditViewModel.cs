using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileKeeper.App.Services;
using FileKeeper.Core.Configuration;
using FileKeeper.Core.Engines;
using FileKeeper.Core.Execution;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;
// 本类有一个同名的 FileNameTemplate 属性，直接写类型名会被解析成属性，故改用别名
using FileNameTemplateRenderer = FileKeeper.Core.Execution.FileNameTemplate;

namespace FileKeeper.App.ViewModels;

/// <summary>
/// 压缩任务编辑页。基于任务副本工作，取消时直接丢弃，不影响配置。
/// </summary>
public sealed partial class CompressionTaskEditViewModel : TaskEditorViewModelBase
{
    private readonly CompressionTask _model;

    public CompressionTaskEditViewModel(
        ConfigStore config,
        IAppLogger logger,
        INavigationService navigation,
        IDialogService dialogs,
        CompressionTask source,
        bool isNew)
        : base(config, logger, navigation, dialogs, isNew)
    {
        _model = source.Clone();

        Name = _model.Name;
        Enabled = _model.Enabled;
        SourceDirectory = _model.SourceDirectory;
        IncludeSubDirectories = _model.IncludeSubDirectories;
        FilterMode = _model.FilterMode;
        Extensions = new List<string>(_model.Extensions);
        ExcludedDirectories = new List<string>(_model.ExcludedDirectories);
        OutputDirectory = _model.OutputDirectory;
        FileNameTemplate = _model.FileNameTemplate;
        Format = _model.Format;
        CompressionLevel = _model.CompressionLevel;
        UsePassword = !string.IsNullOrEmpty(_model.Password);
        Password = _model.Password ?? string.Empty;
        UseVolume = _model.VolumeSizeBytes is > 0;
        VolumeSizeMb = _model.VolumeSizeBytes is > 0
            ? (int)(_model.VolumeSizeBytes.Value / (1024 * 1024))
            : 100;
        DeleteSourceAfterCompress = _model.DeleteSourceAfterCompress;
        Schedule.LoadFrom(_model.Schedule);

        var diagnostics = ArchiveEngineFactory.Diagnose();
        CanCreateRar = diagnostics.CanCreateRar;

        // RAR 是商业格式，没有 WinRAR 就创建不了，界面要置灰并说明原因
        RarHint = CanCreateRar
            ? null
            : ArchiveEngineFactory.GetUnavailableReason(ArchiveFormat.Rar) ?? "未检测到可用的 RAR 创建引擎";

        if (!CanCreateRar && _model.Format == ArchiveFormat.Rar)
        {
            Format = ArchiveFormat.Zip;
        }
    }

    public override string EditorTitle => IsNew ? "新建压缩任务" : $"编辑「{_model.Name}」";

    [ObservableProperty]
    private string _sourceDirectory = string.Empty;

    [ObservableProperty]
    private bool _includeSubDirectories = true;

    [ObservableProperty]
    private FileFilterMode _filterMode;

    [ObservableProperty]
    private List<string> _extensions = new();

    [ObservableProperty]
    private List<string> _excludedDirectories = new();

    [ObservableProperty]
    private string _outputDirectory = string.Empty;

    [ObservableProperty]
    private string _fileNameTemplate = "Backup_{yyyyMMdd}_{HHmmss}";

    [ObservableProperty]
    private ArchiveFormat _format;

    [ObservableProperty]
    private int _compressionLevel = 5;

    [ObservableProperty]
    private bool _usePassword;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _useVolume;

    [ObservableProperty]
    private int _volumeSizeMb = 100;

    [ObservableProperty]
    private bool _deleteSourceAfterCompress;

    /// <summary>压缩级别可选项。0 为仅存储，9 为极限压缩。</summary>
    public List<int> CompressionLevels { get; } = Enumerable.Range(0, 10).ToList();

    /// <summary>是否能创建 RAR（取决于是否装有 WinRAR）</summary>
    public bool CanCreateRar { get; }

    /// <summary>RAR 不可用时的原因，用于界面置灰提示</summary>
    public string? RarHint { get; }

    public string PreviewFileName => FileNameTemplateRenderer.RenderSample(FileNameTemplate, Format);

    public string CompressionLevelText => CompressionLevel switch
    {
        0 => "仅存储（最快）",
        9 => "极限压缩（最慢）",
        _ => $"级别 {CompressionLevel}"
    };

    protected override IScheduledTask WorkingModel => _model;

    protected override string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return "请填写任务名称";
        }

        if (string.IsNullOrWhiteSpace(SourceDirectory))
        {
            return "请选择要压缩的源目录";
        }

        if (!Directory.Exists(SourceDirectory))
        {
            return $"源目录不存在：{SourceDirectory}";
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            return "请选择压缩包的存放目录";
        }

        if (Format == ArchiveFormat.Rar && !CanCreateRar)
        {
            return $"无法创建 RAR：{RarHint}";
        }

        if (UsePassword && string.IsNullOrEmpty(Password))
        {
            return "已勾选加密，但密码为空";
        }

        if (UseVolume && VolumeSizeMb <= 0)
        {
            return "分卷大小必须大于 0 MB";
        }

        return Schedule.Validate();
    }

    protected override void ApplyToModel() => ApplyTo(_model);

    protected override void SaveToConfig()
    {
        Config.Update(config =>
        {
            var existing = config.CompressionTasks.FirstOrDefault(t => t.Id == _model.Id);

            if (existing is null)
            {
                config.CompressionTasks.Add(_model.Clone());
            }
            else
            {
                // 替换时用副本，避免编辑页继续持有配置中的实例
                config.CompressionTasks[config.CompressionTasks.IndexOf(existing)] = _model.Clone();
            }
        });
    }

    [RelayCommand]
    private void BrowseSource()
    {
        var selected = Dialogs.BrowseFolder(SourceDirectory);
        if (selected is not null)
        {
            SourceDirectory = selected;
        }
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var selected = Dialogs.BrowseFolder(OutputDirectory);
        if (selected is not null)
        {
            OutputDirectory = selected;
        }
    }

    private void ApplyTo(CompressionTask target)
    {
        target.Name = Name.Trim();
        target.Enabled = Enabled;
        target.SourceDirectory = SourceDirectory.Trim();
        target.IncludeSubDirectories = IncludeSubDirectories;
        target.FilterMode = FilterMode;
        target.Extensions = new List<string>(Extensions);
        target.ExcludedDirectories = new List<string>(ExcludedDirectories);
        target.OutputDirectory = OutputDirectory.Trim();
        target.FileNameTemplate = FileNameTemplate.Trim();
        target.Format = Format;
        target.CompressionLevel = CompressionLevel;
        target.Password = UsePassword ? Password : null;
        target.VolumeSizeBytes = UseVolume ? (long)VolumeSizeMb * 1024 * 1024 : null;
        target.DeleteSourceAfterCompress = DeleteSourceAfterCompress;

        Schedule.ApplyTo(target.Schedule);
        target.Normalize();
    }

    partial void OnFileNameTemplateChanged(string value) => OnPropertyChanged(nameof(PreviewFileName));

    partial void OnFormatChanged(ArchiveFormat value) => OnPropertyChanged(nameof(PreviewFileName));

    partial void OnCompressionLevelChanged(int value) => OnPropertyChanged(nameof(CompressionLevelText));
}
