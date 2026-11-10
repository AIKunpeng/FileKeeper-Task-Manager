namespace FileKeeper.Core;

/// <summary>
/// 产品署名与版权信息。
///
/// 主窗口、设置页「关于」、安装窗体三处都要展示同一份署名；
/// 各处各写一份字符串，迟早会出现「窗口写 A、安装器写 B」的不一致，
/// 因此收拢到这里做单一出处。
/// </summary>
public static class ProductInfo
{
    public const string Name = "FileKeeper 任务管家";

    public const string Company = "深圳安信联创科技有限公司";

    /// <summary>
    /// 版权声明。年份取当前年份而不是写死：跨年后不必靠人为记得改字符串。
    /// 产品名与公司名之间用全角空格，与「版本信息」类文案的阅读节奏保持一致。
    /// </summary>
    public static string Copyright => $"© {DateTime.Now.Year} {Name}　版权所有 {Company}";
}
