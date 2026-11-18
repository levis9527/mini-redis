using System.Globalization;
using MiniRedis.Core.Commands;
using MiniRedis.Core.Protocol;
using MiniRedis.Core.Scanning;

namespace MiniRedis.Core.Keys;

public enum ExactKind
{
    Empty,
    Error,
    Literal,
    Keys
}

public sealed class ExactRequest
{
    public required ExactKind Kind { get; init; }
    public string Text { get; init; } = "";
    public string? Error { get; init; }
    public bool NeedsConfirmation { get; init; }
    public string Confirmation { get; init; } = "";
}

public static class ExactInput
{
    public static ExactRequest Parse(string? raw, string where)
    {
        var trimmed = raw?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return new ExactRequest { Kind = ExactKind.Empty, Error = "请输入完整键名，或 KEYS 加一个模式。" };
        }

        if (CommandLooksLikeKeys(trimmed, out var pattern, out var keysError))
        {
            if (keysError != null)
                return new ExactRequest { Kind = ExactKind.Error, Error = keysError, Text = trimmed };
            return new ExactRequest
            {
                Kind = ExactKind.Keys,
                Text = pattern ?? "",
                NeedsConfirmation = true,
                Confirmation =
                    "当前：" + where + "\n待执行：KEYS " + pattern + "\n\n"
                    + "KEYS 会遍历当前逻辑库的全部键再做匹配，即使模式不含 * 也有同样风险。这不是 SCAN，会一次返回全部匹配；不能靠分页减轻服务器上的这次扫描。确认后执行，取消不会查询。"
            };
        }

        var literal = new ExactRequest
        {
            Kind = ExactKind.Literal,
            Text = trimmed,
            NeedsConfirmation = trimmed.Contains('*', StringComparison.Ordinal),
            Confirmation =
                "当前：" + where + "\n输入：「" + trimmed + "」\n\n"
                + "输入中含有 *。本次仍按完整键名的字面值查找，不会把 * 当作通配符，也不会执行 KEYS。因此这不是阻塞型全库扫描。若要按模式找出全部匹配，请改用 KEYS "
                + trimmed + "。确认后按字面定位，取消不会查询。"
        };
        return literal;
    }

    private static bool CommandLooksLikeKeys(string trimmed, out string? pattern, out string? error)
    {
        pattern = null;
        error = null;
        if (!CommandParser.TryParse(trimmed, out var parsed, out _))
            return false;
        if (parsed == null || !parsed.Name.Equals("KEYS", StringComparison.Ordinal))
            return false;
        if (parsed.Args.Count == 0)
        {
            error = "KEYS 需要模式，例如 KEYS *。单独的键名请不要只写 KEYS。";
            return true;
        }

        if (parsed.Args.Count != 1)
        {
            error = "这里的 KEYS 只接受一个模式参数。";
            return true;
        }

        pattern = parsed.Args[0];
        return true;
    }
}

public sealed class KeyHeader
{
    public bool Exists { get; init; }
    public string Type { get; init; } = "none";
    public long PttlMilliseconds { get; init; } = -2;
    public string? Error { get; init; }
}

public sealed class StringView
{
    public required byte[] Preview { get; init; }
    public required long TotalBytes { get; init; }
    public required bool Truncated { get; init; }
    public required bool IsUtf8 { get; init; }
    public bool CanEdit => !Truncated && IsUtf8;
    public string EditableText => CanEdit ? System.Text.Encoding.UTF8.GetString(Preview) : "";
    public string ReadOnlyText => IsUtf8
        ? System.Text.Encoding.UTF8.GetString(Preview)
        : RedisText.Display(Preview, 256);
}

public sealed class MemberRow
{
    public required RedisArg Field { get; init; }
    public RedisArg? Value { get; init; }
    public bool CanEdit => Field.IsUtf8 && (Value == null || Value.Value.IsUtf8);
}

public sealed class CollectionPage
{
    public required string Kind { get; init; }
    public long Cardinality { get; init; } = -1;
    public string NextCursor { get; init; } = "0";
    public int NextIndex { get; init; }
    public bool Exhausted { get; init; }
    public IReadOnlyList<MemberRow> Rows { get; init; } = [];
    public string? Error { get; init; }
}

public static class KeyArgs
{
    public static RedisArg[] Of(string command, RedisArg key, params string[] more)
    {
        var args = new RedisArg[2 + more.Length];
        args[0] = RedisArg.FromText(command);
        args[1] = key;
        for (var i = 0; i < more.Length; i++)
            args[i + 2] = RedisArg.FromText(more[i]);
        return args;
    }
}

public sealed class KeyInspector
{
    private readonly Func<IReadOnlyList<RedisArg>, CancellationToken, Task<RedisReply>> _execute;

    public KeyInspector(Func<IReadOnlyList<RedisArg>, CancellationToken, Task<RedisReply>> execute)
    {
        _execute = execute;
    }

    public async Task<KeyHeader> ProbeAsync(RedisArg key, CancellationToken cancellationToken)
    {
        var exists = await _execute(KeyArgs.Of("EXISTS", key), cancellationToken).ConfigureAwait(false);
        if (exists.IsError)
            return new KeyHeader { Error = exists.ErrorText, PttlMilliseconds = -2 };
        var type = await _execute(KeyArgs.Of("TYPE", key), cancellationToken).ConfigureAwait(false);
        if (type.IsError)
            return new KeyHeader { Error = type.ErrorText, PttlMilliseconds = -2 };
        var pttl = await _execute(KeyArgs.Of("PTTL", key), cancellationToken).ConfigureAwait(false);
        if (pttl.IsError)
            return new KeyHeader { Error = pttl.ErrorText, PttlMilliseconds = -2, Type = type.Text };

        var typeName = string.IsNullOrWhiteSpace(type.Text) ? "none" : type.Text;
        var ttl = pttl.Integer ?? ParseLong(pttl.Text);
        var existsCount = exists.Integer ?? ParseLong(exists.Text);
        return new KeyHeader
        {
            Exists = existsCount > 0 && !typeName.Equals("none", StringComparison.OrdinalIgnoreCase),
            Type = typeName,
            PttlMilliseconds = ttl
        };
    }

