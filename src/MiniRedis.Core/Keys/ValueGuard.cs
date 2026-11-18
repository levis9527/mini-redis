using System.Globalization;
using MiniRedis.Core.Protocol;
using MiniRedis.Core.Scanning;

namespace MiniRedis.Core.Keys;

public sealed class StringBuffer
{
    public byte[] Data { get; private set; } = [];
    public long TotalBytes { get; private set; }
    public bool HitLoadCap { get; private set; }

    public bool IsComplete => TotalBytes >= 0 && Data.LongLength >= TotalBytes && !HitLoadCap;
    public bool IsUtf8 => RedisText.IsUtf8(Data);
    public bool CanEdit => IsComplete && IsUtf8;
    public bool CanContinue => Data.LongLength < TotalBytes && Data.Length < AppLimits.MaxLoadedStringBytes;

    public void Reset(byte[] first, long total)
    {
        Data = [];
        HitLoadCap = false;
        TotalBytes = total;
        Accept(first);
    }

    public void Append(byte[] more) => Accept(more);

    public string EditText => CanEdit ? System.Text.Encoding.UTF8.GetString(Data) : "";

    private void Accept(byte[] chunk)
    {
        var room = AppLimits.MaxLoadedStringBytes - Data.Length;
        if (room <= 0)
        {
            HitLoadCap = true;
            return;
        }

        var take = chunk.Length > room ? chunk[..room] : chunk;
        if (take.Length != chunk.Length)
            HitLoadCap = true;
        var merged = new byte[Data.Length + take.Length];
        Data.CopyTo(merged, 0);
        take.CopyTo(merged, Data.Length);
        Data = merged;
        if (Data.LongLength < TotalBytes && Data.Length >= AppLimits.MaxLoadedStringBytes)
            HitLoadCap = true;
    }
}

