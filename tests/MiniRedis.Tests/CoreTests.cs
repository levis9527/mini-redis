using System.Text;
using Xunit;
using MiniRedis.Core.Commands;
using MiniRedis.Core.Keys;
using MiniRedis.Core.Protocol;
using MiniRedis.Core.Runtime;
using MiniRedis.Core.Scanning;
using MiniRedis.Core.Storage;

namespace MiniRedis.Tests;

public class ScanTests
{
    [Fact]
    public async Task Load_more_invokes_exactly_one_scan_each_time()
    {
        var fake = new FakeRedis();
        var cursors = new Queue<string>(["17", "0"]);
        fake.Handler = _ => Page(cursors.Dequeue(), "k");
        var broker = new ScanBroker();
        var query = Query();
        var first = await broker.ScanOnceAsync(fake, query, broker.Generation, CancellationToken.None);
        var second = await broker.ScanOnceAsync(fake, query, broker.Generation, CancellationToken.None);
        Assert.Equal(2, fake.Commands.Count);
        Assert.All(fake.Commands, command => Assert.Equal("SCAN", command[0]));
        Assert.False(first.Exhausted);
        Assert.True(second.Exhausted);
        Assert.Equal(1, first.ScanCallsThisStep);
        Assert.Equal(1, second.ScanCallsThisStep);
    }

    [Fact]
    public async Task Empty_batch_with_nonzero_cursor_is_not_the_end()
    {
        var fake = new FakeRedis();
        fake.Handler = args => args[1].Utf8 == "0"
            ? Page("42", keys: [])
            : Page("0", keys: []);
        var broker = new ScanBroker();
        var step = await broker.ScanOnceAsync(fake, Query(), broker.Generation, CancellationToken.None);
        Assert.False(step.Exhausted);
        Assert.False(broker.Session.Exhausted);
        Assert.Equal("42", broker.Session.Cursor);
        Assert.DoesNotContain("已结束", ScanStatus.Describe(broker.Session));
        var done = await broker.ScanOnceAsync(fake, Query(), broker.Generation, CancellationToken.None);
        Assert.True(done.Exhausted);
        Assert.Equal(2, fake.Commands.Count);
    }

    [Fact]
    public async Task Cursor_above_signed_long_stays_opaque()
    {
        var huge = ulong.MaxValue.ToString();
        var fake = new FakeRedis();
        fake.Handler = args => args[1].Utf8 == "0" ? Page(huge, "a") : Page("0", "b");
        var broker = new ScanBroker();
        await broker.ScanOnceAsync(fake, Query(), broker.Generation, CancellationToken.None);
        Assert.Equal(huge, broker.Session.Cursor);
        Assert.False(ScanCursor.IsComplete(huge));
        await broker.ScanOnceAsync(fake, Query(), broker.Generation, CancellationToken.None);
        Assert.Equal(huge, fake.Commands[1][1]);
    }

    [Fact]
    public async Task Returned_keys_are_deduplicated_without_a_client_side_full_scan()
    {
        var fake = new FakeRedis();
        var round = 0;
        fake.Handler = _ =>
        {
            round++;
            return round == 1 ? Page("5", "a", "b") : Page("0", "b", "c");
        };
        var broker = new ScanBroker();
        await broker.ScanOnceAsync(fake, Query(), broker.Generation, CancellationToken.None);
        var second = await broker.ScanOnceAsync(fake, Query(), broker.Generation, CancellationToken.None);
        Assert.Equal(["a", "b", "c"], broker.Session.Keys.Select(key => key.Utf8).ToArray());
        Assert.Equal(1, second.Added);
        Assert.Equal(2, fake.Commands.Count);
    }

