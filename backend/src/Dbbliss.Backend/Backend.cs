using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dbbliss.Backend.Catalog;
using Dbbliss.Backend.Engines;
using Dbbliss.Backend.Rpc;
using Dbbliss.Backend.Scripts;

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

    /// <summary>A catalog operation (opening its session included) gets this long.</summary>
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(60);

    private readonly Output _output;
    private readonly Dictionary<string, IEngine> _engines;
    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly ConcurrentDictionary<string, QueryRun> _queries = new();
    private readonly TaskCompletionSource _shutdownDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _shuttingDown;
    private int _nextConnection;

    public Backend(Output output, Instances instances)
        : this(output, [new SqlServerEngine(), new PostgresEngine(instances), new Db2iEngine()])
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

            var reply = await DispatchAsync(method, @params);
            try
            {
                if (id is not null)
                {
                    await _output.WriteAsync(new JsonObject { ["id"] = id, ["result"] = reply.Result }, reply.Ordered);
                }
            }
            finally
            {
                // No response to write, or the write failed: run it anyway. Ordered actions are idempotent.
                reply.Ordered?.Invoke();
            }
            reply.After?.Invoke();
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

    /// <param name="Ordered">Runs once the response's place in the output is fixed, before it is written
    /// (see <see cref="Output.WriteAsync(JsonObject, Action?, CancellationToken)"/>). Must be idempotent.</param>
    /// <param name="After">Runs after the response is written.</param>
    private readonly record struct Reply(JsonNode? Result, Action? Ordered = null, Action? After = null);

    private async Task<Reply> DispatchAsync(string method, JsonObject p)
    {
        if (Volatile.Read(ref _shuttingDown) != 0 && method != "shutdown")
        {
            throw new RpcException(RpcErrors.ShuttingDown, "The backend is shutting down.");
        }
        return method switch
        {
            "initialize" => new Reply(Initialize()),
            "connect" => new Reply(await ConnectAsync(p)),
            "disconnect" => new Reply(await DisconnectAsync(p)),
            "script/split" => new Reply(SplitScript(p)),
            "execute" => Execute(p),
            "cancel" => new Reply(Cancel(p)),
            "fetch" => new Reply(Fetch(p)),
            "catalog/children" => new Reply(await CatalogAsync(p, (cat, session, ct) => ChildrenAsync(cat, session, p, ct))),
            "catalog/describe" => new Reply(await CatalogAsync(p, async (cat, session, ct) => (await cat.DescribeAsync(session, ObjectOf(p), ct)).ToJson())),
            "catalog/script" => new Reply(await CatalogAsync(p, async (cat, session, ct) =>
                new JsonObject { ["text"] = await cat.ScriptAsync(session, ObjectOf(p), ct) })),
            "transaction/begin" => await TransactionAsync(p, (s, ct) => s.BeginTransactionAsync(ct)),
            "transaction/commit" => await TransactionAsync(p, (s, ct) => s.CommitAsync(ct)),
            "transaction/rollback" => await TransactionAsync(p, (s, ct) => s.RollbackAsync(ct)),
            "transaction/status" => await TransactionStatusAsync(p),
            "shutdown" => new Reply(new JsonObject(), After: () => _ = ShutdownAsync("shutdown requested")),
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
        var connection = new Connection(id, engine, _output, spec);
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
            // The server decides, not local bookkeeping: a BEGIN typed as SQL counts too. A state the
            // engine cannot ask about ("unknown") counts as open.
            var transaction = await ProbeTransactionAsync(connection);
            if (transaction != "none" && !rollback)
            {
                // Never decide commit-or-rollback for the user: the caller must say so.
                throw new RpcException(RpcErrors.TransactionOpen,
                    $"The connection has an open transaction ({transaction}). Commit or roll back first, or disconnect with rollback = true.");
            }
            _connections.TryRemove(connection.Id, out _);
            await connection.CloseCatalogAsync();
            await connection.Session.DisposeAsync();
            Log.Info($"connection {connection.Id} closed{(transaction != "none" ? $" (transaction {transaction} rolled back)" : "")}");
            return new JsonObject { ["rolled_back"] = transaction != "none" };
        }
    }

    /// <summary>
    /// Splits a script into the units the server is sent one at a time. Needs no connection, only the
    /// engine, whose dialect decides where units end. Lines and columns are 0-based.
    /// </summary>
    private JsonObject SplitScript(JsonObject p)
    {
        var engineName = Str(p, "engine");
        if (!_engines.TryGetValue(engineName, out var engine))
        {
            throw new RpcException(RpcErrors.InvalidParams, $"Unknown engine {engineName}. Known: {string.Join(", ", _engines.Keys)}.");
        }
        var text = p["text"]?.GetValue<string>() ?? throw new RpcException(RpcErrors.InvalidParams, "Missing parameter text.");
        var statements = new JsonArray();
        foreach (var unit in ScriptSplitter.Split(text, engine.Dialect))
        {
            statements.Add(new JsonObject
            {
                ["text"] = unit.Text,
                ["start"] = new JsonObject { ["line"] = unit.Start.Line, ["col"] = unit.Start.Column },
                ["end"] = new JsonObject { ["line"] = unit.End.Line, ["col"] = unit.End.Column },
                ["repeat"] = unit.Repeat,
                ["kind"] = StatementClassifier.Classify(unit.Text, engine.Dialect) == StatementKind.Read ? "read" : "write",
            });
        }
        return new JsonObject { ["statements"] = statements };
    }

    private Reply Execute(JsonObject p)
    {
        var connection = GetConnection(p);
        var sql = Str(p, "sql");
        var lineOffset = p["line_offset"]?.GetValue<int>() ?? 0;
        var window = p["window"]?.GetValue<long>();
        if (window is < 1) throw new RpcException(RpcErrors.InvalidParams, "window must be at least 1.");
        var exportPath = p["export"]?["path"]?.GetValue<string>();
        if (exportPath is not null && window is not null)
        {
            throw new RpcException(RpcErrors.InvalidParams, "An export has no window: it writes the whole result.");
        }
        var queryId = p["query_id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
        var lease = connection.TryAcquire()
            ?? throw new RpcException(RpcErrors.ConnectionBusy, $"Connection {connection.Id} is already running a query.");
        CsvExport? export = null;
        if (exportPath is not null)
        {
            try
            {
                export = new CsvExport(exportPath, p["export"]?["overwrite"]?.GetValue<bool>() ?? false);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        var run = new QueryRun(queryId, connection, new QueryControl(queryId), lineOffset, window, export);
        if (!_queries.TryAdd(queryId, run))
        {
            lease.Dispose();
            if (export is not null) _ = export.DisposeAsync().AsTask();
            throw new RpcException(RpcErrors.InvalidParams, $"Query id {queryId} is already in use.");
        }
        // The query starts after the response is written, so the client always sees the
        // query id before any notification about it.
        return new Reply(new JsonObject { ["query_id"] = queryId }, After: () => run.Task = RunQueryAsync(run, sql, lease));
    }

    private async Task RunQueryAsync(QueryRun run, string sql, IDisposable lease)
    {
        await Task.Yield();
        var connection = run.Connection;
        var control = run.Control;
        var export = run.Export;
        var streamer = export is null ? new ResultStreamer(_output, run.QueryId, control.Token, run.Window) : null;
        IResultSink sink = export is not null ? export : streamer!;
        connection.CurrentStreamer = streamer;
        string status;
        ErrorInfo? error = null;
        long? rowsAffected = null;
        try
        {
            control.Token.ThrowIfCancellationRequested();
            var summary = await connection.Session.ExecuteAsync(sql, sink, control);
            status = "completed";
            rowsAffected = summary.RowsAffected;
        }
        catch (Exception ex)
        {
            // A cancel we asked for is "cancelled" whatever the driver threw; anything else is an error.
            status = control.CancelRequested ? "cancelled" : "error";
            if (!(control.CancelRequested && ex is OperationCanceledException))
            {
                error = LocateError(connection.Engine.DescribeError(ex), sql);
            }
        }
        control.MarkFinished();
        var elapsed = control.Elapsed.ElapsedMilliseconds;
        try
        {
            if (streamer is not null) await streamer.DisposeAsync();
            if (export is not null)
            {
                export.Complete = status == "completed";
                await export.DisposeAsync();
            }
        }
        catch (IOException ex)
        {
            if (export is not null && status == "completed")
            {
                // The file is short: the export did not complete, whatever the query did.
                export.Complete = false;
                status = "error";
                error = new ErrorInfo($"Writing {export.Path} failed: {ex.Message}");
            }
        }
        connection.CurrentStreamer = null;

        var transaction = await ProbeTransactionAsync(connection);

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
        if (error is not null)
        {
            var json = ToJson(error);
            // Where the error is in the buffer the statement came from: the statement's first line plus
            // the engine's line, both counted from 1 here.
            if (error.Line is { } line) json["buffer_line"] = run.LineOffset + line;
            done["error"] = json;
        }
        if (streamer is { TruncatedRows: > 0 })
        {
            // Reported loss only: rows past the post-cancel overflow cap.
            done["truncated_rows"] = streamer.TruncatedRows;
        }
        if (export is not null)
        {
            done["export"] = new JsonObject
            {
                ["path"] = export.Path,
                ["rows"] = export.Rows,
                ["complete"] = export.Complete,
                ["ignored_result_sets"] = export.IgnoredResultSets,
            };
        }
        Log.Info($"query {run.QueryId} {status} after {elapsed} ms");
        var released = false;
        void Release()
        {
            if (released) return;
            released = true;
            lease.Dispose();
            _queries.TryRemove(run.QueryId, out _);
            control.Dispose();
        }
        try
        {
            // The connection is released once query/done holds its place in the output. Released
            // earlier, an operation that takes the connection next could report first, and the client's
            // last transaction report would be this stale one (spec/quint/client.qnt, LeaseAfterWrite).
            // Released after the write, a client sending its next request on reading query/done could
            // find the connection still busy.
            await _output.NotifyAsync("query/done", done, Release);
        }
        catch (IOException)
        {
        }
        Release();
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

    /// <summary>Allows a paused (or about to be paused) query to send more rows.</summary>
    private JsonObject Fetch(JsonObject p)
    {
        var queryId = Str(p, "query_id");
        var rows = p["rows"]?.GetValue<long>() ?? throw new RpcException(RpcErrors.InvalidParams, "Missing parameter rows.");
        if (rows < 1) throw new RpcException(RpcErrors.InvalidParams, "rows must be at least 1.");
        if (!_queries.TryGetValue(queryId, out var run) || run.Connection.CurrentStreamer is not { } streamer)
        {
            return new JsonObject { ["query_id"] = queryId, ["state"] = "not_running" };
        }
        return new JsonObject { ["query_id"] = queryId, ["state"] = "granted", ["granted"] = streamer.Grant(rows) };
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

    /// <summary>
    /// Runs a catalog operation on the connection's catalog session: a second session, opened on first
    /// use, so an aborted transaction or a running query on the user's session does not stop it. One
    /// operation at a time. A driver error drops the session (it reopens next time).
    /// </summary>
    private async Task<JsonObject> CatalogAsync(JsonObject p, Func<ICatalog, IEngineSession, CancellationToken, Task<JsonObject>> work)
    {
        var connection = GetConnection(p);
        var catalog = connection.Engine.Catalog
            ?? throw new RpcException(RpcErrors.InvalidParams, $"{connection.Engine.Name} has no catalog support yet.");
        using var timeout = new CancellationTokenSource(CatalogTimeout);
        try
        {
            await connection.CatalogLock.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new RpcException(RpcErrors.Database, $"Another catalog request on {connection.Id} did not finish within {CatalogTimeout.TotalSeconds:0} s.");
        }
        try
        {
            if (connection.CatalogSession is null)
            {
                IEngineSession session;
                try
                {
                    session = await connection.Engine.OpenAsync(connection.Spec, new DiscardMessages(), timeout.Token);
                    await catalog.PrepareAsync(session, timeout.Token);
                }
                catch (Exception ex) when (ex is not (RpcException or OperationCanceledException))
                {
                    throw DatabaseError(connection.Engine, ex);
                }
                connection.CatalogSession = session;
                Log.Info($"connection {connection.Id}: catalog session open ({session.ServerSessionId})");
            }
            try
            {
                var result = await work(catalog, connection.CatalogSession, timeout.Token);
                // Which server session answered: a tool that watches the server can tell it from the user's.
                result["catalog_session"] = connection.CatalogSession.ServerSessionId;
                return result;
            }
            catch (CatalogException ex)
            {
                throw new RpcException(RpcErrors.Catalog, ex.Message);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                await connection.CloseCatalogLockedAsync();
                throw new RpcException(RpcErrors.Database, $"The catalog query did not finish within {CatalogTimeout.TotalSeconds:0} s.");
            }
            catch (Exception ex) when (ex is not RpcException)
            {
                await connection.CloseCatalogLockedAsync();
                throw DatabaseError(connection.Engine, ex);
            }
        }
        finally
        {
            connection.CatalogLock.Release();
        }
    }

    private static async Task<JsonObject> ChildrenAsync(ICatalog catalog, IEngineSession session, JsonObject p, CancellationToken ct)
    {
        var path = new CatalogPath(OptStr(p, "database"), OptStr(p, "schema"), OptStr(p, "folder"));
        var nodes = await catalog.ChildrenAsync(session, path, ct);
        return new JsonObject { ["nodes"] = new JsonArray(nodes.Select(n => (JsonNode)n.ToJson()).ToArray()) };
    }

    private static ObjectRequest ObjectOf(JsonObject p)
    {
        var typed = OptStr(p, "name");
        var obj = OptStr(p, "object");
        if (typed is null && obj is null) throw new RpcException(RpcErrors.InvalidParams, "Missing parameter name or object.");
        return new ObjectRequest(typed, OptStr(p, "database"), OptStr(p, "schema"), obj, OptStr(p, "kind"), OptStr(p, "identity"));
    }

    private static string? OptStr(JsonObject p, string name) => p[name]?.GetValue<string>() is { Length: > 0 } s ? s : null;

    private sealed class DiscardMessages : IMessageSink
    {
        public void Message(string severity, string text, int? number = null, int? line = null)
        {
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

    /// <summary>
    /// Runs a transaction call under the connection's lease and keeps the lease until the response holds
    /// its place in the output, for the same reason as query/done in <see cref="RunQueryAsync"/>.
    /// </summary>
    private static async Task<Reply> UnderLeaseAsync(Connection connection, Func<Task<JsonObject>> call)
    {
        var lease = connection.TryAcquire()
            ?? throw new RpcException(RpcErrors.ConnectionBusy, $"Connection {connection.Id} is running a query.");
        try
        {
            return new Reply(await call(), Ordered: lease.Dispose);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private Task<Reply> TransactionAsync(JsonObject p, Func<IEngineSession, CancellationToken, Task> action)
    {
        var connection = GetConnection(p);
        return UnderLeaseAsync(connection, () => TransactionAsync(connection, action));
    }

    private Task<Reply> TransactionStatusAsync(JsonObject p)
    {
        var connection = GetConnection(p);
        return UnderLeaseAsync(connection, async () => new JsonObject { ["transaction"] = await ProbeTransactionAsync(connection) });
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
                var close = Task.WhenAll(connection.CloseCatalogAsync(), connection.Session.DisposeAsync().AsTask());
                if (await Task.WhenAny(close, Task.Delay(TimeSpan.FromSeconds(5))) != close)
                {
                    Log.Warn($"connection {connection.Id}: close timed out");
                }
                else
                {
                    Log.Info($"connection {connection.Id} closed (any open transaction rolled back)");
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
        ["position"] = e.Position,
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

    /// <summary>
    /// Gives an error a line within the statement sent. SQL Server reports one; PostgreSQL reports a
    /// character offset, which is converted here (an offset counts characters, not UTF-16 units).
    /// </summary>
    private static ErrorInfo LocateError(ErrorInfo error, string sql)
    {
        if (error.Line is not null || error.Position is not { } position) return error;
        var line = 1;
        var characters = 0;
        for (var i = 0; i < sql.Length && characters < position - 1; i++)
        {
            if (sql[i] == '\n') line++;
            if (!char.IsLowSurrogate(sql[i])) characters++;
        }
        return error with { Line = line };
    }

    private sealed class QueryRun(string queryId, Connection connection, QueryControl control, int lineOffset, long? window, CsvExport? export)
    {
        public int LineOffset { get; } = lineOffset;
        public long? Window { get; } = window;
        public CsvExport? Export { get; } = export;
        public string QueryId { get; } = queryId;
        public Connection Connection { get; } = connection;
        public QueryControl Control { get; } = control;
        public Task? Task { get; set; }
    }

    /// <summary>One open connection. At most one operation (query or transaction call) at a time.</summary>
    private sealed class Connection(string id, IEngine engine, Output output, ConnectionSpec spec) : IMessageSink
    {
        private readonly SemaphoreSlim _busy = new(1, 1);

        public ConnectionSpec Spec { get; } = spec;

        /// <summary>The catalog session (see CatalogAsync); guarded by <see cref="CatalogLock"/>.</summary>
        public IEngineSession? CatalogSession { get; set; }
        public SemaphoreSlim CatalogLock { get; } = new(1, 1);

        public async Task CloseCatalogAsync()
        {
            await CatalogLock.WaitAsync();
            try
            {
                await CloseCatalogLockedAsync();
            }
            finally
            {
                CatalogLock.Release();
            }
        }

        /// <summary>Disposes the catalog session; the caller holds <see cref="CatalogLock"/>.</summary>
        public async Task CloseCatalogLockedAsync()
        {
            var session = CatalogSession;
            CatalogSession = null;
            if (session is null) return;
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Warn($"connection {Id}: closing the catalog session failed: {ex.Message}");
            }
        }

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
