using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using Dbbliss.Backend.Catalog;
using Dbbliss.Backend.Engines;
using Dbbliss.Backend.Sessions;

namespace Dbbliss.ProtocolTests;

/// <summary>The server's real transaction state, as in spec/quint/client.qnt (srvTx).</summary>
public enum ServerTx { None, Active, Aborted }

/// <summary>
/// The database server: every session it has open, and every session that was closed while a
/// transaction was open (closing a session rolls it back, which nobody may do silently).
/// </summary>
public sealed class FakeServer
{
    private int _nextSession;

    public ConcurrentDictionary<string, FakeSession> Open { get; } = new();
    public ConcurrentQueue<string> ClosedWithTransaction { get; } = new();

    /// <summary>"STUCK": a statement that ignores cancels until <see cref="StuckRelease"/> is set, like a
    /// server that never acknowledges. <see cref="CancelRequests"/> counts the protocol cancels it received.</summary>
    public TaskCompletionSource StuckStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource StuckRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int CancelRequests;

    /// <summary>Failure switches for the code that handles a server that misbehaves.</summary>
    public int? OpensBeforeRefusing { get; set; }
    public bool BeginThrows { get; set; }
    public bool DisposeThrows { get; set; }
    public TaskCompletionSource? DisposeHangs { get; set; }
    public int OpenCount;

    /// <summary>A message every new session sends while it opens, like a server banner.</summary>
    public string? MessageOnOpen { get; set; }
    public int DisposeAttempts;

    /// <summary>Sessions of other users on the server, for the session admin.</summary>
    public ConcurrentDictionary<string, ForeignSession> Foreign { get; } = new();
    public bool CanCancelOthers { get; set; } = true;

    internal FakeSession OpenSession(IMessageSink messages)
    {
        if (OpensBeforeRefusing is { } n && Interlocked.Increment(ref OpenCount) > n) throw new FakeServerException("too many sessions");
        if (MessageOnOpen is { } banner) messages.Message("info", banner, null, null);
        var session = new FakeSession(this, "s" + Interlocked.Increment(ref _nextSession), messages);
        Open[session.ServerSessionId] = session;
        return session;
    }

    internal void Close(FakeSession session)
    {
        Open.TryRemove(session.ServerSessionId, out _);
        if (session.State != ServerTx.None) ClosedWithTransaction.Enqueue(session.ServerSessionId);
    }
}

/// <summary>Another user's session. <see cref="Signals"/> false: the server accepts the call but there is nothing to signal.</summary>
public sealed class ForeignSession(string id, string identity)
{
    public string Id { get; } = id;
    public string Identity { get; } = identity;
    public bool InTransaction { get; set; }
    public bool Signals { get; set; } = true;
    public int Cancelled;
    public bool Terminated;
}

public sealed class FakeEngine(FakeServer server, string name = "fake", bool hasCatalog = true, bool hasSessions = true) : IEngine
{
    public string Name => name;

    public FakeCatalog FakeCatalog { get; } = new();

    public ICatalog? Catalog => hasCatalog ? FakeCatalog : null;

    public FakeSessionAdmin FakeSessions { get; } = new(server);

    public ISessionAdmin? Sessions => hasSessions ? FakeSessions : null;

    public Task<IEngineSession> OpenAsync(ConnectionSpec spec, IMessageSink messages, CancellationToken ct) =>
        spec.ConnectionString == "refuse"
            ? throw new FakeServerException("connection refused")
            : Task.FromResult<IEngineSession>(server.OpenSession(messages));

    public ErrorInfo DescribeError(Exception ex) =>
        ex switch
        {
            FakeServerException { Position: { } position } => new ErrorInfo(ex.Message, Position: position),
            FakeServerException { Line: { } line } => new ErrorInfo(ex.Message, Line: line),
            _ => new ErrorInfo(ex.Message),
        };
}

/// <summary>
/// One session. Statements are the model's: BEGIN, COMMIT, ROLLBACK typed as SQL, FAIL (an error
/// that aborts an open transaction, as on PostgreSQL), anything else succeeds. The API calls and the
/// probe act on the server's state, as the IEngineSession contract asks.
/// </summary>
public sealed class FakeSession(FakeServer server, string id, IMessageSink messages) : IEngineSession
{
    public IMessageSink Messages => messages;

    private readonly Lock _gate = new();

    public string ServerSessionId { get; } = id;

    public ServerTx State { get; private set; }

    private bool _probeFails;
    private bool _probeUnknown;

