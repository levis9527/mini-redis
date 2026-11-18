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

    public static async Task<RedisReply> SaveStringPreservingTtlAsync(
        Func<IReadOnlyList<RedisArg>, CancellationToken, Task<RedisReply>> execute,
        RedisArg key,
        string value,
        CancellationToken cancellationToken)
    {
        var pttlReply = await execute(KeyArgs.Of("PTTL", key), cancellationToken).ConfigureAwait(false);
        if (pttlReply.IsError)
            return pttlReply;
        var pttl = pttlReply.Integer ?? ParseLong(pttlReply.Text);
        var keep = await execute(
            [RedisArg.FromText("SET"), key, RedisArg.FromText(value), RedisArg.FromText("KEEPTTL")],
            cancellationToken).ConfigureAwait(false);
        if (!keep.IsError)
            return keep;
        if (!IsKeepTtlUnsupported(keep.ErrorText))
            return keep;

        var set = await execute(
            [RedisArg.FromText("SET"), key, RedisArg.FromText(value)],
            cancellationToken).ConfigureAwait(false);
        if (set.IsError || pttl <= 0)
            return set;
        var restored = await execute(
            KeyArgs.Of("PEXPIRE", key, pttl.ToString(CultureInfo.InvariantCulture)),
            cancellationToken).ConfigureAwait(false);
        return restored.IsError ? restored : set;
    }

    public static bool IsKeepTtlUnsupported(string? error)
    {
        if (string.IsNullOrEmpty(error))
            return false;
        return error.Contains("syntax", StringComparison.OrdinalIgnoreCase)
            || error.Contains("KEEPTTL", StringComparison.OrdinalIgnoreCase);
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
            return new MemberChange(changed, changed && fieldText.Length == 0, kind, originalField, "", fieldText, "", changed ? "将先加入新成员，成功后再删除原成员。" : "");
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
            var added = await execute(
                [RedisArg.FromText("SADD"), key, RedisArg.FromText(change.Field)],
                cancellationToken).ConfigureAwait(false);
            if (added.IsError)
                return added;
            return await execute(
                [RedisArg.FromText("SREM"), key, RedisArg.FromText(change.OriginalField)],
                cancellationToken).ConfigureAwait(false);
        }

        return RedisReply.ErrorOf("不支持的类型。");
    }

    public static bool IsQueued(RedisReply reply) =>
        !reply.IsError && reply.Text.Equals("QUEUED", StringComparison.OrdinalIgnoreCase);

    private static long ParseLong(string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
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
