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

    /// <summary>
    /// Transactions are plain BEGIN/COMMIT/ROLLBACK statements, never NpgsqlTransaction objects, so
    /// a transaction the user opened or ended with typed SQL is the same thing to the backend as one
    /// the API opened. Every decision asks the server first (spec/quint/client.qnt, ServerTruth).
    /// </summary>
    private sealed class Session(NpgsqlConnection conn) : IEngineSession
    {
        public string ServerSessionId { get; } = conn.ProcessID.ToString(CultureInfo.InvariantCulture);

        public async Task<ExecuteSummary> ExecuteAsync(string sql, IResultSink sink, QueryControl control)
        {
            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
            // Explicit NpgsqlCommand.Cancel() (a CancelRequest) rather than a CancellationToken:
            // Npgsql acts on the token only while it is blocked on network I/O. With rows already
            // buffered, the token never reaches the server and disposing the reader then drains
            // the whole remaining result. Found by the streaming_cancel scenario.
            control.SetProtocolCancel(cmd.Cancel);
            try
            {
            // Narrows the window TLC found (ShutdownCancel before Send): a cancel that arrived
            // before this point must not let the statement go out. The remaining window between
            // this check and the send is covered by the re-fire watchdog.
            control.Token.ThrowIfCancellationRequested();
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
            if (await GetTransactionStateAsync(ct) != TransactionState.None)
            {
                throw new InvalidOperationException("A transaction is already open.");
            }
            await RunAsync("BEGIN", ct);
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            switch (await GetTransactionStateAsync(ct))
            {
                case TransactionState.None:
                    throw new InvalidOperationException("No open transaction.");
                case TransactionState.Aborted:
                    // PostgreSQL turns COMMIT of an aborted transaction into a silent ROLLBACK. Refuse instead.
                    throw new InvalidOperationException("The transaction was aborted by the server; only rollback is possible.");
            }
            await RunAsync("COMMIT", ct);
        }

        public async Task RollbackAsync(CancellationToken ct)
        {
            // Skipped when there is nothing to roll back: ROLLBACK outside a transaction is a WARNING.
            if (await GetTransactionStateAsync(ct) != TransactionState.None) await RunAsync("ROLLBACK", ct);
        }

        /// <summary>
        /// A transaction-local setting outlives the statement that sets it only inside a transaction
        /// block, and setting it fails with 25P02 in an aborted one. Two round trips, no server log noise.
        /// </summary>
        public async Task<TransactionState> GetTransactionStateAsync(CancellationToken ct)
        {
            var token = Guid.NewGuid().ToString("N");
            try
            {
                await using var set = new NpgsqlCommand("SELECT set_config('dbbliss.tx_probe', @token, true)", conn);
                set.Parameters.AddWithValue("token", token);
                await set.ExecuteScalarAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InFailedSqlTransaction)
            {
                return TransactionState.Aborted;
            }
            await using var get = new NpgsqlCommand("SELECT current_setting('dbbliss.tx_probe', true)", conn);
            return await get.ExecuteScalarAsync(ct) as string == token ? TransactionState.Active : TransactionState.None;
        }

        private async Task RunAsync(string sql, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Closing the session rolls back any open transaction on the server.
        public ValueTask DisposeAsync() => conn.DisposeAsync();
    }
}
