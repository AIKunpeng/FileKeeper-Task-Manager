using FluentFTP;
using FileKeeper.Core.Models;

namespace FileKeeper.Core.Engines;

/// <summary>
/// 基于 FluentFTP 的 FTP / FTPS 客户端。
///
/// - 明文 FTP：端口 21；
/// - FTPS 显式：<c>AUTH TLS</c> 后加密，端口同样是 21。
///   未实现 FTPS 隐式（990），主流服务器已普遍支持显式。
/// - 统一使用被动模式（PASV），对客户端侧防火墙/NAT 更友好。
/// - 自签证书在备份场景很常见，因此默认接受任意证书（<c>ValidateAnyCertificate</c>）。
/// </summary>
public sealed class FtpSyncClient : ISyncClient
{
    private const string TempSuffix = ".filepart";

    private readonly SyncTask _task;
    private AsyncFtpClient? _client;

    public FtpSyncClient(SyncTask task)
    {
        _task = task;
    }

    public SyncProtocol Protocol => _task.Protocol;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var config = new FtpConfig
        {
            ConnectTimeout = _task.TimeoutSeconds * 1000,
            ReadTimeout = _task.TimeoutSeconds * 1000,
            DataConnectionType = FtpDataConnectionType.AutoPassive,
            EncryptionMode = _task.Protocol == SyncProtocol.FtpsExplicit
                ? FtpEncryptionMode.Explicit
                : FtpEncryptionMode.None,
            ValidateAnyCertificate = true
        };

        var client = new AsyncFtpClient(
            _task.Host,
            _task.Username,
            _task.Credential.Password ?? string.Empty,
            _task.Port,
            config);

        await client.Connect(cancellationToken).ConfigureAwait(false);
        _client = client;
    }

    public async Task<RemoteFileInfo?> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var client = EnsureConnected();

        // 取不到大小时 GetFileSize 返回 -1，据此判定文件不存在
        var size = await client.GetFileSize(remotePath, -1, cancellationToken).ConfigureAwait(false);
        if (size < 0)
        {
            return null;
        }

        var modified = await client.GetModifiedTime(remotePath, cancellationToken).ConfigureAwait(false);

        // FluentFTP 默认按 UTC 返回，取不到时为 MinValue
        var lastModified = modified == DateTime.MinValue
            ? DateTimeOffset.MinValue
            : new DateTimeOffset(modified, TimeSpan.Zero);

        return new RemoteFileInfo(size, lastModified);
    }

    public async Task EnsureDirectoryAsync(string remoteDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteDirectory) || remoteDirectory == "/")
        {
            return;
        }

        var client = EnsureConnected();

        if (await client.DirectoryExists(remoteDirectory, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // force=true：父目录一并创建，无需逐级调用
        await client.CreateDirectory(remoteDirectory, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task UploadAsync(
        string localPath,
        string remotePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var client = EnsureConnected();

        await EnsureDirectoryAsync(RemotePath.GetDirectoryName(remotePath), cancellationToken).ConfigureAwait(false);

        var tempPath = remotePath + TempSuffix;

        if (await client.FileExists(tempPath, cancellationToken).ConfigureAwait(false))
        {
            await client.DeleteFile(tempPath, cancellationToken).ConfigureAwait(false);
        }

        // 原子上传：createRemoteDir=true 保证临时目录存在，写完再改名
        try
        {
            await client.UploadFile(
                localPath,
                tempPath,
                FtpRemoteExists.Overwrite,
                createRemoteDir: true,
                FtpVerify.None,
                progress is null ? null : ToFtpProgress(progress),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 必须清掉半截文件，否则残留会被后续增量比对当作正式文件永久跳过
            await CleanupTempAsync(client, tempPath).ConfigureAwait(false);
            throw;
        }

        await client.Rename(tempPath, remotePath, cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
    }

    public async Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var client = EnsureConnected();

        if (await client.FileExists(remotePath, cancellationToken).ConfigureAwait(false))
        {
            await client.DeleteFile(remotePath, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_client is not null)
        {
            await _client.Disconnect().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _client?.Dispose();
        _client = null;
        GC.SuppressFinalize(this);
    }

    private static async Task CleanupTempAsync(AsyncFtpClient client, string tempPath)
    {
        try
        {
            if (await client.FileExists(tempPath).ConfigureAwait(false))
            {
                await client.DeleteFile(tempPath).ConfigureAwait(false);
            }
        }
        catch
        {
            // 清理失败不能掩盖原始的上传失败原因
        }
    }

    private static IProgress<FtpProgress> ToFtpProgress(IProgress<double> progress) =>
        new Progress<FtpProgress>(report => progress.Report(Math.Clamp(report.Progress, 0, 100)));

    private AsyncFtpClient EnsureConnected() =>
        _client ?? throw new InvalidOperationException("FTP 尚未连接");
}
