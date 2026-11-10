namespace FileKeeper.Core.Models;

/// <summary>
/// 带调度的任务。让服务端的调度循环可以统一处理压缩 / 清理 / 同步三类任务，
/// 无需反射按属性名取值。
/// </summary>
public interface IScheduledTask
{
    Guid Id { get; }

    string Name { get; }

    bool Enabled { get; }

    ScheduleConfig Schedule { get; }
}
