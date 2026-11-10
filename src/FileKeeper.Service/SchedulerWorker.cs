using FileKeeper.Core.Configuration;
using FileKeeper.Core.Execution;
using FileKeeper.Core.Logging;
using FileKeeper.Core.Models;
using FileKeeper.Core.Scheduling;
using Microsoft.Extensions.Hosting;

namespace FileKeeper.Service;

/// <summary>
/// 调度循环。运行在 Session 0（无 UI），开机自启，负责按调度触发三类任务。
///
/// 三条关键约定：
/// 1. <b>观测窗口区分「到期」与「错过」</b>：每轮记录观测起点，执行点落在 (上轮起点, 本轮]
///    内才是正常到期；早于上轮起点说明服务当时没在运行，属错过，由「补跑」开关决定。
///    只用「是否过期」判断的话，关掉补跑的开关会形同虚设。
/// 2. <b>单轮异常不能让循环死掉</b>：执行器内部已兜底，这里再兜一层，任何异常都只记录。
/// 3. <b>写回调度状态时只改调度字段</b>：配置可能在任务执行期间被界面端改过并热重载，
///    因此按 Id 重新定位后只写 LastRunUtc / NextRunUtc，避免覆盖用户刚改的频率与时刻。
/// </summary>
public sealed class SchedulerWorker : BackgroundService
{
    private const int DefaultTickSeconds = 30;

    private readonly IAppLogger _logger;
    private readonly ConfigStore _configStore;
    private readonly int _tickSeconds;

    /// <summary>上一轮观测时刻，用于区分「正常到期」与「服务未运行期间错过」</summary>
    private DateTimeOffset _windowStart;

    public SchedulerWorker(IAppLogger logger, ConfigStore configStore, int tickSeconds = DefaultTickSeconds)
    {
        _logger = logger;
        _configStore = configStore;
        _tickSeconds = tickSeconds <= 0 ? DefaultTickSeconds : tickSeconds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            AppPaths.EnsureCreated();
        }
        catch (Exception ex)
        {
            // 目录建不出来也要让服务活下去：界面端保存配置时会创建目录，
            // 热重载随即生效。为此停掉整个服务反而会让定时任务彻底失效。
            _logger.Warn($"初始化程序目录失败，将在界面端保存配置时补建：{ex.Message}", "调度");
        }

        try
        {
            _configStore.Load();
            _configStore.StartWatching();
            _configStore.ExternalChanged += OnConfigChanged;
        }
        catch (Exception ex)
        {
            _logger.Error($"加载配置失败，将以默认配置运行：{ex.Message}", "调度", ex);
        }

        InitializeSchedules();
        MigrateLogsIfPending();
        ReportStartup();

