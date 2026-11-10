using Microsoft.Win32;

namespace FileKeeper.Core.Engines;

/// <summary>
/// 探测到的外部工具。
/// </summary>
public sealed record ExternalTool(string Name, string ExecutablePath)
{
    /// <summary>版本号文本，可能为空</summary>
    public string? Version { get; init; }
}

/// <summary>
/// 探测系统已安装的 WinRAR / 7-Zip。
/// 结果做内存缓存，避免每次任务执行都读注册表。
/// </summary>
public static class ExternalToolLocator
{
    private static readonly object Sync = new();
    private static ExternalTool? _winRar;
    private static ExternalTool? _sevenZip;
    private static bool _winRarProbed;
    private static bool _sevenZipProbed;

    /// <summary>
    /// 查找 WinRAR 的命令行工具 Rar.exe（控制台版，无弹窗）。
    /// 未安装时返回 null。
    /// </summary>
    public static ExternalTool? FindWinRar()
    {
        lock (Sync)
        {
            if (_winRarProbed)
            {
                return _winRar;
            }

            _winRar = ProbeWinRar();
            _winRarProbed = true;
            return _winRar;
        }
    }

    /// <summary>查找系统安装的 7-Zip（7z.exe），未安装时返回 null</summary>
    public static ExternalTool? Find7Zip()
    {
        lock (Sync)
        {
            if (_sevenZipProbed)
            {
                return _sevenZip;
            }

            _sevenZip = Probe7Zip();
            _sevenZipProbed = true;
            return _sevenZip;
        }
    }

    /// <summary>清除缓存，供设置页「重新检测」使用</summary>
    public static void ResetCache()
    {
        lock (Sync)
        {
            _winRar = null;
            _sevenZip = null;
            _winRarProbed = false;
            _sevenZipProbed = false;
        }
    }

    private static ExternalTool? ProbeWinRar()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var directory = ProbeWinRarDirectory();
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        // 优先控制台版 Rar.exe，避免打包时闪 GUI
        var rar = Path.Combine(directory, "Rar.exe");
        var winRar = Path.Combine(directory, "WinRAR.exe");

        if (File.Exists(rar))
        {
            return new ExternalTool("WinRAR 命令行", rar);
        }

        return File.Exists(winRar) ? new ExternalTool("WinRAR", winRar) : null;
    }

    private static string? ProbeWinRarDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // 注册表：HKLM\SOFTWARE\WinRAR 的 exe64 / exe32 指向 WinRAR.exe
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(@"SOFTWARE\WinRAR");

            if (key is null)
            {
                continue;
            }

            var exe = key.GetValue("exe64") as string ?? key.GetValue("exe32") as string;
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                return Path.GetDirectoryName(exe);
            }
        }

        foreach (var candidate in new[]
                 {
                     @"C:\Program Files\WinRAR",
                     @"C:\Program Files (x86)\WinRAR"
                 })
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static ExternalTool? Probe7Zip()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(@"SOFTWARE\7-Zip");

            var path = key?.GetValue("Path") as string;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var exe = Path.Combine(path, "7z.exe");
            if (File.Exists(exe))
            {
                return new ExternalTool("7-Zip", exe);
            }
        }

        foreach (var candidate in new[]
                 {
                     @"C:\Program Files\7-Zip\7z.exe",
                     @"C:\Program Files (x86)\7-Zip\7z.exe"
                 })
        {
            if (File.Exists(candidate))
            {
                return new ExternalTool("7-Zip", candidate);
            }
        }

        return null;
    }
}
