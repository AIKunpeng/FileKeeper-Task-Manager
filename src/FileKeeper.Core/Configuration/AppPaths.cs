namespace FileKeeper.Core.Configuration;

/// <summary>
/// 统一路径定义。配置与日志固定存放在 %ProgramData%\FileKeeper 下，
/// 保证以 LocalSystem 运行的服务与普通用户运行的界面端访问的是同一份数据。
/// </summary>
public static class AppPaths
{
    public const string RootFolderName = "FileKeeper";

    /// <summary>%ProgramData%\FileKeeper</summary>
    public static string RootDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), RootFolderName);

    public static string ConfigFilePath => Path.Combine(RootDirectory, "config.json");

    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    /// <summary>
    /// 压缩过程中的临时工作目录（文件清单等）。
    /// 放在系统临时目录而非 ProgramData：ProgramData 下建目录需要提权，
    /// 而界面端不应因写临时文件而要求管理员权限。
    /// </summary>
    public static string TempDirectory => Path.Combine(Path.GetTempPath(), RootFolderName);

    /// <summary>确保目录存在；服务启动时必须先调用，否则 LocalSystem 下可能因目录缺失而崩溃</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }

    /// <summary>
    /// 判断路径是否属于禁止作为清理目标的系统/程序目录，防止误删。
    /// </summary>
    public static bool IsProtectedDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return true;
        }

        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var protectedRoots = new List<string>();
        var systemDrive = Path.GetPathRoot(trimmed);
        if (!string.IsNullOrEmpty(systemDrive))
        {
            protectedRoots.Add(systemDrive);
        }

        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.System,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.CommonApplicationData
                 })
        {
            try
            {
                protectedRoots.Add(Environment.GetFolderPath(special));
            }
            catch
            {
                // 忽略无法获取的目录
            }
        }

        // 用户目录整目录删除风险极高，同样视为受保护
        try
        {
            protectedRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch
        {
            // 忽略
        }

        var comparison = StringComparison.OrdinalIgnoreCase;

        return protectedRoots
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Any(p => trimmed.Equals(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison));
    }
}
