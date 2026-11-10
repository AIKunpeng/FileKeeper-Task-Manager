using System.Text;
using FileKeeper.Core.Configuration;
using FileKeeper.Core.Models;

namespace FileKeeper.Core.Engines;

/// <summary>
/// 基于随包分发的 7z.exe 的命令行引擎。
///
/// 作为 <see cref="SevenZipEngine"/> 的补充：库后端的加密压缩无法保留目录结构，
/// 而命令行后端可以同时满足「加密 + 保留目录结构 + 分卷」，因此设置密码时优先走这里。
/// </summary>
public sealed class SevenZipCliEngine : IArchiveEngine
{
    private const string ListFileEncodingHint = "-scsUTF-8";

    public string Name => "内嵌 7-Zip 命令行";

    /// <summary>优先随包分发的 7z.exe，其次系统安装的 7-Zip</summary>
    public static string? ExecutablePath { get; } = ResolveExecutable();

    public static bool IsAvailable => ExecutablePath is not null;

    public bool CanCreate(ArchiveFormat format) => format == ArchiveFormat.Zip;

    public bool CanExtract(ArchiveFormat format) => format is ArchiveFormat.Zip or ArchiveFormat.Rar;

    public static SevenZipCliEngine? TryCreate() => IsAvailable ? new SevenZipCliEngine() : null;

    public async Task CompressAsync(CompressionRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (ExecutablePath is null)
        {
            throw new ArchiveEngineException("未找到 7z.exe（内嵌 7-Zip 命令行缺失且系统未安装 7-Zip）");
        }

        if (!CanCreate(request.Format))
        {
            throw new ArchiveEngineException($"7-Zip 命令行无法创建 {request.Format} 格式（RAR 需使用 WinRAR 后端）");
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
                "-t" + (request.Format == ArchiveFormat.Zip ? "zip" : "7z"),
                $"-mx={Math.Clamp(request.CompressionLevel, 0, 9)}",
                ListFileEncodingHint,
                "-y",
                "-bso0",
                "-bsp0"
            };

            if (!string.IsNullOrEmpty(request.Password))
            {
                arguments.Add("-p" + request.Password);

                // ZIP 默认使用较弱的 ZipCrypto，显式指定 AES-256 以兼容 WinRAR / 7-Zip
                if (request.Format == ArchiveFormat.Zip)
                {
                    arguments.Add("-mem=AES256");
                }
            }

            if (request.VolumeSizeBytes is > 0)
            {
                arguments.Add("-v" + request.VolumeSizeBytes.Value);
            }

            arguments.Add(request.ArchivePath);
            arguments.Add("@" + listFile);

            var result = await CliProcessRunner.RunAsync(ExecutablePath, arguments, workingDirectory, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // 0 = 成功；1 = 有警告（例如部分文件被占用），视为成功但交由上层记录
            if (result.ExitCode is not (0 or 1))
            {
                throw new ArchiveEngineException(
                    $"7z.exe 退出码 {result.ExitCode}：{ExtractMeaningfulMessage(result)}");
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
        if (ExecutablePath is null)
        {
            throw new ArchiveEngineException("未找到 7z.exe（内嵌 7-Zip 命令行缺失且系统未安装 7-Zip）");
        }

        Directory.CreateDirectory(request.OutputDirectory);

        var arguments = new List<string> { "x", request.ArchivePath, "-o" + request.OutputDirectory, ListFileEncodingHint, "-y", "-bso0", "-bsp0" };

        if (!string.IsNullOrEmpty(request.Password))
        {
            arguments.Add("-p" + request.Password);
        }

        var result = await CliProcessRunner.RunAsync(ExecutablePath, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode is not (0 or 1))
        {
            throw new ArchiveEngineException($"7z.exe 解压失败，退出码 {result.ExitCode}：{ExtractMeaningfulMessage(result)}");
        }

        progress?.Report(100);
    }

    private static string ResolveWorkingDirectory(string? baseDirectory, string[] files)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory) && Directory.Exists(baseDirectory))
        {
            return baseDirectory;
        }

        return Path.GetDirectoryName(files[0]) ?? Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// 生成相对路径清单文件，配合工作目录使用，保证包内目录结构正确。
    /// </summary>
    private static async Task<string> WriteListFileAsync(string[] files, string workingDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.TempDirectory);
        var listFile = Path.Combine(AppPaths.TempDirectory, $"filelist-{Guid.NewGuid():N}.txt");

        var content = new StringBuilder();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(workingDirectory, file);
            content.AppendLine(relative);
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
            // 清理清单文件失败可忽略
        }
    }

    private static string? ResolveExecutable()
    {
        var baseDirectory = AppContext.BaseDirectory;

        string[] candidates =
        {
            Path.Combine(baseDirectory, "native", "7z.exe"),
            Path.Combine(baseDirectory, "native", "win-x64", "7z.exe"),
            Path.Combine(baseDirectory, "7z.exe")
        };

        foreach (var candidate in candidates)
        {
            // 7z.exe 必须与 7z.dll 同目录，缺少 dll 时不可用
            if (File.Exists(candidate) && File.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, "7z.dll")))
            {
                return candidate;
            }
        }

        return ExternalToolLocator.Find7Zip()?.ExecutablePath;
    }
}
