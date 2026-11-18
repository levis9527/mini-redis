using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MiniRedis.Core.Commands;
using MiniRedis.Core.Scanning;

namespace MiniRedis.Core.Storage;

public sealed class AuditEntry
{
    public string Time { get; init; } = "";
    public string Tz { get; init; } = "Asia/Shanghai";
    public string TimeUtc { get; init; } = "";
    public string Connection { get; init; } = "";
    public int Db { get; init; }
    public string Command { get; init; } = "";
    public bool Ok { get; init; }
    public string Error { get; init; } = "";
}

public sealed class AuditWriteResult
{
    public bool Written { get; init; }
    public string? Failure { get; init; }

    public static AuditWriteResult Ok() => new() { Written = true };
    public static AuditWriteResult Failed(string message) => new() { Written = false, Failure = message };
}

public sealed class AuditPage
{
    public IReadOnlyList<AuditEntry> Entries { get; init; } = [];
    public bool HasMore { get; init; }
    public bool SkippedMalformed { get; init; }
    public string? ReadWarning { get; init; }
}

public static class ShanghaiClock
{
    public static (string Local, string Utc) Format(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        var local = TimeZoneInfo.ConvertTime(utc, Resolve());
        return (
            local.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture));
    }

    public static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Asia/Shanghai", "China Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    }
}

public sealed class JsonlAuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();

    public JsonlAuditLog(string path, int maxBytes = AppLimits.AuditMaxBytes, int maxFiles = AppLimits.AuditMaxFiles)
    {
        Path = path;
        MaxBytes = maxBytes;
        MaxFiles = maxFiles;
    }

    public string Path { get; }
    public int MaxBytes { get; }
    public int MaxFiles { get; }

    public AuditWriteResult Append(AuditEntry entry)
    {
        var stored = new AuditEntry
        {
            Time = entry.Time,
            Tz = "Asia/Shanghai",
            TimeUtc = entry.TimeUtc,
            Connection = entry.Connection,
            Db = entry.Db,
            Command = CommandSanitizer.ScrubStoredCommand(entry.Command),
            Ok = entry.Ok,
            Error = entry.Ok ? "" : CommandSanitizer.ScrubStoredError(entry.Command, entry.Error)
        };
        var line = JsonSerializer.Serialize(stored, JsonOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        try
        {
            lock (_gate)
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                RotateIfNeeded(bytes.Length);
                using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            return AuditWriteResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AuditWriteResult.Failed("操作记录没有写入：" + ex.Message);
        }
    }

    public AuditPage ReadRecent(int skip, int take)
    {
        if (take <= 0)
            return new AuditPage();
        var entries = new List<AuditEntry>();
        var skipped = false;
        string? warning = null;
        try
        {
            var lines = ReadLinesNewestFirst();
            var matched = 0;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                AuditEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<AuditEntry>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    skipped = true;
                    continue;
                }

                if (entry == null || string.IsNullOrWhiteSpace(entry.Command))
                {
                    skipped = true;
                    continue;
                }

                entry = new AuditEntry
                {
                    Time = entry.Time,
                    Tz = "Asia/Shanghai",
                    TimeUtc = entry.TimeUtc,
                    Connection = entry.Connection,
                    Db = entry.Db,
                    Command = CommandSanitizer.ScrubStoredCommand(entry.Command),
                    Ok = entry.Ok,
                    Error = entry.Ok ? "" : CommandSanitizer.ScrubStoredError(entry.Command, entry.Error)
                };
                if (matched++ < skip)
                    continue;
                entries.Add(entry);
                if (entries.Count > take)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = "操作记录读取失败：" + ex.Message;
        }

        var hasMore = entries.Count > take;
        if (hasMore)
            entries.RemoveAt(entries.Count - 1);
        return new AuditPage
        {
            Entries = entries,
            HasMore = hasMore,
            SkippedMalformed = skipped,
            ReadWarning = warning
        };
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(Path))
                File.Delete(Path);
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (directory == null || !Directory.Exists(directory))
                return;
            foreach (var file in Directory.GetFiles(directory, "audit-*.jsonl"))
                File.Delete(file);
        }
    }

    private void RotateIfNeeded(int incoming)
    {
        if (!File.Exists(Path))
            return;
        var length = new FileInfo(Path).Length;
        if (length + incoming <= MaxBytes)
            return;
        var directory = System.IO.Path.GetDirectoryName(Path) ?? ".";
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var target = System.IO.Path.Combine(directory, "audit-" + stamp + ".jsonl");
        var suffix = 0;
        while (File.Exists(target))
        {
            suffix++;
            target = System.IO.Path.Combine(directory, "audit-" + stamp + "-" + suffix + ".jsonl");
        }

        File.Move(Path, target);
        var archived = Directory.GetFiles(directory, "audit-*.jsonl")
            .OrderByDescending(file => file, StringComparer.Ordinal)
            .Skip(Math.Max(0, MaxFiles - 1))
            .ToArray();
        foreach (var old in archived)
            File.Delete(old);
    }

    private IEnumerable<string> ReadLinesNewestFirst()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        var files = new List<string>();
        if (File.Exists(Path))
            files.Add(Path);
        if (directory != null && Directory.Exists(directory))
        {
            files.AddRange(Directory.GetFiles(directory, "audit-*.jsonl")
                .OrderByDescending(file => file, StringComparer.Ordinal));
        }

        foreach (var file in files.Take(MaxFiles))
        {
            List<string> lines;
            try
            {
                lines = File.ReadLines(file).Reverse().Take(5000).ToList();
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var line in lines)
                yield return line;
        }
    }
}
