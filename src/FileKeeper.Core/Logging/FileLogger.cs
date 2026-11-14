using System.Globalization;
using System.Text;
using FileKeeper.Core.Configuration;
using FileKeeper.Core.Models;

namespace FileKeeper.Core.Logging;

/// <summary>
/// 按天分文件的轻量文件日志。
/// 写入端以 LocalSystem 常驻，读取端是普通用户的界面进程，因此写文件必须以
/// <see cref="FileShare.ReadWrite"/> 打开，否则 keep 独占会导致界面无法读取。
/// </summary>
public sealed class FileLogger : IAppLogger, IDisposable
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>运行记录行的消息前缀，界面据此识别并解析（也用于在原始日志视图中隐藏）</summary>
    public const string RunRecordPrefix = "RUN";

    private static readonly object StaticSync = new();

    private readonly object _sync = new();
    // 非 readonly：设置页改保留天数后要能在运行中热更新
    private int _retentionDays;

    // 目录不是 readonly：改了日志目录后要在进程运行中切换过去，
    // 服务重启一次才能让新位置生效的话，这段时间的历史日志就会出现断层
    private string _logsDirectory;

    private FileStream? _stream;
    private StreamWriter? _writer;
    private DateTime _currentDate;
    private bool _disposed;

    public FileLogger(string? logsDirectory = null, int retentionDays = 30)
    {
        _logsDirectory = LogPaths.Resolve(logsDirectory);
        _retentionDays = retentionDays;
        Directory.CreateDirectory(_logsDirectory);
        PurgeOldLogs(_logsDirectory, retentionDays);
    }

    public string LogsDirectory
    {
        get { lock (_sync) { return _logsDirectory; } }
    }

    /// <summary>
    /// 切换到新的日志目录，并在需要时先把旧目录的历史日志搬过去，返回搬迁的文件数。
    ///
    /// 搬迁与切换放在同一个锁里是有意的：用户在设置页改完目录的那一刻，
    /// 界面线程正在写当天日志，若先切句柄再搬迁，就可能把当天内容留在旧目录。
    /// </summary>
    public int Retarget(string? newDirectory, string? migrateFrom = null)
    {
        var target = LogPaths.Resolve(newDirectory);
        var moved = MigrateLogs(migrateFrom, target);

        lock (_sync)
        {
            if (string.Equals(_logsDirectory, target, StringComparison.OrdinalIgnoreCase))
            {
                return moved;
            }

            _logsDirectory = target;

            // 关掉旧目录的写句柄，下一次 EnsureWriter 会在新目录重建
            CloseWriter();
            Directory.CreateDirectory(target);
            PurgeOldLogs(target, _retentionDays);
        }

        return moved;
    }

    /// <summary>
    /// 热更新保留天数并立即按新值清理一次。
    ///
    /// 保留天数原本只在构造时定格，设置页改小后要等重启或跨天才生效——
    /// 用户改完点「清理过期日志」却发现老日志还在，看起来就像设置没用。
    /// 这里在值变化时马上清一轮，让设置即刻兑现。
    /// </summary>
    public void ApplyRetention(int retentionDays)
    {
        var normalized = Math.Max(1, retentionDays);

        lock (_sync)
        {
            if (_retentionDays == normalized)
            {
                return;
            }

            _retentionDays = normalized;
        }

        PurgeOldLogs(_logsDirectory, normalized);
    }

    /// <summary>
    /// 把 from 目录下的 .log 文件搬到 to 目录，返回成功搬迁的数量。
    ///
    /// 逐个搬运且跳过目标已存在的同名文件，是为了让操作天然幂等：
    /// 界面端与后台服务共享同一份配置，都可能看到「待迁移」标记，
    /// 两边先后或同时尝试都不能把文件搬丢，也不能中途误判迁移完成。
    /// 单个文件失败（比如当天日志正被另一个进程占用）不影响其余文件，
    /// 调用方据此决定要不要保留「待迁移」标记交给另一个进程重试。
    /// </summary>
    public static int MigrateLogs(string? from, string? to)
    {
        if (string.IsNullOrWhiteSpace(from))
        {
            return 0;
        }

        var source = from.Trim().Trim('"').Trim();
        var target = LogPaths.Resolve(to);

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(source))
        {
            return 0;
        }

        Directory.CreateDirectory(target);

        var moved = 0;

        foreach (var file in Directory.EnumerateFiles(source, "*.log"))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));

            try
            {
                if (!File.Exists(destination))
                {
                    File.Move(file, destination);
                    moved++;
                    continue;
                }

                // 目标已存在同名日志（典型是当天文件：两端的进程都写过）。
                // 这里选择「合并内容再删源」而不是跳过：
                // 跳过会让同一天的记录在两边各留一半，且旧目录始终有 .log，
                // 迁移标记就永远撤不掉，服务每轮启动都要重试一次。
                var lines = File.ReadAllLines(file);

                if (lines.Length > 0)
                {
                    File.AppendAllLines(destination, lines);
                }

                File.Delete(file);
                moved++;
            }
            catch (IOException)
            {
                // 文件正被另一个进程写入（典型是当天日志），下轮再试
            }
            catch (UnauthorizedAccessException)
            {
                // 界面端搬 ProgramData 下的历史日志时常见，留给后台服务重试
            }
        }

        return moved;
    }

    public void Info(string message, string? context = null) => Write(LogLevel.Info, message, context, null);

    public void Warn(string message, string? context = null) => Write(LogLevel.Warn, message, context, null);

    public void Error(string message, string? context = null, Exception? exception = null)
        => Write(LogLevel.Error, message, context, exception);

    /// <summary>
    /// 记录任务执行结果：一行摘要 + 至多 20 行明细。密码等敏感信息绝不写入。
    /// </summary>
    public void TaskResult(TaskRunResult result)
    {
        var level = result.Status is TaskRunStatus.Failed ? LogLevel.Error
            : result.Status is TaskRunStatus.PartialSuccess ? LogLevel.Warn
            : LogLevel.Info;

        var kindText = TaskKindText.For(result.Kind);
        var summary = result.Status switch
        {
            TaskRunStatus.Skipped => $"{kindText}任务跳过：{result.ErrorMessage}",
            TaskRunStatus.Failed => $"{kindText}任务失败：{result.ErrorMessage}",
            _ => $"{kindText}任务完成：成功 {result.SuccessCount}，失败 {result.FailedCount}，跳过 {result.SkippedCount}，" +
                 $"处理 {FormatBytes(result.ProcessedBytes)}，耗时 {result.Duration.TotalSeconds:F1}s" +
                 (string.IsNullOrEmpty(result.OutputPath) ? string.Empty : $"，产物 {result.OutputPath}")
        };

        Write(level, summary, result.TaskName, null);

        // 机器可读的运行记录行：界面「运行记录」视图按它精确还原每次任务运行
        // （任务名、计划/启动/结束时间、状态），比解析自然语言摘要可靠。
        // 原始日志视图会隐藏本行，避免干扰阅读
        Write(
            LogLevel.Info,
            string.Join('|',
                RunRecordPrefix,
                result.Status,
                result.ScheduledAt?.ToString(TimestampFormat, CultureInfo.InvariantCulture) ?? "-",
                result.StartedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                result.FinishedAt.ToString(TimestampFormat, CultureInfo.InvariantCulture)),
            result.TaskName,
            null);

        foreach (var detail in result.Details.Take(20))
        {
            Write(level, "  " + detail, result.TaskName, null);
        }

        if (result.Details.Count > 20)
        {
            Write(level, $"  …… 另有 {result.Details.Count - 20} 条明细未展示", result.TaskName, null);
        }
    }

    /// <summary>
    /// 进程级兜底：未处理异常或宿主启动失败时写入，绝不抛出。
    /// </summary>
    public static void WriteFatal(string message, Exception? exception = null, string? logsDirectory = null)
    {
        try
        {
            lock (StaticSync)
            {
                // 兜底日志常常发生在配置还没读出来的启动早期，因此这里必须
                // 「给什么目录就用什么目录，没给就回默认」——交给 LogPaths 统一裁决
                var directory = LogPaths.Resolve(logsDirectory);
                Directory.CreateDirectory(directory);
                var line = FormatLine(DateTime.Now, LogLevel.Error, "Fatal", Compose(message, exception));
                File.AppendAllText(
                    Path.Combine(directory, DateTime.Today.ToString(DateFormat, CultureInfo.InvariantCulture) + ".log"),
                    line + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 兜底日志不允许再抛异常，否则会掩盖原始错误
        }
    }

    /// <summary>
    /// 读取指定日期的日志，供界面展示。可按关键字与任务名过滤。
    /// </summary>
    public static IReadOnlyList<LogEntry> Read(
        DateTime date,
        string? keyword = null,
        string? contextFilter = null,
        IReadOnlyList<LogLevel>? levels = null,
        string? logsDirectory = null)
    {
        var file = GetLogFilePath(LogPaths.Resolve(logsDirectory), date);
        if (!File.Exists(file))
        {
            return Array.Empty<LogEntry>();
        }

        var entries = new List<LogEntry>();

        // 与常驻写入端共享读取，必须允许其他进程同时写入
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (!TryParseLine(line, out var entry))
            {
                continue;
            }

            if (levels is { Count: > 0 } && !levels.Contains(entry.Level))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(contextFilter) &&
                entry.Context?.Contains(contextFilter, StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(keyword) &&
                !entry.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// 列出已存在日志的日期，倒序排列。
    /// 必须显式传入当前生效目录：改了日志目录之后，日志页要跟着读新位置，
    /// 否则界面会把「没有日志」当成「任务从没跑过」。
    /// </summary>
    public static IReadOnlyList<DateTime> GetAvailableDates(string? logsDirectory = null)
    {
        var directory = LogPaths.Resolve(logsDirectory);

        if (!Directory.Exists(directory))
        {
            return Array.Empty<DateTime>();
        }

        var dates = new List<DateTime>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateTime.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                dates.Add(date);
            }
        }

        dates.Sort((a, b) => b.CompareTo(a));
        return dates;
    }

    /// <summary>删除超过保留天数的日志文件。</summary>
    public static void PurgeOldLogs(string? logsDirectory = null, int retentionDays = 30)
    {
        var dir = LogPaths.Resolve(logsDirectory);
        if (!Directory.Exists(dir))
        {
            return;
        }

        var threshold = DateTime.Today.AddDays(-Math.Max(1, retentionDays));

        foreach (var file in Directory.EnumerateFiles(dir, "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!DateTime.TryParseExact(name, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            if (date >= threshold)
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // 文件被占用时下轮再试
            }
            catch (UnauthorizedAccessException)
            {
                // 无权限时忽略
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _writer?.Dispose();
            _stream?.Dispose();
            _writer = null;
            _stream = null;
            _disposed = true;
        }
    }

    /// <summary>释放当前写句柄。切换目录与按天滚动共用同一段收尾逻辑。</summary>
    private void CloseWriter()
    {
        _writer?.Dispose();
        _stream?.Dispose();
        _writer = null;
        _stream = null;
    }

    private void Write(LogLevel level, string message, string? context, Exception? exception)
    {
        if (_disposed)
        {
            return;
        }

        var line = FormatLine(DateTime.Now, level, context, Compose(message, exception));

        lock (_sync)
        {
            try
            {
                EnsureWriter(DateTime.Today);
                _writer!.WriteLine(line);
                _writer.Flush();
            }
            catch (Exception ex)
            {
                // 日志写入失败不能影响任务执行
                Console.Error.WriteLine($"[FileLogger] 写入失败: {ex.Message} | {line}");
            }
        }
    }

    private void EnsureWriter(DateTime date)
    {
        if (_writer is not null && _stream is not null && date == _currentDate)
        {
            return;
        }

        CloseWriter();

        var path = GetLogFilePath(_logsDirectory, date);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false));
        _currentDate = date;

        // 跨天后顺带清理过期日志
        PurgeOldLogs(_logsDirectory, _retentionDays);
    }

    private static string GetLogFilePath(string directory, DateTime date)
        => Path.Combine(directory, date.ToString(DateFormat, CultureInfo.InvariantCulture) + ".log");

    private static string Compose(string message, Exception? exception)
    {
        var text = Sanitize(message);
        if (exception is null)
        {
            return text;
        }

        var sb = new StringBuilder(text);
        if (text.Length > 0)
        {
            sb.Append(" | 异常: ");
        }

        sb.Append(Sanitize(exception.Message));
        return sb.ToString();
    }

    private static string FormatLine(DateTime timestamp, LogLevel level, string? context, string message)
        => string.Join('|',
            timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture),
            level switch { LogLevel.Info => "INFO", LogLevel.Warn => "WARN", _ => "ERROR" },
            context ?? string.Empty,
            message);

    private static bool TryParseLine(string line, out LogEntry entry)
    {
        entry = null!;

        var parts = line.Split('|', 4);
        if (parts.Length < 4)
        {
            return false;
        }

        if (!DateTime.TryParseExact(parts[0], TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
        {
            return false;
        }

        var level = parts[1] switch
        {
            "WARN" => LogLevel.Warn,
            "ERROR" => LogLevel.Error,
            _ => LogLevel.Info
        };

        entry = new LogEntry(
            new DateTimeOffset(timestamp, TimeZoneInfo.Local.GetUtcOffset(timestamp)),
            level,
            parts[3],
            string.IsNullOrEmpty(parts[2]) ? null : parts[2]);

        return true;
    }

    /// <summary>去掉换行，保证一条日志只占一行。</summary>
    private static string Sanitize(string? text)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:F2}{units[unit]}";
    }
}
