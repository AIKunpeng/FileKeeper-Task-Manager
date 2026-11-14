using System.Text.Json;
using FileKeeper.Core.Models;

namespace FileKeeper.Core.Configuration;

/// <summary>任务导入导出文件中的一条任务：类型 + 具体任务对象</summary>
public sealed class TaskExportEntry
{
    public string Kind { get; set; } = string.Empty;

    public JsonElement Task { get; set; }
}

/// <summary>任务导入导出文件的顶层结构</summary>
public sealed class TaskExportFile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;

    public string Machine { get; set; } = Environment.MachineName;

    public List<TaskExportEntry> Tasks { get; set; } = new();
}

/// <summary>导入结果中的一项：任务类型 + 反序列化出的任务对象</summary>
public sealed record TaskImportItem(TaskKind Kind, IScheduledTask Task);

/// <summary>
/// 任务配置的导入导出。导出文件是带版本号的 JSON，任务对象按类型完整序列化，
/// 与 config.json 的字段约定一致，可直接人工审阅或手工修改。
///
/// 安全说明：同步任务的凭据在序列化层就是 DPAPI 密文（明文属性被 JsonIgnore），
/// 导出文件不包含明文密码；压缩任务的 ZIP 密码在配置中为明文存储，
/// 会原样进入导出文件——导出文件应妥善保管。
/// </summary>
public static class TaskTransferService
{
    /// <summary>
    /// 把任务列表写入导出文件。
    /// </summary>
    public static void Export(string path, IEnumerable<(TaskKind Kind, IScheduledTask Task)> tasks)
    {
        var file = new TaskExportFile();

        foreach (var (kind, task) in tasks)
        {
            file.Tasks.Add(new TaskExportEntry
            {
                Kind = kind.ToString(),
                // 必须按运行时类型序列化：按 IScheduledTask 序列化只会保留接口上的
                // Id/Name/Enabled/Schedule 四个成员，其余字段全部丢失
                Task = JsonSerializer.SerializeToElement(task, task.GetType(), ConfigStore.SerializerOptions)
            });
        }

        File.WriteAllText(path, JsonSerializer.Serialize(file, ConfigStore.SerializerOptions));
    }

    /// <summary>
    /// 从导出文件读回任务列表。文件损坏、版本不认识、任务对象不完整都会抛异常，
    /// 由调用方决定如何呈现。
    /// </summary>
    public static IReadOnlyList<TaskImportItem> Import(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        if (!root.TryGetProperty("version", out var versionElement) || versionElement.GetInt32() != TaskExportFile.CurrentVersion)
        {
            throw new InvalidDataException("文件版本不受支持，可能来自更新或更旧版本的程序。");
        }

        if (!root.TryGetProperty("tasks", out var tasksElement) || tasksElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("文件中缺少任务数据（tasks）。");
        }

        var items = new List<TaskImportItem>();

        foreach (var entryElement in tasksElement.EnumerateArray())
        {
            if (!entryElement.TryGetProperty("kind", out var kindElement) ||
                !entryElement.TryGetProperty("task", out var taskElement))
            {
                throw new InvalidDataException("文件中存在不完整的任务条目。");
            }

            if (!Enum.TryParse<TaskKind>(kindElement.GetString(), out var kind))
            {
                throw new InvalidDataException($"未知的任务类型：{kindElement.GetString()}");
            }

            IScheduledTask? task = kind switch
            {
                TaskKind.Compression => JsonSerializer.Deserialize<CompressionTask>(taskElement.GetRawText(), ConfigStore.SerializerOptions),
                TaskKind.Cleanup => JsonSerializer.Deserialize<CleanupTask>(taskElement.GetRawText(), ConfigStore.SerializerOptions),
                TaskKind.Sync => JsonSerializer.Deserialize<SyncTask>(taskElement.GetRawText(), ConfigStore.SerializerOptions),
                TaskKind.Program => JsonSerializer.Deserialize<ProgramTask>(taskElement.GetRawText(), ConfigStore.SerializerOptions),
                _ => null
            };

            if (task is null)
            {
                throw new InvalidDataException($"任务「{kind}」的数据无法解析。");
            }

            // 归一化路径、扩展名等字段；Normalize 是各具体类型的方法
            switch (task)
            {
                case CompressionTask compression:
                    compression.Normalize();
                    break;

                case CleanupTask cleanup:
                    cleanup.Normalize();
                    break;

                case SyncTask sync:
                    sync.Normalize();
                    break;

                case ProgramTask program:
                    program.Normalize();
                    break;
            }

            items.Add(new TaskImportItem(kind, task));
        }

        if (items.Count == 0)
        {
            throw new InvalidDataException("文件中没有任务。");
        }

        return items;
    }
}
