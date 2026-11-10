using System.Diagnostics;
using System.Globalization;
using System.Text;
using FileKeeper.Core.Services;

namespace FileKeeper.Setup;

/// <summary>
/// 后台服务的安装、启动、停止与卸载编排。
///
/// 全部操作一律以子进程方式调用 <c>FileKeeper.Service.exe --install/--start/--stop/--uninstall</c>，
/// <b>禁止</b>直接调用 <see cref="ServiceControl.Install()"/>：
/// ServiceControl 拿 <see cref="Environment.ProcessPath"/> 当服务 binPath，
/// 在安装器进程里调用会把安装器自身注册成 Windows 服务 —— 界面端曾踩过这个坑。
///
/// 结果判定同样遵循项目的既定约定：不看子进程退出码，而是操作后再复查服务真实状态，
/// 这样 UAC 取消、sc.exe 部分失败都能如实反映出来。
/// </summary>
public sealed class ServiceOrchestrator
{
    private readonly InstallLayout _layout;
    private readonly Action<string> _report;

    public ServiceOrchestrator(InstallLayout layout, Action<string>? report = null)
    {
        _layout = layout;
        _report = report ?? (_ => { });
    }

    static ServiceOrchestrator()
    {
        // sc.exe 的输出走系统 OEM 代码页（中文环境为 GBK）。
        // .NET Core 默认不内置这些代码页，不注册的话 GetEncoding 会抛异常。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public bool IsInstalled => ServiceControl.IsInstalled();

    public bool IsRunning => ServiceControl.IsRunning();

    public string StatusText => ServiceControl.GetStatusText();

    /// <summary>
    /// 让服务处于「已注册且正在运行」的状态。
    ///
    /// 已存在时也一律先卸载再重建，而不是跳过：服务里记着旧的 binPath，
    /// 用户改了安装目录后不重建的话，新目录的文件和已注册路径会对不上。
    /// 反正覆盖安装本来就要求先停服释放文件占用，这里多一次重建不额外增加代价。
    /// </summary>
    public async Task<bool> EnsureInstalledAndRunningAsync(CancellationToken cancellationToken = default)
    {
        if (IsInstalled && !await UnregisterAsync(cancellationToken).ConfigureAwait(false))
        {
            _report("旧服务卸载失败，无法继续安装");
            return false;
        }

        if (!File.Exists(_layout.ServiceExecutablePath))
        {
            _report($"服务宿主不存在：{_layout.ServiceExecutablePath}");
            return false;
        }

        var (ok, output) = await RunAsync("--install", cancellationToken).ConfigureAwait(false);
        _report(output);

        if (!ok || !IsInstalled)
        {
            _report("服务注册失败");
            return false;
        }

        _report("服务已注册（自动启动）");
        return await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>启动服务；已在运行时视为成功</summary>
    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            _report("服务已在运行");
            return true;
        }

        var (ok, output) = await RunAsync("--start", cancellationToken).ConfigureAwait(false);
        _report(output);

        if (!ok || !IsRunning)
        {
            _report("服务启动失败");
            return false;
        }

        _report("服务已启动");
        return true;
    }

    /// <summary>
    /// 停止服务（覆盖安装前必须调用，否则服务 exe 被占用，复制文件会 IOException）。
    /// 未安装或已停止都视为成功。
    /// </summary>
    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        if (!ServiceControl.IsInstalled())
        {
            return true;
        }

        if (ServiceControl.IsStopped())
        {
            _report("服务已处于停止状态");
            return true;
        }

        // 目标目录里还没有服务宿主——典型场景是装到一个全新的自定义目录：
        // 这一步跑在释放文件之前，此时根本没有可拉起的子进程。
        // 但同名服务可能正占着旧位置，不停掉过一会儿重新注册就会被挡住，
        // 因此退而直接用 sc.exe 按服务名停止。
        if (!File.Exists(_layout.ServiceExecutablePath))
        {
            _report($"目标目录尚无服务宿主，改用 sc.exe 停止已存在的 {ServiceControl.ServiceName}");
            return await StopByScAsync(cancellationToken).ConfigureAwait(false);
        }

