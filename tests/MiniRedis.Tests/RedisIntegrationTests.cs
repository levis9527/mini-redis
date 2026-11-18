using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using MiniRedis.Core.Protocol;
using MiniRedis.Core.Scanning;
using MiniRedis.Core.Storage;
using MiniRedis.RedisTransport;
using StackExchange.Redis;
using Xunit;

namespace MiniRedis.Tests;

[CollectionDefinition("redis", DisableParallelization = true)]
public sealed class RedisCollection : ICollectionFixture<RedisServerFixture>
{
}

[Collection("redis")]
public class RedisIntegrationTests
{
    private readonly RedisServerFixture _server;

    public RedisIntegrationTests(RedisServerFixture server)
    {
        _server = server;
    }

    [Fact]
    public async Task One_scan_request_is_one_scan_on_the_wire()
    {
        var seen = await MonitorAsync(async () =>
        {
            var session = await OpenAsync();
            var broker = new ScanBroker();
            await broker.ScanOnceAsync(session, new ScanQuery { Pattern = "wire:*", CountHint = 10, TypeFilter = null }, broker.Generation, CancellationToken.None);
        });
        Assert.Equal(1, CountToken(seen, "SCAN"));
    }

    [Fact]
    public async Task Binary_get_keeps_bytes_and_literal_star_is_not_a_glob()
    {
        var mux = await ConnectionMultiplexer.ConnectAsync(Configuration());
        var db = mux.GetDatabase();
        var binaryKey = "bin:" + Guid.NewGuid().ToString("n");
        var bytes = new byte[] { 0x00, 0xFF, 0xFE, 0x80 };
        await db.StringSetAsync(binaryKey, bytes);
        await db.StringSetAsync("a*b", "literal");
        await db.StringSetAsync("aXb", "other");

        var session = await OpenAsync();
        var got = await session.ExecuteAsync([RedisArg.FromText("GET"), RedisArg.FromText(binaryKey)], CancellationToken.None);
        Assert.Equal(bytes, got.Bytes);

        var exists = await session.ExecuteAsync([RedisArg.FromText("EXISTS"), RedisArg.FromText("a*b")], CancellationToken.None);
        Assert.Equal("1", exists.Text);
        var keys = await session.ExecuteAsync([RedisArg.FromText("KEYS"), RedisArg.FromText("a*b")], CancellationToken.None);
        var names = (keys.Items ?? []).Select(item => item.Text).ToArray();
        Assert.True(keys.Kind == RedisReplyKind.Array, keys.ErrorText + " / " + keys.Text);
        Assert.Contains("a*b", names);
        Assert.Contains("aXb", names);
        await mux.CloseAsync();
    }

    [Fact]
    public async Task Command_metadata_classifies_read_write_script_and_unknown()
    {
        var session = await OpenAsync();
        var get = await session.DescribeCommandAsync("GET", CancellationToken.None);
        var set = await session.DescribeCommandAsync("SET", CancellationToken.None);
        var eval = await session.DescribeCommandAsync("EVAL", CancellationToken.None);
        var missing = await session.DescribeCommandAsync("NOSUCHCOMMANDXYZ", CancellationToken.None);
        Assert.NotNull(get);
        Assert.True(get!.IsReadOnly);
        Assert.NotNull(set);
        Assert.False(set!.IsReadOnly);
        Assert.NotNull(eval);
        Assert.False(eval!.IsReadOnly);
        Assert.NotNull(missing);
        Assert.True(missing!.Unknown);
    }

    private async Task<IRedisSession> OpenAsync()
    {
        var client = await StackExchangeRedisClient.ConnectAsync(Profile(), password: null, CancellationToken.None);
        return client.Open(0);
    }

    private ConnectionProfile Profile() => new()
    {
        Name = "临时",
        Host = "127.0.0.1",
        Port = _server.Port,
        ConnectTimeoutMs = 3000,
        CommandTimeoutMs = 3000
    };

    private string Configuration() => "127.0.0.1:" + _server.Port + ",abortConnect=true,connectTimeout=3000,syncTimeout=3000";

    private async Task<string> MonitorAsync(Func<Task> body)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", _server.Port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("*1\r\n$7\r\nMONITOR\r\n"));
        await Task.Delay(100);
        await body();
        await Task.Delay(400);
        var buffer = new StringBuilder();
        var bytes = new byte[8192];
        while (stream.DataAvailable)
        {
            var count = await stream.ReadAsync(bytes);
            if (count <= 0)
                break;
            buffer.Append(Encoding.UTF8.GetString(bytes, 0, count));
        }

        return buffer.ToString();
    }

    private static int CountToken(string text, string token)
    {
        var needle = "\"" + token + "\"";
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}

public sealed class RedisServerFixture : IAsyncLifetime
{
    private Process? _process;
    private string _directory = "";

    public int Port { get; private set; }

    public async Task InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable("MINIREDIS_REDIS_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            server = "redis-server";
        _directory = Path.Combine(Path.GetTempPath(), "mr-redis-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
        Port = FreePort();
        var config = Path.Combine(_directory, "redis.conf");
        await File.WriteAllTextAsync(config,
            "bind 127.0.0.1\nport " + Port + "\nprotected-mode no\nsave \"\"\nappendonly no\ndir " + _directory + "\ndbfilename dump.rdb\n");
        var start = new ProcessStartInfo(server, "\"" + config + "\"")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        _process = Process.Start(start) ?? throw new InvalidOperationException("无法启动临时 redis-server。");
        _process.BeginOutputReadLine();
        var stderr = new StringBuilder();
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stderr.AppendLine(e.Data);
        };
        _process.BeginErrorReadLine();
        var ready = false;
        for (var i = 0; i < 50; i++)
        {
            if (_process.HasExited)
                throw new InvalidOperationException("临时 Redis 退出：" + stderr);
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", Port);
                var stream = tcp.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("*1\r\n$4\r\nPING\r\n"));
                var buffer = new byte[16];
                var read = await stream.ReadAsync(buffer);
                if (Encoding.ASCII.GetString(buffer, 0, read).Contains("+PONG", StringComparison.Ordinal))
                {
                    ready = true;
                    break;
                }
            }
            catch (SocketException)
            {
            }

            await Task.Delay(100);
        }

        if (!ready)
            throw new InvalidOperationException("临时 Redis 未就绪：" + stderr);
    }

    public Task DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch
        {
        }

        try
        {
            if (_directory.Length > 0 && Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }

        return Task.CompletedTask;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
