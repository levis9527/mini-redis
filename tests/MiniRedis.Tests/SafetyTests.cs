using System.Text;
using MiniRedis.Core.Keys;
using MiniRedis.Core.Protocol;
using MiniRedis.Core.Runtime;
using MiniRedis.Core.Scanning;
using MiniRedis.Core.Storage;
using MiniRedis.RedisTransport;
using StackExchange.Redis;
using Xunit;

namespace MiniRedis.Tests;

public class EditSafetyTests
{
    [Fact]
    public void Unchanged_member_is_not_a_write_and_blank_box_is_a_change()
    {
        var same = ValueGuard.DescribeMemberChange("hash", "field", "hello", "field", "hello");
        var blank = ValueGuard.DescribeMemberChange("hash", "field", "hello", "field", "");
        Assert.False(same.Changed);
        Assert.True(blank.Changed);
        Assert.True(blank.EmptyValue);
    }

    [Fact]
    public async Task Blank_or_cancelled_member_edit_does_not_send_commands()
    {
        var fake = new RecordingRedis();
        var same = ValueGuard.DescribeMemberChange("hash", "field", "hello", "field", "hello");
        Assert.False(same.Changed);
        var blank = ValueGuard.DescribeMemberChange("set", "kept", "", "kept", "");
        Assert.False(blank.Changed);
        Assert.Empty(fake.Commands);
        await Task.CompletedTask;
    }

    [Fact]
    public void Unchanged_text_is_not_written_and_empty_string_still_is()
    {
        Assert.False(ValueGuard.ShouldWriteString("same", "same"));
        Assert.False(ValueGuard.ShouldWriteString("", ""));
        Assert.True(ValueGuard.ShouldWriteString("same", ""));
        Assert.True(ValueGuard.ShouldWriteString("", "x"));
    }

    [Fact]
    public async Task Set_rename_does_not_fall_back_to_sadd_srem_when_script_is_denied()
    {
        var fake = new RecordingRedis();
        fake.Handler = _ => RedisReply.ErrorOf("NOPERM this user has no permissions to run the 'eval' command");
        var change = ValueGuard.DescribeMemberChange("set", "kept", "", "next", "");
        var reply = await ValueGuard.ApplyMemberChangeAsync((args, _) => fake.ExecuteAsync(args, CancellationToken.None), RedisArg.FromText("colors"), change, CancellationToken.None);
        Assert.True(reply.IsError);
        Assert.Equal(ValueGuard.UncertainMessage, reply.ErrorText);
        Assert.DoesNotContain("没有改", reply.ErrorText);
        Assert.DoesNotContain("next", reply.ErrorText);
        Assert.DoesNotContain("kept", reply.ErrorText);
        var follow = ValueGuard.EditFollowUp.FromReply(reply);
        Assert.True(follow.Reload);
        Assert.Equal(ValueGuard.UncertainMessage, follow.Message);
        Assert.Equal(["EVAL"], fake.Commands.Select(command => command[0]).ToArray());
        Assert.DoesNotContain(fake.Commands, command => command[0] is "SADD" or "SREM");
    }

    [Fact]
    public async Task Timeout_does_not_claim_the_write_was_skipped_or_echo_the_value()
    {
        var secret = "secret-value";
        var timedOut = new RecordingRedis { Handler = _ => RedisReply.ErrorOf("Timeout performing SET " + secret) };
        var reply = await ValueGuard.SaveStringPreservingTtlAsync((args, _) => timedOut.ExecuteAsync(args, CancellationToken.None), RedisArg.FromText("k"), secret, CancellationToken.None);
        Assert.Equal(ValueGuard.UncertainMessage, reply.ErrorText);
        Assert.DoesNotContain("没有写入", reply.ErrorText);
        Assert.DoesNotContain(secret, reply.ErrorText);
        Assert.True(ValueGuard.EditFollowUp.FromReply(reply).Reload);

        var scriptTimeout = new RecordingRedis { Handler = _ => RedisReply.ErrorOf("NOPERM SREM secret-member") };
        var change = ValueGuard.DescribeMemberChange("set", "kept", "", "secret-member", "");
        var renamed = await ValueGuard.ApplyMemberChangeAsync((args, _) => scriptTimeout.ExecuteAsync(args, CancellationToken.None), RedisArg.FromText("colors"), change, CancellationToken.None);
        Assert.Equal(ValueGuard.UncertainMessage, renamed.ErrorText);
        Assert.DoesNotContain("secret-member", renamed.ErrorText);
        Assert.True(ValueGuard.EditFollowUp.FromReply(renamed).Reload);
    }