    [Fact]
    public async Task Cancel_keeps_cursor_and_does_not_mark_success()
    {
        var fake = new FakeRedis();
        fake.Handler = _ => Page("8", "kept");
        var broker = new ScanBroker();
        await broker.ScanOnceAsync(fake, Query(), broker.Generation, CancellationToken.None);
        var cts = new CancellationTokenSource();
        fake.Hold = new TaskCompletionSource<RedisReply>();
        var pending = broker.ScanOnceAsync(fake, Query(), broker.Generation, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("8", broker.Session.Cursor);
        Assert.Equal("kept", broker.Session.Keys[0].Utf8);
        Assert.Equal(2, fake.Commands.Count);
    }

    [Fact]
    public async Task Stale_generation_discards_the_page()
    {
        var fake = new FakeRedis();
        var hold = new TaskCompletionSource<RedisReply>();
        fake.Hold = hold;
        var broker = new ScanBroker();
        var generation = broker.Generation;
        var pending = broker.ScanOnceAsync(fake, Query(), generation, CancellationToken.None);
        broker.Reset();
        hold.SetResult(Page("9", "late"));
        var step = await pending;
        Assert.True(step.Discarded);
        Assert.Empty(broker.Session.Keys);
        Assert.Equal("0", broker.Session.Cursor);
    }

    [Fact]
    public async Task Type_filter_is_one_scan_argument_not_a_follow_up_loop()
    {
        var fake = new FakeRedis();
        fake.Handler = _ => Page("7", keys: []);
        var broker = new ScanBroker();
        var query = new ScanQuery { Pattern = "user:*", CountHint = 10, TypeFilter = "hash" };
        var step = await broker.ScanOnceAsync(fake, query, broker.Generation, CancellationToken.None);
        Assert.False(step.Exhausted);
        Assert.Single(fake.Commands);
        Assert.Contains("TYPE", fake.Commands[0]);
        Assert.Contains("hash", fake.Commands[0]);
        Assert.Equal("SCAN", fake.Commands[0][0]);
    }

    [Fact]
    public async Task Load_all_stops_at_the_limit_without_claiming_completion()
    {
        var fake = new FakeRedis();
        var n = 0;
        fake.Handler = _ =>
        {
            n++;
            return Page((n + 1).ToString(), "k" + n + "a", "k" + n + "b");
        };
        var broker = new ScanBroker();
        var result = await broker.LoadAllAsync(fake, Query(), broker.Generation, progress: null, CancellationToken.None, maxKeys: 3, maxBatches: 10);
        Assert.False(result.Completed);
        Assert.True(broker.Session.StoppedAtLimit);
        Assert.True(broker.Session.Keys.Count <= 3);
        Assert.Contains("不是已全部加载", result.Status);
        Assert.True(fake.Commands.Count < 10);
    }

    [Fact]
    public async Task Load_all_cancel_preserves_progress()
    {
        var fake = new FakeRedis();
        var calls = 0;
        fake.Handler = _ =>
        {
            calls++;
            if (calls > 1)
                throw new OperationCanceledException();
            return Page("4", "one");
        };
        var broker = new ScanBroker();
        var result = await broker.LoadAllAsync(fake, Query(), broker.Generation, null, CancellationToken.None, maxKeys: 100, maxBatches: 10);
        Assert.True(result.Cancelled);
        Assert.False(result.Completed);
        Assert.Equal("4", broker.Session.Cursor);
        Assert.Equal("one", broker.Session.Keys[0].Utf8);
    }

    private static ScanQuery Query() => new() { Pattern = "*", CountHint = 10, TypeFilter = null };

    private static RedisReply Page(string cursor, params string[] keys)
    {
        var items = keys.Select(key => RedisReply.Bulk(Encoding.UTF8.GetBytes(key))).ToArray();
        return RedisReply.Array([
            RedisReply.Bulk(Encoding.ASCII.GetBytes(cursor)),
            RedisReply.Array(items)
        ]);
    }
}

public class ExactAndValueTests
{
    [Fact]
    public void Literal_star_is_not_described_as_keys()
    {
        var literal = ExactInput.Parse("order:*:1", "本地 / 127.0.0.1:6379 / db0");
        var keys = ExactInput.Parse("KEYS order:*", "本地 / 127.0.0.1:6379 / db0");
        Assert.Equal(ExactKind.Literal, literal.Kind);
        Assert.Contains("字面", literal.Confirmation);
        Assert.Contains("不会执行 KEYS", literal.Confirmation);
        Assert.DoesNotContain("不能靠分页", literal.Confirmation);
        Assert.Equal(ExactKind.Keys, keys.Kind);
        Assert.Contains("不是 SCAN", keys.Confirmation);
        Assert.Contains("不能靠分页", keys.Confirmation);
        Assert.Contains("db0", keys.Confirmation);
    }

