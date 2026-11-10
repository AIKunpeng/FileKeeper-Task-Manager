using System.Globalization;
using System.Windows.Data;

namespace FileKeeper.App.Converters;

/// <summary>
/// UTC 时间 → 本地时间文本。
/// 存储层统一是 UTC，界面必须按本地时区展示，否则用户看到的「下次运行时间」会差 8 小时。
/// 空值显示为「—」，避免出现空白却含义不明的格子。
/// </summary>
public sealed class UtcToLocalTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset timestamp)
        {
            return "—";
        }

        var local = timestamp.ToLocalTime();

        return (parameter as string) switch
        {
            "Date" => local.ToString("yyyy-MM-dd"),
            "Time" => local.ToString("HH:mm:ss"),
            _ => local.ToString("MM-dd HH:mm")
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// TimeOnly 与 "HH:mm" 文本互转。
/// 调度时刻只有一个时刻、没有日期，直接用 TimePicker 控件过重，文本框更轻量。
/// </summary>
public sealed class TimeOnlyTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TimeOnly time ? time.ToString("HH:mm") : "02:00";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text
            && TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        // 解析失败时保留原值，避免用户输入过程中被强行改写
        return Binding.DoNothing;
    }
}

/// <summary>字节数 → 人类可读大小。</summary>
public sealed class FileSizeConverter : IValueConverter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long size)
        {
            return "0 B";
        }

        if (size <= 0)
        {
            return "0 B";
        }

        var unit = 0;
        double display = size;

        while (display >= 1024 && unit < Units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return unit == 0 ? $"{display:0} {Units[unit]}" : $"{display:0.##} {Units[unit]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 字符串集合与逗号分隔文本互转。用于扩展名、排除目录这类标签式输入。
/// </summary>
public sealed class StringListConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<string> items ? string.Join(", ", items) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text)
        {
            return Array.Empty<string>();
        }

        return text.Split(new[] { ',', '，', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
    }
}
