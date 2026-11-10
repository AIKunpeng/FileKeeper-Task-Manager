using FileKeeper.Core.Models;

namespace FileKeeper.Core.Engines;

/// <summary>
/// 压缩请求。
/// </summary>
public sealed class CompressionRequest
{
    /// <summary>压缩包完整路径（含扩展名）</summary>
    public required string ArchivePath { get; init; }

    /// <summary>目标格式</summary>
    public ArchiveFormat Format { get; init; } = ArchiveFormat.Zip;

    /// <summary>待压缩文件完整路径列表</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>
    /// 公共根路径。压缩包内保留相对该目录的子路径；
    /// 为空时仅保留文件名。
    /// </summary>
    public string? BaseDirectory { get; init; }

    /// <summary>压缩级别 0~9</summary>
    public int CompressionLevel { get; init; } = 5;

    /// <summary>密码，为空表示不加密</summary>
    public string? Password { get; init; }

    /// <summary>分卷大小（字节），为空表示不分卷</summary>
    public long? VolumeSizeBytes { get; init; }
}

/// <summary>
/// 解压请求。
/// </summary>
public sealed class ExtractRequest
{
    public required string ArchivePath { get; init; }

    public required string OutputDirectory { get; init; }

    /// <summary>密码，为空表示不加密</summary>
    public string? Password { get; init; }
}

/// <summary>
/// 压缩包引擎抽象。
/// </summary>
public interface IArchiveEngine
{
    /// <summary>引擎名称，用于日志与界面展示</summary>
    string Name { get; }

    /// <summary>是否支持创建该格式的压缩包</summary>
    bool CanCreate(ArchiveFormat format);

    /// <summary>是否支持解压该格式</summary>
    bool CanExtract(ArchiveFormat format);

    /// <summary>压缩指定文件</summary>
    Task CompressAsync(CompressionRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>解压到指定目录</summary>
    Task ExtractAsync(ExtractRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// 引擎不可用或执行失败。
/// </summary>
public sealed class ArchiveEngineException : Exception
{
    public ArchiveEngineException(string message) : base(message)
    {
    }

    public ArchiveEngineException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
