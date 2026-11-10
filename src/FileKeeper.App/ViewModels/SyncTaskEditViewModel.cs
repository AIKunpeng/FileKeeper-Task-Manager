using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileKeeper.App.Services;
using FileKeeper.Core.Configuration;
using FileKeeper.Core.Engines;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;

namespace FileKeeper.App.ViewModels;

/// <summary>
/// 同步任务编辑页。
/// 密码与私钥口令通过 SyncCredential 的明文代理读写，内部走 DPAPI 机器级加解密，
/// 因此这里可以直接回显已保存的密码供用户修改，而明文本身不会进入配置文件。
/// </summary>
public sealed partial class SyncTaskEditViewModel : TaskEditorViewModelBase
{
    private readonly SyncTask _model;

    public SyncTaskEditViewModel(
        ConfigStore config,
        IAppLogger logger,
        INavigationService navigation,
        IDialogService dialogs,
        SyncTask source,
        bool isNew)
        : base(config, logger, navigation, dialogs, isNew)
    {
        _model = source.Clone();

        Name = _model.Name;
        Enabled = _model.Enabled;
        LocalDirectory = _model.LocalDirectory;
        IncludeSubDirectories = _model.IncludeSubDirectories;
        FilterMode = _model.FilterMode;
        Extensions = new List<string>(_model.Extensions);
        ExcludedDirectories = new List<string>(_model.ExcludedDirectories);
        Protocol = _model.Protocol;
        Host = _model.Host;
        Port = _model.Port;
        Username = _model.Username;
        AuthMode = _model.Credential.AuthMode;
        Password = _model.Credential.Password ?? string.Empty;
        PrivateKeyPath = _model.Credential.PrivateKeyPath ?? string.Empty;
        Passphrase = _model.Credential.Passphrase ?? string.Empty;
        RemoteDirectory = _model.RemoteDirectory;
        IncrementalSync = _model.IncrementalSync;
        PreserveDirectoryStructure = _model.PreserveDirectoryStructure;
        DeleteLocalAfterUpload = _model.DeleteLocalAfterUpload;
        TimeoutSeconds = _model.TimeoutSeconds;
        RetryCount = _model.RetryCount;
        Schedule.LoadFrom(_model.Schedule);

        if (Port <= 0)
        {
            Port = SyncTask.GetDefaultPort(Protocol);
        }
    }

    public override string EditorTitle => IsNew ? "新建同步任务" : $"编辑「{_model.Name}」";

    [ObservableProperty]
    private string _localDirectory = string.Empty;

    [ObservableProperty]
    private bool _includeSubDirectories = true;

    [ObservableProperty]
    private FileFilterMode _filterMode;

    [ObservableProperty]
    private List<string> _extensions = new();

    [ObservableProperty]
    private List<string> _excludedDirectories = new();

    [ObservableProperty]
    private SyncProtocol _protocol;

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private int _port = 22;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private SftpAuthMode _authMode;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _privateKeyPath = string.Empty;

    [ObservableProperty]
    private string _passphrase = string.Empty;

    [ObservableProperty]
    private string _remoteDirectory = string.Empty;

    [ObservableProperty]
    private bool _incrementalSync = true;

    [ObservableProperty]
    private bool _preserveDirectoryStructure = true;

    [ObservableProperty]
    private bool _deleteLocalAfterUpload;

    [ObservableProperty]
    private int _timeoutSeconds = 60;

    [ObservableProperty]
    private int _retryCount = 3;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _testingText = string.Empty;

    /// <summary>私钥认证仅对 SFTP 有意义，FTP / FTPS 一律用密码</summary>
    public bool IsPrivateKeyMode => Protocol == SyncProtocol.Sftp && AuthMode == SftpAuthMode.PrivateKey;

    public string PasswordStatusText => _model.Credential.HasPassword ? "当前已保存密码，留空则保持原值" : "尚未设置密码";

    protected override IScheduledTask WorkingModel => _model;