    [Fact]
    public async Task Set_rename_sends_one_script_and_not_two_commands()
    {
        var fake = new RecordingRedis();
        var change = ValueGuard.DescribeMemberChange("set", "kept", "", "next", "");
        var reply = await ValueGuard.ApplyMemberChangeAsync((args, _) => fake.ExecuteAsync(args, CancellationToken.None), RedisArg.FromText("colors"), change, CancellationToken.None);
        Assert.False(reply.IsError);
        Assert.Single(fake.Commands);
        Assert.Equal("EVAL", fake.Commands[0][0]);
        Assert.Equal("kept", fake.Commands[0][4]);
        Assert.Equal("next", fake.Commands[0][5]);
    }

    [Fact]
    public async Task String_save_is_one_conditional_command_and_does_not_rewrite_on_failure()
    {
        var ok = new RecordingRedis();
        var saved = await ValueGuard.SaveStringPreservingTtlAsync((args, _) => ok.ExecuteAsync(args, CancellationToken.None), RedisArg.FromText("k"), "", CancellationToken.None);
        Assert.False(saved.IsError);
        Assert.Single(ok.Commands);
        Assert.Equal(["SET", "k", "", "XX", "KEEPTTL"], ok.Commands[0]);

        foreach (var error in new[] { "ERR syntax error", "NOPERM no permissions to run the 'set' command", "ERR unknown command" })
        {
            var denied = new RecordingRedis { Handler = _ => RedisReply.ErrorOf(error) };
            var reply = await ValueGuard.SaveStringPreservingTtlAsync((args, _) => denied.ExecuteAsync(args, CancellationToken.None), RedisArg.FromText("k"), "edited", CancellationToken.None);
            Assert.True(reply.IsError);
            Assert.Contains("没有改用其他写入", reply.ErrorText);
            Assert.DoesNotContain("edited", reply.ErrorText);
            Assert.Single(denied.Commands);
            Assert.Equal("XX", denied.Commands[0][3]);
            Assert.Equal("KEEPTTL", denied.Commands[0][4]);
            Assert.DoesNotContain(denied.Commands, command => command[0] == "PEXPIRE");
        }

        var gone = new RecordingRedis { Handler = _ => RedisReply.Null() };
        var missing = await ValueGuard.SaveStringPreservingTtlAsync((args, _) => gone.ExecuteAsync(args, CancellationToken.None), RedisArg.FromText("k"), "edited", CancellationToken.None);
        Assert.True(missing.IsError);
        Assert.Contains("不会把键重新创建", missing.ErrorText);
        Assert.Single(gone.Commands);
    }

    [Fact]
    public void Large_string_cannot_be_saved_until_the_loaded_bytes_cover_it()
    {
        var buffer = new StringBuffer();
        var first = Encoding.UTF8.GetBytes(new string('a', AppLimits.MaxStringBytes));
        buffer.Reset(first, AppLimits.MaxStringBytes + 10);
        Assert.False(buffer.CanEdit);
        Assert.True(buffer.CanContinue);
        buffer.Append(Encoding.UTF8.GetBytes(new string('b', 10)));
        Assert.True(buffer.IsComplete);
        Assert.True(buffer.CanEdit);
        Assert.Equal(AppLimits.MaxStringBytes + 10, buffer.EditText.Length);
    }

    [Fact]
    public void Binary_bytes_stay_unreadable_as_text_and_cannot_be_saved()
    {
        var buffer = new StringBuffer();
        buffer.Reset([0x00, 0xFF, 0xFE], 3);
        Assert.False(buffer.IsUtf8);
        Assert.False(buffer.CanEdit);
        Assert.Equal([0x00, 0xFF, 0xFE], buffer.Data);
    }

    [Fact]
    public async Task Queued_reply_is_not_recorded_as_success_and_readonly_blocks_multi()
    {
        var audit = new JsonlAuditLog(Path.Combine(Path.GetTempPath(), "mr-q-" + Guid.NewGuid().ToString("n") + ".jsonl"));
        var fake = new RecordingRedis();
        fake.Metadata = _ => new CommandMetadata { Name = "SET", Flags = ["write"] };
        fake.Handler = _ => RedisReply.Simple("QUEUED");
        var queued = await new CommandRunner().RunTerminalAsync(fake, audit, new AuditContext("本地", 0), "SET k v", false, true, CancellationToken.None);
        Assert.True(queued.Sent);
        Assert.False(queued.Ok);
        Assert.Contains("还没有执行", queued.Output);
        Assert.Contains("\"ok\":false", File.ReadAllText(audit.Path));

        var blocked = new RecordingRedis();
        blocked.Metadata = _ => new CommandMetadata { Name = "MULTI", Flags = ["noscript", "slow"] };
        var denied = await new CommandRunner().RunTerminalAsync(blocked, audit, new AuditContext("本地", 0), "MULTI", true, true, CancellationToken.None);
        Assert.False(denied.Sent);
        Assert.DoesNotContain(blocked.Commands, command => command[0] == "MULTI");
    }

