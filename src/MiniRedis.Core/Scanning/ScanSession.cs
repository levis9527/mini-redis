using System.Globalization;
using MiniRedis.Core.Protocol;

namespace MiniRedis.Core.Scanning;

public static class AppLimits
{
    public const int MaxDisplayedKeys = 20000;
    public const int MaxLoadAllBatches = 10000;
    public const int MaxStringBytes = 256 * 1024;
    public const int CollectionPageSize = 50;
    public const int ReplyMaxChars = 65536;
    public const int AuditMaxBytes = 1024 * 1024;
    public const int AuditMaxFiles = 5;
    public const int AuditPageSize = 40;
}

public sealed class ScanQuery
{
    public required string Pattern { get; init; }
    public required int CountHint { get; init; }
    public string? TypeFilter { get; init; }
}

public sealed class ScanPage
{
    public required string NextCursor { get; init; }
    public required IReadOnlyList<RedisArg> Keys { get; init; }
}

public sealed class ScanStep
{
    public bool Discarded { get; init; }
    public bool Failed { get; init; }
    public string Error { get; init; } = "";
    public int Returned { get; init; }
    public int Added { get; init; }
    public int Dropped { get; init; }
    public string Cursor { get; init; } = "0";
    public bool Exhausted { get; init; }
    public bool AtLimit { get; init; }
    public int ScanCallsThisStep { get; init; }

    public static ScanStep Discard() => new() { Discarded = true };
}

public sealed class ScanRunResult
{
    public required string Status { get; init; }
    public bool Completed { get; init; }
    public bool Cancelled { get; init; }
    public bool Stale { get; init; }
    public bool AtLimit { get; init; }
}

public static class ScanCursor
{
    public static bool IsComplete(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return false;
        return ulong.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value == 0;
    }
}

public static class ScanCommand
{
    public static RedisArg[] Build(ScanQuery query, string cursor)
    {
        if (query.CountHint < 1)
            throw new ArgumentOutOfRangeException(nameof(query), "COUNT 至少为 1。");
        var pattern = string.IsNullOrWhiteSpace(query.Pattern) ? "*" : query.Pattern;
        var args = new List<RedisArg>
        {
            RedisArg.FromText("SCAN"),
            RedisArg.FromText(cursor),
            RedisArg.FromText("MATCH"),
            RedisArg.FromText(pattern),
            RedisArg.FromText("COUNT"),
            RedisArg.FromText(query.CountHint.ToString(CultureInfo.InvariantCulture))
        };
        if (!string.IsNullOrWhiteSpace(query.TypeFilter) &&
            !query.TypeFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            args.Add(RedisArg.FromText("TYPE"));
            args.Add(RedisArg.FromText(query.TypeFilter));
        }

        return args.ToArray();
    }

    public static ScanPage Parse(RedisReply reply)
    {
        if (reply.IsError)
            throw new FormatException(reply.ErrorText ?? "SCAN 失败");
        if (reply.Kind != RedisReplyKind.Array || reply.Items == null || reply.Items.Count < 2)
            throw new FormatException("SCAN 回复不是游标和键数组。");

        var cursor = CursorText(reply.Items[0]);
        var second = reply.Items[1];
        var keys = new List<RedisArg>();
        if (!second.IsNull && second.Items != null)
        {
            foreach (var item in second.Items)
            {
                if (item.Bytes != null)
                    keys.Add(new RedisArg(item.Bytes));
            }
        }

        return new ScanPage { NextCursor = cursor, Keys = keys };
    }

    private static string CursorText(RedisReply item)
    {
        if (item.Integer is long number)
        {
            if (number < 0)
                throw new FormatException("SCAN 游标不能是负数。");
            return number.ToString(CultureInfo.InvariantCulture);
        }

        var text = item.Bytes == null ? "" : System.Text.Encoding.ASCII.GetString(item.Bytes).Trim();
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new FormatException("SCAN 游标不是无符号整数。");
        return text;
    }
}

public sealed class ScanSession
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public string Cursor { get; private set; } = "0";
    public bool Started { get; private set; }
    public bool Exhausted { get; private set; }
    public bool StoppedAtLimit { get; private set; }
    public int BatchCount { get; private set; }
    public int ReturnedCount { get; private set; }
    public int DroppedCount { get; private set; }
    public List<RedisArg> Keys { get; } = [];

    public void MarkLimit() => StoppedAtLimit = true;

    public ScanStep Apply(ScanPage page, int maxKeys)
    {
        Started = true;
        BatchCount++;
        ReturnedCount += page.Keys.Count;
        var added = 0;
        var dropped = 0;
        foreach (var key in page.Keys)
        {
            if (!_seen.Add(key.Identity))
                continue;
            if (Keys.Count >= maxKeys)
            {
                dropped++;
                _seen.Remove(key.Identity);
                continue;
            }

            Keys.Add(key);
            added++;
        }

        Cursor = page.NextCursor;
        Exhausted = ScanCursor.IsComplete(page.NextCursor);
        DroppedCount += dropped;
        if (dropped > 0 || (Keys.Count >= maxKeys && !Exhausted))
            StoppedAtLimit = true;

        return new ScanStep
        {
            Returned = page.Keys.Count,
            Added = added,
            Dropped = dropped,
            Cursor = Cursor,
            Exhausted = Exhausted,
            AtLimit = StoppedAtLimit,
            ScanCallsThisStep = 1
        };
    }
}

