using System.Diagnostics;
using System.Globalization;
using System.ServiceProcess;
using System.Text;
using FileKeeper.Core.Configuration;

namespace FileKeeper.Core.Services;

/// <summary>
/// Windows 服务的安装 / 卸载 / 启停封装。
///
/// 放在 Core 而非 Service 项目，是因为界面端（设置页）同样需要查询与操作服务状态，
/// 而界面端不应该为了一个安装方法去引用整个 Worker 宿主。
///
/// 说明：创建与删除服务没有托管 API，只能调用 <c>sc.exe</c>，且需要管理员权限。
/// 界面端负责以 runas 提权拉起本程序并传入 --install / --uninstall。
/// </summary>
public static class ServiceControl
{
    static ServiceControl()
    {
        // sc.exe 的控制台输出使用系统 OEM 代码页（中文环境为 GBK），
        // 不注册提供程序的话 GetEncoding 会抛异常，中文输出会变成乱码。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public const string ServiceName = "FileKeeperService";

    public const string DisplayName = "FileKeeper 任务管家服务";

    public const string Description = "定时压缩、清理本地文件，同步到 FTP / SFTP，并按计划执行脚本与程序。";

    /// <summary>服务是否已安装</summary>
    public static bool IsInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var controller = new ServiceController(ServiceName);
            _ = controller.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            // 服务不存在（或不可访问）时 ServiceController 会抛此异常
            return false;
        }
    }

    /// <summary>查询状态；未安装时返回 null</summary>
    public static ServiceControllerStatus? GetStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var controller = new ServiceController(ServiceName);
            return controller.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>服务是否正在运行。Core 面向 net8.0，必须做平台判断才能触碰 Windows 专有类型</summary>
    public static bool IsRunning() => OperatingSystem.IsWindows() && GetStatus() == ServiceControllerStatus.Running;

    public static bool IsStopped() => OperatingSystem.IsWindows() && GetStatus() == ServiceControllerStatus.Stopped;

    /// <summary>人可读的状态文本，供界面直接展示</summary>
    public static string GetStatusText()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "仅支持 Windows";
        }

        var status = GetStatus();

        return status switch
        {
            null => "未安装",
            ServiceControllerStatus.Running => "运行中",
            ServiceControllerStatus.Stopped => "已停止",
            ServiceControllerStatus.StartPending => "正在启动",
            ServiceControllerStatus.StopPending => "正在停止",
            ServiceControllerStatus.Paused => "已暂停",
            _ => "未知"
        };
    }

    /// <summary>
    /// 安装服务：自动启动 + 崩溃后自动重启。返回是否成功与命令行输出。
    /// </summary>
    public static (bool Ok, string Output) Install()
    {
        var exePath = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exePath))
        {
            return (false, "无法获取当前程序路径，安装中止");
        }

        var log = new List<string>();

        // binPath 必须带引号，否则路径中含空格时服务无法启动
        var create = RunSc($"create {ServiceName} binPath= \"\\\"{exePath}\\\"\" start= auto displayname= \"{DisplayName}\"");
        log.Add(create.Output);

        if (!create.Ok)
        {
            return (false, string.Join(Environment.NewLine, log));
        }

        // 数据目录由安装环节创建（此时已提权），运行时再建可能因权限不足而失败
        try
        {
            AppPaths.EnsureCreated();
            log.Add($"数据目录已创建：{AppPaths.RootDirectory}");
        }
        catch (Exception ex)
        {
            log.Add($"创建数据目录失败：{ex.Message}");
        }

        // 描述与失败恢复策略失败不影响安装本身
        var description = RunSc($"description {ServiceName} \"{Description}\"");
        log.Add(description.Output);

        // 崩溃后自动重启，共三次，间隔 5 / 10 / 30 秒；24 小时后重置计数
        var failure = RunSc($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");
        log.Add(failure.Output);

        return (true, string.Join(Environment.NewLine, log));
    }

    /// <summary>卸载服务（先停止再删除）</summary>
    public static (bool Ok, string Output) Uninstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, "仅支持 Windows");
        }

        var log = new List<string>();

        var status = GetStatus();
        if (status is not null and not ServiceControllerStatus.Stopped)
        {
            var stop = Stop();
            log.Add(stop.Output);
        }

        var delete = RunSc($"delete {ServiceName}");
        log.Add(delete.Output);

        return (delete.Ok, string.Join(Environment.NewLine, log));
    }

    public static (bool Ok, string Output) Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, "仅支持 Windows");
        }

        try
        {
            using var controller = new ServiceController(ServiceName);

            if (controller.Status == ServiceControllerStatus.Running)
            {
                return (true, "服务已在运行中");
            }

            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            return (true, "服务已启动");
        }
        catch (Exception ex)
        {
            return (false, $"启动服务失败：{ex.Message}");
        }
    }

    public static (bool Ok, string Output) Stop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, "仅支持 Windows");
        }

        try
        {
            using var controller = new ServiceController(ServiceName);

            if (controller.Status == ServiceControllerStatus.Stopped)
            {
                return (true, "服务已停止");
            }

            if (controller.CanStop)
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                return (true, "服务已停止");
            }

            return (false, "该服务不允许被停止");
        }
        catch (Exception ex)
        {
            return (false, $"停止服务失败：{ex.Message}");
        }
    }

    private static (bool Ok, string Output) RunSc(string arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, "仅支持 Windows");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = GetConsoleEncoding(),
                StandardErrorEncoding = GetConsoleEncoding()
            };

            // 必须整体赋值：binPath 的值本身带引号且路径含空格，
            // 按空格切分放进 ArgumentList 会被重新转义而破坏参数。
            startInfo.Arguments = arguments;
            startInfo.WorkingDirectory = Environment.SystemDirectory;

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, $"sc {arguments}：无法启动进程");
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode == 0, $"sc {arguments} → {output.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, $"sc {arguments} 执行失败：{ex.Message}");
        }
    }

    private static Encoding GetConsoleEncoding()
    {
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception)
        {
            return Encoding.UTF8;
        }
    }
}
