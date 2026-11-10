using FileKeeper.Core.Configuration;

namespace FileKeeper.Core.Logging;

/// <summary>
/// 日志目录的唯一解析出口。
///
/// 此前「配置目录还是默认目录」的判断散落在 FileLogger 的每个静态方法里，
/// 只要漏掉一处就会出现「已经写到新目录、界面还在读旧目录」这类不一致。
/// 配置可改之后，界面端、后台服务与安装器都必须经由这里解析，
/// 各调用点不再自己拼路径或自行判断是否为空。
/// </summary>
public static class LogPaths
{
    /// <summary>
    /// 解析实际生效的日志目录：配置值为空时退回 %ProgramData%\FileKeeper\logs。
    /// 保留 <see cref="AppPaths.LogsDirectory"/> 作为默认值来源，历史默认位置不会因本次改造而变。
    /// </summary>
    public static string Resolve(string? configured)
        => string.IsNullOrWhiteSpace(configured)
            ? AppPaths.LogsDirectory
            : configured.Trim().Trim('"').Trim();

    /// <summary>配置值是否为空。为空时界面需要把默认目录显示出来作为提示。</summary>
    public static bool IsUnset(string? configured) => string.IsNullOrWhiteSpace(configured);
}
