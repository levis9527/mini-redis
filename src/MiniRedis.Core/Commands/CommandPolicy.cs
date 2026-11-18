using MiniRedis.Core.Protocol;

namespace MiniRedis.Core.Commands;

public enum CommandDecisionKind
{
    ParseError,
    HardDenied,
    UnsupportedTransport,
    DeniedReadOnly,
    NeedsConfirmation,
    Allowed
}

public sealed class CommandDecision
{
    public required CommandDecisionKind Kind { get; init; }
    public required string Message { get; init; }
    public ParsedCommand? Command { get; init; }
}

public static class CommandPolicy
{
    private static readonly HashSet<string> HardDeny = new(StringComparer.OrdinalIgnoreCase)
    {
        "FLUSHDB", "FLUSHALL", "SHUTDOWN"
    };

    private static readonly HashSet<string> TransportBlock = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUBSCRIBE", "PSUBSCRIBE", "SSUBSCRIBE", "MONITOR", "SYNC", "PSYNC"
    };

    public static CommandDecision Evaluate(ParsedCommand command, bool readOnly, CommandMetadata? metadata, bool confirmed)
    {
        if (HardDeny.Contains(command.Name))
        {
            return new CommandDecision
            {
                Kind = CommandDecisionKind.HardDenied,
                Command = command,
                Message = "已拒绝 " + command.Name + "。FLUSHDB、FLUSHALL、SHUTDOWN 不能执行，也不能通过确认或在参数里写 * 来放行。命令未发送。"
            };
        }

        if (TransportBlock.Contains(command.Name))
        {
            return new CommandDecision
            {
                Kind = CommandDecisionKind.UnsupportedTransport,
                Command = command,
                Message = command.Name + " 会占用连接，不再是一问一答。当前客户端不发送这类命令。这是传输限制，不是一份安全白名单。命令未发送。"
            };
        }

        if (readOnly && !IsReadOnlyCommand(metadata))
        {
            var reason = metadata == null
                ? "没有拿到 COMMAND 元数据"
                : metadata.Unknown
                    ? "服务器不认识这条命令，无法分类"
                    : "COMMAND 标志不是只读";
            return new CommandDecision
            {
                Kind = CommandDecisionKind.DeniedReadOnly,
                Command = command,
                Message = "当前是只读模式，已拒绝 " + command.Name + "（" + reason + "）。命令未发送。只读模式无法分类时会拒绝，而不是放行。"
            };
        }

        var reasons = ConfirmationReasons(command);
        if (reasons.Count > 0 && !confirmed)
        {
            return new CommandDecision
            {
                Kind = CommandDecisionKind.NeedsConfirmation,
                Command = command,
                Message = string.Join("\n", reasons) + "\n取消不会发送命令。"
            };
        }

        return new CommandDecision
        {
            Kind = CommandDecisionKind.Allowed,
            Command = command,
            Message = ""
        };
    }

    public static bool IsReadOnlyCommand(CommandMetadata? metadata) => metadata is { IsReadOnly: true };

    private static List<string> ConfirmationReasons(ParsedCommand command)
    {
        var reasons = new List<string>();
        if (command.Name == "KEYS")
        {
            reasons.Add("KEYS 会在服务器上遍历当前逻辑库再匹配，即使模式里没有 * 也一样。它是一条阻塞命令，一次返回全部匹配，不能靠界面分页减轻这次扫描的成本。这不是 SCAN。");
        }

        if (command.Name is "DEL" or "UNLINK")
        {
            var keys = command.Args.Count == 0 ? "（没有键名）" : string.Join(", ", command.Args);
            reasons.Add(command.Name + " 会删除这些键：" + keys + "。这是按键删除，不是清库。");
        }

        if (command.ContainsStar && command.Name != "KEYS")
        {
            reasons.Add("命令里含有 *。请确认这就是要发送的内容。含 * 不等于 KEYS，也不等于清库。");
        }
        else if (command.ContainsStar && command.Name == "KEYS")
        {
            reasons.Add("模式里还有 *。上面的 KEYS 风险已经包含这种模式，不会再单独确认一次。");
        }

        return reasons;
    }
}