        // 启动时窗口起点取当前时刻：启动前过期的执行点不会被当成「正常到期」，
        // 而是交给补跑逻辑处理，避免一开机就集中触发一堆任务。
        _windowStart = DateTimeOffset.Now;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_tickSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await RunDueTasksAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 调度循环本身绝不能因为一次异常就退出
                _logger.Error($"调度循环异常：{ex.Message}", "调度", ex);
            }
        }

        _configStore.StopWatching();
        _logger.Info("调度服务已停止", "调度");
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        _logger.Info("检测到配置变更，已热重载", "调度");

        // 界面端刚改过日志目录时，这里必须跟着挪过去：
        // 服务不是每次还能指望用户手动重启，否则之后的日志会写回旧目录
        RedirectLogsIfNeeded();
        MigrateLogsIfPending();
        ApplyLogRetentionIfNeeded();
    }

    /// <summary>界面端改了保留天数后，服务的清理策略跟着走，不等重启。</summary>
    private void ApplyLogRetentionIfNeeded()
    {
        if (_logger is not FileLogger fileLogger)
        {
            return;
        }

        var days = Math.Max(1, _configStore.Current.Settings.LogRetentionDays);
        fileLogger.ApplyRetention(days);
    }

    /// <summary>把本进程的日志写入切到配置指定的目录。</summary>
    private void RedirectLogsIfNeeded()
    {
        if (_logger is not FileLogger fileLogger)
        {
            return;
        }

        var target = LogPaths.Resolve(_configStore.Current.Settings.LogDirectory);

        if (string.Equals(target, fileLogger.LogsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        fileLogger.Retarget(target);
        _logger.Info($"日志目录已切换到 {target}", "日志");
    }

    /// <summary>
    /// 完成界面端没能做完的日志搬迁。
    ///
    /// 界面端搬不动是常态而非异常：默认日志目录在 %ProgramData% 下，
    /// 普通权限的界面进程对它未必有写权限，这里以 LocalSystem 运行，
    /// 是历史日志最后一次能被捡起来的机会。
    /// </summary>
    private void MigrateLogsIfPending()
    {
        var pending = _configStore.Current.Settings.PendingLogMigrationFrom;

        if (string.IsNullOrWhiteSpace(pending))
        {
            return;
        }

        try
        {
            var target = LogPaths.Resolve(_configStore.Current.Settings.LogDirectory);
            var moved = FileLogger.MigrateLogs(pending, target);
            var remaining = HasLogFiles(pending);

            if (moved > 0)
            {
                _logger.Info($"已将 {moved} 个历史日志文件迁移到 {target}", "日志");
            }

            // 还有剩余说明确实搬不动，标记留给下次；否则到此为止，标记可以撤了
            if (remaining)
            {
                return;
            }

            _configStore.Update(config => config.Settings.PendingLogMigrationFrom = null);
        }
        catch (Exception ex)
        {
            _logger.Warn($"迁移历史日志失败，标记保留待下次重试：{ex.Message}", "日志");
        }
    }

    private static bool HasLogFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.log").Any();
        }
        catch
        {
            // 枚举不了就当作还没搬完，保留标记
            return true;
        }
    }

    private void ReportStartup()
    {
        var config = _configStore.Current;

        _logger.Info(
            $"调度服务已启动，轮询间隔 {_tickSeconds} 秒；" +
            $"任务：压缩 {config.CompressionTasks.Count} / 清理 {config.CleanupTasks.Count} / 同步 {config.SyncTasks.Count}",
            "调度");
    }

    /// <summary>为尚未排定下次执行时间的任务补算（新建任务、或首次启动）</summary>
    private void InitializeSchedules()
    {
        var now = DateTimeOffset.Now;
        var updates = new List<ScheduleUpdate>();
        var config = _configStore.Current;

        CollectMissing(config.CompressionTasks, TaskKind.Compression, updates, now);
        CollectMissing(config.CleanupTasks, TaskKind.Cleanup, updates, now);
        CollectMissing(config.SyncTasks, TaskKind.Sync, updates, now);
        CollectMissing(config.ProgramTasks, TaskKind.Program, updates, now);

        ApplyUpdates(updates);
    }

    private static void CollectMissing<T>(
        IEnumerable<T> tasks,
        TaskKind kind,
        List<ScheduleUpdate> updates,
        DateTimeOffset now) where T : class, IScheduledTask
    {
        foreach (var task in tasks)
        {
            if (ScheduleCalculator.InitializeIfNeeded(task.Schedule, now))
            {
                updates.Add(Capture(task, kind));
            }
        }
    }

    private async Task RunDueTasksAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var windowStart = _windowStart;
        _windowStart = now;

        var updates = new List<ScheduleUpdate>();
        var config = _configStore.Current;

        foreach (var task in config.CompressionTasks)
        {
            await HandleAsync(
                task,
                TaskKind.Compression,
                ct => TaskRunnerFactory.RunAsync(task, _logger, cancellationToken: ct),
                updates,
                now,
                windowStart,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var task in config.CleanupTasks)
        {
            await HandleAsync(
                task,
                TaskKind.Cleanup,
                ct => TaskRunnerFactory.RunAsync(task, _logger, cancellationToken: ct),
                updates,
                now,
                windowStart,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var task in config.SyncTasks)
        {
            await HandleAsync(
                task,
                TaskKind.Sync,
                ct => TaskRunnerFactory.RunAsync(task, _logger, cancellationToken: ct),
                updates,
                now,
                windowStart,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var task in config.ProgramTasks)
        {
            await HandleAsync(
                task,
                TaskKind.Program,
                ct => TaskRunnerFactory.RunAsync(task, _logger, cancellationToken: ct),
                updates,
                now,
                windowStart,
                cancellationToken).ConfigureAwait(false);
        }

        ApplyUpdates(updates);
    }

    private async Task HandleAsync<T>(
        T task,
        TaskKind kind,
        Func<CancellationToken, Task<TaskRunResult>> execute,
        List<ScheduleUpdate> updates,
        DateTimeOffset now,
        DateTimeOffset windowStart,
        CancellationToken cancellationToken) where T : class, IScheduledTask
    {
        if (!task.Enabled)
        {
            return;
        }

        var changed = ScheduleCalculator.InitializeIfNeeded(task.Schedule, now);

        switch (ScheduleCalculator.Decide(task.Schedule, now, windowStart))
        {
            case ScheduleDecision.Due:
                if (await ExecuteSafelyAsync(execute, task.Name, cancellationToken).ConfigureAwait(false))
                {
                    ScheduleCalculator.Advance(task.Schedule, now);
                }

                changed = true;
                break;

            case ScheduleDecision.Missed:
                _logger.Info($"检测到错过的执行点，开始补跑", task.Name);

                if (await ExecuteSafelyAsync(execute, task.Name, cancellationToken).ConfigureAwait(false))
                {
                    ScheduleCalculator.Advance(task.Schedule, now);
                }

                changed = true;
                break;

            case ScheduleDecision.ExpiredSkipped:
                _logger.Info("跳过错过的执行点（未开启补跑）", task.Name);
                ScheduleCalculator.SkipMissed(task.Schedule, now);
                changed = true;
                break;
        }

        if (changed)
        {
            updates.Add(Capture(task, kind));
        }
    }

    /// <summary>
    /// 执行单个任务。返回是否「正常返回」——执行器内部已兜底异常，
    /// 这里只拦截执行器之外（如引擎创建）的意外，返回 false 表示不应推进调度。
    /// </summary>
    private async Task<bool> ExecuteSafelyAsync(
        Func<CancellationToken, Task<TaskRunResult>> execute,
        string taskName,
        CancellationToken cancellationToken)
    {
        try
        {
            await execute(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"执行任务失败：{ex.Message}", taskName, ex);
            return false;
        }
    }

    /// <summary>
    /// 把调度状态写回配置。按 Id 重新定位，只更新两个调度字段，
    /// 避免覆盖界面端在任务执行期间对频率、时刻等其它配置的修改。
    /// </summary>
    private void ApplyUpdates(IReadOnlyList<ScheduleUpdate> updates)
    {
        if (updates.Count == 0)
        {
            return;
        }

        try
        {
            _configStore.Update(current =>
            {
                foreach (var update in updates)
                {
                    var schedule = FindSchedule(current, update.Kind, update.TaskId);
                    if (schedule is null)
                    {
                        continue;
                    }

                    schedule.LastRunUtc = update.LastRunUtc;
                    schedule.NextRunUtc = update.NextRunUtc;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error($"保存调度状态失败：{ex.Message}", "调度", ex);
        }
    }

    private static ScheduleConfig? FindSchedule(AppConfig config, TaskKind kind, Guid id) => kind switch
    {
        TaskKind.Compression => config.CompressionTasks.FirstOrDefault(t => t.Id == id)?.Schedule,
        TaskKind.Cleanup => config.CleanupTasks.FirstOrDefault(t => t.Id == id)?.Schedule,
        TaskKind.Sync => config.SyncTasks.FirstOrDefault(t => t.Id == id)?.Schedule,
        TaskKind.Program => config.ProgramTasks.FirstOrDefault(t => t.Id == id)?.Schedule,
        _ => null
    };

    private static ScheduleUpdate Capture<T>(T task, TaskKind kind) where T : IScheduledTask =>
        new(task.Id, kind, task.Schedule.LastRunUtc, task.Schedule.NextRunUtc);

    private readonly record struct ScheduleUpdate(
        Guid TaskId,
        TaskKind Kind,
        DateTimeOffset? LastRunUtc,
        DateTimeOffset? NextRunUtc);
}
