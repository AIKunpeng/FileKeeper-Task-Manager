using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using FileKeeper.Core.Configuration;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Services;

namespace FileKeeper.Setup;

/// <summary>
/// 安装程序入口。
///
/// 两种动作走同一份 exe：
/// <list type="bullet">
/// <item>直接运行或以 <c>/silent</c> 运行 → 安装</item>
/// <item>带 <c>/uninstall</c> 运行 → 卸载（卸载项里的 UninstallString 指向安装目录内的同一份副本）</item>
/// </list>
///
/// 流程刻意保持「先释放文件，再注册服务，最后写卸载项」这个次序：
/// 服务注册依赖安装目录里已经存在 Service.exe，卸载项又必须指向准备好的卸载器副本。
/// </summary>
public static class Program
{
    // 目录选择窗体基于 WinForms，而 FolderBrowserDialog 只允许在 STA 线程上弹出
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        // 显式切到 UTF-8：静默批量部署时 stdout 会被重定向进日志文件，
        // 沿用控制台的 GBK 代码页会让日志变成一堆问号，失去排查价值
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine();
        Console.WriteLine($"FileKeeper 安装程序 {VersionText}");
        Console.WriteLine(new string('-', 60));

        if (HasFlag(args, "?", "help", "h"))
        {
            PrintUsage();
            return 0;
        }

        var options = SetupOptions.Parse(args);

        if (!IsElevated())
        {
            Console.Error.WriteLine("需要管理员权限才能注册服务并写入 Program Files。");
            Console.Error.WriteLine("请右键选择「以管理员身份运行」，或在已提权的命令行中重新执行。");
            if (!options.Silent)
            {
                NativeMethods.ShowMessage(
                    "安装 FileKeeper 需要管理员权限。\n\n请右键选择「以管理员身份运行」后重试。",
                    "FileKeeper 安装程序");
            }

            return 1;
        }

