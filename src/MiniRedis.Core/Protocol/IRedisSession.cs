namespace MiniRedis.Core.Protocol;

public interface IRedisSession
{
    int Database { get; }

    Task<RedisReply> ExecuteAsync(IReadOnlyList<RedisArg> arguments, CancellationToken cancellationToken);

    Task<CommandMetadata?> DescribeCommandAsync(string commandName, CancellationToken cancellationToken);
}

public sealed class CommandMetadata
{
    public required string Name { get; init; }
    public bool Unknown { get; init; }
    public IReadOnlyList<string> Flags { get; init; } = [];

    public bool HasFlag(string flag) =>
        Flags.Any(item => item.Equals(flag, StringComparison.OrdinalIgnoreCase));

    public bool IsReadOnly => !Unknown && HasFlag("readonly") && !HasFlag("write");

    public static CommandMetadata UnknownCommand(string name) => new()
    {
        Name = name,
        Unknown = true
    };
}

public sealed class RedisConnectException : Exception
{
    public RedisConnectException(string message) : base(message)
    {
    }

    public override string ToString() => Message;
}