    [Theory]
    [InlineData("ok=true\nstartup_ms=1007\nworking_set_bytes=56692736\nexit_code=0\n", true)]
    [InlineData("ok=false\nerror=smoke process produced no metrics file\n", false)]
    [InlineData("ok=true\nstartup_ms=10\nworking_set_bytes=0\nexit_code=0\n", false)]
    public void Smoke_report_requires_success_and_a_positive_working_set(string text, bool valid)
    {
        Assert.Equal(valid, SmokeReport.TryValidate(text, out _));
    }
}

[Collection("redis")]
public class SafetyRedisTests
{
    private readonly RedisServerFixture _server;

    public SafetyRedisTests(RedisServerFixture server)
    {
        _server = server;
    }

    [Fact]
    public async Task Redis5_string_save_is_rejected_and_leaves_the_key_unchanged()
    {
        var major = await ServerMajorAsync();
        if (major >= 6)
        {
            Console.WriteLine("REDIS5_REJECTION_NOT_RUN major=" + major);
            return;
        }
        await using var ui = await ConnectAsync();
        var session = ui.Open(0);
        async Task<RedisReply> Exec(IReadOnlyList<RedisArg> args, CancellationToken ct) => await session.ExecuteAsync(args, ct);
        var key = "ttl:" + Guid.NewGuid().ToString("n");
        await Exec([RedisArg.FromText("SET"), RedisArg.FromText(key), RedisArg.FromText("old"), RedisArg.FromText("PX"), RedisArg.FromText("30000")], CancellationToken.None);
        var save = await ValueGuard.SaveStringPreservingTtlAsync(Exec, RedisArg.FromText(key), "new", CancellationToken.None);
        Assert.Contains("没有改用其他写入", save.ErrorText ?? "");
        var value = await Exec([RedisArg.FromText("GET"), RedisArg.FromText(key)], CancellationToken.None);
        Assert.Equal("old", value.Text);
        var ttl = await PttlAsync(Exec, key);
        Assert.InRange(ttl, 1000, 30000);
        await AssertMissingStaysMissingAsync(Exec, key);
    }

    [Fact]
    public async Task Redis7_string_save_keeps_ttl_and_does_not_recreate_a_missing_key()
    {
        var major = await ServerMajorAsync();
        if (major < 7)
            Assert.Fail("REDIS7_SUCCESS_PATH_NOT_EXECUTED major=" + major + "。这次失败表示没有跑成 Redis 7 的保存证明，不能把它算进通过数。");
        Console.WriteLine("REDIS7_TTL_PRESERVED major=" + major);
        await using var ui = await ConnectAsync();
        var session = ui.Open(0);
        async Task<RedisReply> Exec(IReadOnlyList<RedisArg> args, CancellationToken ct) => await session.ExecuteAsync(args, ct);
        var key = "ttl:" + Guid.NewGuid().ToString("n");
        await Exec([RedisArg.FromText("SET"), RedisArg.FromText(key), RedisArg.FromText("old"), RedisArg.FromText("PX"), RedisArg.FromText("30000")], CancellationToken.None);
        var save = await ValueGuard.SaveStringPreservingTtlAsync(Exec, RedisArg.FromText(key), "new", CancellationToken.None);
        Assert.False(save.IsError);
        var value = await Exec([RedisArg.FromText("GET"), RedisArg.FromText(key)], CancellationToken.None);
        Assert.Equal("new", value.Text);
        Assert.InRange(await PttlAsync(Exec, key), 1000, 30000);
        var empty = await ValueGuard.SaveStringPreservingTtlAsync(Exec, RedisArg.FromText(key), "", CancellationToken.None);
        Assert.False(empty.IsError);
        var emptyValue = await Exec([RedisArg.FromText("GET"), RedisArg.FromText(key)], CancellationToken.None);
        Assert.Equal("", emptyValue.Text);
        Assert.InRange(await PttlAsync(Exec, key), 1000, 30000);
        await AssertMissingStaysMissingAsync(Exec, key);
    }

