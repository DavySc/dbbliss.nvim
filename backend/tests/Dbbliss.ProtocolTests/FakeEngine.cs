using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using Dbbliss.Backend.Engines;

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

    internal FakeSession OpenSession()
    {
        var session = new FakeSession(this, "s" + Interlocked.Increment(ref _nextSession));
        Open[session.ServerSessionId] = session;
        return session;
    }

    internal void Close(FakeSession session)
    {
        Open.TryRemove(session.ServerSessionId, out _);
        if (session.State != ServerTx.None) ClosedWithTransaction.Enqueue(session.ServerSessionId);
    }
}

public sealed class FakeEngine(FakeServer server) : IEngine
{
    public string Name => "fake";

    public Task<IEngineSession> OpenAsync(ConnectionSpec spec, IMessageSink messages, CancellationToken ct) =>
        Task.FromResult<IEngineSession>(server.OpenSession());

    public ErrorInfo DescribeError(Exception ex) =>
        ex is FakeServerException { Position: { } position } ? new ErrorInfo(ex.Message, Position: position) : new ErrorInfo(ex.Message);
}

/// <summary>
/// One session. Statements are the model's: BEGIN, COMMIT, ROLLBACK typed as SQL, FAIL (an error
/// that aborts an open transaction, as on PostgreSQL), anything else succeeds. The API calls and the
/// probe act on the server's state, as the IEngineSession contract asks.
/// </summary>
public sealed class FakeSession(FakeServer server, string id) : IEngineSession
{
    private readonly Lock _gate = new();

    public string ServerSessionId { get; } = id;

    public ServerTx State { get; private set; }

    public Task<ExecuteSummary> ExecuteAsync(string sql, IResultSink sink, QueryControl control)
    {
        if (sql.StartsWith("ROWS ", StringComparison.Ordinal)) return StreamRowsAsync(int.Parse(sql[5..]), sink);
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

    public ValueTask DisposeAsync()
    {
        server.Close(this);
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeServerException(string message, int? position = null) : Exception(message)
{
    public int? Position { get; } = position;
}
