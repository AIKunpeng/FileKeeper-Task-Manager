namespace FileKeeper.Core.Models;

/// <summary>
/// 定时压缩任务。
/// </summary>
public sealed class CompressionTask : IScheduledTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "新建压缩任务";

    public bool Enabled { get; set; } = true;

    /// <summary>待压缩的源目录</summary>
    public string SourceDirectory { get; set; } = string.Empty;

    /// <summary>是否包含子目录</summary>
    public bool IncludeSubDirectories { get; set; } = true;

    /// <summary>文件过滤方式</summary>
    public FileFilterMode FilterMode { get; set; } = FileFilterMode.All;

    /// <summary>指定扩展名，形如 ".log"；统一存小写带点形式</summary>
    public List<string> Extensions { get; set; } = new();

    /// <summary>
    /// 排除的子目录（名称或完整路径）。与清理任务保持一致，
    /// 否则用户无法在压缩时跳过 logs、node_modules 这类无需备份的子目录。
    /// </summary>
    public List<string> ExcludedDirectories { get; set; } = new();

    /// <summary>压缩包存放目录</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 文件名模板，支持 {yyyy} {MM} {dd} {HH} {mm} {ss} {yyyyMMdd} {yyyyMMddHHmmss} 占位符。
    /// 不含扩展名，扩展名由 <see cref="Format"/> 决定。
    /// </summary>
    public string FileNameTemplate { get; set; } = "Backup_{yyyyMMdd}_{HHmmss}";

    /// <summary>压缩格式</summary>
    public ArchiveFormat Format { get; set; } = ArchiveFormat.Zip;

    /// <summary>压缩级别 0（仅存储）~ 9（极限压缩）</summary>
    public int CompressionLevel { get; set; } = 5;

    /// <summary>压缩包密码，可为空</summary>
    public string? Password { get; set; }

    /// <summary>分卷大小（字节），为空表示不分卷</summary>
    public long? VolumeSizeBytes { get; set; }

    /// <summary>压缩成功后删除源文件（危险操作，界面需二次确认）</summary>
    public bool DeleteSourceAfterCompress { get; set; }

    public ScheduleConfig Schedule { get; set; } = new();

    /// <summary>
    /// 深拷贝。编辑页基于副本工作，取消时直接丢弃，不会污染原配置。
    /// </summary>
    public CompressionTask Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        SourceDirectory = SourceDirectory,
        IncludeSubDirectories = IncludeSubDirectories,
        FilterMode = FilterMode,
        Extensions = new List<string>(Extensions),
        ExcludedDirectories = new List<string>(ExcludedDirectories),
        OutputDirectory = OutputDirectory,
        FileNameTemplate = FileNameTemplate,
        Format = Format,
        CompressionLevel = CompressionLevel,
        Password = Password,
        VolumeSizeBytes = VolumeSizeBytes,
        DeleteSourceAfterCompress = DeleteSourceAfterCompress,
        Schedule = Schedule.Clone()
    };

    /// <summary>
    /// 归一化：清理路径首尾引号、补齐扩展名前缀并转小写、压缩级别夹取到 0~9。
    /// </summary>
    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "未命名压缩任务" : Name.Trim();
        SourceDirectory = NormalizePath(SourceDirectory);
        OutputDirectory = NormalizePath(OutputDirectory);
        CompressionLevel = Math.Clamp(CompressionLevel, 0, 9);

        if (string.IsNullOrWhiteSpace(FileNameTemplate))
        {
            FileNameTemplate = "Backup_{yyyyMMdd}_{HHmmss}";
        }

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

        if (VolumeSizeBytes is <= 0)
        {
            VolumeSizeBytes = null;
        }

        Schedule.Normalize();
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.Trim().Trim('"').Trim().TrimEnd('\\');
    }
}
