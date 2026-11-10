namespace FileKeeper.Core.Models;

/// <summary>
/// 任务调度频率。
/// </summary>
public enum ScheduleFrequency
{
    /// <summary>每天</summary>
    Daily = 0,

    /// <summary>每周（可指定周几）</summary>
    Weekly = 1,

    /// <summary>每月（可指定几号）</summary>
    Monthly = 2
}

/// <summary>
/// 压缩包格式。RAR 为商业格式，仅 WinRAR 可创建。
/// </summary>
public enum ArchiveFormat
{
    Zip = 0,
    Rar = 1
}

/// <summary>
/// 清理任务判定文件年龄所依据的时间属性。
/// </summary>
public enum AgeTimeBasis
{
    /// <summary>最后修改时间</summary>
    LastWriteTime = 0,

    /// <summary>创建时间</summary>
    CreationTime = 1
}

/// <summary>
/// 文件过滤方式。
/// </summary>
public enum FileFilterMode
{
    /// <summary>全部文件</summary>
    All = 0,

    /// <summary>仅指定扩展名</summary>
    IncludeExtensions = 1
}

/// <summary>
/// 清理阈值的输入单位，最终统一换算为 <see cref="CleanupTask.Threshold"/> 时间跨度。
/// </summary>
public enum CleanupThresholdUnit
{
    Days = 0,
    Weeks = 1,
    Months = 2,

    /// <summary>小时级阈值：临时文件这类短命数据的清理粒度到不了「天」</summary>
    Hours = 3
}

/// <summary>
/// 任务类型。
/// </summary>
public enum TaskKind
{
    Compression = 0,
    Cleanup = 1,
    Sync = 2,
    Program = 3
}

/// <summary>
/// 任务类型的中文短名，唯一出处。
/// 日志摘要、结果弹窗、界面文案都从这里取，避免同一类型在不同文件里翻译得不一样。
/// </summary>
public static class TaskKindText
{
    public static string For(TaskKind kind) => kind switch
    {
        TaskKind.Compression => "压缩",
        TaskKind.Cleanup => "清理",
        TaskKind.Sync => "同步",
        TaskKind.Program => "程序",
        _ => "任务"
    };
}

/// <summary>
/// 程序任务启动子进程时的窗口形态。
/// 服务在后台会话（session 0）中运行，窗口本来不可见，这里主要影响界面端手动执行时的观感。
/// </summary>
public enum ProcessWindow
{
    /// <summary>隐藏窗口（推荐：多数脚本不需要弹窗）</summary>
    Hidden = 0,

    /// <summary>最小化启动</summary>
    Minimized = 1,

    /// <summary>正常显示</summary>
    Normal = 2
}

/// <summary>
/// 同步协议。
/// </summary>
public enum SyncProtocol
{
    /// <summary>SFTP（SSH 文件传输，端口 22）</summary>
    Sftp = 0,

    /// <summary>FTP 明文（端口 21）</summary>
    Ftp = 1,

    /// <summary>FTPS 显式（AUTH TLS，端口 21）</summary>
    FtpsExplicit = 2
}

/// <summary>
/// 一轮调度观测后对某个任务的执行判定。
/// </summary>
public enum ScheduleDecision
{
    /// <summary>未到期</summary>
    NotDue = 0,

    /// <summary>恰在本轮观测窗口内到期，正常执行</summary>
    Due = 1,

    /// <summary>执行点早于观测窗口起点（服务当时没在运行），按补跑执行</summary>
    Missed = 2,

    /// <summary>执行点已过期且未开启补跑，跳过这一轮</summary>
    ExpiredSkipped = 3
}

/// <summary>
/// SFTP 认证方式。FTP / FTPS 一律使用用户名 + 密码。
/// </summary>
public enum SftpAuthMode
{
    /// <summary>用户名 + 密码</summary>
    Password = 0,

    /// <summary>私钥文件（可另带私钥口令）</summary>
    PrivateKey = 1
}

/// <summary>
/// 任务单次执行结果。
/// </summary>
public enum TaskRunStatus
{
    Success = 0,

    /// <summary>部分文件失败，但任务整体完成</summary>
    PartialSuccess = 1,
    Failed = 2,
    Skipped = 3
}

/// <summary>
/// 日志级别。
/// </summary>
public enum LogLevel
{
    Info = 0,
    Warn = 1,
    Error = 2
}
