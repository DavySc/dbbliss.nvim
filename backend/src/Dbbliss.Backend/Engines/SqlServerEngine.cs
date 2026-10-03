using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Dbbliss.Backend.Engines;

/// <summary>SQL Server via Microsoft.Data.SqlClient. Cancel = SqlCommand.Cancel() → TDS attention.</summary>
public sealed class SqlServerEngine : IEngine
{
    public string Name => "sqlserver";

    public async Task<IEngineSession> OpenAsync(ConnectionSpec spec, IMessageSink messages, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(spec.ConnectionString)
        {
            // A pooled connection would outlive "disconnect" and keep its transaction until reset.
            Pooling = false,
        };
        if (spec.Password is not null) builder.Password = spec.Password;
        if (string.IsNullOrEmpty(builder.ApplicationName) || builder.ApplicationName == "Core Microsoft SqlClient Data Provider")
        {
            builder.ApplicationName = "dbbliss.nvim";
        }

        var conn = new SqlConnection(builder.ConnectionString);
        conn.InfoMessage += (_, e) =>
        {
            foreach (SqlError err in e.Errors)
            {
                messages.Message(err.Class > 10 ? "error" : "info", err.Message, err.Number, err.LineNumber);
            }
        };
        try
        {
            await conn.OpenAsync(ct);
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
            var first = sql.Errors.Count > 0 ? sql.Errors[0] : null;
            return new ErrorInfo(
                sql.Message,
                Code: sql.Number.ToString(CultureInfo.InvariantCulture),
                Line: first?.LineNumber,
                Severity: sql.Class.ToString(CultureInfo.InvariantCulture));
        }
        return new ErrorInfo(ex.Message);
    }

    private sealed class Session(SqlConnection conn) : IEngineSession
    {
        private SqlTransaction? _tx;

        public string ServerSessionId { get; } = conn.ServerProcessId.ToString(CultureInfo.InvariantCulture);

        // A zombied SqlTransaction (server rolled it back, e.g. XACT_ABORT) has Connection == null.
        private SqlTransaction? LiveTx => _tx?.Connection is not null ? _tx : null;

        public bool InTransaction => LiveTx is not null;

        public async Task<ExecuteSummary> ExecuteAsync(string sql, IResultSink sink, QueryControl control)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            cmd.Transaction = LiveTx;
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

        public async Task BeginTransactionAsync(CancellationToken ct)
        {
            if (InTransaction) throw new InvalidOperationException("A transaction is already open.");
            _tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            var tx = LiveTx ?? throw new InvalidOperationException("No open transaction (it may have been rolled back by the server).");
            await tx.CommitAsync(ct);
            await tx.DisposeAsync();
            _tx = null;
        }

        public async Task RollbackAsync(CancellationToken ct)
        {
            var tx = LiveTx;
            if (tx is not null) await tx.RollbackAsync(ct);
            if (_tx is not null) await _tx.DisposeAsync();
            _tx = null;
        }

        public async Task<TransactionState> GetTransactionStateAsync(CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT @@TRANCOUNT, XACT_STATE()";
            cmd.Transaction = LiveTx;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var trancount = reader.GetInt32(0);
            var xactState = reader.GetInt16(1);
            return xactState switch
            {
                -1 => TransactionState.Aborted,
                1 => TransactionState.Active,
                _ when trancount > 0 => TransactionState.Active,
                _ => TransactionState.None,
            };
        }

        public async ValueTask DisposeAsync()
        {
            // Closing a non-pooled connection ends the server session, which rolls back any open transaction.
            if (_tx is not null) await _tx.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
