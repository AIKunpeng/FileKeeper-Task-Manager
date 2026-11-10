using CommunityToolkit.Mvvm.ComponentModel;
using FileKeeper.Core.Models;
using FileKeeper.Core.Scheduling;

namespace FileKeeper.App.ViewModels;

/// <summary>
/// 任务总览中一张卡片的数据。
/// 配置字段做成一次成型的快照而不是逐属性绑定：配置由服务进程负责回写并可能随时热重载，
/// 快照式重建能保证界面看到的一定是某一时刻的完整状态，不会读到半改的对象。
/// 唯一的例外是 <see cref="IsRunning"/>——它描述的是「此刻」而不是「某次配置快照」，
/// 所以做成可观察属性，由执行流程直接在已生成的卡片上原地更新。
/// </summary>
public sealed partial class TaskItemViewModel : ObservableObject
{
    private TaskItemViewModel(
        Guid id,
        TaskKind kind,
        string name,
        bool enabled,
        string locationLabel,
        string locationText,
        ScheduleConfig schedule)
    {
        Id = id;
        Kind = kind;
        Name = name;
        Enabled = enabled;
        LocationLabel = locationLabel;
        LocationText = locationText;
        ScheduleText = ScheduleCalculator.Describe(schedule);
        NextRunText = Format(schedule.NextRunUtc, "尚未排定");
        LastRunText = Format(schedule.LastRunUtc, "从未执行");
    }

    public Guid Id { get; }

    public TaskKind Kind { get; }

    public string Name { get; }

    public bool Enabled { get; }

    public string LocationLabel { get; }

    public string LocationText { get; }

    public string ScheduleText { get; }

    public string NextRunText { get; }

    public string LastRunText { get; }

    /// <summary>该任务正在执行中（界面端手动触发期间）。驱动卡片的「执行中」标记与终止按钮</summary>
    [ObservableProperty]
    private bool _isRunning;

    public static TaskItemViewModel FromModel(CompressionTask task) => new(
        task.Id,
        TaskKind.Compression,
        task.Name,
        task.Enabled,
        "源目录",
        task.SourceDirectory,
        task.Schedule);

    public static TaskItemViewModel FromModel(CleanupTask task) => new(
        task.Id,
        TaskKind.Cleanup,
        task.Name,
        task.Enabled,
        "目录",
        task.TargetDirectory,
        task.Schedule);

    public static TaskItemViewModel FromModel(SyncTask task) => new(
        task.Id,
        TaskKind.Sync,
        task.Name,
        task.Enabled,
        "服务器",
        $"{task.Host}:{task.Port}",
        task.Schedule);

    public static TaskItemViewModel FromModel(ProgramTask task) => new(
        task.Id,
        TaskKind.Program,
        task.Name,
        task.Enabled,
        "程序",
        task.FilePath,
        task.Schedule);

    private static string Format(DateTimeOffset? timestamp, string placeholder) =>
        timestamp is null ? placeholder : timestamp.Value.ToLocalTime().ToString("MM-dd HH:mm");
}
