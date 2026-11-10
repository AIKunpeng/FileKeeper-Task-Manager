using SevenZip;
using FileKeeper.Core.Configuration;
using FileKeeper.Core.Models;

namespace FileKeeper.Core.Engines;

/// <summary>
/// 基于随包分发的 7z.dll 的内嵌压缩引擎（SevenZipSharp 封装）。
///
/// 能力边界：可创建 ZIP、可解压 ZIP 与 RAR。
/// 7-Zip 的 OutArchiveFormat 中不存在 Rar —— RAR 是商业格式，任何开源实现都无法创建，
/// 创建 RAR 必须走 <see cref="WinRarCliEngine"/>。
/// </summary>
public sealed class SevenZipEngine : IArchiveEngine
{
    private static readonly object InitLock = new();
    private static bool _initialized;
    private static string? _initError;

    public string Name => "内嵌 7-Zip";

    /// <summary>随包分发的 7z.dll 路径，未找到时为 null</summary>
    public static string? LibraryPath { get; } = ResolveLibraryPath();

    public static bool IsAvailable => LibraryPath is not null && EnsureInitialized() is null;

    public static string? InitializationError =>
        LibraryPath is null ? "未找到随包分发的内嵌 7z.dll" : EnsureInitialized();

    public bool CanCreate(ArchiveFormat format) => format == ArchiveFormat.Zip;

    public bool CanExtract(ArchiveFormat format) => format is ArchiveFormat.Zip or ArchiveFormat.Rar;

    public Task CompressAsync(CompressionRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var error = InitializationError;
        if (error is not null)
        {
            throw new ArchiveEngineException($"内嵌 7-Zip 引擎不可用：{error}");
        }

        if (!CanCreate(request.Format))
        {
            throw new ArchiveEngineException($"内嵌 7-Zip 引擎无法创建 {request.Format} 格式（RAR 需使用 WinRAR 后端）");
        }

        if (!string.IsNullOrEmpty(request.Password))
        {
            throw new ArchiveEngineException("内嵌 7-Zip 引擎的加密压缩不支持保留目录结构，请使用命令行后端");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // SevenZipSharp 的 Async 方法是事件驱动（EAP）而非 Task，这里放到后台线程执行
        return Task.Run(() => CompressCore(request, progress), cancellationToken);
    }

    public Task ExtractAsync(ExtractRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var error = InitializationError;
        if (error is not null)
        {
            throw new ArchiveEngineException($"内嵌 7-Zip 引擎不可用：{error}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() => ExtractCore(request, progress), cancellationToken);
    }

    private static void CompressCore(CompressionRequest request, IProgress<double>? progress)
    {
        var archivePath = request.ArchivePath;
        var directory = Path.GetDirectoryName(archivePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var files = request.Files.Where(File.Exists).ToArray();
        if (files.Length == 0)
        {
            throw new ArchiveEngineException("没有可压缩的文件（文件列表为空或均不存在）");
        }

        Directory.CreateDirectory(AppPaths.TempDirectory);

        var compressor = new SevenZipCompressor
        {
            ArchiveFormat = ToOutFormat(request.Format),
            CompressionLevel = ToCompressionLevel(request.CompressionLevel),
            DirectoryStructure = true,
            IncludeEmptyDirectories = false,
            PreserveDirectoryRoot = false,
            TempFolderPath = AppPaths.TempDirectory
        };

        if (request.VolumeSizeBytes is > 0)
        {
            compressor.VolumeSize = request.VolumeSizeBytes.Value;
        }

        if (progress is not null)
        {
            compressor.Compressing += (_, e) => progress.Report(e.PercentDone);
        }

        var commonRootLength = ComputeCommonRootLength(request.BaseDirectory, files);
        compressor.CompressFiles(archivePath, commonRootLength, files);
    }

    private static void ExtractCore(ExtractRequest request, IProgress<double>? progress)
    {
        Directory.CreateDirectory(request.OutputDirectory);

        using var extractor = string.IsNullOrEmpty(request.Password)
            ? new SevenZipExtractor(request.ArchivePath)
            : new SevenZipExtractor(request.ArchivePath, request.Password);

        if (progress is not null)
        {
            extractor.Extracting += (_, e) => progress.Report(e.PercentDone);
        }

        extractor.ExtractArchive(request.OutputDirectory);
    }

    /// <summary>
    /// 计算需要从文件路径中剥离的公共前缀长度，从而在包内保留相对目录结构。
    /// </summary>
    private static int ComputeCommonRootLength(string? baseDirectory, string[] files)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return 0;
        }

        var root = baseDirectory.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;

        // 只有所有文件都位于该目录下时才剥离，避免出现半截路径
        return files.All(f => f.StartsWith(root, StringComparison.OrdinalIgnoreCase)) ? root.Length : 0;
    }

    private static OutArchiveFormat ToOutFormat(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => OutArchiveFormat.Zip,
        _ => throw new ArchiveEngineException($"内嵌 7-Zip 引擎不支持创建 {format} 格式")
    };

    private static CompressionLevel ToCompressionLevel(int level) => level switch
    {
        <= 0 => CompressionLevel.None,
        <= 2 => CompressionLevel.Fast,
        <= 4 => CompressionLevel.Low,
        <= 6 => CompressionLevel.Normal,
        <= 8 => CompressionLevel.High,
        _ => CompressionLevel.Ultra
    };

    private static string? EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initialized)
            {
                return _initError;
            }

            try
            {
                SevenZipBase.SetLibraryPath(LibraryPath!);

                // 读取库特性会强制加载原生库，可在此暴露位数不匹配等问题
                _ = SevenZipBase.CurrentLibraryFeatures;
                _initError = null;
            }
            catch (Exception ex)
            {
                _initError = ex.Message;
            }

            _initialized = true;
            return _initError;
        }
    }

    private static string? ResolveLibraryPath()
    {
        var baseDirectory = AppContext.BaseDirectory;

        string[] candidates =
        {
            Path.Combine(baseDirectory, "native", "7z.dll"),
            Path.Combine(baseDirectory, "native", "win-x64", "7z.dll"),
            Path.Combine(baseDirectory, "runtimes", "win-x64", "native", "7z.dll"),
            Path.Combine(baseDirectory, "7z.dll")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 开发期兜底：从程序目录向上回溯到仓库，直接引用 Native 目录
        var directory = new DirectoryInfo(baseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        while (directory is not null)
        {
            var developerCopy = Path.Combine(directory.FullName, "src", "FileKeeper.Core", "Native", "win-x64", "7z.dll");
            if (File.Exists(developerCopy))
            {
                return developerCopy;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
