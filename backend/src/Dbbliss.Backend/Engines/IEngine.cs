using System.Text.Json.Nodes;

namespace Dbbliss.Backend.Engines;

/// <summary>One implementation per database engine. Nothing engine-specific leaks past this.</summary>
public interface IEngine
{
    string Name { get; }

    Task<IEngineSession> OpenAsync(ConnectionSpec spec, IMessageSink messages, CancellationToken ct);

    /// <summary>Extracts code, SQLSTATE, severity and line number from a driver exception.</summary>
    ErrorInfo DescribeError(Exception ex);
}

public sealed record ErrorInfo(string Message, string? Code = null, string? SqlState = null, int? Line = null, string? Severity = null);

public sealed record ConnectionSpec(string ConnectionString, string? Password, JsonObject? Options);

/// <summary>One open server session. Not thread-safe: the backend runs at most one query per session.</summary>
public interface IEngineSession : IAsyncDisposable
{
    /// <summary>Server-side identity of this session: PG backend pid, SQL Server SPID, IBM i job name.</summary>
    string ServerSessionId { get; }

    bool InTransaction { get; }

    /// <summary>
    /// Runs <paramref name="sql"/> and streams every result set into <paramref name="sink"/>.
    /// Cancellation is driven by <paramref name="control"/>: the engine registers the protocol-level
    /// cancel for its driver, and must treat <see cref="QueryControl.Token"/> as a request to stop.
    /// </summary>
    Task<ExecuteSummary> ExecuteAsync(string sql, IResultSink sink, QueryControl control);

    Task BeginTransactionAsync(CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);

    /// <summary>Asks the server what state the transaction is in. Never answers from local bookkeeping alone.</summary>
    Task<TransactionState> GetTransactionStateAsync(CancellationToken ct);
}

public enum TransactionState
{
    None,
    Active,
    /// <summary>Server marked it failed (PG aborted / SQL Server uncommittable); only rollback is possible.</summary>
    Aborted,
    /// <summary>The engine cannot ask the server; the value is local bookkeeping only.</summary>
    Unknown,
}

public sealed record ColumnInfo(string Name, string Type);

public sealed record ExecuteSummary(long RowsAffected);

public interface IResultSink
{
    ValueTask ResultSetAsync(int index, IReadOnlyList<ColumnInfo> columns, CancellationToken ct);
    ValueTask RowAsync(int index, JsonArray row, CancellationToken ct);
    ValueTask ResultSetDoneAsync(int index, long rows, CancellationToken ct);
}

public interface IMessageSink
{
    void Message(string severity, string text, int? number = null, int? line = null);
}
