using FileKeeper.Core.Models;

namespace FileKeeper.Core.Engines;

/// <summary>
/// 远端文件的比对信息。
/// </summary>
/// <param name="Size">字节数</param>
/// <param name="LastModified">最后修改时间（UTC）</param>
public sealed record RemoteFileInfo(long Size, DateTimeOffset LastModified);

/// <summary>
/// 同步客户端抽象。
///
/// 抽象成这一个接口，是为了让「增量比对 / 原子上传 / 重试 / 上传后删源」
/// 这套逻辑在 <c>SyncRunner</c> 里只写一份，而 SFTP 与 FTP 两套 API 差异很大。
/// </summary>
public interface ISyncClient : IAsyncDisposable
{
    SyncProtocol Protocol { get; }

    /// <summary>建立连接</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>取远端文件信息，不存在时返回 null</summary>
    Task<RemoteFileInfo?> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>确保远端目录存在（逐级创建）</summary>
    Task EnsureDirectoryAsync(string remoteDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    /// 上传文件。实现必须是原子的：先传到临时名，成功后再改名，
    /// 避免中断时在远端留下半截文件被后续增量比对误判为「已上传」。
    /// </summary>
    Task UploadAsync(
        string localPath,
        string remotePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>删除远端文件（用于清理残留的临时文件）</summary>
    Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default);

    Task DisconnectAsync();
}

/// <summary>
/// 远端路径工具。远端（SFTP / FTP）一律使用正斜杠，与 Windows 本地路径不同。
/// </summary>
public static class RemotePath
{
    /// <summary>拼接远端路径</summary>
    public static string Combine(string basePath, string relativePath)
    {
        if (string.IsNullOrEmpty(basePath))
        {
            return relativePath.Replace('\\', '/').TrimStart('/');
        }

        return basePath.TrimEnd('/') + "/" + relativePath.Replace('\\', '/').TrimStart('/');
    }

    /// <summary>取远端路径的父目录部分</summary>
    public static string GetDirectoryName(string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath))
        {
            return string.Empty;
        }

        var index = remotePath.LastIndexOf('/');
        return index <= 0 ? string.Empty : remotePath[..index];
    }
}
