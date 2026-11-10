using Renci.SshNet;
using FileKeeper.Core.Models;

namespace FileKeeper.Core.Engines;

/// <summary>
/// 基于 SSH.NET 的 SFTP 客户端。支持密码认证与私钥认证（私钥可另带口令）。
/// </summary>
public sealed class SftpSyncClient : ISyncClient
{
    private const string TempSuffix = ".filepart";
    private const int BufferSize = 64 * 1024;

    private readonly SyncTask _task;
    private SftpClient? _client;

    public SftpSyncClient(SyncTask task)
    {
        _task = task;
    }

    public SyncProtocol Protocol => SyncProtocol.Sftp;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var client = new SftpClient(CreateConnectionInfo());
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(_task.TimeoutSeconds);

        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        _client = client;
    }

    public async Task<RemoteFileInfo?> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var client = EnsureConnected();

        if (!await client.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var attributes = await client.GetAttributesAsync(remotePath, cancellationToken).ConfigureAwait(false);

        // SftpFileAttributes 的时间均为 UTC
        return new RemoteFileInfo(attributes.Size, new DateTimeOffset(attributes.LastWriteTimeUtc));
    }

    public async Task EnsureDirectoryAsync(string remoteDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteDirectory) || remoteDirectory == "/")
        {
            return;
        }

        var client = EnsureConnected();
        var isAbsolute = remoteDirectory.StartsWith('/');
        var parts = remoteDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var built = string.Empty;

        foreach (var part in parts)
        {
            // 逐级创建时必须保持原路径的相对性，否则相对目录会被误建成根目录下的绝对路径
            built = built.Length == 0
                ? (isAbsolute ? "/" + part : part)
                : built + "/" + part;

            if (!await client.ExistsAsync(built, cancellationToken).ConfigureAwait(false))
            {
                await client.CreateDirectoryAsync(built, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task UploadAsync(
        string localPath,
        string remotePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var client = EnsureConnected();

        // 先保证目标目录存在：上传不会自动创建目录，而临时文件也要落在同一目录
        await EnsureDirectoryAsync(RemotePath.GetDirectoryName(remotePath), cancellationToken).ConfigureAwait(false);

        var tempPath = remotePath + TempSuffix;

        if (await client.ExistsAsync(tempPath, cancellationToken).ConfigureAwait(false))
        {
            await client.DeleteFileAsync(tempPath, cancellationToken).ConfigureAwait(false);
        }

        var fileSize = new FileInfo(localPath).Length;

        await using var stream = new FileStream(
            localPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);

        // 原子上传：先写临时名，全部写完后再改名，中途失败不会污染正式文件
        try
        {
            await client.UploadFileAsync(
                stream,
                tempPath,
                canOverride: true,
                progress is null ? null : ToSshProgress(progress, fileSize),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 必须清掉半截文件：否则残留的 .filepart 会被后续增量比对当成正式文件而永久跳过
            // 用 CancellationToken.None，取消时也要清理
            CleanupTemp(client, tempPath);
            throw;
        }

        await client.RenameFileAsync(tempPath, remotePath, cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
    }

    public async Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var client = EnsureConnected();

        if (await client.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false))
        {
            await client.DeleteFileAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task DisconnectAsync()
    {
        _client?.Disconnect();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _client?.Dispose();
        _client = null;
        GC.SuppressFinalize(this);
    }

    private ConnectionInfo CreateConnectionInfo()
    {
        if (_task.Credential.AuthMode == SftpAuthMode.PrivateKey)
        {
            var keyPath = _task.Credential.PrivateKeyPath;

            if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
            {
                throw new ArchiveEngineException($"SFTP 私钥文件不存在：{keyPath}");
            }

            var passphrase = _task.Credential.Passphrase;
            var keyFile = string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(keyPath)
                : new PrivateKeyFile(keyPath, passphrase);

            return new PrivateKeyConnectionInfo(_task.Host, _task.Port, _task.Username, keyFile);
        }

        var password = _task.Credential.Password ?? string.Empty;

        // 双认证回退：只发 password 一种方式，碰到把密码验证放在
        // keyboard-interactive 交互环节的服务器（NAS、带 PAM 的面板机很常见）
        // 会直接 Permission denied，哪怕账号密码完全正确。
        // 两种方式都注册上，SSH.NET 会按顺序尝试，都失败才报错。
        var passwordAuth = new PasswordAuthenticationMethod(_task.Username, password);

        var interactiveAuth = new KeyboardInteractiveAuthenticationMethod(_task.Username);
        interactiveAuth.AuthenticationPrompt += (_, e) =>
        {
            // 服务器无论问几个问题（密码、验证码提示……），能拿到的只有这一个密码，
            // 全部用它作答——答不上就让它失败，与真实交互语义一致
            foreach (var prompt in e.Prompts)
            {
                prompt.Response = password;
            }
        };

        return new ConnectionInfo(_task.Host, _task.Port, _task.Username, passwordAuth, interactiveAuth);
    }

    private static void CleanupTemp(SftpClient client, string tempPath)
    {
        try
        {
            if (client.Exists(tempPath))
            {
                client.DeleteFile(tempPath);
            }
        }
        catch
        {
            // 清理失败不能掩盖原始的上传失败原因
        }
    }

    private static IProgress<UploadFileProgressReport> ToSshProgress(IProgress<double> progress, long fileSize)
    {
        if (fileSize <= 0)
        {
            return new Progress<UploadFileProgressReport>(_ => { });
        }

        // SSH.NET 只上报已上传字节数，总大小需要自己按本地文件大小折算
        return new Progress<UploadFileProgressReport>(report =>
            progress.Report(Math.Min(100, report.TotalBytesUploaded * 100.0 / fileSize)));
    }

    private SftpClient EnsureConnected() =>
        _client ?? throw new InvalidOperationException("SFTP 尚未连接");
}
