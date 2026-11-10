using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FileKeeper.App.Converters;

/// <summary>
/// bool 与 Visibility 互转。
/// 约定：true → Visible，false → Collapsed；传 "Invert" 则反转。
/// 用 Collapsed 而不是 Hidden，是为了让隐藏的元素彻底不占位。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;

        if (IsInvert(parameter))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is Visibility v && v == Visibility.Visible;
        return IsInvert(parameter) ? !visible : visible;
    }

    private static bool IsInvert(object? parameter) =>
        parameter is string text && string.Equals(text, "Invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>取反。用于「未安装时才显示安装按钮」这类互斥可见性。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>null → Collapsed，非 null → Visible。传 "Invert" 反转。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null;

        if (parameter is string text && string.Equals(text, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            isNull = !isNull;
        }

        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 枚举 → bool，用于把一组 RadioButton 直接绑到同一个枚举属性。
/// ConverterParameter 传枚举名字符串。
///
/// 必须显式指定 <see cref="EnumType"/>：ConvertBack 回调里拿到的 targetType
/// 是绑定目标属性（bool）而不是源属性，拿它去 Enum.Parse 会在运行时抛异常。
/// </summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    /// <summary>目标枚举类型，XAML 中以 {x:Type} 指定</summary>
    public Type? EnumType { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null
        && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
        {
            return Binding.DoNothing;
        }

        var type = EnumType ?? targetType;

        if (!type.IsEnum)
        {
            return Binding.DoNothing;
        }

        return Enum.Parse(type, parameter.ToString()!, ignoreCase: true);
    }
}

/// <summary>
/// 枚举值相等 → Visible，否则 Collapsed。
/// 用于「选了每周才显示周几面板」这类整块显隐，省掉一层 bool 中间属性。
/// </summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public Type? EnumType { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var matched = value is not null && parameter is not null
            && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

        return matched ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 整数 → Visibility。用于「集合为空时显示空状态」这类判断。
/// 默认 0 时显示；传 "NonZero" 则非 0 时显示。
/// </summary>
public sealed class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value as int? ?? 0;
        var showOnZero = parameter as string != "NonZero";

        return (showOnZero ? count == 0 : count != 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>空字符串或 null → Collapsed。用于「有内容才显示」的提示块。</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value is null or string { Length: 0 };

        if (parameter is string text && string.Equals(text, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            isEmpty = !isEmpty;
        }

        return isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