        return options.Action switch
        {
            SetupAction.Uninstall => await UninstallAsync(options).ConfigureAwait(false),
            _ => await InstallAsync(options).ConfigureAwait(false)
        };
    }

    // ==================== 安装 ====================

    private static async Task<int> InstallAsync(SetupOptions options)
    {
        string root;
        string? logDirectory = options.LogDir;

        if (options.Action == SetupAction.Install && !options.Silent && options.InstallDir is null)
        {
            // 交互安装：弹出选择窗体让用户定路径。给了 /dir= 或 /silent 就不再打扰——
            // 批量部署通常一次发几十台，弹窗会让整条流水线卡在这里等一个永远不会有的点击
            var choice = InstallerForm.Prompt(
                ResolveInstallRoot(includeSelfDirectory: false),
                ReadConfiguredLogDirectory());

            if (choice is null)
            {
                Console.WriteLine("已取消安装。");
                return 1;
            }

            root = choice.InstallDirectory;
            logDirectory = choice.LogDirectory;
        }
        else
        {
            root = options.InstallDir ?? ResolveInstallRoot(includeSelfDirectory: false);
        }

        var invalid = InstallerForm.ValidateInstallDirectory(root);
        if (invalid is not null)
        {
            return Fail(options, invalid);
        }

        if (InstallerForm.ValidateLogDirectory(logDirectory) is { } logError)
        {
            return Fail(options, logError);
        }

        var layout = new InstallLayout(root);
        Console.WriteLine($"安装目录：{layout.Root}");

        // 1. 先把安装包自身复制到安装目录，卸载入口必须留在系统里，
        //    否则用户删掉下载的安装包后就再也卸载不掉
        if (!CopyUninstallerInto(layout))
        {
            return Fail(options, $"无法向安装目录写入卸载程序：{Path.GetDirectoryName(layout.UninstallerPath)}");
        }

        try
        {
            using var payload = PayloadExtractor.Extract();
            payload.Validate();
            Console.WriteLine($"载荷已释放到临时目录（{payload.ExtractDirectory}）");

            var orchestrator = new ServiceOrchestrator(layout, Report);

            // 2. 覆盖安装前必须让服务停下来：Service.exe 正被系统加载，
            //    不停服务直接复制会 IOException
            if (!await orchestrator.StopAsync().ConfigureAwait(false))
            {
                return Fail(options, "无法停止已运行的服务，请手动停止 FileKeeperService 后重试。");
            }

            var terminated = TerminateRunningApp();
            if (terminated > 0)
            {
                Console.WriteLine($"已结束正在运行的配置界面进程 {terminated} 个");
            }

            // 3. 释放文件，并清掉上次安装留下、本次已不带的文件
            Console.WriteLine("正在复制程序文件…");
            var released = FileInstaller.Install(payload.ExtractDirectory, layout);
            var stale = FileInstaller.RemoveStaleFiles(layout, released);
            FileInstaller.WriteManifest(layout, released);
            Console.WriteLine($"已释放 {released.Count} 个文件" + (stale > 0 ? $"，清理旧版残留 {stale} 个" : string.Empty));

            // 日志目录要在服务起来之前写进配置，否则服务的头几条日志会落在默认目录，
            // 之后界面读新目录就会凭空少一段启动记录
            if (!ConfigureLogDirectory(logDirectory, out var logPath))
            {
                return Fail(options, $"日志目录不可用：{logPath}");
            }

            // 4. 注册并启动服务
            if (!await orchestrator.EnsureInstalledAndRunningAsync().ConfigureAwait(false))
            {
                return Fail(options, $"服务未能启动（当前状态：{orchestrator.StatusText}）。详情见上方输出。");
            }

            // 5. 写卸载项与快捷方式
            new UninstallRegistry(layout, Report).Write(MeasureDirectory(layout.Root));
            new ShortcutService(layout, Report).Create();

            Console.WriteLine(new string('-', 60));
            Console.WriteLine("安装完成。");
            Console.WriteLine($"已注册「{ServiceControl.DisplayName}」为自动启动，并已开始运行。");
            Console.WriteLine($"配置界面快捷方式：桌面与开始菜单「{InstallLayout.ProductName}」");
            Console.WriteLine($"配置文件目录：{AppPaths.RootDirectory}");
            Console.WriteLine($"日志目录：{logPath}");

            if (!options.Silent)
            {
                NativeMethods.ShowMessage(
                    $"FileKeeper 已安装完成。\n\n"
                    + "后台服务已注册为自动启动并开始运行，\n"
                    + "可通过桌面或开始菜单快捷方式打开配置界面。",
                    "FileKeeper 安装完成");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"安装失败：{ex.Message}");
            return Fail(options, ex.Message);
        }
    }

    // ==================== 卸载 ====================

    private static async Task<int> UninstallAsync(SetupOptions options)
    {
        // 卸载必须反查真实安装位置：装到自定义目录后，默认路径里根本没有东西，
        // 沿用默认值会「卸载成功」却什么都没删，还会在系统里留下一个指向空目录的服务
        var layout = new InstallLayout(options.InstallDir ?? ResolveInstallRoot(includeSelfDirectory: true));
        Console.WriteLine($"卸载目录：{layout.Root}");

        var orchestrator = new ServiceOrchestrator(layout, Report);

        // 服务注销放在最前面：这一步失败意味着卸载后系统会留一个指向已删除目录的服务，
        // 比剩几个文件严重得多
        var serviceRemoved = await orchestrator.UnregisterAsync().ConfigureAwait(false);

        new ShortcutService(layout, Report).Remove();
        new UninstallRegistry(layout, Report).Remove();

        if (options.Purge)
        {
            PurgeDataDirectory();
        }

        var selfPath = Environment.ProcessPath;
        FileInstaller.RemoveDirectoryContents(layout, keepFilePath: selfPath);

        // 卸载器自己删不了自己：交给 cmd 在进程退出后延时清理
        ScheduleSelfRemoval(layout, selfPath);

        Console.WriteLine(new string('-', 60));

        if (!serviceRemoved)
        {
            Console.Error.WriteLine("服务未能成功注销，请手动执行 sc.exe 检查 FileKeeperService。");
        }

        var remaining = Directory.Exists(layout.Root)
            ? Directory.EnumerateFileSystemEntries(layout.Root).Count()
            : 0;

        Console.WriteLine(remaining == 0 ? "卸载完成。" : $"卸载完成，仍有 {remaining} 项待系统稍后清理。");

        if (!options.Purge)
        {
            Console.WriteLine($"已保留任务配置与日志：{InstallLayout.DataDirectory}（如需清除请重新卸载并加 /purge）");
        }

        if (!options.Silent)
        {
            NativeMethods.ShowMessage(
                options.Purge
                    ? "FileKeeper 已卸载，任务配置与日志已一并删除。"
                    : $"FileKeeper 已卸载。\n\n任务配置与日志仍保留在：\n{InstallLayout.DataDirectory}",
                "FileKeeper 卸载完成");
        }

        return serviceRemoved ? 0 : 1;
    }

    // ==================== 辅助 ====================

    /// <summary>
    /// 推断本次要作用的安装根目录，按「已登记 → 自身所在目录 → 默认」逐级回退：
    /// 安装路径既然可以自选，卸载与重复安装都不能再假定是默认的 Program Files，
    /// 而卸载项里的 InstallLocation 是上次安装亲手记下的答案，优先级自然最高。
    ///
    /// 「自身所在目录」这一级只在卸载时启用：卸载时运行的正是安装目录内的那份副本，
    /// 它自己站的位置就是答案，卸载项丢了或被清理过时靠它兜底。
    /// 安装时绝不能看这一级——从发布目录（dist）双击安装包时，dist 本身就摆着
    /// App / Service 的载荷布局，会被误判成「已安装在这里」，把服务注册到构建输出目录。
    /// </summary>
    private static string ResolveInstallRoot(bool includeSelfDirectory)
    {
        var candidates = new List<string?> { UninstallRegistry.ReadInstallLocation() };

        if (includeSelfDirectory)
        {
            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self))
            {
                candidates.Add(Path.GetDirectoryName(self));
            }
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            // 只有真的装着界面的才算安装过：卸载项可能残留于上次卸载失败的场景，
            // 拿空目录当根会让用户看到「卸载完成」却什么都没删
            if (File.Exists(Path.Combine(candidate, InstallLayout.AppSubDirectory, InstallLayout.AppExecutableName)))
            {
                return candidate;
            }
        }

        return InstallLayout.DefaultInstallRoot;
    }

    /// <summary>读取当前配置里的日志目录，用作安装窗体的默认值。</summary>
    private static string? ReadConfiguredLogDirectory()
    {
        try
        {
            using var store = new ConfigStore();
            store.Load();
            return store.Current.Settings.LogDirectory;
        }
        catch
        {
            // 配置文件损坏或尚不存在都不该拦住安装流程，取不到就交由调用方回落默认值
            return null;
        }
    }

    /// <summary>
    /// 把安装时指定的日志目录写进配置，返回最终生效的目录。
    /// 界面端与后台服务都从这份配置解析日志位置，因此这一步之后两边会同时改向。
    /// </summary>
    private static bool ConfigureLogDirectory(string? requested, out string effective)
    {
        effective = LogPaths.Resolve(null);

        try
        {
            using var store = new ConfigStore();
            store.Load();

            // requested 为 null 表示命令行压根没给这一项：配置必须原样保留，
            // 否则「省略沿用」会被当成「复位到默认」，把用户的自定义日志目录悄悄改掉
            if (requested is null)
            {
                effective = LogPaths.Resolve(store.Current.Settings.LogDirectory);
                return true;
            }

            // 给了等号却是空串，则是显式意图「回到默认位置」
            var target = requested.Trim() is { Length: > 0 } value ? value : null;

            if (target is not null)
            {
                Directory.CreateDirectory(target);
            }

            store.Update(config =>
            {
                // 关键：待迁移的来源是「解析后的旧目录」而不是配置原值。
                // 从未改过日志目录时配置值是 null（表示默认位置），历史日志其实躺在默认目录里，
                // 只看原值就会误判「没有旧目录」，历史日志从此再也没人搬。
                var previous = LogPaths.Resolve(config.Settings.LogDirectory);

                if (string.Equals(previous, LogPaths.Resolve(target), StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // 历史日志由后台服务（LocalSystem）搬过去：
                // 界面端对 %ProgramData% 下的旧目录未必有写权限，当场搬很可能失败
                config.Settings.PendingLogMigrationFrom = previous;
                config.Settings.LogDirectory = target;
            });

            effective = LogPaths.Resolve(store.Current.Settings.LogDirectory);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"设置日志目录失败：{ex.Message}");
            effective = string.IsNullOrWhiteSpace(requested) ? effective : requested;
            return false;
        }
    }

    /// <summary>把当前运行中的安装包复制到安装目录作为卸载入口</summary>
    private static bool CopyUninstallerInto(InstallLayout layout)
    {
        var source = Environment.ProcessPath;

        if (string.IsNullOrEmpty(source) || !File.Exists(source))
        {
            return false;
        }

        if (string.Equals(Path.GetFullPath(source), layout.UninstallerPath, StringComparison.OrdinalIgnoreCase))
        {
            // 从安装目录内运行（比如重复安装），无需复制
            Console.WriteLine("检测到正在从安装目录运行，跳过卸载程序复制");
            return true;
        }

        try
        {
            Directory.CreateDirectory(layout.Root);
            File.Copy(source, layout.UninstallerPath, overwrite: true);
            Console.WriteLine($"卸载程序已就位：{layout.UninstallerPath}");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"复制卸载程序失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 结束正在运行的配置界面进程。托盘常驻的界面会占着 App\FileKeeper.App.exe，
    /// 不结束会导致覆盖安装失败。
    /// </summary>
    private static int TerminateRunningApp()
    {
        var count = 0;

        foreach (var process in Process.GetProcessesByName("FileKeeper.App"))
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000);
                count++;
            }
            catch
            {
                // 进程可能已经退出，或被系统保护；交由后续的文件复制去暴露真实问题
            }
            finally
            {
                process.Dispose();
            }
        }

        return count;
    }

    /// <summary>计算已安装体积（卸载项里的 EstimatedSize 需要，单位为 KB）</summary>
    private static long MeasureDirectory(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    try
                    {
                        return new FileInfo(path).Length;
                    }
                    catch
                    {
                        return 0L;
                    }
                })
                .Sum();
        }
        catch
        {
            return 0;
        }
    }

    private static void PurgeDataDirectory()
    {
        try
        {
            if (Directory.Exists(InstallLayout.DataDirectory))
            {
                Directory.Delete(InstallLayout.DataDirectory, recursive: true);
                Console.WriteLine($"已删除数据目录：{InstallLayout.DataDirectory}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"删除数据目录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 延时自清理。卸载进程结束时 exe 仍在被系统占用，只能交给外部 shell 稍后再删。
    /// </summary>
    private static void ScheduleSelfRemoval(InstallLayout layout, string? selfPath)
    {
        if (string.IsNullOrEmpty(selfPath) || !Directory.Exists(layout.Root))
        {
            return;
        }

        try
        {
            // ping 作为跨 Windows 版本都可用的延时手段；先删文件再删目录
            var script = $"/c ping -n 3 127.0.0.1 > nul & del /f /q \"{selfPath}\" & rmdir /s /q \"{layout.Root}\"";

            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                Arguments = script,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"（无法自动清理残留目录，请手动删除：{layout.Root} —— {ex.Message}）");
        }
    }

    private static void Report(string message) => Console.WriteLine("  " + message);

    private static int Fail(SetupOptions options, string message)
    {
        Console.Error.WriteLine(new string('-', 60));
        Console.Error.WriteLine("安装未完成：" + message);

        if (!options.Silent)
        {
            NativeMethods.ShowMessage("安装未完成：\n\n" + message, "FileKeeper 安装程序");
        }

        return 1;
    }

    private static bool HasFlag(string[] args, params string[] names) =>
        args.Any(arg => names.Any(name =>
            string.Equals(arg.TrimStart('-', '/'), name, StringComparison.OrdinalIgnoreCase)));

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string VersionText
    {
        get
        {
            var version = typeof(Program).Assembly.GetName().Version;
            return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法：FileKeeper-Setup.exe [选项]");
        Console.WriteLine();
        Console.WriteLine("  /uninstall       卸载（先停服注销，再清除文件）");
        Console.WriteLine("  /silent          静默执行，不弹窗体与结果窗口");
        Console.WriteLine("  /purge           卸载时一并删除 %ProgramData%\\FileKeeper");
        Console.WriteLine("  /dir=\"路径\"      指定安装根目录（给了它就不会再弹选择窗体）");
        Console.WriteLine("  /logdir=\"路径\"   指定日志目录，省略则沿用配置，空则回到默认位置");
        Console.WriteLine("  /?               显示本帮助");
        Console.WriteLine();
        Console.WriteLine($"默认安装位置：{InstallLayout.DefaultInstallRoot}");
        Console.WriteLine($"默认日志位置：{AppPaths.LogsDirectory}");
        Console.WriteLine();
        Console.WriteLine("直接双击运行会弹出路径选择窗体；卸载按卸载项记录的 InstallLocation 定位。");
        Console.WriteLine();
        Console.WriteLine("示例：FileKeeper-Setup.exe /silent");
        Console.WriteLine("      FileKeeper-Setup.exe /silent /dir=\"D:\\FileKeeper\" /logdir=\"D:\\FileKeeperLogs\"");
        Console.WriteLine("      FileKeeper-Setup.exe /uninstall /purge /silent");
    }

    private static class NativeMethods
    {
        private const uint OkIcon = 0x00000040;

        private const uint ErrorIcon = 0x00000010;

        public static void ShowMessage(string text, string caption, bool isError = false)
        {
            MessageBox(IntPtr.Zero, text, caption, isError ? ErrorIcon : OkIcon);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
    }
}

/// <summary>安装程序动作</summary>
public enum SetupAction
{
    Install,
    Uninstall
}

/// <summary>
/// 命令行选项。参数解析刻意保持宽松：卸载项由系统拼字符串调用，
/// 大小写与多余空格都可能出现。
///
/// <para>
/// <c>InstallDir</c> 与 <c>LogDir</c> 为 null 表示没显式给出：
/// 前者交给交互窗体让用户选（静默时回退到已登记目录或默认位置），
/// 后者表示沿用配置里已有的日志目录。区分「没给」与「给了空串」很重要，
/// 空串在日志目录上是一个有效意图——回到默认位置。
/// </para>
/// </summary>
public sealed record SetupOptions(
    SetupAction Action,
    bool Silent,
    bool Purge,
    string? InstallDir = null,
    string? LogDir = null)
{
    public static SetupOptions Parse(string[] args)
    {
        var uninstall = false;
        var silent = false;
        var purge = false;
        string? installDir = null;
        string? logDir = null;

        foreach (var argument in args)
        {
            var value = argument.TrimStart('-', '/');

            if (TryReadAssignment(value, "dir=", ref installDir) ||
                TryReadAssignment(value, "logdir=", ref logDir))
            {
                continue;
            }

            switch (value.ToLowerInvariant())
            {
                case "uninstall":
                    uninstall = true;
                    break;
                case "silent":
                    silent = true;
                    break;
                case "purge":
                    purge = true;
                    break;
            }
        }

        return new SetupOptions(
            uninstall ? SetupAction.Uninstall : SetupAction.Install,
            silent,
            purge,
            installDir,
            logDir);
    }

    /// <summary>
    /// 解析 <c>key="值"</c> 形式的参数。取值失败时保留 null，交给调用方回退，
    /// 而不是塞一个空串进去冒充「已指定」。
    /// </summary>
    private static bool TryReadAssignment(string argument, string key, ref string? result)
    {
        if (!argument.StartsWith(key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 给了等号却没给值是一个明确意图——「回到默认」，不能当成「没给这一项」跳过，
        // 否则帮助里承诺的 /logdir="" 复位就无法实现
        result = argument[key.Length..].Trim().Trim('"').Trim();
        return true;
    }
}
