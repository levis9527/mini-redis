using MiniRedis.Core.Commands;
using MiniRedis.Core.Keys;
using MiniRedis.Core.Protocol;
using MiniRedis.Core.Scanning;
using MiniRedis.Core.Storage;

namespace MiniRedis.Core.Runtime;

public sealed record AuditContext(string Connection, int Database);

public sealed class TerminalResult
{
    public bool Sent { get; init; }
    public bool Ok { get; init; }
    public bool Cancelled { get; init; }
    public bool NeedsConfirmation { get; init; }
    public string Echo { get; init; } = "";
    public string Output { get; init; } = "";
    public bool AuditFailed { get; init; }
    public string? AuditFailure { get; init; }
    public ArgVisibility Visibility { get; init; } = ArgVisibility.HideAll;
    public ParsedCommand? Command { get; init; }
}

public sealed class CommandRunner
{
    private static readonly HashSet<string> AppReadCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "PING", "SCAN", "EXISTS", "TYPE", "PTTL", "TTL", "STRLEN", "GET", "GETRANGE",
        "HSCAN", "HLEN", "SSCAN", "SCARD", "ZSCAN", "ZCARD", "LRANGE", "LLEN", "KEYS",
        "OBJECT", "MEMORY"
    };

    public async Task<TerminalResult> RunTerminalAsync(
        IRedisSession redis,
        JsonlAuditLog audit,
        AuditContext context,
        string line,
        bool readOnly,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (!CommandParser.TryParse(line, out var parsed, out var parseError) || parsed == null)
        {
            return new TerminalResult { Output = parseError ?? "无法解析命令。", Echo = "" };
        }

        if (parsed.Name is "HELP")
        {
            return new TerminalResult
            {
                Ok = true,
                Echo = "HELP",
                Output = HelpText,
                Visibility = ArgVisibility.ShowAll,
                Command = parsed
            };
        }

        if (parsed.Name is "CLEAR" or "CLS")
        {
            return new TerminalResult
            {
                Ok = true,
                Echo = parsed.Name,
                Output = "",
                Visibility = ArgVisibility.ShowAll,
                Command = parsed
            };
        }

        CommandMetadata? metadata = null;
        var decision = CommandPolicy.Evaluate(parsed, readOnly, metadata: readOnly ? null : PlaceholderWritable(parsed), confirmed: false);
        if (decision.Kind is CommandDecisionKind.HardDenied or CommandDecisionKind.UnsupportedTransport)
            return Refuse(parsed, decision.Message);

        if (readOnly || !CommandSanitizer.IsCredential(parsed) && !CommandSanitizer.IsScript(parsed))
        {
            try
            {
                metadata = await redis.DescribeCommandAsync(parsed.Name, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new TerminalResult { Cancelled = true, Echo = parsed.Name, Output = "已取消，命令未发送。", Command = parsed };
            }
            catch (Exception ex)
            {
                metadata = null;
                if (readOnly)
                    return Refuse(parsed, "无法取得命令元数据，只读模式已拒绝。命令未发送。" + CommandSanitizer.SanitizeConnectError(ex.Message));
            }
        }

        decision = CommandPolicy.Evaluate(parsed, readOnly, metadata, confirmed);
        var visibility = CommandSanitizer.VisibilityFor(parsed, metadata);
        var echo = CommandSanitizer.SanitizeCommand(parsed, visibility);
        if (decision.Kind != CommandDecisionKind.Allowed)
        {
            return new TerminalResult
            {
                NeedsConfirmation = decision.Kind == CommandDecisionKind.NeedsConfirmation,
                Echo = echo,
                Output = decision.Message,
                Visibility = visibility,
                Command = parsed
            };
        }

        var sent = await SendAndAuditAsync(redis, audit, context, parsed, ToArgs(parsed), visibility, echo, cancellationToken).ConfigureAwait(false);
        return sent.Result;
    }

    public async Task<RedisReply> ExecuteAppAsync(
        IRedisSession redis,
        JsonlAuditLog audit,
        AuditContext context,
        IReadOnlyList<RedisArg> arguments,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        if (arguments.Count == 0)
            return RedisReply.ErrorOf("缺少命令。");
        var tokens = arguments.Select(arg => arg.IsUtf8 ? arg.Utf8 : arg.Display).ToArray();
        var parsed = CommandParser.FromTokens(tokens);
        if (CommandPolicy.Evaluate(parsed, readOnly: false, metadata: null, confirmed: true).Kind == CommandDecisionKind.HardDenied)
            return RedisReply.ErrorOf("已拒绝 " + parsed.Name + "，命令未发送。");
        if (readOnly && !AppReadCommands.Contains(parsed.Name))
            return RedisReply.ErrorOf("当前是只读模式，界面不会发送 " + parsed.Name + "。");

        var visibility = AppReadCommands.Contains(parsed.Name) && !CommandSanitizer.IsValueCommand(parsed)
            ? ArgVisibility.ShowAll
            : CommandSanitizer.IsCredential(parsed) || CommandSanitizer.IsScript(parsed)
                ? ArgVisibility.HideAll
                : ArgVisibility.HideValues;
        var sent = await SendAndAuditAsync(
            redis,
            audit,
            context,
            parsed,
            arguments,
            visibility,
            CommandSanitizer.SanitizeCommand(parsed, visibility),
            cancellationToken).ConfigureAwait(false);
        if (sent.Result.Cancelled)
            throw new OperationCanceledException(cancellationToken);
        if (!sent.Result.Sent || !sent.Result.Ok)
            return sent.Reply ?? RedisReply.ErrorOf(sent.Result.Output);
        return sent.Reply ?? RedisReply.Simple("OK");
    }

    private readonly record struct Sent(TerminalResult Result, RedisReply? Reply);

    private async Task<Sent> SendAndAuditAsync(
        IRedisSession redis,
        JsonlAuditLog audit,
        AuditContext context,
        ParsedCommand parsed,
        IReadOnlyList<RedisArg> arguments,
        ArgVisibility visibility,
        string echo,
        CancellationToken cancellationToken)
    {
        RedisReply reply;
        try
        {
            reply = await redis.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new Sent(new TerminalResult
            {
                Cancelled = true,
                Echo = echo,
                Output = "已取消。若这条命令已经到达服务器，界面不会把它记成成功，列表游标也不会前进。",
                Visibility = visibility,
                Command = parsed
            }, null);
        }
        catch (Exception ex)
        {
            reply = RedisReply.ErrorOf(ex.Message);
        }

        var queued = ValueGuard.IsQueued(reply);
        var error = reply.IsError
            ? CommandSanitizer.SanitizeServerError(reply.ErrorText, parsed, visibility)
            : queued ? "已入队，尚未执行" : "";
        var formatted = reply.IsError
            ? error
            : ReplyFormatter.Format(reply, AppLimits.ReplyMaxChars, out _);
        if (queued)
            formatted += "\n这条回复是 QUEUED，写入还没有执行。";
        var clock = ShanghaiClock.Format(DateTimeOffset.UtcNow);
        var write = audit.Append(new AuditEntry
        {
            Time = clock.Local,
            TimeUtc = clock.Utc,
            Connection = context.Connection,
            Db = redis.Database,
            Command = echo,
            Ok = !reply.IsError && !queued,
            Error = error
        });
        return new Sent(new TerminalResult
        {
            Sent = true,
            Ok = !reply.IsError && !queued,
            Echo = echo,
            Output = formatted,
            Visibility = visibility,
            Command = parsed,
            AuditFailed = !write.Written,
            AuditFailure = write.Failure
        }, reply);
    }

    private static TerminalResult Refuse(ParsedCommand parsed, string message) => new()
    {
        Echo = CommandSanitizer.SanitizeCommand(parsed, CommandSanitizer.IsCredential(parsed) || CommandSanitizer.IsScript(parsed)
            ? ArgVisibility.HideAll
            : ArgVisibility.ShowAll),
        Output = message,
        Command = parsed
    };

    private static CommandMetadata PlaceholderWritable(ParsedCommand parsed) =>
        CommandSanitizer.IsValueCommand(parsed)
            ? new CommandMetadata { Name = parsed.Name, Flags = ["write"] }
            : new CommandMetadata { Name = parsed.Name, Flags = ["readonly"] };

    private static RedisArg[] ToArgs(ParsedCommand parsed)
    {
        var args = new RedisArg[parsed.Args.Count + 1];
        args[0] = RedisArg.FromText(parsed.Command);
        for (var i = 0; i < parsed.Args.Count; i++)
            args[i + 1] = RedisArg.FromText(parsed.Args[i]);
        return args;
    }

    private const string HelpText =
        "命令面板把这一行 Redis 命令发给当前连接，不是操作系统 shell。\n"
        + "硬拒绝 FLUSHDB、FLUSHALL、SHUTDOWN，确认也不能放行。\n"
        + "只读模式只放行 COMMAND INFO 标成 readonly 且没有 write 的命令；拿不到元数据就拒绝。\n"
        + "可写模式不会用命令名挡住 EVAL、FCALL、MULTI 或模块命令的间接效果。那部分要靠服务器 ACL。\n"
        + "SUBSCRIBE、MONITOR、SYNC 一类会占用连接的命令不发送。";
}