    protected override string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return "请填写任务名称";
        }

        if (string.IsNullOrWhiteSpace(LocalDirectory))
        {
            return "请选择要上传的本地目录";
        }

        if (!Directory.Exists(LocalDirectory))
        {
            return $"本地目录不存在：{LocalDirectory}";
        }

        if (Port is <= 0 or > 65535)
        {
            return "端口需在 1 ~ 65535 之间";
        }

        if (IsPrivateKeyMode && !File.Exists(PrivateKeyPath))
        {
            return $"私钥文件不存在：{PrivateKeyPath}";
        }

        // 借 Core 的校验，保证界面与执行端用的是同一套规则，不会出现「界面通过但执行时报错」
        var snapshot = _model.Clone();
        ApplyTo(snapshot);

        var configError = SyncClientFactory.Validate(snapshot);
        if (configError is not null)
        {
            return configError;
        }

        return Schedule.Validate();
    }

    protected override void ApplyToModel() => ApplyTo(_model);

    protected override void SaveToConfig()
    {
        Config.Update(config =>
        {
            var existing = config.SyncTasks.FirstOrDefault(t => t.Id == _model.Id);

            if (existing is null)
            {
                config.SyncTasks.Add(_model.Clone());
            }
            else
            {
                config.SyncTasks[config.SyncTasks.IndexOf(existing)] = _model.Clone();
            }
        });
    }

    [RelayCommand]
    private void BrowseLocal()
    {
        var selected = Dialogs.BrowseFolder(LocalDirectory);
        if (selected is not null)
        {
            LocalDirectory = selected;
        }
    }

    [RelayCommand]
    private void BrowseKey()
    {
        var selected = Dialogs.OpenFile("私钥文件|*.*|所有文件|*.*");
        if (selected is not null)
        {
            PrivateKeyPath = selected;
        }
    }

    /// <summary>
    /// 测试连接。不落盘，只验证「能连上 + 远端目录可用」。
    /// 用户改了服务器配置后可以先验证再保存，避免存下一份跑不通的配置。
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        var error = Validate();

        if (error is not null)
        {
            Dialogs.ShowMessage("无法测试连接", error);
            return;
        }

        ApplyToModel();

        IsTesting = true;
        TestingText = "正在连接...";

        try
        {
            // ISyncClient 实现的是 IAsyncDisposable，必须用 await using 才能确保连接被释放
            await using var client = SyncClientFactory.Create(_model);
            // 后面的连接结果弹窗与忙碌状态更新需要 UI 上下文，续体保持在 UI 线程
            await client.ConnectAsync(cancellationToken);

            var remote = _model.RemoteDirectory;
            if (!string.IsNullOrWhiteSpace(remote))
            {
                await client.EnsureDirectoryAsync(remote, cancellationToken);
            }

            var target = string.IsNullOrWhiteSpace(remote) ? "（默认目录）" : remote;
            Dialogs.ShowMessage(
                "连接成功",
                $"已连接到 {_model.Host}:{_model.Port}\n远端目录 {target} 可用。");
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不提示
        }
        catch (Exception ex)
        {
            Dialogs.ShowMessage("连接失败", ex.Message);
        }
        finally
        {
            IsTesting = false;
            TestingText = string.Empty;
        }
    }

    private void ApplyTo(SyncTask target)
    {
        target.Name = Name.Trim();
        target.Enabled = Enabled;
        target.LocalDirectory = LocalDirectory.Trim();
        target.IncludeSubDirectories = IncludeSubDirectories;
        target.FilterMode = FilterMode;
        target.Extensions = new List<string>(Extensions);
        target.ExcludedDirectories = new List<string>(ExcludedDirectories);
        target.Protocol = Protocol;
        target.Host = Host.Trim();
        target.Port = Port;
        target.Username = Username.Trim();
        target.RemoteDirectory = RemoteDirectory.Trim();
        target.IncrementalSync = IncrementalSync;
        target.PreserveDirectoryStructure = PreserveDirectoryStructure;
        target.DeleteLocalAfterUpload = DeleteLocalAfterUpload;
        target.TimeoutSeconds = TimeoutSeconds;
        target.RetryCount = RetryCount;

        target.Credential.AuthMode = AuthMode;

        if (IsPrivateKeyMode)
        {
            target.Credential.PrivateKeyPath = PrivateKeyPath.Trim();
            target.Credential.Passphrase = string.IsNullOrEmpty(Passphrase) ? null : Passphrase;
            target.Credential.Password = null;
        }
        else
        {
            // 留空表示「保持原密码」，与界面提示一致
            if (!string.IsNullOrEmpty(Password))
            {
                target.Credential.Password = Password;
            }

            target.Credential.PrivateKeyPath = null;
            target.Credential.Passphrase = null;
        }

        Schedule.ApplyTo(target.Schedule);
        target.Normalize();
    }

    /// <summary>切换协议时自动带出标准端口，但不覆盖用户已改过的自定义端口</summary>
    partial void OnProtocolChanged(SyncProtocol value)
    {
        if (Port is 21 or 22 or <= 0)
        {
            Port = SyncTask.GetDefaultPort(value);
        }
    }

    partial void OnAuthModeChanged(SftpAuthMode value) => RaiseAuthChanged();

    private void RaiseAuthChanged()
    {
        OnPropertyChanged(nameof(IsPrivateKeyMode));
        OnPropertyChanged(nameof(PasswordStatusText));
    }
}
