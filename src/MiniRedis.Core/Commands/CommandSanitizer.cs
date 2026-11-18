using System.Text.RegularExpressions;
using MiniRedis.Core.Protocol;

namespace MiniRedis.Core.Commands;

public enum ArgVisibility
{
    ShowAll,
    HideValues,
    HideAll
}

public static partial class CommandSanitizer
{
    private static readonly HashSet<string> CredentialCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "AUTH", "HELLO", "ACL", "MIGRATE", "CONFIG"
    };

    private static readonly HashSet<string> ScriptCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "EVAL", "EVALSHA", "EVAL_RO", "EVALSHA_RO", "FCALL", "FCALL_RO", "SCRIPT", "FUNCTION"
    };

    private static readonly HashSet<string> ValueCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "SET", "SETEX", "PSETEX", "SETNX", "GETSET", "GETEX", "MSET", "MSETNX", "APPEND", "SETRANGE",
        "HSET", "HMSET", "HSETNX", "LPUSH", "RPUSH", "LPUSHX", "RPUSHX", "LSET", "LINSERT",
        "SADD", "ZADD", "ZINCRBY", "XADD", "RESTORE", "ECHO", "SETBIT"
    };

    public static bool IsCredential(ParsedCommand command) => CredentialCommands.Contains(command.Name);

    public static bool IsScript(ParsedCommand command) => ScriptCommands.Contains(command.Name);

    public static bool IsValueCommand(ParsedCommand command) => ValueCommands.Contains(command.Name);

    public static ArgVisibility VisibilityFor(ParsedCommand command, CommandMetadata? metadata)
    {
        if (IsCredential(command) || IsScript(command))
            return ArgVisibility.HideAll;
        if (metadata == null || metadata.Unknown)
            return command.Args.Count > 0 ? ArgVisibility.HideAll : ArgVisibility.ShowAll;
        if (metadata.HasFlag("write") || IsValueCommand(command))
            return ArgVisibility.HideValues;
        if (metadata.IsReadOnly)
            return ArgVisibility.ShowAll;
        return command.Args.Count > 0 ? ArgVisibility.HideAll : ArgVisibility.ShowAll;
    }

    public static string SanitizeCommand(ParsedCommand command, ArgVisibility visibility)
    {
        if (visibility == ArgVisibility.HideAll)
            return command.Args.Count == 0 ? command.Name : command.Name + " [参数已隐藏]";
        if (visibility == ArgVisibility.HideValues)
            return FormatHiddenValues(command);
        var text = command.Name + (command.Args.Count == 0 ? "" : " " + string.Join(' ', command.Args));
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    public static string SanitizeServerError(string? error, ParsedCommand command, ArgVisibility visibility)
    {
        if (visibility == ArgVisibility.HideAll)
            return "执行失败（详情已隐藏）";
        if (string.IsNullOrEmpty(error))
            return "";

        var text = error.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        text = PasswordAssignment().Replace(text, "$1[已隐藏]");
        if (visibility == ArgVisibility.HideValues)
        {
            var values = ValueArguments(command);
            if (values.Any(item => item.Length > 0 && item.Length < 3))
                return "执行失败（详情已隐藏）";
            foreach (var value in values.Where(item => item.Length >= 3).OrderByDescending(item => item.Length))
                text = text.Replace(value, "[已隐藏]", StringComparison.Ordinal);
        }

        if (text.Length > 180)
            text = text[..180] + "…";
        return text;
    }

    public static string SanitizeConnectError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "连接失败。";
        var text = PasswordAssignment().Replace(error, "$1[已隐藏]");
        if (text.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("passwd", StringComparison.OrdinalIgnoreCase))
            return "连接失败（详情已隐藏）";
        return text.Length > 240 ? text[..240] + "…" : text;
    }

    public static string ScrubStoredCommand(string command)
    {
        if (!CommandParser.TryParse(command, out var parsed, out _) || parsed == null)
            return "[记录无法解析，已隐藏]";
        if (IsCredential(parsed) || IsScript(parsed))
            return SanitizeCommand(parsed, ArgVisibility.HideAll);
        if (IsValueCommand(parsed))
            return SanitizeCommand(parsed, ArgVisibility.HideValues);
        return command.Length > 200 ? command[..200] + "…" : command;
    }

    public static string ScrubStoredError(string command, string error)
    {
        if (!CommandParser.TryParse(command, out var parsed, out _) || parsed == null)
            return "执行失败（详情已隐藏）";
        var visibility = IsCredential(parsed) || IsScript(parsed) ? ArgVisibility.HideAll
            : IsValueCommand(parsed) ? ArgVisibility.HideValues
            : ArgVisibility.ShowAll;
        return SanitizeServerError(error, parsed, visibility);
    }

    private static string FormatHiddenValues(ParsedCommand command)
    {
        var name = command.Name;
        var args = command.Args;
        if (args.Count == 0)
            return name + " [值已隐藏]";
        if (name is "MSET" or "MSETNX" or "ECHO")
            return name + " [值已隐藏]";
        if (name is "SETEX" or "PSETEX" or "LSET" or "SETRANGE")
        {
            var head = name + " " + args[0];
            if (args.Count >= 2)
                head += " " + args[1];
            return head + " [值已隐藏]";
        }

        return name + " " + args[0] + " [值已隐藏]";
    }

    private static IEnumerable<string> ValueArguments(ParsedCommand command)
    {
        var name = command.Name;
        if (name is "MSET" or "MSETNX" or "ECHO")
            return command.Args;
        if (name is "SETEX" or "PSETEX" or "LSET" or "SETRANGE")
            return command.Args.Skip(2);
        return command.Args.Skip(1);
    }

    [GeneratedRegex(@"(?i)(password\s*=\s*)(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordAssignment();
}
