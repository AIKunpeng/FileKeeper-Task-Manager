using System.Diagnostics;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;

namespace FileKeeper.Core.Execution;

/// <summary>
/// 程序任务执行器：启动子进程、等待退出、捕获输出、超时强杀。
///
/// 三条设计约定：
/// 1. <b>必须有超时</b>：定时任务挂在永不退出的脚本上是调度系统最危险的形态，
///    到时未退出的一律 kill 并判定失败，而不是无限等待；
/// 2. <b>输出只留尾部</b>：脚本的完整输出可能非常大，日志里只保留最后若干行，
///    足够定位「为什么失败」，又不至于把日志撑爆；
/// 3. <b>退出码即成败</b>：0 视为成功，非 0 判定失败并在错误信息里带上退出码，
///    不猜测各脚本自己的错误码语义。
/// </summary>
public sealed class ProgramRunner
{
    /// <summary>写入结果明细的输出尾部行数上限</summary>
    private const int OutputTailLines = 20;

    private readonly IAppLogger _logger;

    public ProgramRunner(IAppLogger logger)
    {
        _logger = logger;
    }

    public async Task<TaskRunResult> RunAsync(
        ProgramTask task,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = TaskRunResult.Create(task, TaskKind.Program);

        try
        {
            task.Normalize();
            cancellationToken.ThrowIfCancellationRequested();

            var configurationError = task.ValidateConfiguration();
            if (configurationError is not null)
            {
                return Fail(result, configurationError);
            }

            var startInfo = BuildStartInfo(task);
            _logger.Info($"开始执行：{startInfo.FileName} {startInfo.Arguments}", task.Name);

            using var process = new Process { StartInfo = startInfo };

            if (!process.Start())
            {
                return Fail(result, "进程启动失败：系统拒绝创建进程");
            }

            // 图形界面程序走「启动即完成」：不等待、不捕获输出、不强杀，
            // 只观察一小段时间确认没有秒退（路径坏、缺依赖会在这两秒内暴露）
            if (!task.WaitForExit)
            {
                return await RunAndForgetAsync(task, process, result).ConfigureAwait(false);
            }

            // 输出读取必须先于 WaitForExit 启动：等进程退出再读管道，
            // 缓冲区填满后子进程会卡在写入上，双向死锁
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(task.TimeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 只有超时分支会走到这里：正常取消由外层 catch 处理
                Kill(process);
                return Fail(result, $"执行超时（{task.TimeoutSeconds} 秒），已强制结束进程");
            }

            var stdout = await SafeAwait(outputTask).ConfigureAwait(false);
            var stderr = await SafeAwait(errorTask).ConfigureAwait(false);
            var exitCode = process.ExitCode;

            result.ExitCode = exitCode;
            AppendOutputTail(result, stdout, stderr, exitCode);

            if (exitCode == 0)
            {
                result.SuccessCount = 1;
                result.Complete();
                _logger.TaskResult(result);
                return result;
            }

            return Fail(result, $"程序退出码 {exitCode}（非 0 视为失败），详见日志明细");
        }
        catch (OperationCanceledException)
        {
            result.Skip("任务已取消（服务停止或手动中断）");
            _logger.Warn("任务已取消", task.Name);
            _logger.TaskResult(result);
            return result;
        }
        catch (Exception ex)
        {
            return Fail(result, $"执行失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 启动后短暂观察：两秒内仍在运行即判定成功并返回（进程留给系统，本任务不再跟踪）；
    /// 秒退则按退出码判定成败——能让 GUI 程序「开了就算成功」，同时不至于把
    /// 配置错误（文件损坏、缺依赖）也当成成功。
    /// 不重定向输出：不读管道就不会有缓冲区死锁，GUI 程序的控制台输出本来也无处可看。
    /// </summary>
    private async Task<TaskRunResult> RunAndForgetAsync(ProgramTask task, Process process, TaskRunResult result)
    {
        const int graceSeconds = 2;

        try
        {
            // 启动信息固定重定向了标准输出/错误，必须持续排空管道，
            // 否则输出一多子进程会卡在写管道上，永远退不出来
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await Task.Delay(TimeSpan.FromSeconds(graceSeconds)).ConfigureAwait(false);

            if (process.HasExited)
            {
                result.ExitCode = process.ExitCode;

                if (process.ExitCode == 0)
                {
                    result.AddDetail($"程序在 {graceSeconds} 秒内自行退出（退出码 0）");
                    result.SuccessCount = 1;
                    result.Complete();
                    _logger.TaskResult(result);
                    return result;
                }

                return Fail(result, $"程序启动后立即退出，退出码 {process.ExitCode}（非 0 视为失败）");
            }

            result.AddDetail("程序已启动并保持运行，本任务不等待其退出（启动即完成模式）");
            result.SuccessCount = 1;
            result.Complete();
            _logger.TaskResult(result);
            return result;
        }
        catch (Exception ex)
        {
            return Fail(result, $"执行失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 按扩展名决定启动方式：bat/cmd 交给 cmd.exe，ps1 交给 PowerShell，
    /// 其余直接启动。三者统一关闭 shell 执行，避免弹出无关窗口与提权提示。
    /// </summary>
    private static ProcessStartInfo BuildStartInfo(ProgramTask task)
    {
        var extension = Path.GetExtension(task.FilePath).ToLowerInvariant();
        var workingDirectory = task.WorkingDirectory
                               ?? Path.GetDirectoryName(task.FilePath)
                               ?? Environment.CurrentDirectory;

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = task.WindowMode == ProcessWindow.Hidden,
            WindowStyle = task.WindowMode switch
            {
                ProcessWindow.Minimized => ProcessWindowStyle.Minimized,
                ProcessWindow.Normal => ProcessWindowStyle.Normal,
                _ => ProcessWindowStyle.Hidden
            },
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Console.OutputEncoding,
            StandardErrorEncoding = Console.OutputEncoding
        };

        switch (extension)
        {
            case ".bat" or ".cmd":
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = $"/c \"{task.FilePath}\" {task.Arguments}";
                break;

            case ".ps1":
                startInfo.FileName = "powershell.exe";
                startInfo.Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{task.FilePath}\" {task.Arguments}";
                break;

            default:
                startInfo.FileName = task.FilePath;
                startInfo.Arguments = task.Arguments;
                break;
        }

        return startInfo;
    }

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // 进程被 kill 时管道读取可能抛异常，输出拿不到就算了，成败由退出码裁决
            return string.Empty;
        }
    }

    private static void AppendOutputTail(TaskRunResult result, string stdout, string stderr, int exitCode)
    {
        var lines = (stdout + "\n" + stderr)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToList();

        result.AddDetail($"退出码 {exitCode}，输出 {lines.Count} 行");

        foreach (var line in lines.TakeLast(OutputTailLines))
        {
            result.AddDetail(line);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程已自行退出或权限不足，无需处理
        }
    }

    private TaskRunResult Fail(TaskRunResult result, string message, Exception? exception = null)
    {
        result.Fail(message);
        _logger.Error(message, result.TaskName, exception);
        _logger.TaskResult(result);
        return result;
    }
}