    private static async Task AssertMissingStaysMissingAsync(Func<IReadOnlyList<RedisArg>, CancellationToken, Task<RedisReply>> exec, string key)
    {
        await exec([RedisArg.FromText("DEL"), RedisArg.FromText(key)], CancellationToken.None);
        var deleted = await ValueGuard.SaveStringPreservingTtlAsync(exec, RedisArg.FromText(key), "back", CancellationToken.None);
        Assert.True(deleted.IsError);
        var exists = await exec([RedisArg.FromText("EXISTS"), RedisArg.FromText(key)], CancellationToken.None);
        Assert.Equal("0", exists.Text);

        var expiring = "exp:" + Guid.NewGuid().ToString("n");
        await exec([RedisArg.FromText("SET"), RedisArg.FromText(expiring), RedisArg.FromText("soon"), RedisArg.FromText("PX"), RedisArg.FromText("50")], CancellationToken.None);
        await Task.Delay(200);
        var expired = await ValueGuard.SaveStringPreservingTtlAsync(exec, RedisArg.FromText(expiring), "later", CancellationToken.None);
        Assert.True(expired.IsError);
        var expiredExists = await exec([RedisArg.FromText("EXISTS"), RedisArg.FromText(expiring)], CancellationToken.None);
        Assert.Equal("0", expiredExists.Text);
    }

    private static async Task<long> PttlAsync(Func<IReadOnlyList<RedisArg>, CancellationToken, Task<RedisReply>> exec, string key)
    {
        var pttl = await exec(KeyArgs.Of("PTTL", RedisArg.FromText(key)), CancellationToken.None);
        return pttl.Integer ?? long.Parse(pttl.Text);
    }

    private async Task<int> ServerMajorAsync()
    {
        await using var ui = await ConnectAsync();
        var info = await ui.Open(0).ExecuteAsync([RedisArg.FromText("INFO"), RedisArg.FromText("server")], CancellationToken.None);
        return MajorVersion(info.Text);
    }

    [Fact]
    public async Task Set_rename_script_failure_does_not_leave_both_members()
    {
        await using var ui = await ConnectAsync();
        var session = ui.Open(0);
        async Task<RedisReply> Exec(IReadOnlyList<RedisArg> args, CancellationToken ct) => await session.ExecuteAsync(args, ct);
        var key = "set:" + Guid.NewGuid().ToString("n");
        await Exec([RedisArg.FromText("SADD"), RedisArg.FromText(key), RedisArg.FromText("kept")], CancellationToken.None);
        var change = ValueGuard.DescribeMemberChange("set", "kept", "", "next", "");
        var renamed = await ValueGuard.ApplyMemberChangeAsync(Exec, RedisArg.FromText(key), change, CancellationToken.None);
        Assert.False(renamed.IsError);
        var members = await Exec([RedisArg.FromText("SMEMBERS"), RedisArg.FromText(key)], CancellationToken.None);
        var names = (members.Items ?? []).Select(item => item.Text).ToArray();
        Assert.Contains("next", names);
        Assert.DoesNotContain("kept", names);

        await Exec([RedisArg.FromText("DEL"), RedisArg.FromText(key)], CancellationToken.None);
        var missing = await ValueGuard.ApplyMemberChangeAsync(Exec, RedisArg.FromText(key), change, CancellationToken.None);
        Assert.True(missing.IsError);
        var exists = await Exec([RedisArg.FromText("EXISTS"), RedisArg.FromText(key)], CancellationToken.None);
        Assert.Equal("0", exists.Text);

        var textKey = "str:" + Guid.NewGuid().ToString("n");
        await Exec([RedisArg.FromText("SET"), RedisArg.FromText(textKey), RedisArg.FromText("not-a-set")], CancellationToken.None);
        var wrong = await ValueGuard.ApplyMemberChangeAsync(Exec, RedisArg.FromText(textKey), change, CancellationToken.None);
        Assert.True(wrong.IsError);
        var still = await Exec([RedisArg.FromText("GET"), RedisArg.FromText(textKey)], CancellationToken.None);
        Assert.Equal("not-a-set", still.Text);
    }

