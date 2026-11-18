namespace MiniRedis.Core.Storage;

public sealed class StorageNotWritableException : Exception
{
    public StorageNotWritableException(string root, Exception? inner = null)
        : base("无法在程序目录写入配置或日志：" + root + "。Mini Redis 只使用可执行文件所在目录，不会改存到 AppData 或其他位置。请把程序放到当前用户可写的文件夹后再运行。", inner)
    {
        Root = root;
    }

    public string Root { get; }
}

public sealed class PasswordProtectionException : Exception
{
    public PasswordProtectionException(string message) : base(message)
    {
    }
}

public interface ISecretProtector
{
    bool IsAvailable { get; }
    string ScopeDescription { get; }
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] payload);
}

public sealed class AppStorage
{
    private AppStorage(string root)
    {
        Root = root;
        DataDirectory = Path.Combine(root, "data");
        LogDirectory = Path.Combine(root, "logs");
        ConnectionFile = Path.Combine(DataDirectory, "connections.json");
        AuditFile = Path.Combine(LogDirectory, "audit.jsonl");
    }

    public string Root { get; }
    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public string ConnectionFile { get; }
    public string AuditFile { get; }

    public static AppStorage Open(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new StorageNotWritableException(root ?? "");
        var full = Path.GetFullPath(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(full, "data"));
            Directory.CreateDirectory(Path.Combine(full, "logs"));
            Probe(Path.Combine(full, "data"));
            Probe(Path.Combine(full, "logs"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageNotWritableException(full, ex);
        }

        return new AppStorage(full);
    }

    private static void Probe(string directory)
    {
        var path = Path.Combine(directory, ".write-probe");
        File.WriteAllText(path, "ok");
        File.Delete(path);
    }
}
