using FileKeeper.Core.Models;

namespace FileKeeper.Core.Engines;

/// <summary>
/// 引擎能力诊断结果，供设置页展示。
/// </summary>
public sealed record EngineDiagnostics(
    bool EmbeddedLibraryAvailable,
    string? EmbeddedLibraryPath,
    string? EmbeddedLibraryError,
    bool CliAvailable,
    string? CliPath,
    bool WinRarAvailable,
    string? WinRarPath,
    string? RarUnavailableReason)
{
    /// <summary>是否支持创建 ZIP（内置引擎与命令行二者有其一即可）</summary>
    public bool CanCreateZip => EmbeddedLibraryAvailable || CliAvailable;

    /// <summary>是否支持解压 RAR</summary>
    public bool CanExtractRar => EmbeddedLibraryAvailable || CliAvailable || WinRarAvailable;

    /// <summary>是否支持创建 RAR（仅 WinRAR 后端）</summary>
    public bool CanCreateRar => WinRarAvailable;
}

/// <summary>
/// 按「格式 + 能力」选择压缩引擎。
/// </summary>
public static class ArchiveEngineFactory
{
    /// <summary>
    /// 创建用于压缩的引擎。
    /// </summary>
    /// <param name="format">目标格式</param>
    /// <param name="requiresPassword">是否需要加密。加密时优先命令行后端，以便保留目录结构</param>
    /// <exception cref="ArchiveEngineException">没有任何可用引擎时抛出，并说明原因</exception>
    public static IArchiveEngine Create(ArchiveFormat format, bool requiresPassword = false)
    {
        if (format == ArchiveFormat.Rar)
        {
            var rar = WinRarCliEngine.TryCreate();
            if (rar is null)
            {
                throw new ArchiveEngineException(WinRarCliEngine.UnavailableReason ?? "无法创建 RAR");
            }

            return rar;
        }

        if (requiresPassword)
        {
            var cli = SevenZipCliEngine.TryCreate();
            if (cli is not null)
            {
                return cli;
            }
        }

        if (SevenZipEngine.IsAvailable)
        {
            return new SevenZipEngine();
        }

        var fallback = SevenZipCliEngine.TryCreate();
        if (fallback is not null)
        {
            return fallback;
        }

        throw new ArchiveEngineException(
            $"没有可用的压缩引擎：内嵌 7z.dll 缺失（{SevenZipEngine.InitializationError ?? "未找到"}），且未随包分发或系统安装 7-Zip");
    }

    /// <summary>是否支持创建指定格式</summary>
    public static bool CanCreate(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => SevenZipEngine.IsAvailable || SevenZipCliEngine.IsAvailable,
        ArchiveFormat.Rar => WinRarCliEngine.IsAvailable,
        _ => false
    };

    /// <summary>是否支持解压指定格式</summary>
    public static bool CanExtract(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => SevenZipEngine.IsAvailable || SevenZipCliEngine.IsAvailable,
        ArchiveFormat.Rar => SevenZipEngine.IsAvailable || SevenZipCliEngine.IsAvailable || WinRarCliEngine.IsAvailable,
        _ => false
    };

    /// <summary>不可用原因说明，可用时返回 null</summary>
    public static string? GetUnavailableReason(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Rar => WinRarCliEngine.UnavailableReason,
        _ => CanCreate(format) ? null : "未找到可用的 ZIP 压缩引擎"
    };

    /// <summary>引擎诊断信息，用于设置页展示</summary>
    public static EngineDiagnostics Diagnose() => new(
        SevenZipEngine.IsAvailable,
        SevenZipEngine.LibraryPath,
        SevenZipEngine.InitializationError,
        SevenZipCliEngine.IsAvailable,
        SevenZipCliEngine.ExecutablePath,
        WinRarCliEngine.IsAvailable,
        WinRarCliEngine.Tool?.ExecutablePath,
        WinRarCliEngine.UnavailableReason);
}