    [Fact]
    public async Task Literal_lookup_does_not_scan_or_use_keys()
    {
        var fake = new FakeRedis();
        fake.Handler = args => args[0].Utf8 switch
        {
            "EXISTS" => RedisReply.IntegerOf(1),
            "TYPE" => RedisReply.Simple("string"),
            "PTTL" => RedisReply.IntegerOf(-1),
            _ => throw new InvalidOperationException(args[0].Utf8)
        };
        var header = await new KeyInspector((args, ct) => fake.ExecuteAsync(args, ct)).ProbeAsync(RedisArg.FromText("a*b"), CancellationToken.None);
        Assert.True(header.Exists);
        Assert.Equal(-1, header.PttlMilliseconds);
        Assert.DoesNotContain(fake.Commands, command => command[0] is "SCAN" or "KEYS");
        Assert.Equal("a*b", fake.Commands[0][1]);
    }

    [Fact]
    public async Task Truncated_or_binary_strings_cannot_be_edited()
    {
        var fake = new FakeRedis();
        fake.Handler = args => args[0].Utf8 switch
        {
            "STRLEN" => RedisReply.IntegerOf(300_000),
            "GETRANGE" => RedisReply.Bulk(Encoding.UTF8.GetBytes("preview")),
            "GET" => throw new InvalidOperationException("should not GET"),
            _ => throw new InvalidOperationException(args[0].Utf8)
        };
        var view = await new KeyInspector((args, ct) => fake.ExecuteAsync(args, ct)).ReadStringAsync(RedisArg.FromText("big"), CancellationToken.None);
        Assert.True(view.Truncated);
        Assert.False(view.CanEdit);
        Assert.DoesNotContain(fake.Commands, command => command[0] == "GET");

        fake.Commands.Clear();
        fake.Handler = args => args[0].Utf8 == "STRLEN"
            ? RedisReply.IntegerOf(4)
            : RedisReply.Bulk([0x00, 0xFF, 0xFE, 0x80]);
        var binary = await new KeyInspector((args, ct) => fake.ExecuteAsync(args, ct)).ReadStringAsync(RedisArg.FromText("bin"), CancellationToken.None);
        Assert.False(binary.IsUtf8);
        Assert.False(binary.CanEdit);
        Assert.Equal([0x00, 0xFF, 0xFE, 0x80], binary.Preview);
    }
}

public class PolicyAndSanitizerTests
{
    [Theory]
    [InlineData("flushdb")]
    [InlineData("  FLUSHALL  ")]
    [InlineData("\"SHUTDOWN\"")]
    [InlineData("Shutdown NOSAVE")]
    public void Hard_deny_cannot_be_confirmed(string line)
    {
        var parsed = Parse(line);
        var decision = CommandPolicy.Evaluate(parsed, readOnly: false, metadata: null, confirmed: true);
        Assert.Equal(CommandDecisionKind.HardDenied, decision.Kind);
    }

    [Fact]
    public async Task Read_only_uses_metadata_and_fails_closed()
    {
        var audit = TempAudit();
        var fake = new FakeRedis();
        fake.Metadata = name => name switch
        {
            "GET" => new CommandMetadata { Name = name, Flags = ["readonly", "fast"] },
            "SET" => new CommandMetadata { Name = name, Flags = ["write"] },
            "EVAL" => new CommandMetadata { Name = name, Flags = ["noscript"] },
            "NOPE" => CommandMetadata.UnknownCommand(name),
            _ => null
        };
        var runner = new CommandRunner();
        var get = await runner.RunTerminalAsync(fake, audit.Log, Ctx(), "GET a", readOnly: true, confirmed: true, CancellationToken.None);
        Assert.True(get.Sent);
        var set = await runner.RunTerminalAsync(fake, audit.Log, Ctx(), "SET a secret", readOnly: true, confirmed: true, CancellationToken.None);
        Assert.False(set.Sent);
        Assert.False(set.Ok);
        var eval = await runner.RunTerminalAsync(fake, audit.Log, Ctx(), "EVAL \"redis.call('FLUSHALL')\" 0", readOnly: true, confirmed: true, CancellationToken.None);
        Assert.False(eval.Sent);
        var unknown = await runner.RunTerminalAsync(fake, audit.Log, Ctx(), "NOPE secret-arg", readOnly: true, confirmed: true, CancellationToken.None);
        Assert.False(unknown.Sent);
        Assert.DoesNotContain(fake.Commands, command => command[0] is "SET" or "EVAL" or "NOPE");
        Assert.DoesNotContain("secret", File.ReadAllText(audit.Path));
    }

