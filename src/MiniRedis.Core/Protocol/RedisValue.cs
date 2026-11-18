using System.Globalization;
using System.Text;

namespace MiniRedis.Core.Protocol;

public readonly struct RedisArg
{
    public RedisArg(byte[] bytes)
    {
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        IsUtf8 = RedisText.IsUtf8(bytes);
        Utf8 = IsUtf8 ? Encoding.UTF8.GetString(bytes) : "";
        Display = RedisText.Display(bytes);
    }

    public byte[] Bytes { get; }
    public bool IsUtf8 { get; }
    public string Utf8 { get; }
    public string Display { get; }
    public string Identity => Convert.ToHexString(Bytes);

    public static RedisArg FromText(string text) => new(Encoding.UTF8.GetBytes(text ?? ""));
}

public static class RedisText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool IsUtf8(ReadOnlySpan<byte> data)
    {
        try
        {
            StrictUtf8.GetString(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public static string Display(ReadOnlySpan<byte> data, int maxBytes = 120)
    {
        if (IsUtf8(data))
        {
            var text = Encoding.UTF8.GetString(data);
            return text.Length > 200 ? text[..200] + "…" : text;
        }

        var n = Math.Min(data.Length, maxBytes);
        var hex = Convert.ToHexString(data[..n]);
        return data.Length > n ? "二进制 " + hex + "…" : "二进制 " + hex;
    }
}

public enum RedisReplyKind
{
    Simple,
    Bulk,
    Integer,
    Array,
    Null,
    Error
}

public sealed class RedisReply
{
    public RedisReplyKind Kind { get; init; }
    public byte[]? Bytes { get; init; }
    public long? Integer { get; init; }
    public IReadOnlyList<RedisReply>? Items { get; init; }
    public string? ErrorText { get; init; }

    public bool IsError => Kind == RedisReplyKind.Error;
    public bool IsNull => Kind == RedisReplyKind.Null;

    public string Text => Bytes == null ? "" : Encoding.UTF8.GetString(Bytes);

    public static RedisReply Null() => new() { Kind = RedisReplyKind.Null };

    public static RedisReply ErrorOf(string message) => new()
    {
        Kind = RedisReplyKind.Error,
        ErrorText = message ?? ""
    };

    public static RedisReply Simple(string text) => Bulk(Encoding.UTF8.GetBytes(text ?? ""));

    public static RedisReply Bulk(byte[] bytes) => new()
    {
        Kind = RedisReplyKind.Bulk,
        Bytes = bytes ?? []
    };

    public static RedisReply IntegerOf(long value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return new RedisReply
        {
            Kind = RedisReplyKind.Integer,
            Integer = value,
            Bytes = Encoding.ASCII.GetBytes(text)
        };
    }

    public static RedisReply Unsigned(ulong value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return new RedisReply
        {
            Kind = RedisReplyKind.Bulk,
            Bytes = Encoding.ASCII.GetBytes(text)
        };
    }

    public static RedisReply Array(IReadOnlyList<RedisReply> items) => new()
    {
        Kind = RedisReplyKind.Array,
        Items = items
    };
}

public static class ReplyFormatter
{
    public static string Format(RedisReply reply, int maxChars, out bool truncated)
    {
        var builder = new StringBuilder();
        truncated = false;
        Write(reply, builder, 0, maxChars, ref truncated);
        if (truncated)
            builder.Append("\n… 输出已截断。截断只影响显示，不代表命令失败或成功。");
        return builder.ToString();
    }

    private static void Write(RedisReply reply, StringBuilder builder, int depth, int maxChars, ref bool truncated)
    {
        if (truncated || builder.Length >= maxChars)
        {
            truncated = true;
            return;
        }

        switch (reply.Kind)
        {
            case RedisReplyKind.Null:
                Append(builder, "(nil)", maxChars, ref truncated);
                break;
            case RedisReplyKind.Error:
                Append(builder, "(error) " + (reply.ErrorText ?? ""), maxChars, ref truncated);
                break;
            case RedisReplyKind.Integer:
                Append(builder, "(integer) " + (reply.Integer?.ToString(CultureInfo.InvariantCulture) ?? reply.Text), maxChars, ref truncated);
                break;
            case RedisReplyKind.Array:
                WriteArray(reply, builder, depth, maxChars, ref truncated);
                break;
            default:
                AppendBulk(reply.Bytes, builder, maxChars, ref truncated);
                break;
        }
    }

    private static void WriteArray(RedisReply reply, StringBuilder builder, int depth, int maxChars, ref bool truncated)
    {
        var items = reply.Items;
        if (items == null)
        {
            Append(builder, "(empty array)", maxChars, ref truncated);
            return;
        }

        if (items.Count == 0)
        {
            Append(builder, "(empty array)", maxChars, ref truncated);
            return;
        }

        var pad = new string(' ', depth * 2);
        for (var i = 0; i < items.Count; i++)
        {
            if (builder.Length > 0)
                Append(builder, "\n", maxChars, ref truncated);
            Append(builder, pad + (i + 1) + ") ", maxChars, ref truncated);
            if (items[i].Kind == RedisReplyKind.Array)
                Write(items[i], builder, depth + 1, maxChars, ref truncated);
            else
                Write(items[i], builder, depth, maxChars, ref truncated);
            if (truncated)
                return;
        }
    }

    private static void AppendBulk(byte[]? bytes, StringBuilder builder, int maxChars, ref bool truncated)
    {
        if (bytes == null)
        {
            Append(builder, "(nil)", maxChars, ref truncated);
            return;
        }

        if (!RedisText.IsUtf8(bytes))
        {
            var n = Math.Min(bytes.Length, 64);
            Append(builder, "二进制 " + bytes.Length + " 字节 " + Convert.ToHexString(bytes.AsSpan(0, n)), maxChars, ref truncated);
            return;
        }

        Append(builder, Encoding.UTF8.GetString(bytes), maxChars, ref truncated);
    }

    private static void Append(StringBuilder builder, string text, int maxChars, ref bool truncated)
    {
        if (truncated)
            return;
        var room = maxChars - builder.Length;
        if (room <= 0)
        {
            truncated = true;
            return;
        }

        if (text.Length <= room)
        {
            builder.Append(text);
            return;
        }

        builder.Append(text.AsSpan(0, room));
        truncated = true;
    }
}
