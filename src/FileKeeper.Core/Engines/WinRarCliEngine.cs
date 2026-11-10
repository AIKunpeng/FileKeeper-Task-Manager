using System.Text;
using FileKeeper.Core.Configuration;
using FileKeeper.Core.Models;

namespace FileKeeper.Core.Engines;

/// <summary>
/// 调用 WinRAR 命令行 Rar.exe 创建 RAR 压缩包。
///
/// RAR 是商业格式，7-Zip 与任何开源库都只能解压、无法创建，
/// 因此这是唯一能产出 RAR 的后端；未安装 WinRAR 时 <see cref="IsAvailable"/> 为 false，
/// 界面应据此把「RAR」选项置灰。
/// </summary>
public sealed class WinRarCliEngine : IArchiveEngine
{
    public string Name => "WinRAR 命令行";

    /// <summary>探测到的 Rar.exe / WinRAR.exe 路径</summary>
    public static ExternalTool? Tool => ExternalToolLocator.FindWinRar();

    public static bool IsAvailable => Tool is not null;

    public static string? UnavailableReason =>
        IsAvailable ? null : "未检测到 WinRAR，无法创建 RAR 压缩包（解压 RAR 不受影响）";

    public static WinRarCliEngine? TryCreate() => IsAvailable ? new WinRarCliEngine() : null;

    public bool CanCreate(ArchiveFormat format) => format == ArchiveFormat.Rar && IsAvailable;

    public bool CanExtract(ArchiveFormat format) => format == ArchiveFormat.Rar && IsAvailable;

    public async Task CompressAsync(CompressionRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var tool = Tool ?? throw new ArchiveEngineException(UnavailableReason ?? "未检测到 WinRAR");

        if (!CanCreate(request.Format))
        {
            throw new ArchiveEngineException($"WinRAR 后端无法创建 {request.Format} 格式");
        }

        var files = request.Files.Where(File.Exists).ToArray();
        if (files.Length == 0)
        {
            throw new ArchiveEngineException("没有可压缩的文件（文件列表为空或均不存在）");
        }

        var archiveDirectory = Path.GetDirectoryName(request.ArchivePath);
        if (!string.IsNullOrEmpty(archiveDirectory))
        {
            Directory.CreateDirectory(archiveDirectory);
        }

        var workingDirectory = ResolveWorkingDirectory(request.BaseDirectory, files);
        var listFile = await WriteListFileAsync(files, workingDirectory, cancellationToken).ConfigureAwait(false);

        try
        {
            var arguments = new List<string>
            {
                "a",
                // 不加 -ep* 开关：清单一律使用相对路径并配合工作目录，
                // RAR 会原样保留相对子目录；加 -ep1 反而会把子目录一起剥掉导致文件被拍平。
                $"-m{ToRarCompressionLevel(request.CompressionLevel)}",
                "-y",
                "-idq"                                   // 静默模式，服务环境下不弹窗
            };

            if (!string.IsNullOrEmpty(request.Password))
            {
                arguments.Add("-p" + request.Password);
            }

            if (request.VolumeSizeBytes is > 0)
            {
                arguments.Add("-v" + request.VolumeSizeBytes.Value);
            }

            arguments.Add(request.ArchivePath);
            arguments.Add("@" + listFile);

            var result = await CliProcessRunner.RunAsync(tool.ExecutablePath, arguments, workingDirectory, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // RAR 退出码：0 成功；1 警告；其余为致命错误
            if (result.ExitCode is not (0 or 1))
            {
                throw new ArchiveEngineException($"Rar.exe 退出码 {result.ExitCode}：{ExtractMeaningfulMessage(result)}");
            }

            progress?.Report(100);
        }
        finally
        {
            TryDelete(listFile);
        }
    }

    public async Task ExtractAsync(ExtractRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var tool = Tool ?? throw new ArchiveEngineException(UnavailableReason ?? "未检测到 WinRAR");

        Directory.CreateDirectory(request.OutputDirectory);

        var arguments = new List<string> { "x", "-y", "-idq", request.ArchivePath, request.OutputDirectory + Path.DirectorySeparatorChar };

        if (!string.IsNullOrEmpty(request.Password))
        {
            arguments.Add("-p" + request.Password);
        }

        var result = await CliProcessRunner.RunAsync(tool.ExecutablePath, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode is not (0 or 1))
        {
            throw new ArchiveEngineException($"Rar.exe 解压失败，退出码 {result.ExitCode}：{ExtractMeaningfulMessage(result)}");
        }

        progress?.Report(100);
    }

    /// <summary>把 0~9 的压缩级别映射到 RAR 的 -m0~-m5</summary>
    private static int ToRarCompressionLevel(int level) => level switch
    {
        <= 0 => 0,
        <= 2 => 1,
        <= 4 => 2,
        <= 6 => 3,
        <= 8 => 4,
        _ => 5
    };

    private static string ResolveWorkingDirectory(string? baseDirectory, string[] files)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory) && Directory.Exists(baseDirectory))
        {
            return baseDirectory;
        }

        return Path.GetDirectoryName(files[0]) ?? Directory.GetCurrentDirectory();
    }

    private static async Task<string> WriteListFileAsync(string[] files, string workingDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.TempDirectory);
        var listFile = Path.Combine(AppPaths.TempDirectory, $"rarlist-{Guid.NewGuid():N}.txt");

        var content = new StringBuilder();
        foreach (var file in files)
        {
            content.AppendLine(Path.GetRelativePath(workingDirectory, file));
        }

        await File.WriteAllTextAsync(listFile, content.ToString(), new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        return listFile;
    }

    private static string ExtractMeaningfulMessage(CliResult result)
    {
        var text = (result.StandardError + Environment.NewLine + result.StandardOutput).Trim();
        return string.IsNullOrWhiteSpace(text) ? "无详细输出" : text.Replace("\r", " ").Replace("\n", " ")[..Math.Min(300, text.Length)];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略
        }
    }
}
