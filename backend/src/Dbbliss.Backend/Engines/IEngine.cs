using System.Text.Json.Nodes;
using Dbbliss.Backend.Catalog;
using Dbbliss.Backend.Scripts;
using Dbbliss.Backend.Sessions;

namespace Dbbliss.Backend.Engines;

/// <summary>One implementation per database engine. Nothing engine-specific leaks past this.</summary>
public interface IEngine
{
    string Name { get; }

    /// <summary>How scripts for this engine split into the units sent to the server one at a time.</summary>
    ScriptDialect Dialect => ScriptDialect.Semicolon;

    /// <summary>Catalog queries (schema tree, object info, scripting); null if the engine has none.</summary>
    ICatalog? Catalog => null;

    /// <summary>Listing, cancelling and ending other sessions; null if the engine has none.</summary>
    ISessionAdmin? Sessions { get; }

    Task<IEngineSession> OpenAsync(ConnectionSpec spec, IMessageSink messages, CancellationToken ct);

    /// <summary>Extracts code, SQLSTATE, severity and line number from a driver exception.</summary>
    ErrorInfo DescribeError(Exception ex);
}

/// <param name="Line">1-based line within the statement sent, when the engine reports one.</param>
/// <param name="Position">1-based character offset within the statement sent, when the engine reports
/// that instead of a line (PostgreSQL). The backend turns it into a line.</param>
public sealed record ErrorInfo(string Message, string? Code = null, string? SqlState = null, int? Line = null, string? Severity = null, int? Position = null);

public sealed record ConnectionSpec(string ConnectionString, string? Password, JsonObject? Options);

/// <summary>One open server session. Not thread-safe: the backend runs at most one query per session.</summary>
public interface IEngineSession : IAsyncDisposable
{
    /// <summary>Server-side identity of this session: PG backend pid, SQL Server SPID, IBM i job name.</summary>
    string ServerSessionId { get; }

    /// <summary>
    /// Runs <paramref name="sql"/> and streams every result set into <paramref name="sink"/>.
    /// Cancellation is driven by <paramref name="control"/>: the engine registers the protocol-level
    /// cancel for its driver, and must treat <see cref="QueryControl.Token"/> as a request to stop.
    /// </summary>
    Task<ExecuteSummary> ExecuteAsync(string sql, IResultSink sink, QueryControl control);

    /// <summary>
    /// The API's transaction calls. They act on the server's transaction, whoever opened it (the
    /// API or typed SQL), and throw <see cref="InvalidOperationException"/> when the server's state
    /// does not allow the call. Rollback with no transaction open does nothing.
    /// </summary>
    Task BeginTransactionAsync(CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);

    /// <summary>
    /// Runs a read-only query and returns every result set, for catalog work. Parameters are named
    /// without the @ and written @name in the SQL. Not for the user's statements: those stream.
    /// </summary>
    Task<IReadOnlyList<QueryTable>> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct) =>
        throw new NotSupportedException("This engine cannot run catalog queries.");

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

/// <summary>One result set read completely, for catalog queries.</summary>
public sealed record QueryTable(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows)
{
    public int Index(string column) => Columns.ToList().FindIndex(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase)) is var i and >= 0
        ? i
        : throw new InvalidOperationException($"No column {column}.");
}

public sealed record ExecuteSummary(long RowsAffected);

/// <summary>
/// Receives everything the driver reads. Never throws because of a cancel: rows the server sent
/// must not be dropped by unwinding the driver's reader (see spec/tla/QueryLifecycle.tla).
/// </summary>
public interface IResultSink
{
    ValueTask ResultSetAsync(int index, IReadOnlyList<ColumnInfo> columns);
    ValueTask RowAsync(int index, JsonArray row);
    ValueTask ResultSetDoneAsync(int index, long rows);
}

public interface IMessageSink
{
    void Message(string severity, string text, int? number = null, int? line = null);
}
