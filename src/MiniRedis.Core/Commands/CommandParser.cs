using System.Globalization;
using System.Text;

namespace MiniRedis.Core.Commands;

public sealed class ParsedCommand
{
    public required string Command { get; init; }
    public required IReadOnlyList<string> Args { get; init; }
    public required string Raw { get; init; }

    public string Name => Command.ToUpperInvariant();

    public bool ContainsStar
    {
        get
        {
            if (Command.Contains('*', StringComparison.Ordinal))
                return true;
            foreach (var arg in Args)
            {
                if (arg.Contains('*', StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}

public static class CommandParser
{
    public static bool TryParse(string? line, out ParsedCommand? command, out string? error)
    {
        command = null;
        error = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            error = "请输入命令。";
            return false;
        }

        try
        {
            var tokens = Tokenize(line);
            if (tokens.Count == 0)
            {
                error = "请输入命令。";
                return false;
            }

            command = new ParsedCommand
            {
                Command = tokens[0],
                Args = tokens.Skip(1).ToArray(),
                Raw = line.Trim()
            };
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static ParsedCommand FromTokens(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
            throw new FormatException("请输入命令。");
        return new ParsedCommand
        {
            Command = tokens[0],
            Args = tokens.Skip(1).ToArray(),
            Raw = string.Join(' ', tokens)
        };
    }

    public static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote == '"')
            {
                if (ch == '\\')
                {
                    if (i + 1 >= line.Length)
                        throw new FormatException("双引号里的转义不完整。");
                    current.Append(ReadEscape(line, ref i));
                    continue;
                }

                if (ch == '"')
                {
                    quote = null;
                    continue;
                }

                current.Append(ch);
                continue;
            }

            if (quote == '\'')
            {
                if (ch == '\'')
                {
                    quote = null;
                    continue;
                }

                current.Append(ch);
                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                quote = ch;
                continue;
            }

            if (ch is ' ' or '\t' or '\r' or '\n')
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (quote != null)
            throw new FormatException("引号没有闭合，命令未发送。");
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static char ReadEscape(string line, ref int index)
    {
        var ch = line[++index];
        return ch switch
        {
            '\\' => '\\',
            '"' => '"',
            '\'' => '\'',
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            'a' => '\a',
            'b' => '\b',
            'f' => '\f',
            'v' => '\v',
            'x' => ReadHex(line, ref index),
            _ => ch
        };
    }

    private static char ReadHex(string line, ref int index)
    {
        if (index + 1 >= line.Length || !Uri.IsHexDigit(line[index + 1]))
            throw new FormatException("\\x 后面需要十六进制数字。");
        var length = index + 2 < line.Length && Uri.IsHexDigit(line[index + 2]) ? 2 : 1;
        var hex = line.Substring(index + 1, length);
        index += length;
        return (char)byte.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