        var (ok, output) = await RunAsync("--stop", cancellationToken).ConfigureAwait(false);
        _report(output);

        if (!ok || !ServiceControl.IsStopped())
        {
            _report("服务停止失败，无法覆盖正在使用的文件");
            return false;
        }

        _report("服务已停止");
        return true;
    }

    /// <summary>
    /// 直接用 <c>sc.exe stop</c> 停掉同名服务。只在目标目录还没有服务宿主时使用：
    /// 那说明这次不是覆盖升级，而是一次新的落点安装，手上没有可执行的宿主。
    /// </summary>
    private async Task<bool> StopByScAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await RunScAsync("stop", cancellationToken).ConfigureAwait(false);
            _report(output is { Length: > 0 } text ? text : "（sc.exe 无输出）");
        }
        catch (Exception ex)
        {
            _report($"sc.exe 停止服务失败：{ex.Message}");
            return false;
        }

        // sc stop 是异步的：不等一会儿读到的还是「正在停止」
        for (var i = 0; i < 30 && !ServiceControl.IsStopped(); i++)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        if (ServiceControl.IsStopped())
        {
            _report("服务已停止");
            return true;
        }

        _report("服务停止失败，无法覆盖正在使用的文件");
        return false;
    }

    /// <summary>注销服务（内部会先停止再删除）。未安装时视为成功</summary>
    public async Task<bool> UnregisterAsync(CancellationToken cancellationToken = default)
    {
        if (!ServiceControl.IsInstalled())
        {
            _report("服务未安装，跳过注销");
            return true;
        }

        // 同上：目标目录里已无服务宿主时（目录被人为清空、或卸载指定到了别的落点），
        // 只能按服务名清爽掉，否则会在系统里留下一个指向已不存在文件的服务
        if (!File.Exists(_layout.ServiceExecutablePath))
        {
            _report($"目标目录已无服务宿主，改用 sc.exe 删除已存在的 {ServiceControl.ServiceName}");

            await RunScAsync("stop", cancellationToken).ConfigureAwait(false);
            await RunScAsync("delete", cancellationToken).ConfigureAwait(false);

            return !ServiceControl.IsInstalled();
        }

        var (ok, output) = await RunAsync("--uninstall", cancellationToken).ConfigureAwait(false);
        _report(output);

        if (!ok || ServiceControl.IsInstalled())
        {
            _report("服务注销失败");
            return false;
        }

        _report("服务已注销");
        return true;
    }

    /// <summary>
    /// 执行 <c>sc.exe &lt;命令&gt; FileKeeperService</c> 并返回输出。
    /// 在「安装目录里已经没有可用的服务宿主」这条退路上使用。
    /// </summary>
    private static async Task<string?> RunScAsync(string command, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("sc.exe")
        {
            Arguments = $"{command} {ServiceControl.ServiceName}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding
        };

        using var process = Process.Start(startInfo);

        if (process is null)
        {
            throw new InvalidOperationException("无法启动 sc.exe");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var combined = (stdout + stderr).Trim();
        return combined.Length == 0 ? null : combined;
    }

    /// <summary>子进程输出的编码。取不到系统代码页时退回 UTF-8，宁可少几个字也不要崩在签名或读取上</summary>
    private static Encoding ConsoleEncoding
    {
        get
        {
            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch
            {
                return Encoding.UTF8;
            }
        }
    }

    private async Task<(bool Ok, string Output)> RunAsync(string argument, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_layout.ServiceExecutablePath)
        {
            Arguments = argument,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // 子进程（乃至它调用的 sc.exe）输出是系统 OEM 代码页的字节。
        // 注意不能用 Encoding.Default：在 .NET Core 里它恒为 UTF-8，
        // 拿它解 GBK 字节会得到一堆问号。
        startInfo.StandardOutputEncoding = ConsoleEncoding;
        startInfo.StandardErrorEncoding = ConsoleEncoding;

        using var process = Process.Start(startInfo);

        if (process is null)
        {
            return (false, $"无法启动 {_layout.ServiceExecutablePath}");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var output = (stdout + stderr).Trim();
        return (process.ExitCode == 0, string.IsNullOrWhiteSpace(output) ? "（服务宿主无输出）" : output);
    }
}
