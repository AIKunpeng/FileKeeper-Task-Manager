using System.Runtime.InteropServices;

namespace FileKeeper.Setup;

/// <summary>
/// 桌面与开始菜单快捷方式的创建和删除。
///
/// 用 <c>WScript.Shell</c> 的 COM 后期绑定而不是 IWshRuntimeLibrary 互操作程序集：
/// 后期绑定不需要额外的 NuGet 包或 COM 引用，构建完全离线，
/// 代价是编译期没有类型检查，所以每一步都包了明确的失败提示。
/// </summary>
public sealed class ShortcutService
{
    private readonly InstallLayout _layout;
    private readonly Action<string> _report;

    public ShortcutService(InstallLayout layout, Action<string>? report = null)
    {
        _layout = layout;
        _report = report ?? (_ => { });
    }

    /// <summary>开始菜单里的程序文件夹路径</summary>
    public static string StartMenuFolder =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs", InstallLayout.ProductName);

    /// <summary>公共桌面目录（所有用户共享）</summary>
    public static string DesktopFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    public string DesktopShortcutPath => Path.Combine(DesktopFolder, InstallLayout.ProductName + ".lnk");

    public string StartMenuShortcutPath => Path.Combine(StartMenuFolder, InstallLayout.ProductName + ".lnk");

    public void Create()
    {
        // 图标取自安装目录下的 app.ico，而不是界面端 exe：
        // 快捷方式在 exe 被卸载后仍可能在开始菜单残留一个索引，独立图标文件更稳妥
        var icon = Path.Combine(_layout.Root, "app.ico");

        CreateCore(DesktopShortcutPath, icon);
        CreateCore(StartMenuShortcutPath, icon);

        _report($"快捷方式已创建：桌面、开始菜单「{InstallLayout.ProductName}」");
    }

    public void Remove()
    {
        DeleteFile(DesktopShortcutPath);
        DeleteFile(StartMenuShortcutPath);

        try
        {
            if (Directory.Exists(StartMenuFolder) && !Directory.EnumerateFileSystemEntries(StartMenuFolder).Any())
            {
                Directory.Delete(StartMenuFolder);
            }
        }
        catch (Exception ex)
        {
            _report($"删除开始菜单目录失败：{ex.Message}");
        }
    }

    private void CreateCore(string shortcutPath, string iconPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("系统未提供 WScript.Shell，无法创建快捷方式");

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);

            shortcut.TargetPath = _layout.AppExecutablePath;
            shortcut.WorkingDirectory = _layout.AppDirectory;
            shortcut.Description = InstallLayout.ProductName;

            if (File.Exists(iconPath))
            {
                shortcut.IconLocation = iconPath;
            }

            shortcut.Save();
            _report($"已创建快捷方式：{shortcutPath}");
        }
        catch (COMException ex)
        {
            _report($"创建快捷方式失败（COM）：{shortcutPath} —— {ex.Message}");
        }
        catch (Exception ex)
        {
            _report($"创建快捷方式失败：{shortcutPath} —— {ex.Message}");
        }
    }

    private void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _report($"删除快捷方式失败：{path} —— {ex.Message}");
        }
    }
}
