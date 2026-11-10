using FileKeeper.Core;
using FileKeeper.Core.Configuration;

namespace FileKeeper.Setup;

/// <summary>
/// 安装布局常量与路径计算。
///
/// 采用「<c>{根}\App</c> + <c>{根}\Service</c>」两层结构，这不是随意定的：
/// 界面端 <c>Services\DialogService.FindServiceExecutable()</c> 会从自身目录逐级上溯，
/// 每层依次尝试 <c>{dir}\FileKeeper.Service.exe</c> 与 <c>{dir}\Service\FileKeeper.Service.exe</c>。
/// 界面端位于根的下一层，上溯一层到根时正好命中第二个候选，
/// 因此采用这套布局不需要改动任何产品代码，界面端就能找到服务宿主。
/// </summary>
public sealed class InstallLayout
{
    public const string AppSubDirectory = "App";

    public const string ServiceSubDirectory = "Service";

    public const string AppExecutableName = "FileKeeper.App.exe";

    public const string ServiceExecutableName = "FileKeeper.Service.exe";

    /// <summary>本次安装释放的文件清单，用于下次升级时识别旧版残留</summary>
    public const string InstallManifestFileName = "install-manifest.txt";

    /// <summary>对外分发的产品名（同时用作卸载项、快捷方式与开始菜单目录名）</summary>
    /// <summary>产品显示名。出处收敛到 Core，避免安装器与界面端各写一份后走偏</summary>
    public static string ProductName => ProductInfo.Name;

    /// <summary>安装目录内保留的安装程序副本名，卸载入口指向它</summary>
    public const string UninstallerName = "FileKeeper-Setup.exe";

    public InstallLayout(string root)
    {
        Root = root;
        AppDirectory = Path.Combine(root, AppSubDirectory);
        ServiceDirectory = Path.Combine(root, ServiceSubDirectory);
        AppExecutablePath = Path.Combine(AppDirectory, AppExecutableName);
        ServiceExecutablePath = Path.Combine(ServiceDirectory, ServiceExecutableName);
        UninstallerPath = Path.Combine(root, UninstallerName);
        InstallManifestPath = Path.Combine(root, InstallManifestFileName);
    }

    public string Root { get; }

    public string AppDirectory { get; }

    public string ServiceDirectory { get; }

    public string AppExecutablePath { get; }

    public string ServiceExecutablePath { get; }

    public string UninstallerPath { get; }

    public string InstallManifestPath { get; }

    /// <summary>
    /// 默认安装根目录。刻意用 <see cref="Environment.SpecialFolder.ProgramFiles"/> 而非硬编码
    /// C:\Program Files：64 位进程取到 Program Files，万一变成 32 位进程也不会落到 x86 目录。
    /// </summary>
    public static string DefaultInstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FileKeeper");

    /// <summary>已安装的上一版文件相对路径清单；未安装过时返回空</summary>
    public IReadOnlyList<string> ReadManifest()
    {
        if (!File.Exists(InstallManifestPath))
        {
            return Array.Empty<string>();
        }

        return File.ReadAllLines(InstallManifestPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToList();
    }

    /// <summary>
    /// 是否在已有安装之上做覆盖安装。
    /// 判定依据是界面端 exe 是否存在，而不是卸载项：卸载项可能残留于上次卸载失败，
    /// 但 exe 存在就一定说明这个目录里真的装过东西。
    /// </summary>
    public bool IsInstalled() => File.Exists(AppExecutablePath);

    /// <summary>
    /// 该任务的数据目录（%ProgramData%\FileKeeper）。卸载时默认保留，
    /// 因此这里只提供路径、不负责删除。
    /// </summary>
    public static string DataDirectory => AppPaths.RootDirectory;
}
