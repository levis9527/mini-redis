using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MiniRedis.Core.Commands;
using MiniRedis.Core.Protocol;
using MiniRedis.Core.Storage;
using StackExchange.Redis;

namespace MiniRedis.RedisTransport;

public sealed class StackExchangeRedisClient : IAsyncDisposable
{
    private readonly ConnectionMultiplexer _multiplexer;
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;

    private StackExchangeRedisClient(ConnectionMultiplexer multiplexer)
    {
        _multiplexer = multiplexer;
    }

    public static async Task<StackExchangeRedisClient> ConnectAsync(
        ConnectionProfile profile,
        string? password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = new ConfigurationOptions
        {
            AbortOnConnectFail = true,
            ConnectTimeout = profile.ConnectTimeoutMs,
            SyncTimeout = profile.CommandTimeoutMs,
            AsyncTimeout = profile.CommandTimeoutMs,
            Ssl = profile.UseTls,
            ClientName = "MiniRedis",
            // KEYS 被这个库当成管理命令；不打开的话，确认后的 KEYS 也发不出去。
            // 清库和 SHUTDOWN 仍在调用 Execute 之前拒绝。AllowAdmin 不是安全开关。
            AllowAdmin = true,
            User = string.IsNullOrEmpty(profile.Username) ? null : profile.Username,
            Password = string.IsNullOrEmpty(password) ? null : password
        };
        if (profile.UseTls && !string.IsNullOrWhiteSpace(profile.Host))
            options.SslHost = profile.Host;
        options.EndPoints.Add(profile.Host, profile.Port);
        if (profile.AllowUntrustedTls)
        {
            options.CertificateValidation += AcceptAnyCertificate;
        }

        try
        {
            var connecting = ConnectionMultiplexer.ConnectAsync(options);
            var finished = await Task.WhenAny(connecting, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            if (finished != connecting)
            {
                _ = connecting.ContinueWith(task =>
                {
                    if (task.IsFaulted)
                        _ = task.Exception;
                    try
                    {
                        task.Result.Dispose();
                    }
                    catch
                    {
                    }
                }, TaskScheduler.Default);
                throw new OperationCanceledException(cancellationToken);
            }

            return new StackExchangeRedisClient(await connecting.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not RedisConnectException)
        {
            throw new RedisConnectException(CommandSanitizer.SanitizeConnectError(ex.Message));
        }
    }

    public RedisSession Open(int database) => new(this, database);

    public async ValueTask DisposeAsync()
    {
        await _multiplexer.CloseAsync().ConfigureAwait(false);
        _multiplexer.Dispose();
    }

    internal async Task<RedisReply> ExecuteAsync(int database, IReadOnlyList<RedisArg> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count == 0)
            throw new ArgumentException("缺少命令。", nameof(arguments));
        cancellationToken.ThrowIfCancellationRequested();

        Task previous;
        var caller = new TaskCompletionSource<RedisReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            previous = _tail;
            _tail = tail.Task;
        }

        _ = Run();
        return await caller.Task.ConfigureAwait(false);

        async Task Run()
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch
            {
            }

            if (cancellationToken.IsCancellationRequested)
            {
                caller.TrySetCanceled(cancellationToken);
                tail.TrySetResult();
                return;
            }

            Task<RedisResult> send;
            try
            {
                var name = Encoding.UTF8.GetString(arguments[0].Bytes);
                var rest = new object[arguments.Count - 1];
                for (var i = 1; i < arguments.Count; i++)
                    rest[i - 1] = arguments[i].Bytes;
                send = _multiplexer.GetDatabase(database).ExecuteAsync(name, rest, CommandFlags.None);
            }
            catch (Exception ex)
            {
                caller.TrySetResult(RedisReply.ErrorOf(CommandSanitizer.SanitizeConnectError(ex.Message)));
                tail.TrySetResult();
                return;
            }

            var winner = await Task.WhenAny(send, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            if (winner != send)
            {
                caller.TrySetCanceled(cancellationToken);
                _ = send.ContinueWith(task =>
                {
                    if (task.IsFaulted)
                        _ = task.Exception;
                    tail.TrySetResult();
                }, TaskScheduler.Default);
                return;
            }

            try
            {
                caller.TrySetResult(Map(await send.ConfigureAwait(false)));
            }
            catch (Exception ex)
            {
                caller.TrySetResult(RedisReply.ErrorOf(ex.Message));
            }
            finally
            {
                tail.TrySetResult();
            }
        }
    }