    public async Task<StringView> ReadStringAsync(RedisArg key, CancellationToken cancellationToken)
    {
        var lengthReply = await _execute(KeyArgs.Of("STRLEN", key), cancellationToken).ConfigureAwait(false);
        if (lengthReply.IsError)
            throw new InvalidOperationException(lengthReply.ErrorText);
        var length = lengthReply.Integer ?? ParseLong(lengthReply.Text);
        if (length > AppLimits.MaxStringBytes)
        {
            var slice = await _execute(
                KeyArgs.Of("GETRANGE", key, "0", (AppLimits.MaxStringBytes - 1).ToString(CultureInfo.InvariantCulture)),
                cancellationToken).ConfigureAwait(false);
            if (slice.IsError)
                throw new InvalidOperationException(slice.ErrorText);
            return MakeString(slice.Bytes ?? [], length, truncated: true);
        }

        var get = await _execute(KeyArgs.Of("GET", key), cancellationToken).ConfigureAwait(false);
        if (get.IsError)
            throw new InvalidOperationException(get.ErrorText);
        return MakeString(get.Bytes ?? [], length, truncated: false);
    }

    public async Task<CollectionPage> ReadCollectionAsync(
        RedisArg key,
        string type,
        string cursor,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        count = Math.Clamp(count, 1, 500);
        var kind = type.ToLowerInvariant();
        var cardCommand = kind switch
        {
            "hash" => "HLEN",
            "set" => "SCARD",
            "zset" => "ZCARD",
            "list" => "LLEN",
            _ => null
        };
        long card = -1;
        if (cardCommand != null)
        {
            var cardReply = await _execute(KeyArgs.Of(cardCommand, key), cancellationToken).ConfigureAwait(false);
            if (!cardReply.IsError)
                card = cardReply.Integer ?? ParseLong(cardReply.Text);
        }

        if (kind == "list")
        {
            var stop = start + count - 1;
            var reply = await _execute(
                KeyArgs.Of("LRANGE", key, start.ToString(CultureInfo.InvariantCulture), stop.ToString(CultureInfo.InvariantCulture)),
                cancellationToken).ConfigureAwait(false);
            if (reply.IsError)
                return new CollectionPage { Kind = kind, Error = reply.ErrorText, Cardinality = card };
            var rows = new List<MemberRow>();
            var items = reply.Items ?? [];
            for (var i = 0; i < items.Count; i++)
            {
                var bytes = items[i].Bytes ?? [];
                rows.Add(new MemberRow
                {
                    Field = RedisArg.FromText((start + i).ToString(CultureInfo.InvariantCulture)),
                    Value = new RedisArg(bytes)
                });
            }

            var exhausted = rows.Count < count || (card >= 0 && start + rows.Count >= card);
            return new CollectionPage
            {
                Kind = kind,
                Cardinality = card,
                Rows = rows,
                NextIndex = start + rows.Count,
                NextCursor = "0",
                Exhausted = exhausted
            };
        }

        var scanName = kind switch
        {
            "hash" => "HSCAN",
            "set" => "SSCAN",
            "zset" => "ZSCAN",
            _ => null
        };
        if (scanName == null)
        {
            return new CollectionPage
            {
                Kind = kind,
                Cardinality = card,
                Exhausted = true,
                Error = "此类型只显示 TYPE 和 PTTL，不在界面里整块读取。"
            };
        }

        var page = await _execute(
            KeyArgs.Of(scanName, key, cursor, "COUNT", count.ToString(CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);
        if (page.IsError)
            return new CollectionPage { Kind = kind, Error = page.ErrorText, Cardinality = card };
        if (page.Kind != RedisReplyKind.Array || page.Items == null || page.Items.Count < 2)
            return new CollectionPage { Kind = kind, Error = "游标回复无法解析。", Cardinality = card };

        var next = page.Items[0].Text;
        var flat = page.Items[1].Items ?? [];
        var members = new List<MemberRow>();
        if (kind == "set")
        {
            foreach (var item in flat)
                members.Add(new MemberRow { Field = new RedisArg(item.Bytes ?? []) });
        }
        else
        {
            for (var i = 0; i + 1 < flat.Count; i += 2)
            {
                members.Add(new MemberRow
                {
                    Field = new RedisArg(flat[i].Bytes ?? []),
                    Value = new RedisArg(flat[i + 1].Bytes ?? [])
                });
            }
        }

        return new CollectionPage
        {
            Kind = kind,
            Cardinality = card,
            NextCursor = next,
            Rows = members,
            Exhausted = ScanCursor.IsComplete(next)
        };
    }

    private static StringView MakeString(byte[] preview, long total, bool truncated)
    {
        var utf8 = RedisText.IsUtf8(preview);
        return new StringView
        {
            Preview = preview,
            TotalBytes = total,
            Truncated = truncated,
            IsUtf8 = utf8
        };
    }

    private static long ParseLong(string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
