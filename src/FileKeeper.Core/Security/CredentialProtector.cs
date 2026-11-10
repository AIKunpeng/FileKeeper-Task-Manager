using System.Security.Cryptography;
using System.Text;

namespace FileKeeper.Core.Security;

/// <summary>
/// 同步任务凭据的加解密（DPAPI）。
///
/// <b>必须使用 <see cref="DataProtectionScope.LocalMachine"/></b>：
/// 界面端以管理员用户运行、服务端以 LocalSystem 运行，两者 DPAPI 的用户上下文不同。
/// 若使用默认的 <see cref="DataProtectionScope.CurrentUser"/>，
/// 界面加密的密码在服务端将无法解密，同步任务会在无人值守时静默失败。
/// 机器级作用域的代价是本机任意用户可解密，而该机器本就承载着服务，风险可接受。
/// </summary>
public static class CredentialProtector
{
    /// <summary>
    /// 加密明文，返回 Base64 密文。明文为空时返回 null（表示「未设置」）。
    /// </summary>
    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            // DPAPI 仅 Windows 可用，本程序面向 Windows，非 Windows 下不做伪加密
            return null;
        }

        try
        {
            var encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainText), null, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(encrypted);
        }
        catch (Exception)
        {
            // 加密失败时宁可不保存，也不保存明文
            return null;
        }
    }

    /// <summary>
    /// 解密 Base64 密文。密文为空或解密失败时返回 null。
    /// </summary>
    public static string? Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(cipherText), null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception)
        {
            // 解密失败通常是「换机器打开配置」或密文被外部改动，不能把异常抛给调度循环
            return null;
        }
    }
}