    internal async Task<CommandMetadata?> DescribeAsync(int database, string commandName, CancellationToken cancellationToken)
    {
        var reply = await ExecuteAsync(
            database,
            [RedisArg.FromText("COMMAND"), RedisArg.FromText("INFO"), RedisArg.FromText(commandName)],
            cancellationToken).ConfigureAwait(false);
        if (reply.IsError || reply.IsNull)
            return null;
        if (reply.Kind != RedisReplyKind.Array || reply.Items == null || reply.Items.Count == 0)
            return CommandMetadata.UnknownCommand(commandName);
        var spec = reply.Items[0];
        if (spec.IsNull)
            return CommandMetadata.UnknownCommand(commandName);
        if (spec.Kind != RedisReplyKind.Array || spec.Items == null || spec.Items.Count < 3)
            return CommandMetadata.UnknownCommand(commandName);
        var flags = new List<string>();
        var flagReply = spec.Items[2].Items;
        if (flagReply != null)
        {
            foreach (var flag in flagReply)
            {
                if (!string.IsNullOrWhiteSpace(flag.Text))
                    flags.Add(flag.Text);
            }
        }

        return new CommandMetadata { Name = commandName, Flags = flags };
    }

    private static RedisReply Map(RedisResult result)
    {
        var type = result.Resp3Type == ResultType.None ? result.Resp2Type : result.Resp3Type;
        if (result.IsNull || type == ResultType.Null)
            return RedisReply.Null();
        if (type is ResultType.Error or ResultType.BlobError)
            return RedisReply.ErrorOf(result.ToString() ?? "ERR");
        if (type is ResultType.Array or ResultType.Set or ResultType.Map or ResultType.Push || result.Resp2Type == ResultType.Array)
        {
            var items = new RedisReply[Math.Max(0, result.Length)];
            for (var i = 0; i < items.Length; i++)
                items[i] = Map(result[i]);
            return RedisReply.Array(items);
        }

        if (type == ResultType.Integer)
        {
            try
            {
                var signed = (long)result;
                return RedisReply.IntegerOf(signed);
            }
            catch (Exception)
            {
                var unsigned = (ulong)result;
                return RedisReply.Unsigned(unsigned);
            }
        }

        if (type == ResultType.BigInteger)
        {
            var text = result.ToString() ?? "0";
            return RedisReply.Bulk(Encoding.ASCII.GetBytes(text));
        }

        try
        {
            var bytes = (byte[]?)result;
            return bytes == null ? RedisReply.Null() : RedisReply.Bulk(bytes);
        }
        catch (InvalidCastException)
        {
            return RedisReply.Bulk(Encoding.UTF8.GetBytes(result.ToString() ?? ""));
        }
    }

    private static bool AcceptAnyCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) => true;
}

public sealed class RedisSession : IRedisSession
{
    private readonly StackExchangeRedisClient _client;

    internal RedisSession(StackExchangeRedisClient client, int database)
    {
        _client = client;
        Database = database;
    }

    public int Database { get; }

    public Task<RedisReply> ExecuteAsync(IReadOnlyList<RedisArg> arguments, CancellationToken cancellationToken) =>
        _client.ExecuteAsync(Database, arguments, cancellationToken);

    public Task<CommandMetadata?> DescribeCommandAsync(string commandName, CancellationToken cancellationToken) =>
        _client.DescribeAsync(Database, commandName, cancellationToken);
}

public sealed class TrackedSession : IRedisSession
{
    private readonly StackExchangeRedisClient _client;
    private int _database;

    public TrackedSession(StackExchangeRedisClient client, int database)
    {
        _client = client;
        _database = database;
    }

    public int Database => _database;

    public void SetDatabase(int database) => _database = database;

    public async Task<RedisReply> ExecuteAsync(IReadOnlyList<RedisArg> arguments, CancellationToken cancellationToken)
    {
        var reply = await _client.ExecuteAsync(_database, arguments, cancellationToken).ConfigureAwait(false);
        if (!reply.IsError
            && arguments.Count >= 2
            && arguments[0].IsUtf8
            && arguments[0].Utf8.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(arguments[1].Utf8, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var selected)
            && selected >= 0)
        {
            _database = selected;
        }

        return reply;
    }

    public Task<CommandMetadata?> DescribeCommandAsync(string commandName, CancellationToken cancellationToken) =>
        _client.DescribeAsync(_database, commandName, cancellationToken);
}
