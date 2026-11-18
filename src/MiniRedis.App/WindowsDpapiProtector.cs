using System.Security.Cryptography;
using MiniRedis.Core.Storage;

namespace MiniRedis.App;

public sealed class WindowsDpapiProtector : ISecretProtector
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public string ScopeDescription =>
        "使用 Windows DPAPI 的 CurrentUser 范围加密，只属于当前 Windows 用户和这台电脑。不用 LocalMachine。换用户或换机器后无法解密，复制程序目录不会把可用密码一起带走。";

    public byte[] Protect(byte[] plaintext)
    {
        if (!IsAvailable)
            throw new PlatformNotSupportedException(ScopeDescription);
        return ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] payload)
    {
        if (!IsAvailable)
            throw new PlatformNotSupportedException(ScopeDescription);
        return ProtectedData.Unprotect(payload, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }
}
