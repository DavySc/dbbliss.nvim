using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dbbliss.Backend.Catalog;
using Dbbliss.Backend.Management;
using Dbbliss.Backend.Plans;
using Dbbliss.Backend.Scripts;
using Dbbliss.Backend.Sessions;
using Microsoft.Data.SqlClient;

namespace Dbbliss.Backend.Engines;

/// <summary>SQL Server via Microsoft.Data.SqlClient. Cancel = SqlCommand.Cancel() → TDS attention.</summary>
public sealed partial class SqlServerEngine : IEngine
{
    public string Name => "sqlserver";

    public ScriptDialect Dialect => ScriptDialect.GoBatches;

    public ICatalog? Catalog { get; } = new SqlServerCatalog();

    public ISessionAdmin? Sessions { get; } = new SqlServerSessions();

    public IPlanner? Planner { get; } = new SqlServerPlanner();

    public IManagement? Management { get; } = new SqlServerManagement();

    /// <summary>
    /// SET options applied to every new session, as SSMS does. Microsoft.Data.SqlClient already logs in
    /// with ANSI_NULLS, ANSI_PADDING, ANSI_WARNINGS, CONCAT_NULL_YIELDS_NULL, QUOTED_IDENTIFIER and
    /// ANSI_NULL_DFLT_ON on, but leaves ARITHABORT off while SSMS turns it on. The two then get separate
    /// plan cache entries, so a query can be slow in one and fast in the other (parameter sniffing).
    /// Override or extend with <c>options.mssql_set_options</c>, for example <c>{ ARITHABORT = "OFF" }</c>.
    /// </summary>
    private static readonly (string Name, string Value)[] DefaultSetOptions = [("ARITHABORT", "ON")];

    [GeneratedRegex("^[A-Za-z_]+$")]
    private static partial Regex OptionName();

    [GeneratedRegex(@"^(-?[0-9]+|[A-Za-z0-9_.]+)$")]
    private static partial Regex OptionValue();

    /// <summary>
    /// The SET batch for a new session: the defaults, then the user's entries (same name replaces the
    /// default). Names and values are checked against a narrow pattern because they are pasted into SQL.
    /// </summary>
    public static string BuildSetBatch(JsonObject? options)
    {
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in DefaultSetOptions) set[name] = value;
        var configured = options?["mssql_set_options"];
        if (configured is not null and not JsonObject)
        {
            throw new ArgumentException("options.mssql_set_options must be a table of SET option names and values.");
        }
        if (configured is JsonObject user)
        {
            foreach (var (name, node) in user)
            {
                var value = node?.GetValueKind() switch
                {
                    System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
                    System.Text.Json.JsonValueKind.True => "ON",
                    System.Text.Json.JsonValueKind.False => "OFF",
                    System.Text.Json.JsonValueKind.Number when long.TryParse(node.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
                        => n.ToString(CultureInfo.InvariantCulture),
                    _ => throw new ArgumentException($"options.mssql_set_options.{name} must be a string, an integer or a boolean."),
                };
                if (!OptionName().IsMatch(name) || !OptionValue().IsMatch(value))
                {
                    throw new ArgumentException($"options.mssql_set_options: \"{name}\" = \"{value}\" is not a plain SET option and value.");
                }
                set[name] = value;
            }
        }
        return string.Join("; ", set.Select(kv => $"SET {kv.Key.ToUpperInvariant()} {kv.Value}"));
    }

