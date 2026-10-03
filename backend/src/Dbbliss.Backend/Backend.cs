using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dbbliss.Backend.Engines;
using Dbbliss.Backend.Rpc;

namespace Dbbliss.Backend;

/// <summary>
/// The backend process: reads JSON-RPC requests from stdin, tracks connections and running
/// queries (query id → command), and guarantees that every query ends with exactly one
/// query/done notification.
/// </summary>
public sealed class Backend
{
    public const int ProtocolVersion = 1;

    /// <summary>
    /// After a cancel, the protocol cancel is re-sent on this schedule (then every second) until the
    /// query ends. A cancel that lands before the statement is on the wire is a no-op in SqlClient;
    /// the cancel_immediately scenario hits that race.
    /// </summary>
    private static readonly TimeSpan[] CancelRefireDelays =
        [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)];

    /// <summary>A cancel the server has not acknowledged after this long is reported to the user.</summary>
    private static readonly TimeSpan CancelWarnAfter = TimeSpan.FromSeconds(10);

    /// <summary>How long shutdown waits for cancelled queries before closing their connections anyway.</summary>
    private static readonly TimeSpan ShutdownQueryWait = TimeSpan.FromSeconds(5);

    private readonly Output _output;
    private readonly Dictionary<string, IEngine> _engines;
    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly ConcurrentDictionary<string, QueryRun> _queries = new();
    private readonly TaskCompletionSource _shutdownDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _shuttingDown;
    private int _nextConnection;

    public Backend(Output output)
        : this(output, [new SqlServerEngine(), new PostgresEngine(), new Db2iEngine()])
    {
    }

    /// <summary>Tests pass fake engines here.</summary>
    public Backend(Output output, IEnumerable<IEngine> engines)
    {
        _output = output;
        _engines = engines.ToDictionary(e => e.Name);
        _output.Broken += () => _ = ShutdownAsync("stdout closed");
    }

    public Task Completion => _shutdownDone.Task;

    /// <summary>Reads requests until stdin closes, then shuts down (Neovim exited or was killed).</summary>
    public async Task RunAsync(Stream stdin)
    {
        using var reader = new StreamReader(stdin, new System.Text.UTF8Encoding(false));
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync();
            }
            catch (IOException ex)
            {
                Log.Warn($"stdin read failed: {ex.Message}");
                line = null;
            }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            _ = HandleLineAsync(line);
        }
        await ShutdownAsync("stdin closed");
    }

    private async Task HandleLineAsync(string line)
    {
        JsonNode? id = null;
        try
        {
            JsonObject request;
            try
            {
                request = JsonNode.Parse(line) as JsonObject ?? throw new RpcException(RpcErrors.InvalidRequest, "Request must be a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new RpcException(RpcErrors.ParseError, ex.Message);
            }
            id = request["id"]?.DeepClone();
            var method = request["method"]?.GetValue<string>() ?? throw new RpcException(RpcErrors.InvalidRequest, "Missing method.");
            var @params = request["params"] as JsonObject ?? [];

            var (result, after) = await DispatchAsync(method, @params);
            if (id is not null)
            {
                await _output.WriteAsync(new JsonObject { ["id"] = id, ["result"] = result });
            }
            after?.Invoke();
        }
        catch (Exception ex) when (ex is not IOException)
        {
            var (code, message, data) = ex switch
            {
                RpcException rpc => (rpc.Code, rpc.Message, rpc.ErrorData),
                _ => (RpcErrors.Internal, $"{ex.GetType().Name}: {ex.Message}", null),
            };
            if (ex is not RpcException) Log.Error(ex.ToString());
            try
            {
                await _output.WriteAsync(new JsonObject
                {
                    ["id"] = id,
                    ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["data"] = data },
                });
            }
            catch (IOException)
            {
                // stdout is gone; shutdown is already under way.
            }
        }
        catch (IOException)
        {
            // stdout is gone; shutdown is already under way.
        }
    }

    private async Task<(JsonNode? Result, Action? After)> DispatchAsync(string method, JsonObject p)
    {
        if (Volatile.Read(ref _shuttingDown) != 0 && method != "shutdown")
        {
            throw new RpcException(RpcErrors.ShuttingDown, "The backend is shutting down.");
        }
        return method switch
        {
            "initialize" => (Initialize(), null),
            "connect" => (await ConnectAsync(p), null),
            "disconnect" => (await DisconnectAsync(p), null),
            "execute" => Execute(p),
            "cancel" => (Cancel(p), null),
            "transaction/begin" => (await TransactionAsync(p, (s, ct) => s.BeginTransactionAsync(ct)), null),
            "transaction/commit" => (await TransactionAsync(p, (s, ct) => s.CommitAsync(ct)), null),
            "transaction/rollback" => (await TransactionAsync(p, (s, ct) => s.RollbackAsync(ct)), null),
            "transaction/status" => (await TransactionStatusAsync(p), null),
            "shutdown" => (new JsonObject(), () => _ = ShutdownAsync("shutdown requested")),
            _ => throw new RpcException(RpcErrors.MethodNotFound, $"Unknown method {method}."),
        };
    }

    private JsonObject Initialize() => new()
    {
        ["protocol"] = ProtocolVersion,
        ["version"] = typeof(Backend).Assembly.GetName().Version?.ToString(),
        ["pid"] = Environment.ProcessId,
        ["engines"] = new JsonArray(_engines.Keys.Select(k => (JsonNode)k).ToArray()),
    };

    private async Task<JsonObject> ConnectAsync(JsonObject p)
    {
        var engineName = Str(p, "engine");
        if (!_engines.TryGetValue(engineName, out var engine))
        {
            throw new RpcException(RpcErrors.InvalidParams, $"Unknown engine {engineName}. Known: {string.Join(", ", _engines.Keys)}.");
        }
        var spec = new ConnectionSpec(Str(p, "connection_string"), Credentials.Resolve(p["password"]), p["options"] as JsonObject);
        var id = "c" + Interlocked.Increment(ref _nextConnection);
        var connection = new Connection(id, engine, _output);
        try
        {
            connection.Session = await engine.OpenAsync(spec, connection, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw DatabaseError(engine, ex);
        }
        _connections[id] = connection;
        Log.Info($"connection {id} open ({engine.Name}, server session {connection.Session.ServerSessionId})");
        return new JsonObject
        {
            ["connection_id"] = id,
            ["engine"] = engine.Name,
            ["server_session_id"] = connection.Session.ServerSessionId,
        };
    }

    private async Task<JsonObject> DisconnectAsync(JsonObject p)
    {
        var connection = GetConnection(p);
        var rollback = p["rollback"]?.GetValue<bool>() ?? false;
        await using (await connection.AcquireAsync())
        {
            if (connection.Session.InTransaction && !rollback)
            {
                // Never decide commit-or-rollback for the user: the caller must say so.
                throw new RpcException(RpcErrors.TransactionOpen,
                    "The connection has an open transaction. Commit or roll back first, or disconnect with rollback = true.");
            }
            _connections.TryRemove(connection.Id, out _);
            var hadTransaction = connection.Session.InTransaction;
            await connection.Session.DisposeAsync();
            Log.Info($"connection {connection.Id} closed{(hadTransaction ? " (open transaction rolled back)" : "")}");
            return new JsonObject { ["rolled_back"] = hadTransaction };
        }
    }

    private (JsonNode, Action) Execute(JsonObject p)
    {
        var connection = GetConnection(p);
        var sql = Str(p, "sql");
        var queryId = p["query_id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
        var lease = connection.TryAcquire()
            ?? throw new RpcException(RpcErrors.ConnectionBusy, $"Connection {connection.Id} is already running a query.");
        var run = new QueryRun(queryId, connection, new QueryControl(queryId));
        if (!_queries.TryAdd(queryId, run))
        {
            lease.Dispose();
            throw new RpcException(RpcErrors.InvalidParams, $"Query id {queryId} is already in use.");
        }
        // The query starts after the response is written, so the client always sees the
        // query id before any notification about it.
        return (new JsonObject { ["query_id"] = queryId }, () => run.Task = RunQueryAsync(run, sql, lease));
    }

    private async Task RunQueryAsync(QueryRun run, string sql, IDisposable lease)
    {
        await Task.Yield();
        var connection = run.Connection;
        var control = run.Control;
        var streamer = new ResultStreamer(_output, run.QueryId, control.Token);
        connection.CurrentStreamer = streamer;
        string status;
        ErrorInfo? error = null;
        long? rowsAffected = null;
        try
        {
            control.Token.ThrowIfCancellationRequested();
            var summary = await connection.Session.ExecuteAsync(sql, streamer, control);
            status = "completed";
            rowsAffected = summary.RowsAffected;
        }
        catch (Exception ex)
        {
            // A cancel we asked for is "cancelled" whatever the driver threw; anything else is an error.
            status = control.CancelRequested ? "cancelled" : "error";
            if (!(control.CancelRequested && ex is OperationCanceledException))
            {
                error = connection.Engine.DescribeError(ex);
            }
        }
        control.MarkFinished();
        var elapsed = control.Elapsed.ElapsedMilliseconds;
        try
        {
            await streamer.DisposeAsync();
        }
        catch (IOException)
        {
        }
        connection.CurrentStreamer = null;

        var transaction = await ProbeTransactionAsync(connection);
        lease.Dispose();
        _queries.TryRemove(run.QueryId, out _);
        control.Dispose();

        var done = new JsonObject
        {
            ["query_id"] = run.QueryId,
            ["status"] = status,
            ["elapsed_ms"] = elapsed,
            ["rows_affected"] = rowsAffected is >= 0 ? rowsAffected : null,
            ["transaction"] = transaction,
        };
        if (control.CancelRequestedAtMs is { } cancelAt)
        {
            done["cancel"] = new JsonObject { ["requested_at_ms"] = cancelAt, ["ack_ms"] = elapsed - cancelAt };
        }
        if (error is not null) done["error"] = ToJson(error);
        if (streamer.TruncatedRows > 0)
        {
            // Reported loss only: rows past the post-cancel overflow cap.
            done["truncated_rows"] = streamer.TruncatedRows;
        }
        Log.Info($"query {run.QueryId} {status} after {elapsed} ms");
        try
        {
            await _output.NotifyAsync("query/done", done);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Asks the server for the transaction state after every statement, so a server-side
    /// rollback (XACT_ABORT, aborted PG transaction) is never hidden.</summary>
    private static async Task<string> ProbeTransactionAsync(Connection connection)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var state = await connection.Session.GetTransactionStateAsync(timeout.Token);
            return StateName(state);
        }
        catch (Exception ex)
        {
            Log.Warn($"connection {connection.Id}: transaction probe failed: {ex.Message}");
            return "unknown";
        }
    }

    private JsonObject Cancel(JsonObject p)
    {
        var queryId = Str(p, "query_id");
        if (!_queries.TryGetValue(queryId, out var run) || !run.Control.Cancel())
        {
            return new JsonObject { ["query_id"] = queryId, ["state"] = "not_running" };
        }
        Log.Info($"query {queryId}: cancel sent");
        _ = WatchCancelAsync(run);
        return new JsonObject { ["query_id"] = queryId, ["state"] = "cancel_sent" };
    }

    private async Task WatchCancelAsync(QueryRun run)
    {
        var started = DateTime.UtcNow;
        var warned = false;
        for (var attempt = 0; !run.Control.Finished; attempt++)
        {
            await Task.Delay(attempt < CancelRefireDelays.Length ? CancelRefireDelays[attempt] : TimeSpan.FromSeconds(1));
            if (run.Control.Finished) break;
            run.Control.RefireProtocolCancel();
            if (!warned && DateTime.UtcNow - started >= CancelWarnAfter)
            {
                warned = true;
                var text = $"The server has not acknowledged the cancel after {CancelWarnAfter.TotalSeconds:0} s; still waiting.";
                Log.Warn($"query {run.QueryId}: {text}");
                try
                {
                    await _output.NotifyAsync("query/message", new JsonObject
                    {
                        ["query_id"] = run.QueryId,
                        ["severity"] = "warning",
                        ["text"] = text,
                    });
                }
                catch (IOException)
                {
                    return;
                }
            }
        }
    }

    private static async Task<JsonObject> TransactionAsync(Connection connection, Func<IEngineSession, CancellationToken, Task> action)
    {
        try
        {
            await action(connection.Session, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(RpcErrors.InvalidParams, ex.Message);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw DatabaseError(connection.Engine, ex);
        }
        return new JsonObject { ["transaction"] = await ProbeTransactionAsync(connection) };
    }

    private async Task<JsonObject> TransactionAsync(JsonObject p, Func<IEngineSession, CancellationToken, Task> action)
    {
        var connection = GetConnection(p);
        using var lease = connection.TryAcquire()
            ?? throw new RpcException(RpcErrors.ConnectionBusy, $"Connection {connection.Id} is running a query.");
        return await TransactionAsync(connection, action);
    }

    private async Task<JsonObject> TransactionStatusAsync(JsonObject p)
    {
        var connection = GetConnection(p);
        using var lease = connection.TryAcquire()
            ?? throw new RpcException(RpcErrors.ConnectionBusy, $"Connection {connection.Id} is running a query.");
        return new JsonObject
        {
            ["transaction"] = await ProbeTransactionAsync(connection),
            ["local_transaction"] = connection.Session.InTransaction,
        };
    }

    /// <summary>
    /// Cancels every running query at the protocol level, waits briefly for them to end, then closes
    /// every connection (which rolls back open transactions). Runs once.
    /// </summary>
    public async Task ShutdownAsync(string reason)
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0) return;
        Log.Info($"shutting down: {reason}");
        try
        {
            var running = _queries.Values.ToArray();
            foreach (var run in running)
            {
                if (run.Control.Cancel()) Log.Info($"query {run.QueryId}: cancel sent (shutdown)");
            }
            var tasks = running.Select(r => r.Task).OfType<Task>().ToArray();
            var all = Task.WhenAll(tasks);
            if (await Task.WhenAny(all, Task.Delay(ShutdownQueryWait)) != all)
            {
                Log.Warn($"{tasks.Count(t => !t.IsCompleted)} queries did not end within {ShutdownQueryWait.TotalSeconds:0} s; closing their connections.");
            }
            foreach (var connection in _connections.Values)
            {
                var inTx = connection.Session.InTransaction;
                var close = connection.Session.DisposeAsync().AsTask();
                if (await Task.WhenAny(close, Task.Delay(TimeSpan.FromSeconds(5))) != close)
                {
                    Log.Warn($"connection {connection.Id}: close timed out");
                }
                else
                {
                    Log.Info($"connection {connection.Id} closed{(inTx ? " (open transaction rolled back)" : "")}");
                }
            }
            _connections.Clear();
        }
        catch (Exception ex)
        {
            Log.Error($"shutdown: {ex}");
        }
        finally
        {
            _shutdownDone.TrySetResult();
        }
    }

    private Connection GetConnection(JsonObject p)
    {
        var id = Str(p, "connection_id");
        return _connections.TryGetValue(id, out var c)
            ? c
            : throw new RpcException(RpcErrors.UnknownConnection, $"Unknown connection {id}.");
    }

    private static RpcException DatabaseError(IEngine engine, Exception ex) =>
        new(RpcErrors.Database, engine.DescribeError(ex).Message, ToJson(engine.DescribeError(ex)));

    private static JsonObject ToJson(ErrorInfo e) => new()
    {
        ["message"] = e.Message,
        ["code"] = e.Code,
        ["sqlstate"] = e.SqlState,
        ["line"] = e.Line,
        ["severity"] = e.Severity,
    };

    private static string StateName(TransactionState s) => s switch
    {
        TransactionState.None => "none",
        TransactionState.Active => "active",
        TransactionState.Aborted => "aborted",
        _ => "unknown",
    };

    private static string Str(JsonObject p, string name) =>
        p[name]?.GetValue<string>() is { Length: > 0 } s
            ? s
            : throw new RpcException(RpcErrors.InvalidParams, $"Missing parameter {name}.");

    private sealed class QueryRun(string queryId, Connection connection, QueryControl control)
    {
        public string QueryId { get; } = queryId;
        public Connection Connection { get; } = connection;
        public QueryControl Control { get; } = control;
        public Task? Task { get; set; }
    }

    /// <summary>One open connection. At most one operation (query or transaction call) at a time.</summary>
    private sealed class Connection(string id, IEngine engine, Output output) : IMessageSink
    {
        private readonly SemaphoreSlim _busy = new(1, 1);

        public string Id { get; } = id;
        public IEngine Engine { get; } = engine;
        public IEngineSession Session { get; set; } = null!;
        public ResultStreamer? CurrentStreamer { get; set; }

        public IDisposable? TryAcquire() => _busy.Wait(0) ? new Lease(_busy) : null;

        public async Task<IAsyncDisposable> AcquireAsync()
        {
            await _busy.WaitAsync();
            return new Lease(_busy);
        }

        public void Message(string severity, string text, int? number = null, int? line = null)
        {
            if (CurrentStreamer is { } streamer)
            {
                streamer.Message(severity, text, number, line);
                return;
            }
            _ = output.NotifyAsync("connection/message", new JsonObject
            {
                ["connection_id"] = Id,
                ["severity"] = severity,
                ["text"] = text,
                ["number"] = number,
                ["line"] = line,
            }).ContinueWith(t => Log.Warn($"connection/message lost: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable, IAsyncDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