    [Fact]
    public async Task Writable_mode_allows_unknown_commands_but_hides_arguments()
    {
        var audit = TempAudit();
        var fake = new FakeRedis();
        fake.Metadata = name => CommandMetadata.UnknownCommand(name);
        fake.Handler = _ => RedisReply.ErrorOf("ERR unknown secret-arg");
        var result = await new CommandRunner().RunTerminalAsync(fake, audit.Log, Ctx(), "WIDGET secret-arg", readOnly: false, confirmed: true, CancellationToken.None);
        Assert.True(result.Sent);
        Assert.False(result.Ok);
        Assert.Equal("WIDGET [参数已隐藏]", result.Echo);
        Assert.DoesNotContain("secret-arg", result.Output);
        Assert.DoesNotContain("secret-arg", result.Echo);
        Assert.DoesNotContain("secret-arg", File.ReadAllText(audit.Path));
        Assert.Contains("\"ok\":false", File.ReadAllText(audit.Path));
    }

    [Fact]
    public async Task Credential_script_and_write_values_are_scrubbed_including_errors()
    {
        var audit = TempAudit();
        var fake = new FakeRedis();
        fake.Metadata = name => new CommandMetadata { Name = name, Flags = name == "SET" ? ["write"] : ["readonly"] };
        fake.Handler = args => RedisReply.ErrorOf("ERR bad " + string.Join(' ', args.Skip(1).Select(arg => arg.Utf8)));
        var runner = new CommandRunner();
        var auth = await runner.RunTerminalAsync(fake, audit.Log, Ctx(), "AUTH hunter2", false, true, CancellationToken.None);
        var hello = await runner.RunTerminalAsync(fake, audit.Log, Ctx(), "HELLO 3 AUTH default hunter2", false, true, CancellationToken.None);
        var acl = await runner.RunTerminalAsync(fake, audit.Log, Ctx(), "ACL SETUSER ada on >hunter2", false, true, CancellationToken.None);
        var migrate = await runner.RunTerminalAsync(fake, audit.Log, Ctx(), "MIGRATE h 1 k 0 5 AUTH hunter2", false, true, CancellationToken.None);
        var eval = await runner.RunTerminalAsync(fake, audit.Log, Ctx(), "EVAL \"return 'hunter2'\" 0", false, true, CancellationToken.None);
        var set = await runner.RunTerminalAsync(fake, audit.Log, Ctx(), "SET mykey hunter2", false, true, CancellationToken.None);
        foreach (var result in new[] { auth, hello, acl, migrate, eval, set })
        {
            Assert.DoesNotContain("hunter2", result.Echo);
            Assert.DoesNotContain("hunter2", result.Output);
        }

        Assert.Contains("mykey", set.Echo);
        var raw = File.ReadAllText(audit.Path);
        Assert.DoesNotContain("hunter2", raw);
        var history = new CommandHistory();
        history.Accept(Parse("AUTH hunter2"), auth.Visibility);
        history.Accept(Parse("GET mykey"), ArgVisibility.ShowAll);
        Assert.DoesNotContain(history.Items, item => item.Contains("hunter2", StringComparison.Ordinal));
        Assert.Equal("GET mykey", history.Previous(""));
    }

    [Fact]
    public async Task Keys_and_delete_share_one_confirmation_and_cancel_is_not_success()
    {
        var audit = TempAudit();
        var fake = new FakeRedis();
        fake.Metadata = name => new CommandMetadata { Name = name, Flags = name == "KEYS" ? ["readonly"] : ["write"] };
        var runner = new CommandRunner();
        var keys = await runner.RunTerminalAsync(fake, audit.Log, Ctx(), "KEYS *", false, false, CancellationToken.None);
        Assert.True(keys.NeedsConfirmation);
        Assert.False(keys.Sent);
        Assert.Contains("这不是 SCAN", keys.Output);
        Assert.Contains("分页", keys.Output);
        Assert.Contains("*", keys.Output);
        Assert.Empty(fake.Commands);
        var deleted = await runner.RunTerminalAsync(fake, audit.Log, Ctx(), "DEL a", false, false, CancellationToken.None);
        Assert.True(deleted.NeedsConfirmation);
        Assert.False(deleted.Sent);
        var ping = new FakeRedis
        {
            Metadata = name => new CommandMetadata { Name = name, Flags = ["readonly"] },
            Handler = _ => throw new OperationCanceledException()
        };
        var cancelled = await runner.RunTerminalAsync(ping, audit.Log, Ctx(), "PING", true, true, CancellationToken.None);
        Assert.True(cancelled.Cancelled);
        Assert.False(cancelled.Ok);
        Assert.False(File.Exists(audit.Path) && File.ReadAllText(audit.Path).Contains("\"ok\":true", StringComparison.Ordinal));
    }

