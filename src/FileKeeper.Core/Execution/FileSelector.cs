using FileKeeper.Core.Models;

namespace FileKeeper.Core.Execution;

/// <summary>
/// 文件枚举与筛选。
///
/// 设计要点：
/// - 惰性枚举 + 手动递归，以便在递归前剪掉排除目录（避免进入体积巨大的无关目录树）；
/// - 跳过重解析点（符号链接 / 目录联接），避免递归时形成环；
/// - <c>IgnoreInaccessible</c> 与逐层 try/catch，保证单个无权限目录不会中断整个任务；
/// - 跳过系统文件，隐藏文件仍然包含（备份场景漏掉隐藏文件的代价比多备份几个文件更高）。
/// </summary>
public static class FileSelector
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
    };

    /// <summary>
    /// 枚举目录下符合过滤条件的文件，返回完整路径（已排序）。
    /// </summary>
    public static IReadOnlyList<string> SelectFiles(
        string directory,
        bool includeSubDirectories,
        FileFilterMode filterMode,
        IReadOnlyList<string>? extensions = null,
        IReadOnlyList<string>? excludedDirectories = null,
        CancellationToken cancellationToken = default)
    {
        var files = new List<string>();

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return files;
        }

        var excluded = BuildExclusionSet(excludedDirectories);
        var extensionSet = filterMode == FileFilterMode.IncludeExtensions
            ? new HashSet<string>(extensions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase)
            : null;

        Walk(directory, includeSubDirectories, extensionSet, excluded, files, cancellationToken);

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>
    /// 文件是否可被独占打开。用于压缩前跳过被其他进程占用的文件，
    /// 避免压缩到一半失败导致整个任务失败。
    /// </summary>
    public static bool IsAccessible(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>按扩展名集合判断文件是否命中（扩展名须为 ".log" 形式）</summary>
    public static bool MatchesExtensions(string path, IReadOnlyList<string> extensions)
    {
        if (extensions.Count == 0)
        {
            return true;
        }

        var extension = Path.GetExtension(path);
        return extension.Length > 0 && extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断 <paramref name="file"/> 是否位于 <paramref name="directory"/> 之下。
    /// 删除前做二次校验，防止路径异常导致误删目录外的文件。
    /// </summary>
    public static bool IsUnderDirectory(string file, string directory)
    {
        try
        {
            var fullFile = Path.GetFullPath(file);
            var fullDirectory = EnsureTrailingSeparator(Path.GetFullPath(directory));

            return fullFile.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void Walk(
        string directory,
        bool recurse,
        HashSet<string>? extensionSet,
        HashSet<string> excluded,
        List<string> result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", Options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (extensionSet is null || MatchesExtensionFast(file, extensionSet))
                {
                    result.Add(file);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // 单个目录不可访问不应中断整个任务
            _ = ex;
        }

        if (!recurse)
        {
            return;
        }

        List<string> subDirectories;
        try
        {
            subDirectories = new List<string>(Directory.EnumerateDirectories(directory, "*", Options));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return;
        }

        foreach (var subDirectory in subDirectories)
        {
            if (IsExcluded(subDirectory, excluded))
            {
                continue;
            }

            Walk(subDirectory, true, extensionSet, excluded, result, cancellationToken);
        }
    }

    private static bool MatchesExtensionFast(string path, HashSet<string> extensionSet)
    {
        var extension = Path.GetExtension(path);
        return extension.Length > 0 && extensionSet.Contains(extension);
    }

    private static HashSet<string> BuildExclusionSet(IReadOnlyList<string>? excludedDirectories)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (excludedDirectories is null)
        {
            return set;
        }

        foreach (var item in excludedDirectories)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            try
            {
                set.Add(Path.GetFullPath(item).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch
            {
                // 非法路径直接忽略
            }
        }

        return set;
    }

    private static bool IsExcluded(string directory, HashSet<string> excluded)
    {
        if (excluded.Count == 0)
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        return excluded.Contains(full);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}
