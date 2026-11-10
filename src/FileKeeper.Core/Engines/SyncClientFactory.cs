using FileKeeper.Core.Models;

namespace FileKeeper.Core.Engines;

/// <summary>
/// 按协议创建同步客户端，并对任务配置做前置校验。
/// </summary>
public static class SyncClientFactory
{
    public static ISyncClient Create(SyncTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return task.Protocol switch
        {
            SyncProtocol.Sftp => new SftpSyncClient(task),
            SyncProtocol.Ftp or SyncProtocol.FtpsExplicit => new FtpSyncClient(task),
            _ => throw new ArchiveEngineException($"不支持的同步协议：{task.Protocol}")
        };
    }

    /// <summary>
    /// 校验同步任务配置是否完整。返回 null 表示可运行，否则返回人类可读的原因。
    /// 界面保存前与调度执行前都应调用，避免运行时才发现配置缺失。
    /// </summary>
    public static string? Validate(SyncTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Host))
        {
            return "未填写服务器地址";
        }

        // Port 为 0 表示「按协议取默认值」，Normalize() 会补全，不算错误；
        // 只有越界才是真错误。否则端口检查会挡住后面更关键的缺失项。
        if (task.Port > 65535)
        {
            return $"端口非法：{task.Port}";
        }

        if (string.IsNullOrWhiteSpace(task.Username))
        {
            return "未填写用户名";
        }

        if (string.IsNullOrWhiteSpace(task.LocalDirectory))
        {
            return "未选择本地目录";
        }

        if (task.Protocol == SyncProtocol.Sftp && task.Credential.AuthMode == SftpAuthMode.PrivateKey)
        {
            if (string.IsNullOrWhiteSpace(task.Credential.PrivateKeyPath))
            {
                return "未选择私钥文件";
            }

            if (!File.Exists(task.Credential.PrivateKeyPath))
            {
                return $"私钥文件不存在：{task.Credential.PrivateKeyPath}";
            }
        }
        else if (!task.Credential.HasPassword)
        {
            return "未设置密码";
        }

        return null;
    }
}
