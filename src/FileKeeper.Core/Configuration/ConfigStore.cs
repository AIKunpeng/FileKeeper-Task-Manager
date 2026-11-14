using System.Text.Json;
using System.Text.Json.Serialization;
using FileKeeper.Core.Models;

namespace FileKeeper.Core.Configuration;

/// <summary>
/// 配置读写。界面端与服务端可能同时访问同一个 config.json：
/// 写入采用「写临时文件 → 原子替换」，读取采用「共享读 + 重试」，
/// 并通过 <see cref="FileSystemWatcher"/> 加去抖实现配置热重载。
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private const int DebounceMilliseconds = 500;
    private const int SelfWriteSuppressionMilliseconds = 2000;
    private const int ReadRetryCount = 3;
    private const int ReadRetryDelayMilliseconds = 150;

    /// <summary>
    /// 配置序列化选项。任务导入导出与 config.json 保持同一套约定
    /// （camelCase 命名 + 枚举字符串），导入文件可直接人工审阅。
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();
    private readonly string _path;

    private FileSystemWatcher? _watcher;
    private System.Timers.Timer? _debounceTimer;
    private long _lastSelfWriteTicks;
    private bool _disposed;

    public ConfigStore(string? configPath = null)
    {
        _path = string.IsNullOrWhiteSpace(configPath) ? AppPaths.ConfigFilePath : configPath;
        Current = new AppConfig();
    }

    /// <summary>当前内存中的配置</summary>
    public AppConfig Current { get; private set; }

    /// <summary>外部（另一端进程）修改配置并热重载后触发</summary>
    public event EventHandler? ExternalChanged;

    /// <summary>
    /// 本进程修改配置（<see cref="Update"/> / <see cref="Save"/>）后触发。
    /// 自写不会走 <see cref="ExternalChanged"/>（抑制回环），但本进程内依赖配置的组件
    /// （如界面自己的日志目录）仍需要知道「刚改过」，否则要等重启才生效。
    /// </summary>
    public event EventHandler? SelfChanged;

    public string ConfigFilePath => _path;

    /// <summary>
    /// 加载配置。文件不存在则创建默认配置；文件损坏则备份后回退到默认配置。
    /// </summary>
    public AppConfig Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
            {
                Current = new AppConfig();
                SaveCore();
                return Current;
            }

            for (var attempt = 0; attempt < ReadRetryCount; attempt++)
            {
                try
                {
                    // 与另一端进程的写入共享，避免共享冲突
                    using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var config = JsonSerializer.Deserialize<AppConfig>(stream, SerializerOptions);

                    Current = config ?? new AppConfig();
                    Current.Normalize();
                    return Current;
                }
                catch (JsonException ex)
                {
                    BackupCorruptedFile(ex);
                    Current = new AppConfig();
                    SaveCore();
                    return Current;
                }
                catch (IOException) when (attempt < ReadRetryCount - 1)
                {
                    Thread.Sleep(ReadRetryDelayMilliseconds);
                }
            }

            // 三次都失败（极可能是被独占锁定），返回已有内存配置保证进程可用
            return Current;
        }
    }

    /// <summary>保存当前内存配置</summary>
    public void Save()
    {
        lock (_sync)
        {
            SaveCore();
        }

        RaiseSelfChanged();
    }

    /// <summary>在锁内修改配置并立即保存，避免「读-改-写」竞争</summary>
    public void Update(Action<AppConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_sync)
        {
            mutate(Current);
            Current.Normalize();
            SaveCore();
        }

        RaiseSelfChanged();
    }

    /// <summary>
    /// 在锁外触发，允许处理器内再读配置甚至再写配置而不死锁。
    /// 处理器是幂等的（重复触发直接早退），连写两次配置也只会多走一轮。
    /// </summary>
    private void RaiseSelfChanged()
    {
        try
        {
            SelfChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 通知失败不应影响保存本身；各处理器内部有自己的异常兜底
        }
    }

    /// <summary>开始监听外部配置变更（热重载）</summary>
    public void StartWatching()
    {
        if (_watcher is not null || _disposed)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);

        _debounceTimer = new System.Timers.Timer(DebounceMilliseconds) { AutoReset = false };
        _debounceTimer.Elapsed += (_, _) => OnDebounceElapsed();

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
        {
            // 必须包含 FileName：原子写入是「写 .tmp 再 Move 覆盖目标」，
            // 它只对目标文件产生 Deleted 与 Renamed，不产生 Changed。
            // NotifyFilter 少了 FileName 的话，Renamed 会在源头被滤掉，
            // 热重载将永远收不到通知——而两端进程都用原子写入，等于整条链路失效。
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                                                   | NotifyFilters.CreationTime
                                                   | NotifyFilters.FileName
        };
        _watcher.Changed += OnConfigFileChanged;
        _watcher.Created += OnConfigFileChanged;
        _watcher.Renamed += OnConfigFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    public void StopWatching()
    {
        _debounceTimer?.Stop();
        _debounceTimer?.Dispose();
        _debounceTimer = null;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnConfigFileChanged;
            _watcher.Created -= OnConfigFileChanged;
            _watcher.Renamed -= OnConfigFileChanged;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopWatching();
        _disposed = true;
    }

    /// <summary>
    /// 原子写入：先写同目录临时文件，再整体替换，避免另一端读到写了一半的文件。
    /// </summary>
    private void SaveCore()
    {
        Current.Normalize();

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _path + ".tmp";

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, Current, SerializerOptions);
                stream.Flush(true);
            }

            // 必须在 Move 之前置位：文件事件是异步投递的。
            // 若等 Move 返回后再置位，事件处理线程会抢先执行，
            // 此时读到的是上一次的自写时间戳，抑制就会失效，
            // 导致自己写的文件反过来触发热重载（重载 → 回写 → 再重载的回环）。
            Interlocked.Exchange(ref _lastSelfWriteTicks, Environment.TickCount64);

            File.Move(tempPath, _path, overwrite: true);
        }
        catch (Exception)
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // 自己刚写的文件变更不能立即触发重载，否则会造成「重载 → 回写 → 再重载」的循环。
        // 但也不能直接丢弃：若另一端恰好在抑制窗口内也写了配置，这个外部变更会被吞掉。
        // 重新挂一次防抖，窗口过后重读文件——内容没变（自家事件）就只是空转一轮
        _debounceTimer?.Stop();
        _debounceTimer?.Start();
    }

    private void OnDebounceElapsed()
    {
        AppConfig? loaded = null;

        lock (_sync)
        {
            for (var attempt = 0; attempt < ReadRetryCount; attempt++)
            {
                try
                {
                    using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    loaded = JsonSerializer.Deserialize<AppConfig>(stream, SerializerOptions);
                    break;
                }
                catch (JsonException)
                {
                    // 另一端正在写入或文件损坏，保留现有配置
                    return;
                }
                catch (IOException) when (attempt < ReadRetryCount - 1)
                {
                    Thread.Sleep(ReadRetryDelayMilliseconds);
                }
                catch (IOException)
                {
                    return;
                }
            }
        }

        if (loaded is null)
        {
            return;
        }

        loaded.Normalize();

        // 内容没变就不打扰（典型：自写抑制窗口过后收到的自家文件事件），
        // 直接触发会让另一端把「自己写的配置」当成外部变更再处理一轮
        var currentJson = JsonSerializer.Serialize(Current, SerializerOptions);
        var loadedJson = JsonSerializer.Serialize(loaded, SerializerOptions);

        if (string.Equals(currentJson, loadedJson, StringComparison.Ordinal))
        {
            return;
        }

        lock (_sync)
        {
            Current = loaded;
        }

        ExternalChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BackupCorruptedFile(Exception ex)
    {
        try
        {
            var backup = Path.Combine(
                Path.GetDirectoryName(_path) ?? AppPaths.RootDirectory,
                $"config.corrupt-{DateTime.Now:yyyyMMddHHmmss}.json");

            File.Move(_path, backup, overwrite: true);
            Console.Error.WriteLine($"[ConfigStore] 配置文件已损坏，备份至 {backup}：{ex.Message}");
        }
        catch (Exception backupEx)
        {
            Console.Error.WriteLine($"[ConfigStore] 配置文件损坏且备份失败：{backupEx.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理临时文件失败可忽略
        }
    }
}