    public Task<ExecuteSummary> ExecuteAsync(string sql, IResultSink sink, QueryControl control)
    {
        if (sql.StartsWith("ROWS ", StringComparison.Ordinal)) return StreamRowsAsync(int.Parse(sql[5..]), sink);
        if (sql == "STUCK") return StuckAsync(control);
        if (sql.StartsWith("ROWSFAIL ", StringComparison.Ordinal)) return RowsThenFailAsync(int.Parse(sql[9..]), sink);
        if (sql == "POSFAR") throw new FakeServerException("syntax error", 9999);
        if (sql == "LINEFAIL") throw new FakeServerException("syntax error", line: 3);
        if (sql.StartsWith("NOTICE ", StringComparison.Ordinal))
        {
            messages.Message("info", sql[7..], 1, 1);
            return Task.FromResult(new ExecuteSummary(-1));
        }
        if (sql.StartsWith("LATENOTICE ", StringComparison.Ordinal))
        {
            // A message that arrives after the statement has ended, like an asynchronous server notice.
            var text = sql[11..];
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                messages.Message("info", text, null, null);
            });
            return Task.FromResult(new ExecuteSummary(-1));
        }
        if (sql == "PROBEFAIL") _probeFails = true;
        if (sql == "PROBEUNKNOWN") _probeUnknown = true;
        lock (_gate)
        {
            switch (sql.Trim().ToUpperInvariant())
            {
                case "BEGIN":
                    if (State == ServerTx.None) State = ServerTx.Active;
                    break;
                case "COMMIT":
                case "ROLLBACK":
                    // COMMIT of an aborted transaction rolls it back; either way it is over.
                    State = ServerTx.None;
                    break;
                case "FAIL":
                    if (State == ServerTx.Active) State = ServerTx.Aborted;
                    throw new FakeServerException("statement failed");
                default:
                    var at = sql.IndexOf("POSFAIL", StringComparison.Ordinal);
                    if (at >= 0)
                    {
                        // Like PostgreSQL: the error carries a 1-based character offset, not a line.
                        var position = sql[..at].EnumerateRunes().Count() + 1;
                        throw new FakeServerException("syntax error", position);
                    }
                    if (State == ServerTx.Aborted) throw new FakeServerException("current transaction is aborted");
                    break;
            }
        }
        return Task.FromResult(new ExecuteSummary(-1));
    }

    /// <summary>"ROWSFAIL n": n rows, then an error, as a server does when a statement dies half way.</summary>
    private static async Task<ExecuteSummary> RowsThenFailAsync(int n, IResultSink sink)
    {
        await sink.ResultSetAsync(0, [new ColumnInfo("id", "int"), new ColumnInfo("v", "text"), new ColumnInfo("n", "text"), new ColumnInfo("q", "text")]);
        for (var i = 1; i <= n; i++) await sink.RowAsync(0, new JsonArray(i, "v" + i, null, "q\"x"));
        throw new FakeServerException("statement failed after the rows");
    }

    private async Task<ExecuteSummary> StuckAsync(QueryControl control)
    {
        control.SetProtocolCancel(() => Interlocked.Increment(ref server.CancelRequests));
        server.StuckStarted.TrySetResult();
        await server.StuckRelease.Task;
        // A driver reports the cancel once the server finally lets go of the statement.
        if (control.CancelRequested) throw new OperationCanceledException();
        return new ExecuteSummary(-1);
    }

    /// <summary>
    /// "ROWS n": one result set of n rows, read to the end whatever happens (a cancel does not stop a
    /// driver that is draining its socket). Row i is [i, "v<i>", null, "q\"x"].
    /// </summary>
    private static async Task<ExecuteSummary> StreamRowsAsync(int n, IResultSink sink)
    {
        await sink.ResultSetAsync(0, [new ColumnInfo("id", "int"), new ColumnInfo("v", "text"), new ColumnInfo("n", "text"), new ColumnInfo("q", "text")]);
        for (var i = 1; i <= n; i++)
        {
            await sink.RowAsync(0, new JsonArray(i, "v" + i, null, "q\"x"));
        }
        await sink.ResultSetDoneAsync(0, n);
        return new ExecuteSummary(n);
    }

    public Task BeginTransactionAsync(CancellationToken ct)
    {
        if (server.BeginThrows) throw new FakeServerException("begin failed");
        lock (_gate)
        {
            if (State != ServerTx.None) throw new InvalidOperationException("A transaction is already open.");
            State = ServerTx.Active;
        }
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (State == ServerTx.None) throw new InvalidOperationException("No open transaction.");
            if (State == ServerTx.Aborted) throw new InvalidOperationException("The transaction was aborted by the server; only rollback is possible.");
            State = ServerTx.None;
        }
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            State = ServerTx.None;
        }
        return Task.CompletedTask;
    }

    public Task<TransactionState> GetTransactionStateAsync(CancellationToken ct)
    {
        if (_probeFails) throw new FakeServerException("the probe failed");
        if (_probeUnknown) return Task.FromResult(TransactionState.Unknown);
        lock (_gate)
        {
            return Task.FromResult(State switch
            {
                ServerTx.Active => TransactionState.Active,
                ServerTx.Aborted => TransactionState.Aborted,
                _ => TransactionState.None,
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref server.DisposeAttempts);
        if (server.DisposeHangs is { } hang) await hang.Task;
        server.Close(this);
        if (server.DisposeThrows) throw new FakeServerException("the close failed");
    }
}

public sealed class FakeServerException(string message, int? position = null, int? line = null) : Exception(message)
{
    public int? Position { get; } = position;
    public int? Line { get; } = line;
}
