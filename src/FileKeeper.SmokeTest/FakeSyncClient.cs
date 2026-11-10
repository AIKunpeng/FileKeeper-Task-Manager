using System.Security.Cryptography;
using System.Text;
using FileKeeper.Core.Engines;
using FileKeeper.Core.Models;

namespace FileKeeper.SmokeTest;

/// <summary>
/// 内存级同步客户端测试替身。
///
/// 用字典模拟远端文件系统，验证 SyncRunner 的增量比对、重试与删源逻辑；
/// 真实协议（SFTP / FTP）的连通性由「测试连接」按钮与实际使用覆盖，
/// 冒烟阶段不依赖外部服务端。
/// </summary>
public sealed class FakeSyncClient : ISyncClient
{
    /// <summary>远端文件：路径 → （内容，最后修改时间 UTC）</summary>
    public readonly Dictionary<string, (byte[] Content, DateTimeOffset Modified)> Store = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>累计真实上传次数（跳过不计入）</summary>
    public int UploadCount { get; private set; }

    /// <summary>建过的远端目录</summary>
    public readonly List<string> CreatedDirectories = new();

    /// <summary>上传前主动抛异常，用于验证重试路径</summary>
    public int FailNextUploads;

    public SyncProtocol Protocol => SyncProtocol.Ftp;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<RemoteFileInfo?> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Store.TryGetValue(remotePath, out var entry)
            ? new RemoteFileInfo(entry.Content.LongLength, entry.Modified)
            : null);
    }

    public Task EnsureDirectoryAsync(string remoteDirectory, CancellationToken cancellationToken = default)
    {
        CreatedDirectories.Add(remoteDirectory);
        return Task.CompletedTask;
    }

    public Task UploadAsync(
        string localPath,
        string remotePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (FailNextUploads > 0)
        {
            FailNextUploads--;
            throw new IOException("模拟网络中断");
        }

        var content = File.ReadAllBytes(localPath);
        Store[remotePath] = (content, File.GetLastWriteTimeUtc(localPath));
        UploadCount++;
        progress?.Report(100);
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        Store.Remove(remotePath);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>生成带确定内容的测试文件（无 BOM，便于字节级比对）</summary>
    public static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>计算字节数组的 SHA-256，用于校验压缩包内容完整性</summary>
    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
