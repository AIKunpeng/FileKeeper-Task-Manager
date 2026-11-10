namespace FileKeeper.Setup;

/// <summary>
/// 文件释放、旧版清理与卸载删除。
///
/// 清单（install-manifest.txt）是整个类的核心：每次安装后记下本次释放的全部文件相对路径，
/// 下次升级时用它做差集，删掉「上次有、这次没有」的文件。
/// 没有它，跨版本升级会不断堆积过期 dll，直到某天加载到残缺的依赖组合才暴露问题。
/// </summary>
public sealed class FileInstaller
{
    /// <summary>
    /// 把载荷内容复制到安装目录，返回本次释放的文件相对路径清单（统一用 '/' 分隔）。
    /// </summary>
    public static IReadOnlyList<string> Install(string payloadRoot, InstallLayout layout)
    {
        Directory.CreateDirectory(layout.Root);

        var released = new List<string>();

        foreach (var file in Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories))
        {
            var relative = ToManifestPath(Path.GetRelativePath(payloadRoot, file));
            var target = FromManifestPath(layout.Root, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // 旧版本的 dll 可能带只读属性，直接覆盖会抛异常；先清掉再复制
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(target, FileAttributes.Normal);
            }

            File.Copy(file, target, overwrite: true);
            released.Add(relative);
        }

        return released;
    }

    /// <summary>
    /// 删除上一版遗留文件（清单里有、本次载荷里没有的），并返回删除数量。
    /// 删除后同步清理变空的目录，避免留下层层空的废目录。
    /// </summary>
    public static int RemoveStaleFiles(InstallLayout layout, IReadOnlyList<string> currentFiles)
    {
        var current = new HashSet<string>(currentFiles, StringComparer.OrdinalIgnoreCase);
        var removed = 0;

        foreach (var relative in layout.ReadManifest())
        {
            if (current.Contains(relative))
            {
                continue;
            }

            var target = FromManifestPath(layout.Root, relative);

            if (TryDeleteFile(target))
            {
                removed++;
            }
        }

        RemoveEmptyDirectories(layout.Root);
        return removed;
    }

    public static void WriteManifest(InstallLayout layout, IReadOnlyList<string> files)
    {
        File.WriteAllLines(layout.InstallManifestPath, files);
    }

    /// <summary>
    /// 删除安装目录内的全部内容，但可以保留一个文件。
    /// 卸载必须保留卸载器自身 —— 正在运行的 exe 无法删除自己，
    /// 这是 Windows 的既有约束，只能在进程退出后再清掉。
    /// </summary>
    public static void RemoveDirectoryContents(InstallLayout layout, string? keepFilePath = null)
    {
        if (!Directory.Exists(layout.Root))
        {
            return;
        }

        var keep = keepFilePath is null ? null : Path.GetFullPath(keepFilePath);

        foreach (var file in Directory.EnumerateFiles(layout.Root, "*", SearchOption.AllDirectories))
        {
            if (keep is not null && string.Equals(Path.GetFullPath(file), keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteFile(file);
        }

        RemoveEmptyDirectories(layout.Root);

        // 清掉根目录下的散装文件（清单文件等）
        foreach (var file in Directory.EnumerateFiles(layout.Root))
        {
            if (keep is not null && string.Equals(Path.GetFullPath(file), keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteFile(file);
        }
    }

    /// <summary>
    /// 单个文件删除失败不应中断整个流程：
    /// 少数文件残留不至于让卸载失败，最终由 self-check 报告出来。
    /// </summary>
    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        // 先按路径长度倒序，深层目录先尝试删除，空了才能连带上层一起删掉
        var directories = Directory
            .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length);

        foreach (var directory in directories)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
                // 删不掉就留着，不影响主流程
            }
        }
    }

    /// <summary>清单里统一用 '/'：与 zip 内条目一致，读写两侧不必各自处理平台分隔符</summary>
    private static string ToManifestPath(string relativePath) => relativePath.Replace('\\', '/');

    private static string FromManifestPath(string root, string manifestPath) =>
        Path.Combine(root, manifestPath.Replace('/', Path.DirectorySeparatorChar));
}