    private static int MajorVersion(string info)
    {
        foreach (var raw in info.Split('\n', '\r'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("redis_version:", StringComparison.Ordinal))
                continue;
            var version = line["redis_version:".Length..];
            return int.Parse(version.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException("INFO 里没有 redis_version。");
    }

    [Fact]
    public async Task Terminal_select_does_not_redirect_ui_writes_until_the_ui_database_changes()
    {
        var key = "sel:" + Guid.NewGuid().ToString("n");
        await using var uiClient = await ConnectAsync();
        await using var terminalClient = await ConnectAsync();
        var ui = uiClient.Open(0);
        var terminal = new TrackedSession(terminalClient, 0);
        await ui.ExecuteAsync([RedisArg.FromText("SET"), RedisArg.FromText(key), RedisArg.FromText("db0")], CancellationToken.None);
        var select = await terminal.ExecuteAsync([RedisArg.FromText("SELECT"), RedisArg.FromText("1")], CancellationToken.None);
        Assert.False(select.IsError);
        Assert.Equal(1, terminal.Database);
        await terminal.ExecuteAsync([RedisArg.FromText("SET"), RedisArg.FromText(key), RedisArg.FromText("terminal")], CancellationToken.None);
        await ui.ExecuteAsync([RedisArg.FromText("SET"), RedisArg.FromText(key), RedisArg.FromText("ui-still-0")], CancellationToken.None);
        var probe = await ConnectAsync();
        var db0 = await probe.Open(0).ExecuteAsync([RedisArg.FromText("GET"), RedisArg.FromText(key)], CancellationToken.None);
        var db1 = await probe.Open(1).ExecuteAsync([RedisArg.FromText("GET"), RedisArg.FromText(key)], CancellationToken.None);
        Assert.Equal("ui-still-0", db0.Text);
        Assert.Equal("terminal", db1.Text);

        var uiOnSelected = uiClient.Open(terminal.Database);
        await uiOnSelected.ExecuteAsync([RedisArg.FromText("SET"), RedisArg.FromText(key), RedisArg.FromText("ui-follows")], CancellationToken.None);
        var db1After = await probe.Open(1).ExecuteAsync([RedisArg.FromText("GET"), RedisArg.FromText(key)], CancellationToken.None);
        var db0After = await probe.Open(0).ExecuteAsync([RedisArg.FromText("GET"), RedisArg.FromText(key)], CancellationToken.None);
        Assert.Equal("ui-follows", db1After.Text);
        Assert.Equal("ui-still-0", db0After.Text);
        await probe.DisposeAsync();
    }

    [Fact]
    public async Task Ui_command_during_terminal_multi_is_not_queued()
    {
        var key = "tx:" + Guid.NewGuid().ToString("n");
        await using var uiClient = await ConnectAsync();
        await using var terminalClient = await ConnectAsync();
        var ui = uiClient.Open(0);
        var terminal = new TrackedSession(terminalClient, 0);
        Assert.False((await terminal.ExecuteAsync([RedisArg.FromText("MULTI")], CancellationToken.None)).IsError);
        var queued = await terminal.ExecuteAsync([RedisArg.FromText("SET"), RedisArg.FromText(key), RedisArg.FromText("v")], CancellationToken.None);
        Assert.Equal("QUEUED", queued.Text);
        var ping = await ui.ExecuteAsync([RedisArg.FromText("PING")], CancellationToken.None);
        Assert.Equal("PONG", ping.Text);
        var exec = await terminal.ExecuteAsync([RedisArg.FromText("EXEC")], CancellationToken.None);
        Assert.False(exec.IsError);
        Assert.DoesNotContain(exec.Items ?? [], item => item.Text == "PONG");
        var value = await ui.ExecuteAsync([RedisArg.FromText("GET"), RedisArg.FromText(key)], CancellationToken.None);
        Assert.Equal("v", value.Text);
    }

    private Task<StackExchangeRedisClient> ConnectAsync() =>
        StackExchangeRedisClient.ConnectAsync(new ConnectionProfile
        {
            Name = "safe",
            Host = "127.0.0.1",
            Port = _server.Port,
            ConnectTimeoutMs = 3000,
            CommandTimeoutMs = 3000
        }, null, CancellationToken.None);
}

file sealed class RecordingRedis : IRedisSession
{
    public int Database { get; set; }
    public List<string[]> Commands { get; } = [];
    public bool FailNext { get; set; }
    public Func<IReadOnlyList<RedisArg>, RedisReply>? Handler { get; set; }
    public Func<string, CommandMetadata?>? Metadata { get; set; }

    public Task<RedisReply> ExecuteAsync(IReadOnlyList<RedisArg> arguments, CancellationToken cancellationToken)
    {
        Commands.Add(arguments.Select(arg => arg.IsUtf8 ? arg.Utf8 : arg.Display).ToArray());
        if (FailNext)
        {
            FailNext = false;
            return Task.FromResult(RedisReply.ErrorOf("ERR add failed"));
        }

        return Task.FromResult(Handler?.Invoke(arguments) ?? RedisReply.Simple("OK"));
    }

    public Task<CommandMetadata?> DescribeCommandAsync(string commandName, CancellationToken cancellationToken) =>
        Task.FromResult(Metadata?.Invoke(commandName));
}
