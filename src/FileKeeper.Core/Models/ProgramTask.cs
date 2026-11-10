namespace FileKeeper.Core.Models;

/// <summary>
/// 定时执行程序 / 脚本的任务。
///
/// 支持三类目标：
/// 1. 可执行文件（.exe），直接启动；
/// 2. 批处理（.bat / .cmd），经由 cmd.exe /c 包装执行；
/// 3. PowerShell 脚本（.ps1），经由 powershell.exe -File 包装执行。
/// 其余扩展名一律按可执行文件尝试启动，启动失败会在结果里写明。
/// </summary>
public sealed class ProgramTask : IScheduledTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "新建程序任务";

    public bool Enabled { get; set; } = true;

    /// <summary>程序或脚本的完整路径</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>命令行参数。原样传给目标程序，不做二次转义</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>工作目录。为空时使用程序所在目录</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>窗口形态</summary>
    public ProcessWindow WindowMode { get; set; } = ProcessWindow.Hidden;

    /// <summary>
    /// 是否等待进程退出并用退出码判定成败（脚本类任务的默认行为）。
    /// 关掉则「启动成功即执行成功」，不等待、不强杀——
    /// 适合 Typora、微信这类启动后长期驻留的图形界面程序，
    /// 否则任务会一直挂在「正在执行」直到超时，还可能把正开着的程序杀掉。
    /// </summary>
    public bool WaitForExit { get; set; } = true;

    /// <summary>
    /// 超时秒数。到时仍未退出的子进程会被强制结束并判定失败——
    /// 定时任务必须能收敛，挂死的脚本是调度系统最危险的形态。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 3600;

    public ScheduleConfig Schedule { get; set; } = new();

    public ProgramTask Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        FilePath = FilePath,
        Arguments = Arguments,
        WorkingDirectory = WorkingDirectory,
        WindowMode = WindowMode,
        WaitForExit = WaitForExit,
        TimeoutSeconds = TimeoutSeconds,
        Schedule = Schedule.Clone()
    };

    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "未命名程序任务" : Name.Trim();
        FilePath = NormalizePath(FilePath) ?? string.Empty;
        Arguments = Arguments?.Trim() ?? string.Empty;
        WorkingDirectory = NormalizePath(WorkingDirectory);
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 1, 86400);
        Schedule.Normalize();
    }

    /// <summary>配置校验，返回首条错误；无误返回 null。执行器与编辑页共用同一套规则</summary>
    public string? ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            return "请选择要执行的程序或脚本";
        }

        if (!File.Exists(FilePath))
        {
            return $"文件不存在：{FilePath}";
        }

        if (!string.IsNullOrEmpty(WorkingDirectory) && !Directory.Exists(WorkingDirectory))
        {
            return $"工作目录不存在：{WorkingDirectory}";
        }

        return null;
    }

    /// <summary>归一化手填路径：去引号、去多余空格与尾部路径分隔符</summary>
    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().Trim('"').Trim();

        while (text.Length > 3 &&
               (text[^1] == Path.DirectorySeparatorChar || text[^1] == Path.AltDirectorySeparatorChar))
        {
            text = text[..^1];
        }

        return text.Length == 0 ? null : text;
    }
}