public static class ValueGuard
{
    public static bool SameText(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    public static string TtlBoxText(long pttlMilliseconds)
    {
        if (pttlMilliseconds < 0)
            return "-1";
        var seconds = (pttlMilliseconds + 999) / 1000;
        if (seconds < 1)
            seconds = 1;
        return seconds.ToString(CultureInfo.InvariantCulture);
    }

    public const string StringTtlUnsafeMessage =
        "服务器不能用一条 SET XX KEEPTTL 更新字符串并保留当前过期时间（需要 Redis 6 及以上，且 ACL 允许这条命令）。为避免把已过期或已删除的键重新创建，或把仍有过期时间的键写成永不过期，这次没有写入。";

    public const string StringMissingMessage =
        "键已经不存在或已过期。没有写入，也没有把它重新创建。";

    public static bool ShouldWriteString(string original, string edited) => !SameText(original, edited);

    public static RedisArg[] StringKeepTtlCommand(RedisArg key, string value) =>
        [RedisArg.FromText("SET"), key, RedisArg.FromText(value), RedisArg.FromText("XX"), RedisArg.FromText("KEEPTTL")];

    public static async Task<RedisReply> SaveStringPreservingTtlAsync(
        Func<IReadOnlyList<RedisArg>, CancellationToken, Task<RedisReply>> execute,
        RedisArg key,
        string value,
        CancellationToken cancellationToken)
    {
        var reply = await execute(StringKeepTtlCommand(key, value), cancellationToken).ConfigureAwait(false);
        if (reply.IsNull)
            return RedisReply.ErrorOf(StringMissingMessage);
        if (reply.IsError && IsAtomicStringUpdateUnavailable(reply.ErrorText))
            return RedisReply.ErrorOf(StringTtlUnsafeMessage);
        return reply;
    }

    public static bool IsAtomicStringUpdateUnavailable(string? error)
    {
        if (string.IsNullOrEmpty(error))
            return false;
        return error.Contains("syntax", StringComparison.OrdinalIgnoreCase)
            || error.Contains("KEEPTTL", StringComparison.OrdinalIgnoreCase)
            || error.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
            || error.Contains("NOPERM", StringComparison.OrdinalIgnoreCase)
            || error.Contains("no permissions", StringComparison.OrdinalIgnoreCase);
    }

    public static MemberChange DescribeMemberChange(string type, string originalField, string originalValue, string fieldText, string valueText)
    {
        var kind = (type ?? "").ToLowerInvariant();
        if (kind is "hash" or "list")
        {
            var changed = !SameText(originalValue, valueText);
            return new MemberChange(changed, changed && valueText.Length == 0, kind, originalField, originalValue, originalField, valueText, changed ? "将修改这个成员的值。" : "");
        }

        if (kind == "zset")
        {
            var changed = !SameText(originalValue, valueText);
            var scoreOk = !changed || double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            var message = !scoreOk ? "分数不是数字，没有写入。" : changed ? "将修改这个成员的分数。" : "";
            return new MemberChange(changed && scoreOk, false, kind, originalField, originalValue, originalField, valueText, message, !scoreOk);
        }

        if (kind == "set")
        {
            var changed = !SameText(originalField, fieldText);
            return new MemberChange(changed, changed && fieldText.Length == 0, kind, originalField, "", fieldText, "", changed ? "将用一条服务器脚本替换成员名。服务器若拒绝脚本，集合保持原样，不会改用先加后删。" : "");
        }

        return new MemberChange(false, false, kind, originalField, originalValue, fieldText, valueText, "此类型不在界面里修改成员。", true);
    }

    public static async Task<RedisReply> ApplyMemberChangeAsync(
        Func<IReadOnlyList<RedisArg>, CancellationToken, Task<RedisReply>> execute,
        RedisArg key,
        MemberChange change,
        CancellationToken cancellationToken)
    {
        if (!change.Changed || change.Invalid)
            return RedisReply.ErrorOf(change.Message);
        if (change.Kind is "hash")
        {
            return await execute(
                [RedisArg.FromText("HSET"), key, RedisArg.FromText(change.Field), RedisArg.FromText(change.Value)],
                cancellationToken).ConfigureAwait(false);
        }

        if (change.Kind == "list")
        {
            return await execute(
                [RedisArg.FromText("LSET"), key, RedisArg.FromText(change.Field), RedisArg.FromText(change.Value)],
                cancellationToken).ConfigureAwait(false);
        }

        if (change.Kind == "zset")
        {
            return await execute(
                [RedisArg.FromText("ZADD"), key, RedisArg.FromText(change.Value), RedisArg.FromText(change.Field)],
                cancellationToken).ConfigureAwait(false);
        }

        if (change.Kind == "set")
        {
            var reply = await execute(
            [
                RedisArg.FromText("EVAL"),
                RedisArg.FromText(SetRenameScript),
                RedisArg.FromText("1"),
                key,
                RedisArg.FromText(change.OriginalField),
                RedisArg.FromText(change.Field)
            ], cancellationToken).ConfigureAwait(false);
            if (reply.IsError && IsScriptUnavailable(reply.ErrorText))
                return RedisReply.ErrorOf("服务器不能执行替换集合成员名的脚本，集合没有改。不会改用 SADD 和 SREM，因为第二步失败时会同时留下新旧两个成员。");
            return reply;
        }

        return RedisReply.ErrorOf("不支持的类型。");
    }

    public static bool IsQueued(RedisReply reply) =>
        !reply.IsError && reply.Text.Equals("QUEUED", StringComparison.OrdinalIgnoreCase);

    public static bool IsScriptUnavailable(string? error)
    {
        if (string.IsNullOrEmpty(error))
            return false;
        return error.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
            || error.Contains("NOPERM", StringComparison.OrdinalIgnoreCase)
            || error.Contains("no permissions", StringComparison.OrdinalIgnoreCase)
            || error.Contains("NOSCRIPT", StringComparison.OrdinalIgnoreCase);
    }

    private const string SetRenameScript =
        "if redis.call('EXISTS', KEYS[1]) == 0 then return redis.error_reply('ERR key missing') end " +
        "local t = redis.call('TYPE', KEYS[1]) " +
        "local name = t if type(t) == 'table' then name = t['ok'] end " +
        "if name ~= 'set' then return redis.error_reply('WRONGTYPE') end " +
        "if ARGV[1] == ARGV[2] then return 0 end " +
        "redis.call('SADD', KEYS[1], ARGV[2]) redis.call('SREM', KEYS[1], ARGV[1]) return 1";
}

public readonly record struct MemberChange(
    bool Changed,
    bool EmptyValue,
    string Kind,
    string OriginalField,
    string OriginalValue,
    string Field,
    string Value,
    string Message,
    bool Invalid = false);

public static class SmokeReport
{
    public static bool TryValidate(string? text, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "没有冒烟结果。";
            return false;
        }

        if (!HasLine(text, "ok=true"))
        {
            error = "ok 不是 true。";
            return false;
        }

        if (!HasLine(text, "exit_code=0"))
        {
            error = "退出码不是 0。";
            return false;
        }

        if (!TryLong(text, "startup_ms=", out var startup) || startup < 0 || startup > 90000)
        {
            error = "startup_ms 无效。";
            return false;
        }

        if (!TryLong(text, "working_set_bytes=", out var workingSet) || workingSet <= 0)
        {
            error = "working_set_bytes 无效。";
            return false;
        }

        return true;
    }

    private static bool HasLine(string text, string line) =>
        text.Split('\n', '\r').Any(item => item.Trim().Equals(line, StringComparison.Ordinal));

    private static bool TryLong(string text, string prefix, out long value)
    {
        value = 0;
        foreach (var raw in text.Split('\n', '\r'))
        {
            var line = raw.Trim();
            if (line.StartsWith(prefix, StringComparison.Ordinal)
                && long.TryParse(line[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return true;
        }

        return false;
    }
}
