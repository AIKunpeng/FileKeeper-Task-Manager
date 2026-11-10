using System.Globalization;
using System.Text.RegularExpressions;
using FileKeeper.Core.Models;

namespace FileKeeper.Core.Execution;

/// <summary>
/// 压缩包文件名模板渲染。
///
/// 除约定的 <c>{yyyy}</c> <c>{MM}</c> <c>{dd}</c> <c>{HH}</c> <c>{mm}</c> <c>{ss}</c>
/// <c>{yyyyMMdd}</c> <c>{yyyyMMddHHmmss}</c> <c>{name}</c> 之外，
/// 花括号内任意内容都会直接作为 .NET 自定义日期格式串处理，
/// 因此 <c>{yyyy-MM-dd_HH-mm}</c> 这类写法同样有效。
/// </summary>
public static partial class FileNameTemplate
{
    private static readonly Regex TokenPattern = TokenRegex();
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// 渲染模板并生成完整的压缩包路径（含扩展名），同名时自动追加序号。
    /// </summary>
    /// <param name="outputDirectory">存放目录</param>
    /// <param name="template">文件名模板（不含扩展名）</param>
    /// <param name="format">压缩包格式，决定扩展名</param>
    /// <param name="timestamp">用于填充时间占位符</param>
    /// <param name="taskName">用于填充 {name}</param>
    public static string BuildArchivePath(
        string outputDirectory,
        string template,
        ArchiveFormat format,
        DateTimeOffset timestamp,
        string? taskName = null)
    {
        var fileName = SanitizeFileName(Render(template, timestamp, taskName));

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "Backup";
        }

        var extension = format switch
        {
            ArchiveFormat.Rar => ".rar",
            _ => ".zip"
        };

        var fullPath = Path.Combine(outputDirectory, fileName + extension);
        return MakeUnique(fullPath);
    }

    /// <summary>
    /// 仅渲染文件名（不含扩展名、不含目录），供界面实时预览。
    /// </summary>
    public static string Render(string? template, DateTimeOffset timestamp, string? taskName = null) =>
        SanitizeFileName(RenderCore(template, timestamp, taskName));

    /// <summary>渲染后的文件名示例（界面提示用）</summary>
    public static string RenderSample(string? template, ArchiveFormat format, string? taskName = null, DateTimeOffset? at = null)
    {
        var extension = format == ArchiveFormat.Rar ? ".rar" : ".zip";
        return Render(template, at ?? DateTimeOffset.Now, taskName) + extension;
    }

    /// <summary>把非法文件名字符替换为下划线并压缩空白</summary>
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(InvalidChars, chars[i]) >= 0 || chars[i] is ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                chars[i] = '_';
            }
        }

        var sanitized = new string(chars).Trim().TrimEnd('.');
        return sanitized.Length == 0 ? "Backup" : sanitized;
    }

    private static string RenderCore(string? template, DateTimeOffset timestamp, string? taskName)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "Backup";
        }

        var localTime = timestamp.LocalDateTime;

        return TokenPattern.Replace(template, match =>
        {
            var token = match.Groups[1].Value.Trim();

            if (token.Length == 0)
            {
                return string.Empty;
            }

            if (token.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                return taskName ?? string.Empty;
            }

            try
            {
                return localTime.ToString(token, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                // 无法识别的占位符原样保留，避免静默吞掉用户输入
                return match.Value;
            }
        });
    }

    private static string MakeUnique(string fullPath)
    {
        if (!File.Exists(fullPath) && !VolumeExists(fullPath))
        {
            return fullPath;
        }

        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);

        for (var index = 1; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, $"{nameWithoutExtension}_{index}{extension}");
            if (!File.Exists(candidate) && !VolumeExists(candidate))
            {
                return candidate;
            }
        }

        // 极端情况下回退到时间戳，保证不会出现死循环
        return Path.Combine(directory, $"{nameWithoutExtension}_{DateTime.Now:HHmmss}{extension}");
    }

    /// <summary>
    /// 分卷压缩时第一分卷会带后缀（例如 xxx.zip.001 / xxx.part1.rar），
    /// 判断重名时必须一并考虑，否则会覆盖既有分卷。
    /// </summary>
    private static bool VolumeExists(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        var prefix = Path.GetFileName(fullPath);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(prefix) || !Directory.Exists(directory))
        {
            return false;
        }

        return Directory.EnumerateFiles(directory, prefix + ".*").Any();
    }

    [GeneratedRegex(@"\{([^{}]+)\}", RegexOptions.Compiled)]
    private static partial Regex TokenRegex();
}