    public async Task<IEngineSession> OpenAsync(ConnectionSpec spec, IMessageSink messages, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(spec.ConnectionString)
        {
            // A pooled connection would outlive "disconnect" and keep its transaction until reset.
            Pooling = false,
        };
        if (spec.Password is not null) builder.Password = spec.Password;
        // SqlClient reports its own name when the connection string sets none (an empty one counts as none).
        if (builder.ApplicationName == "Core Microsoft SqlClient Data Provider")
        {
            builder.ApplicationName = "dbbliss.nvim";
        }

        var setBatch = BuildSetBatch(spec.Options);
        var conn = new SqlConnection(builder.ConnectionString);
        conn.InfoMessage += (_, e) =>
        {
            foreach (SqlError err in e.Errors)
            {
                // SqlClient raises errors (class 11 and above) as exceptions unless FireInfoMessageEventOnUserErrors is
                // set, which it is not: only messages of class 0 to 10 arrive here.
                messages.Message("info", err.Message, err.Number, err.LineNumber);
            }
        };
        try
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = setBatch;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
        return new Session(conn);
    }

    public ErrorInfo DescribeError(Exception ex)
    {
        if (ex is SqlException sql)
        {
            return new ErrorInfo(
                sql.Message,
                Code: sql.Number.ToString(CultureInfo.InvariantCulture),
                // 0 for an error that is not in a statement (a failed login, a refused connection).
                Line: sql.LineNumber > 0 ? sql.LineNumber : null,
                Severity: sql.Class.ToString(CultureInfo.InvariantCulture));
        }
        return new ErrorInfo(ex.Message);
    }

    /// <summary>
    /// Transactions are plain T-SQL statements, never SqlTransaction objects, so one opened or ended
    /// by typed SQL is the same thing to the backend as one the API opened, and commands never need
    /// a Transaction property. Every decision asks the server first (spec/quint/client.qnt, ServerTruth).
    /// </summary>
    private sealed class Session(SqlConnection conn) : IEngineSession
    {
        public string ServerSessionId { get; } = conn.ServerProcessId.ToString(CultureInfo.InvariantCulture);

        public async Task<ExecuteSummary> ExecuteAsync(string sql, IResultSink sink, QueryControl control)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            control.SetProtocolCancel(cmd.Cancel);
            try
            {
            // Narrows the window TLC found (ShutdownCancel before Send): a cancel that arrived
            // before this point must not let the statement go out. The remaining window between
            // this check and the send is covered by the re-fire watchdog.
            control.Token.ThrowIfCancellationRequested();
                // CancellationToken.None: cancel goes through cmd.Cancel() only, never twice.
                await using var reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
                await AdoStreaming.StreamAsync(reader, sink);
                await reader.CloseAsync();
                return new ExecuteSummary(reader.RecordsAffected);
            }
            finally
            {
                control.ClearProtocolCancel();
            }
        }

        public async Task<IReadOnlyList<QueryTable>> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 30;
            AdoQuery.AddParameters(cmd, parameters);
            return await AdoQuery.ReadAllAsync(cmd, ct);
        }

        public async Task BeginTransactionAsync(CancellationToken ct)
        {
            if (await GetTransactionStateAsync(ct) != TransactionState.None)
            {
                throw new InvalidOperationException("A transaction is already open.");
            }
            await RunAsync("BEGIN TRANSACTION", ct);
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            switch (await GetTransactionStateAsync(ct))
            {
                case TransactionState.None:
                    throw new InvalidOperationException("No open transaction (it may have been rolled back by the server).");
                case TransactionState.Aborted:
                    throw new InvalidOperationException("The transaction is uncommittable; only rollback is possible.");
            }
            await RunAsync("COMMIT TRANSACTION", ct);
        }

        public Task RollbackAsync(CancellationToken ct) => RunAsync("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION", ct);

        private async Task RunAsync(string sql, CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<TransactionState> GetTransactionStateAsync(CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            // 1: an open transaction that can be committed; -1: one that can only be rolled back; 0: none.
            cmd.CommandText = "SELECT XACT_STATE()";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return reader.GetInt16(0) switch
            {
                -1 => TransactionState.Aborted,
                1 => TransactionState.Active,
                _ => TransactionState.None,
            };
        }

        // Closing a non-pooled connection ends the server session, which rolls back any open transaction.
        public ValueTask DisposeAsync() => conn.DisposeAsync();
    }
}
