using System.IO.Compression;
using System.Reflection;

namespace FileKeeper.Setup;

/// <summary>
/// 从程序自身的嵌入资源里取出程序载荷并解压到临时目录。
///
/// 载荷是构建期由 <c>scripts\publish.ps1</c> 打好的 zip，内含 <c>App/</c>、<c>Service/</c>
/// 两个自包含目录与 <c>app.ico</c>。之所以走「zip 载荷 + 释放到磁盘」而不是直接单文件发布所有依赖，
/// 是因为界面端要在真实文件系统里加载 <c>native\7z.dll</c> / <c>7z.exe</c>，它们必须存在磁盘上。
/// </summary>
public sealed class PayloadExtractor : IDisposable
{
    /// <summary>载荷资源名，与 csproj 里的 LogicalName 一一对应</summary>
    public const string PayloadResourceName = "payload.zip";

    private PayloadExtractor(string extractDirectory) => ExtractDirectory = extractDirectory;

    /// <summary>临时解压目录（位于 %TEMP% 下，Dispose 时递归删除）</summary>
    public string ExtractDirectory { get; }

    public string AppDirectory => Path.Combine(ExtractDirectory, "App");

    public string ServiceDirectory => Path.Combine(ExtractDirectory, "Service");

    public string IconPath => Path.Combine(ExtractDirectory, "app.ico");

    public static PayloadExtractor Extract()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidOperationException(
                "安装包内没有程序载荷。请先运行 scripts\\publish.ps1 完整构建，单独编译 FileKeeper.Setup 不会带上 App / Service。");

        var directory = Path.Combine(Path.GetTempPath(), "FileKeeperSetup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(directory, overwriteFiles: true);

        return new PayloadExtractor(directory);
    }

    /// <summary>
    /// 校验载荷内容完整。缺少 native 7z 会让压缩功能直接不可用，
    /// 宁可在安装前就失败，也不要装出一个跑不起来的程序。
    /// </summary>
    public void Validate()
    {
        var missing = new[]
            {
                Path.Combine(AppDirectory, "FileKeeper.App.exe"),
                Path.Combine(AppDirectory, "native", "7z.dll"),
                Path.Combine(AppDirectory, "native", "7z.exe"),
                Path.Combine(ServiceDirectory, "FileKeeper.Service.exe")
            }
            .Where(path => !File.Exists(path))
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "安装包载荷不完整，缺少：" + string.Join("、", missing.Select(Path.GetFileName)));
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(ExtractDirectory))
            {
                Directory.Delete(ExtractDirectory, recursive: true);
            }
        }
        catch
        {
            // 临时目录删不掉不影响安装结果，忽略即可
        }
    }
}
