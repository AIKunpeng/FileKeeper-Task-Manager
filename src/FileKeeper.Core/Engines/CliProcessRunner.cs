using System.Diagnostics;
using System.Text;

namespace FileKeeper.Core.Engines;

/// <summary>
/// 命令行进程执行结果。
/// </summary>
/// <param name="ExitCode">进程退出码</param>
/// <param name="StandardOutput">标准输出</param>
/// <param name="StandardError">标准错误</param>
public sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// 统一的外部进程执行器：异步等待、超时可控、支持取消、参数通过 ArgumentList 传递以避免转义问题。
/// </summary>
internal static class CliProcessRunner
{
    public static async Task<CliResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        var error = new StringBuilder();

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                error.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        CancellationTokenSource? timeoutSource = null;
        CancellationTokenSource? linked = null;

        try
        {
            if (timeout.HasValue)
            {
                timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(timeout.Value);
            }

            linked = timeoutSource ?? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            // WaitForExitAsync 返回时异步输出事件处理器可能还没排空，
            // 补一次同步等待确保输出尾部（结果摘要）已收全
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            Terminate(process);

            if (timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"命令执行超时（{timeout}）：{Path.GetFileName(executable)}");
            }

            throw;
        }
        finally
        {
            timeoutSource?.Dispose();
            linked?.Dispose();
        }

        return new CliResult(process.ExitCode, output.ToString(), error.ToString());
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能已退出，忽略
        }
    }
}