    [Fact]
    public void Quoted_arguments_keep_spaces_and_reject_open_quotes()
    {
        var parsed = Parse("SET \"my key\" \"a b\"");
        Assert.Equal("my key", parsed.Args[0]);
        Assert.Equal("a b", parsed.Args[1]);
        Assert.False(CommandParser.TryParse("SET \"open", out _, out var error));
        Assert.Contains("引号", error);
    }

    private static ParsedCommand Parse(string line)
    {
        Assert.True(CommandParser.TryParse(line, out var command, out var error), error);
        return command!;
    }

    private static AuditContext Ctx() => new("本地", 0);

    private static (JsonlAuditLog Log, string Path) TempAudit()
    {
        var path = Path.Combine(Path.GetTempPath(), "mr-audit-" + Guid.NewGuid().ToString("n") + ".jsonl");
        return (new JsonlAuditLog(path), path);
    }
}

public class StorageTests
{
    [Fact]
    public void Config_stays_beside_the_chosen_root_when_cwd_changes()
    {
        var root = NewDir();
        var other = NewDir();
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(other);
            var storage = AppStorage.Open(root);
            var store = new ConnectionStore(storage.ConnectionFile);
            var profile = new ConnectionProfile { Name = "本地", Host = "127.0.0.1", Port = 6379 };
            var protector = new TestProtector();
            store.SetPassword(profile, "s3cret-value", save: true, protector);
            store.Save([profile]);
            var json = File.ReadAllText(storage.ConnectionFile);
            Assert.DoesNotContain("s3cret-value", json);
            Assert.DoesNotContain("\"password\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(other, "data", "connections.json")));
            var loaded = store.Load().Single();
            Assert.Equal("s3cret-value", store.TryUnprotect(loaded, protector, out var error));
            Assert.Null(error);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void Unavailable_protector_refuses_to_save_a_password()
    {
        var storage = AppStorage.Open(NewDir());
        var store = new ConnectionStore(storage.ConnectionFile);
        var profile = new ConnectionProfile { Name = "本地", Host = "127.0.0.1", Port = 6379 };
        var protector = new TestProtector { IsAvailable = false };
        Assert.Throws<PasswordProtectionException>(() => store.SetPassword(profile, "s3cret-value", true, protector));
        store.Save([profile]);
        Assert.DoesNotContain("s3cret-value", File.ReadAllText(storage.ConnectionFile));
    }

    [Fact]
    public void Unwritable_root_is_reported_and_does_not_mention_a_fallback_path_as_a_destination()
    {
        var root = NewDir();
        var blocker = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var blocked = Path.Combine(blocker, "child");
        var error = Assert.Throws<StorageNotWritableException>(() => AppStorage.Open(blocked));
        Assert.Contains(blocked, error.Message);
        Assert.Contains("AppData", error.Message);
        Assert.Contains("不会改存", error.Message);
    }

    [Fact]
    public void Audit_round_trips_rotates_and_skips_bad_lines()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "audit.jsonl");
        var log = new JsonlAuditLog(path, maxBytes: 280, maxFiles: 3);
        for (var i = 0; i < 12; i++)
            Assert.True(log.Append(Entry("GET k" + i, ok: true)).Written);
        var files = Directory.GetFiles(dir, "audit*.jsonl");
        Assert.InRange(files.Length, 2, 3);
        File.AppendAllText(path, "this is not json\n");
        Assert.True(log.Append(Entry("GET newest", ok: false, "boom")).Written);
        var page = log.ReadRecent(0, 5);
        Assert.Equal("GET newest", page.Entries[0].Command);
        Assert.False(page.Entries[0].Ok);
        Assert.True(page.SkippedMalformed);
        Assert.Equal("2026-11-18 11:16:00", ShanghaiClock.Format(DateTimeOffset.Parse("2026-11-18T03:16:00Z")).Local);

        var plainPath = Path.Combine(dir, "plain.jsonl");
        var plain = new JsonlAuditLog(plainPath, maxBytes: 1_000_000, maxFiles: 2);
        Assert.True(plain.Append(Entry("GET kept", ok: true)).Written);
        AssertAppendFails(plain, plainPath);
        var again = plain.ReadRecent(0, 20);
        Assert.Contains(again.Entries, entry => entry.Command == "GET kept");
        Assert.DoesNotContain(again.Entries, entry => entry.Command == "GET after");
    }

