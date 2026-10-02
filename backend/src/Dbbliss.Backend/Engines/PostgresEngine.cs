using System.Globalization;
using Npgsql;

namespace Dbbliss.Backend.Engines;

/// <summary>PostgreSQL via Npgsql. Cancel = NpgsqlCommand.Cancel() → CancelRequest on a separate connection.</summary>
public sealed class PostgresEngine : IEngine
{
    /// <summary>
    /// Default for client_connection_check_interval (PG 14+, Linux/BSD/macOS servers). Without it the
    /// server keeps running a statement whose client died until the statement tries to send data.
    /// </summary>
    private const int DefaultConnectionCheckMs = 2000;

    public string Name => "postgres";

    public async Task<IEngineSession> OpenAsync(ConnectionSpec spec, IMessageSink messages, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(spec.ConnectionString)
        {
            // A pooled connection would outlive "disconnect" and keep its transaction until reset.
            Pooling = false,
        };
        if (spec.Password is not null) builder.Password = spec.Password;
        if (string.IsNullOrEmpty(builder.ApplicationName)) builder.ApplicationName = "dbbliss.nvim";

        var conn = new NpgsqlConnection(builder.ConnectionString);
        conn.Notice += (_, e) => messages.Message(e.Notice.Severity.ToLowerInvariant(), e.Notice.MessageText);
        try
        {
            await conn.OpenAsync(ct);
            var checkMs = spec.Options?["pg_client_connection_check_interval_ms"]?.GetValue<int>() ?? DefaultConnectionCheckMs;
            if (checkMs > 0)
            {
                await ApplyConnectionCheckAsync(conn, checkMs, messages, ct);
            }
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
        return new Session(conn);
    }

    private static async Task ApplyConnectionCheckAsync(NpgsqlConnection conn, int ms, IMessageSink messages, CancellationToken ct)
    {
        if (conn.PostgreSqlVersion.Major < 14)
        {
            messages.Message("warning",
                $"PostgreSQL {conn.PostgreSqlVersion} has no client_connection_check_interval: a query keeps running on the server if the backend dies.");
            return;
        }
        try
        {
            await using var cmd = new NpgsqlCommand($"SET client_connection_check_interval = {ms}", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException ex)
        {
            // Servers on platforms without POLLRDHUP (e.g. Windows) reject any non-zero value.
            messages.Message("warning",
                $"Could not enable client_connection_check_interval ({ex.MessageText}): a query keeps running on the server if the backend dies.");
        }
    }

    public ErrorInfo DescribeError(Exception ex)
    {
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        if (pg is not null)
        {
            // Position is a 1-based character offset into the statement; Phase 1 maps it to a line.
            return new ErrorInfo(pg.MessageText, Code: pg.SqlState, SqlState: pg.SqlState,
                Line: pg.Position > 0 ? pg.Position : null, Severity: pg.Severity);
        }
        return new ErrorInfo(ex.Message);
    }

    private sealed class Session(NpgsqlConnection conn) : IEngineSession
    {
        private NpgsqlTransaction? _tx;

        public string ServerSessionId { get; } = conn.ProcessID.ToString(CultureInfo.InvariantCulture);

        public bool InTransaction => _tx is not null;

        public async Task<ExecuteSummary> ExecuteAsync(string sql, IResultSink sink, QueryControl control)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, _tx) { CommandTimeout = 0 };
            // Explicit NpgsqlCommand.Cancel() (a CancelRequest) rather than a CancellationToken:
            // Npgsql acts on the token only while it is blocked on network I/O. With rows already
            // buffered, the token never reaches the server and disposing the reader then drains
            // the whole remaining result. Found by the streaming_cancel scenario.
            control.SetProtocolCancel(cmd.Cancel);
            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
                await AdoStreaming.StreamAsync(reader, sink, control, CancellationToken.None);
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
            if (_tx is not null) throw new InvalidOperationException("A transaction is already open.");
            _tx = await conn.BeginTransactionAsync(ct);
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            var tx = _tx ?? throw new InvalidOperationException("No open transaction.");
            if (await GetTransactionStateAsync(ct) == TransactionState.Aborted)
            {
                // PostgreSQL turns COMMIT of an aborted transaction into a silent ROLLBACK. Refuse instead.
                throw new InvalidOperationException("The transaction was aborted by the server; only rollback is possible.");
            }
            await tx.CommitAsync(ct);
            await tx.DisposeAsync();
            _tx = null;
        }

        public async Task RollbackAsync(CancellationToken ct)
        {
            if (_tx is null) return;
            await _tx.RollbackAsync(ct);
            await _tx.DisposeAsync();
            _tx = null;
        }

        public async Task<TransactionState> GetTransactionStateAsync(CancellationToken ct)
        {
            if (_tx is null) return TransactionState.None;
            try
            {
                await using var cmd = new NpgsqlCommand("SELECT 1", conn, _tx);
                await cmd.ExecuteScalarAsync(ct);
                return TransactionState.Active;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InFailedSqlTransaction)
            {
                return TransactionState.Aborted;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_tx is not null) await _tx.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
