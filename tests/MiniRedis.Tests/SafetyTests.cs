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
    public async Task Set_rename_adds_before_removing_and_stops_when_add_fails()
    {
        var fake = new RecordingRedis { FailNext = true };
        var change = ValueGuard.DescribeMemberChange("set", "kept", "", "next", "");
        var reply = await ValueGuard.ApplyMemberChangeAsync((args, _) => fake.ExecuteAsync(args, CancellationToken.None), RedisArg.FromText("colors"), change, CancellationToken.None);
        Assert.True(reply.IsError);
        Assert.Equal(["SADD"], fake.Commands.Select(command => command[0]).ToArray());
    }

    [Fact]
    public async Task Set_rename_removes_original_only_after_add()
    {
        var fake = new RecordingRedis();
        var change = ValueGuard.DescribeMemberChange("set", "kept", "", "next", "");
        var reply = await ValueGuard.ApplyMemberChangeAsync((args, _) => fake.ExecuteAsync(args, CancellationToken.None), RedisArg.FromText("colors"), change, CancellationToken.None);
        Assert.False(reply.IsError);
        Assert.Equal(["SADD", "SREM"], fake.Commands.Select(command => command[0]).ToArray());
        Assert.Equal("next", fake.Commands[0][2]);
        Assert.Equal("kept", fake.Commands[1][2]);
    }

    [Fact]
    public async Task String_save_uses_keepttl_and_falls_back_without_clearing_expiry()
    {
        var kept = new RecordingRedis();
        kept.Handler = args => args[0].Utf8 == "PTTL" ? RedisReply.IntegerOf(30000) : RedisReply.Simple("OK");
        await ValueGuard.SaveStringPreservingTtlAsync((args, _) => kept.ExecuteAsync(args, CancellationToken.None), RedisArg.FromText("k"), "edited", CancellationToken.None);
        Assert.Contains(kept.Commands, command => command[0] == "SET" && command.Any(part => part == "KEEPTTL"));
        Assert.DoesNotContain(kept.Commands, command => command[0] == "PEXPIRE");

        var fallback = new RecordingRedis();
        fallback.Handler = args =>
        {
            if (args[0].Utf8 == "PTTL")
                return RedisReply.IntegerOf(30000);
            if (args.Any(part => part.Utf8 == "KEEPTTL"))
                return RedisReply.ErrorOf("ERR syntax error");
            return RedisReply.Simple("OK");
        };
        await ValueGuard.SaveStringPreservingTtlAsync((args, _) => fallback.ExecuteAsync(args, CancellationToken.None), RedisArg.FromText("k"), "edited", CancellationToken.None);
        Assert.Contains(fallback.Commands, command => command[0] == "SET" && command.All(part => part != "KEEPTTL"));
        Assert.Contains(fallback.Commands, command => command[0] == "PEXPIRE" && command[2] == "30000");
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
    public async Task Saving_a_string_keeps_ttl_within_the_previous_window()
    {
        var key = "ttl:" + Guid.NewGuid().ToString("n");
        await using var ui = await ConnectAsync();
        var session = ui.Open(0);
        async Task<RedisReply> Exec(IReadOnlyList<RedisArg> args, CancellationToken ct) => await session.ExecuteAsync(args, ct);
        await Exec([RedisArg.FromText("SET"), RedisArg.FromText(key), RedisArg.FromText("old"), RedisArg.FromText("PX"), RedisArg.FromText("30000")], CancellationToken.None);
        await ValueGuard.SaveStringPreservingTtlAsync(Exec, RedisArg.FromText(key), "new", CancellationToken.None);
        var pttl = await Exec(KeyArgs.Of("PTTL", RedisArg.FromText(key)), CancellationToken.None);
        var ttl = pttl.Integer ?? long.Parse(pttl.Text);
        Assert.InRange(ttl, 1000, 30000);
        var value = await Exec([RedisArg.FromText("GET"), RedisArg.FromText(key)], CancellationToken.None);
        Assert.Equal("new", value.Text);
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