    private static AuditEntry Entry(string command, bool ok, string error = "")
    {
        var clock = ShanghaiClock.Format(DateTimeOffset.Parse("2026-11-18T03:16:00Z"));
        return new AuditEntry
        {
            Time = clock.Local,
            TimeUtc = clock.Utc,
            Connection = "本地",
            Db = 0,
            Command = command,
            Ok = ok,
            Error = error
        };
    }

    private static void AssertAppendFails(JsonlAuditLog log, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.False(log.Append(Entry("GET after", ok: true)).Written);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        try
        {
            Assert.False(log.Append(Entry("GET after", ok: true)).Written);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string NewDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "mr-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public class SourceGuardTests
{
    [Fact]
    public void Transport_does_not_call_hidden_multi_scan_helpers()
    {
        var root = FindRoot();
        var redis = File.ReadAllText(Path.Combine(root, "src", "MiniRedis.Redis", "StackExchangeRedisClient.cs"));
        Assert.DoesNotContain(".Keys(", redis);
        Assert.DoesNotContain("KeysAsync", redis);
        Assert.DoesNotContain("CursorEnumerable", redis);
        var sources = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);
        var joined = string.Join('\n', sources.Select(File.ReadAllText));
        Assert.DoesNotContain("GetCurrentDirectory", joined);
        Assert.DoesNotContain("SpecialFolder", joined);
        var project = File.ReadAllText(Path.Combine(root, "src", "MiniRedis.App", "MiniRedis.App.csproj"));
        Assert.Contains("<PublishSingleFile>false</PublishSingleFile>", project);
        Assert.Contains("<IncludeNativeLibrariesForSelfExtract>false</IncludeNativeLibrariesForSelfExtract>", project);
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "windows.yml"));
        Assert.Contains("PublishSingleFile=false", workflow);
        Assert.Contains("--self-contained true", workflow);
        Assert.DoesNotContain("PublishSingleFile=true", workflow);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MiniRedis.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

file sealed class TestProtector : ISecretProtector
{
    public bool IsAvailable { get; set; } = true;
    public string ScopeDescription => "测试用保护器，不是 DPAPI。";
    public byte[] Protect(byte[] plaintext) => plaintext.Select(item => (byte)(item ^ 0x5A)).ToArray();
    public byte[] Unprotect(byte[] payload) => payload.Select(item => (byte)(item ^ 0x5A)).ToArray();
}

file sealed class FakeRedis : IRedisSession
{
    public int Database { get; set; }
    public List<string[]> Commands { get; } = [];
    public Func<IReadOnlyList<RedisArg>, RedisReply>? Handler { get; set; }
    public Func<string, CommandMetadata?>? Metadata { get; set; }
    public TaskCompletionSource<RedisReply>? Hold { get; set; }

    public async Task<RedisReply> ExecuteAsync(IReadOnlyList<RedisArg> arguments, CancellationToken cancellationToken)
    {
        Commands.Add(arguments.Select(arg => arg.IsUtf8 ? arg.Utf8 : arg.Display).ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        if (Hold != null)
        {
            var pending = Hold;
            Hold = null;
            return await pending.Task.WaitAsync(cancellationToken);
        }

        return Handler?.Invoke(arguments) ?? RedisReply.Simple("OK");
    }

    public Task<CommandMetadata?> DescribeCommandAsync(string commandName, CancellationToken cancellationToken) =>
        Task.FromResult(Metadata?.Invoke(commandName));
}
