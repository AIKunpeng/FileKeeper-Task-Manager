using Microsoft.Win32;
using System.Reflection;

namespace FileKeeper.Setup;

/// <summary>
/// 「添加或删除程序」里的卸载项读写。
///
/// 一律显式指定 <c>RegistryView.Registry64</c>：32 位视图（WOW6432Node）下的卸载项
/// 不会出现在系统设置的程序列表里，而这里恰好不能假设安装器一定是 64 位进程。
/// </summary>
public sealed class UninstallRegistry
{
    private const string RegistryKeyName = "FileKeeper";

    private const string UninstallRootPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly InstallLayout _layout;
    private readonly Action<string> _report;

    public UninstallRegistry(InstallLayout layout, Action<string>? report = null)
    {
        _layout = layout;
        _report = report ?? (_ => { });
    }

    public bool IsRegistered
    {
        get
        {
            using var machine = OpenMachine();
            using var key = machine.OpenSubKey(Path.Combine(UninstallRootPath, RegistryKeyName));
            return key is not null;
        }
    }

    /// <summary>
    /// 读取已登记的安装目录。安装路径可由用户自选之后，卸载不能再假定是默认位置，
    /// 而这个值正是安装时写下的真实落点，是卸载定位的第一手依据。
    /// </summary>
    public static string? ReadInstallLocation()
    {
        try
        {
            using var machine = OpenMachine();
            using var key = machine.OpenSubKey(Path.Combine(UninstallRootPath, RegistryKeyName));

            var location = key?.GetValue("InstallLocation") as string;
            return string.IsNullOrWhiteSpace(location) ? null : location;
        }
        catch
        {
            // 读不到就当作未安装：调用方还有后续回退，这里抛出只会让卸载彻底失去机会
            return null;
        }
    }

    /// <summary>
    /// 写入卸载项。卸载命令指向安装目录内的副本而不是原始安装包：
    /// 用户通常会在装完后删掉下载的安装包，卸载入口必须留在系统里。
    /// </summary>
    public void Write(long installedBytes)
    {
        try
        {
            using var machine = OpenMachine();
            using var key = machine.CreateSubKey(Path.Combine(UninstallRootPath, RegistryKeyName));

            key.SetValue("DisplayName", InstallLayout.ProductName);
            key.SetValue("DisplayVersion", ProductVersion);
            key.SetValue("Publisher", "FileKeeper");
            key.SetValue("InstallLocation", _layout.Root);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));

            var icon = Path.Combine(_layout.Root, "app.ico");
            if (File.Exists(icon))
            {
                key.SetValue("DisplayIcon", icon);
            }

            key.SetValue("UninstallString", $"\"{_layout.UninstallerPath}\" /uninstall");
            key.SetValue("QuietUninstallString", $"\"{_layout.UninstallerPath}\" /uninstall /silent");

            // EstimatedSize 的单位是 KB，且必须是 DWORD
            key.SetValue("EstimatedSize", (int)Math.Max(1, installedBytes / 1024), RegistryValueKind.DWord);

            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            _report("卸载项已写入「添加或删除程序」");
        }
        catch (UnauthorizedAccessException)
        {
            _report("写入卸载项失败：没有管理员权限。请右键以管理员身份运行安装程序。");
            throw;
        }
        catch (Exception ex)
        {
            _report($"写入卸载项失败：{ex.Message}");
            throw;
        }
    }

    public void Remove()
    {
        try
        {
            using var machine = OpenMachine();
            machine.DeleteSubKeyTree(Path.Combine(UninstallRootPath, RegistryKeyName), throwOnMissingSubKey: false);
            _report("卸载项已删除");
        }
        catch (Exception ex)
        {
            _report($"删除卸载项失败：{ex.Message}");
        }
    }

    private static string ProductVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;

            // 前三位即 Major.Minor.Build，第四位的修订号对用户没有意义
            return version is null
                ? "1.0.0"
                : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        }
    }

    private static RegistryKey OpenMachine() =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
}
