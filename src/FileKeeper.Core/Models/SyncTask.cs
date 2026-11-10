namespace FileKeeper.Core.Models;

/// <summary>
/// 定时同步任务：把本地目录的文件按调度上传到 SFTP / FTP 服务器。
///
/// 方向固定为「本地 → 远程」，不做下载、不做双向镜像、不删除远程多余文件。
/// </summary>
public sealed class SyncTask : IScheduledTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "新建同步任务";

    public bool Enabled { get; set; } = true;

    /// <summary>待上传的本地目录</summary>
    public string LocalDirectory { get; set; } = string.Empty;

    /// <summary>是否包含子目录</summary>
    public bool IncludeSubDirectories { get; set; } = true;

    /// <summary>文件过滤方式</summary>
    public FileFilterMode FilterMode { get; set; } = FileFilterMode.All;

    /// <summary>指定扩展名，形如 ".log"；统一存小写带点形式</summary>
    public List<string> Extensions { get; set; } = new();

    /// <summary>排除的子目录（名称或完整路径）</summary>
    public List<string> ExcludedDirectories { get; set; } = new();

    /// <summary>协议：SFTP / FTP / FTPS 显式</summary>
    public SyncProtocol Protocol { get; set; } = SyncProtocol.Sftp;

    /// <summary>服务器地址（域名或 IP）</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>端口，0 时按协议取默认值（SFTP 22 / FTP 21）</summary>
    public int Port { get; set; }

    public string Username { get; set; } = string.Empty;

    public SyncCredential Credential { get; set; } = new();

    /// <summary>远端根目录，例如 /backup。留空表示用户家目录（远端默认工作目录）</summary>
    public string RemoteDirectory { get; set; } = string.Empty;

    /// <summary>增量同步：仅上传远端不存在或已变更的文件</summary>
    public bool IncrementalSync { get; set; } = true;

    /// <summary>按本地相对路径在远端创建同名目录结构</summary>
    public bool PreserveDirectoryStructure { get; set; } = true;

    /// <summary>上传成功后删除本地源文件（归档模式，危险操作）</summary>
    public bool DeleteLocalAfterUpload { get; set; }

    /// <summary>连接与读写超时（秒）</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>单文件上传失败后的重试次数</summary>
    public int RetryCount { get; set; } = 3;

    public ScheduleConfig Schedule { get; set; } = new();

    /// <summary>该协议的标准端口</summary>
    public static int GetDefaultPort(SyncProtocol protocol) => protocol == SyncProtocol.Sftp ? 22 : 21;

    /// <summary>
    /// 深拷贝。编辑页基于副本工作，取消时直接丢弃，不会污染原配置。
    /// 凭据只需拷密文——明文属性是 DPAPI 代理，读它反而会多做一次解密。
    /// </summary>
    public SyncTask Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        LocalDirectory = LocalDirectory,
        IncludeSubDirectories = IncludeSubDirectories,
        FilterMode = FilterMode,
        Extensions = new List<string>(Extensions),
        ExcludedDirectories = new List<string>(ExcludedDirectories),
        Protocol = Protocol,
        Host = Host,
        Port = Port,
        Username = Username,
        Credential = Credential.Clone(),
        RemoteDirectory = RemoteDirectory,
        IncrementalSync = IncrementalSync,
        PreserveDirectoryStructure = PreserveDirectoryStructure,
        DeleteLocalAfterUpload = DeleteLocalAfterUpload,
        TimeoutSeconds = TimeoutSeconds,
        RetryCount = RetryCount,
        Schedule = Schedule.Clone()
    };

    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "未命名同步任务" : Name.Trim();
        LocalDirectory = CompressionTask.NormalizePath(LocalDirectory);
        Host = Host.Trim().Trim('"').Trim();

        Credential ??= new SyncCredential();

        if (Port <= 0 || Port > 65535)
        {
            Port = GetDefaultPort(Protocol);
        }

        // 远端路径统一使用正斜杠，去掉结尾斜杠，便于拼接
        RemoteDirectory = (RemoteDirectory ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');

        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 3600);
        RetryCount = Math.Clamp(RetryCount, 0, 10);

        Extensions = Extensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().ToLowerInvariant())
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .Distinct()
            .ToList();

        ExcludedDirectories = ExcludedDirectories
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().Trim('"').Trim().TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Schedule.Normalize();
    }
}
