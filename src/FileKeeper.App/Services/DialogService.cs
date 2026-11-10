using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using FileKeeper.Core.Services;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Window = System.Windows.Window;
// 文件夹选择只能用 WinForms 的 FolderBrowserDialog（WPF 没有对应的文件夹对话框）。
// 用别名而不是整条 using 引入，避免 Forms 与 WPF 的同名类型（OpenFileDialog 等）冲突。
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;
using FormsDialogResult = System.Windows.Forms.DialogResult;

namespace FileKeeper.App.Services;

public interface IDialogService
{
    /// <summary>选择文件夹，取消时返回 null</summary>
    string? BrowseFolder(string? initialDirectory = null);

    /// <summary>选择文件，取消时返回 null</summary>
    string? OpenFile(string filter, string? initialDirectory = null);

    /// <summary>危险操作确认。detail 非空时展示红色警示条。</summary>
    bool Confirm(string title, string message, string confirmText, string? detail = null, bool isDanger = false);

    /// <summary>纯提示，只有一个按钮</summary>
    void ShowMessage(string title, string message);
}

public sealed class DialogService : IDialogService
{
    public string? BrowseFolder(string? initialDirectory = null)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == FormsDialogResult.OK ? dialog.SelectedPath : null;
    }

    public string? OpenFile(string filter, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message, string confirmText, string? detail = null, bool isDanger = false) =>
        Views.ConfirmDialog.Show(GetOwner(), title, message, confirmText, detail, isDanger);

    public void ShowMessage(string title, string message) =>
        Views.ConfirmDialog.ShowMessage(GetOwner(), title, message);

    /// <summary>
    /// 取当前活动窗口作为弹窗宿主。取不到时退回主窗口，
    /// 否则 CenterOwner 会抛异常或弹到屏幕角落。
    /// </summary>
    private static Window? GetOwner()
    {
        if (Application.Current is null)
        {
            return null;
        }

        return Application.Current.Windows
                   .OfType<Window>()
                   .FirstOrDefault(w => w.IsActive)
               ?? Application.Current.MainWindow;
    }
}

/// <summary>
/// 服务的安装、卸载、启动、停止都需要管理员权限。
///
/// 这里统一以 runas 重新拉起 <c>FileKeeper.Service.exe</c> 来完成，而不直接调用
/// <see cref="ServiceControl"/>：因为 <see cref="ServiceControl.Install"/> 用的是当前进程的
/// 路径（界面端就是 FileKeeper.App.exe），直接在界面进程里调用会把界面程序注册成服务。
///
/// 只在需要的那一刻提权，程序清单就不必写 requireAdministrator，日常使用不会反复弹 UAC。
/// </summary>
public static class ServiceAdmin
{
    private const string ServiceExecutableName = "FileKeeper.Service.exe";

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static (bool Ok, string Output) Install() =>
        Run("--install", "安装", () => ServiceControl.IsInstalled());

    public static (bool Ok, string Output) Uninstall() =>
        Run("--uninstall", "卸载", () => !ServiceControl.IsInstalled());

    public static (bool Ok, string Output) Start() =>
        Run("--start", "启动", ServiceControl.IsRunning);

    public static (bool Ok, string Output) Stop() =>
        Run("--stop", "停止", ServiceControl.IsStopped);

    public static string? FindServiceExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (var level = 0; level < 6 && directory is not null; level++, directory = directory.Parent)
        {
            // 发布布局：App 与 Service 平级，或 Service 就在同目录
            var candidates = new[]
            {
                Path.Combine(directory.FullName, ServiceExecutableName),
                Path.Combine(directory.FullName, "Service", ServiceExecutableName)
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // 开发布局：上溯到 src 后，去 FileKeeper.Service 的 bin 里找
            if (directory.Name == "src")
            {
                var serviceProject = Path.Combine(directory.FullName, "FileKeeper.Service");
                if (Directory.Exists(serviceProject))
                {
                    var found = Directory
                        .GetFiles(serviceProject, ServiceExecutableName, SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTime)
                        .FirstOrDefault();

                    if (found is not null)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 拉起提权的 Service 进程执行操作，并用服务的真实状态判定结果。
    ///
    /// 提权子进程的标准输出无法重定向，所以不看它打印了什么，而是操作之后再查一次服务状态：
    /// UAC 被取消、sc.exe 部分失败等情况都会体现在最终状态里，比退出码可靠。
    /// </summary>
    private static (bool Ok, string Output) Run(string argument, string actionName, Func<bool> succeeded)
    {
        var executable = FindServiceExecutable();

        if (executable is null)
        {
            return (false, $"未找到 {ServiceExecutableName}，无法管理服务。请确认发布包完整。");
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            Arguments = argument,
            // 已提权时 runas 不会重复弹窗，因此两种情况下都统一走提权路径
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "无法启动提权进程");
            }

            process.WaitForExit();

            return succeeded()
                ? (true, $"服务已{actionName}")
                : (false, $"服务{actionName}未完成，可能是用户取消了授权或系统拒绝了操作");
        }
        catch (Win32Exception)
        {
            // 用户拒绝了 UAC 授权
            return (false, "未获得管理员权限授权，操作已取消");
        }
    }
}