public static class ScanStatus
{
    public static string Describe(ScanSession session)
    {
        if (!session.Started)
            return "尚未开始";
        if (session.DroppedCount > 0 || (session.StoppedAtLimit && !session.Exhausted))
        {
            return "已停止：界面上限。已收下 " + session.Keys.Count + " 个键，另有 " + session.DroppedCount
                + " 个键没有放进列表。这不是已全部加载。游标保留为 " + session.Cursor + "。";
        }

        if (session.Exhausted)
            return "游标为 0，扫描已结束。已去重 " + session.Keys.Count + " 个键。";
        if (session.StoppedAtLimit)
            return "已达到批次上限，尚未扫完。已去重 " + session.Keys.Count + " 个键，游标 " + session.Cursor + "。";
        return "已去重 " + session.Keys.Count + " 个键，游标 " + session.Cursor + "。空批且游标不是 0 并不表示结束。";
    }
}

public sealed class ScanBroker
{
    private int _generation;

    public ScanSession Session { get; private set; } = new();
    public int Generation => Volatile.Read(ref _generation);

    public int Reset()
    {
        Session = new ScanSession();
        return Interlocked.Increment(ref _generation);
    }

    public bool IsCurrent(int generation) => generation == Volatile.Read(ref _generation);

    public async Task<ScanStep> ScanOnceAsync(
        IRedisSession redis,
        ScanQuery query,
        int generation,
        CancellationToken cancellationToken,
        int maxKeys = AppLimits.MaxDisplayedKeys)
    {
        var session = Session;
        if (!IsCurrent(generation))
            return ScanStep.Discard();
        if (session.Exhausted)
            throw new InvalidOperationException("游标已经是 0，不能再 SCAN。");

        cancellationToken.ThrowIfCancellationRequested();
        var reply = await redis.ExecuteAsync(ScanCommand.Build(query, session.Cursor), cancellationToken).ConfigureAwait(false);
        if (!IsCurrent(generation) || !ReferenceEquals(Session, session))
            return ScanStep.Discard();
        if (reply.IsError)
        {
            return new ScanStep
            {
                Failed = true,
                Error = reply.ErrorText ?? "SCAN 失败",
                Cursor = session.Cursor,
                ScanCallsThisStep = 1
            };
        }

        return session.Apply(ScanCommand.Parse(reply), maxKeys);
    }

    public async Task<ScanRunResult> LoadAllAsync(
        IRedisSession redis,
        ScanQuery query,
        int generation,
        IProgress<ScanStep>? progress,
        CancellationToken cancellationToken,
        int maxKeys = AppLimits.MaxDisplayedKeys,
        int maxBatches = AppLimits.MaxLoadAllBatches)
    {
        try
        {
            while (IsCurrent(generation) && !Session.Exhausted && !Session.StoppedAtLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Session.Keys.Count >= maxKeys || Session.BatchCount >= maxBatches)
                {
                    Session.MarkLimit();
                    break;
                }

                var step = await ScanOnceAsync(redis, query, generation, cancellationToken, maxKeys).ConfigureAwait(false);
                if (step.Discarded)
                    return new ScanRunResult { Status = "结果已过期，未写入当前列表。", Stale = true };
                if (step.Failed)
                    return new ScanRunResult { Status = step.Error, Stale = false };
                progress?.Report(step);
                if (step.AtLimit)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            return new ScanRunResult
            {
                Status = "已停止。已保留已加载的键和游标，可以继续「加载更多」。",
                Cancelled = true,
                AtLimit = Session.StoppedAtLimit
            };
        }

        if (!IsCurrent(generation))
            return new ScanRunResult { Status = "连接或条件已变化，这批结果已丢弃。", Stale = true };

        var completed = Session.Exhausted && !Session.StoppedAtLimit && Session.DroppedCount == 0;
        return new ScanRunResult
        {
            Status = ScanStatus.Describe(Session),
            Completed = completed,
            AtLimit = Session.StoppedAtLimit || !completed && Session.Started
        };
    }
}
