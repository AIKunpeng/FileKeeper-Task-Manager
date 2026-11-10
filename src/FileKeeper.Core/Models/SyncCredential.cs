using System.Text.Json.Serialization;
using FileKeeper.Core.Security;

namespace FileKeeper.Core.Models;

/// <summary>
/// 同步任务的认证信息。
///
/// <b>序列化时只落密文</b>：真正的密码与私钥口令通过 <see cref="Password"/> / <see cref="Passphrase"/>
/// 属性进出，二者内部调用 <see cref="CredentialProtector"/> 做 DPAPI 加解密，
/// 明文字段被标记为 <c>[JsonIgnore]</c>，因此明文永远不会进入 config.json。
/// </summary>
public sealed class SyncCredential
{
    /// <summary>认证方式，仅对 SFTP 有意义；FTP / FTPS 始终使用密码</summary>
    public SftpAuthMode AuthMode { get; set; } = SftpAuthMode.Password;

    /// <summary>DPAPI 加密后的密码（Base64）</summary>
    public string? PasswordCipher { get; set; }

    /// <summary>私钥文件路径（PEM / OpenSSH 格式）</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>DPAPI 加密后的私钥口令（Base64）</summary>
    public string? PassphraseCipher { get; set; }

    /// <summary>读写明文密码（经 DPAPI 转换），不参与序列化</summary>
    [JsonIgnore]
    public string? Password
    {
        get => CredentialProtector.Unprotect(PasswordCipher);
        set => PasswordCipher = CredentialProtector.Protect(value);
    }

    /// <summary>读写明文私钥口令（经 DPAPI 转换），不参与序列化</summary>
    [JsonIgnore]
    public string? Passphrase
    {
        get => CredentialProtector.Unprotect(PassphraseCipher);
        set => PassphraseCipher = CredentialProtector.Protect(value);
    }

    /// <summary>是否已设置密码（用于界面显示「已设置」而不是回显明文）</summary>
    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(PasswordCipher);

    /// <summary>是否已设置私钥口令</summary>
    [JsonIgnore]
    public bool HasPassphrase => !string.IsNullOrEmpty(PassphraseCipher);

    public SyncCredential Clone() => new()
    {
        AuthMode = AuthMode,
        PasswordCipher = PasswordCipher,
        PrivateKeyPath = PrivateKeyPath,
        PassphraseCipher = PassphraseCipher
    };
}
