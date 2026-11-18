using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniRedis.Core.Storage;

public sealed class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6379;
    public int Database { get; set; }
    public string Username { get; set; } = "";
    public bool UseTls { get; set; }
    public bool AllowUntrustedTls { get; set; }
    public int ConnectTimeoutMs { get; set; } = 5000;
    public int CommandTimeoutMs { get; set; } = 5000;
    public bool SavePassword { get; set; }
    public string? ProtectedPasswordBase64 { get; set; }
}

public sealed class ConnectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ConnectionStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public IReadOnlyList<ConnectionProfile> Load()
    {
        if (!File.Exists(Path))
            return [];
        var json = File.ReadAllText(Path);
        var list = JsonSerializer.Deserialize<List<ConnectionProfile>>(json, JsonOptions) ?? [];
        return list;
    }

    public void Save(IReadOnlyList<ConnectionProfile> profiles)
    {
        foreach (var profile in profiles)
            Validate(profile);
        var directory = System.IO.Path.GetDirectoryName(Path) ?? throw new InvalidOperationException("配置路径无效。");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(profiles, JsonOptions);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, Path, overwrite: true);
    }

    public void SetPassword(ConnectionProfile profile, string? password, bool save, ISecretProtector protector)
    {
        if (!save || string.IsNullOrEmpty(password))
        {
            profile.SavePassword = false;
            profile.ProtectedPasswordBase64 = null;
            return;
        }

        if (!protector.IsAvailable)
            throw new PasswordProtectionException("当前环境不能用 Windows DPAPI（当前用户）保存密码。密码不会写入配置文件。");
        var blob = protector.Protect(Encoding.UTF8.GetBytes(password));
        profile.SavePassword = true;
        profile.ProtectedPasswordBase64 = Convert.ToBase64String(blob);
    }

    public string? TryUnprotect(ConnectionProfile profile, ISecretProtector protector, out string? error)
    {
        error = null;
        if (!profile.SavePassword || string.IsNullOrEmpty(profile.ProtectedPasswordBase64))
            return null;
        if (!protector.IsAvailable)
        {
            error = "这台环境没有 DPAPI，不能解开已保存的密码。请重新输入，密码只留在本次运行中。";
            return null;
        }

        try
        {
            var bytes = protector.Unprotect(Convert.FromBase64String(profile.ProtectedPasswordBase64));
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is FormatException or PlatformNotSupportedException or System.Security.Cryptography.CryptographicException)
        {
            error = "已保存的密码无法解密。DPAPI 密文只属于当初的 Windows 用户和这台电脑，复制文件夹不会把密码一起带走。请重新输入。";
            return null;
        }
    }

    public static void Validate(ConnectionProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("请填写连接名称。");
        if (string.IsNullOrWhiteSpace(profile.Host))
            throw new InvalidOperationException("请填写主机。");
        if (profile.Port is < 1 or > 65535)
            throw new InvalidOperationException("端口无效。");
        if (profile.Database < 0 || profile.Database > 1023)
            throw new InvalidOperationException("逻辑库编号无效。");
        if (profile.ConnectTimeoutMs is < 100 or > 120000)
            throw new InvalidOperationException("连接超时超出范围。");
        if (profile.CommandTimeoutMs is < 100 or > 600000)
            throw new InvalidOperationException("命令超时超出范围。");
    }
}
