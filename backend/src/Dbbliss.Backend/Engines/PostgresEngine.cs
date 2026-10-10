using System.Globalization;
using Dbbliss.Backend.Catalog;
using Dbbliss.Backend.Management;
using Dbbliss.Backend.Sessions;
using Npgsql;

namespace Dbbliss.Backend.Engines;

/// <summary>PostgreSQL via Npgsql. Cancel = NpgsqlCommand.Cancel() → CancelRequest on a separate connection.</summary>
/// <param name="instances">This backend's registration. Sessions are tagged with its id, and every
/// connect ends sessions left by dead instances (decision 19).</param>
public sealed class PostgresEngine(Instances instances) : IEngine
{
    /// <summary><c>application_name</c> prefix; the instance id follows (pgAdmin tags its sessions the same way).</summary>
    public const string AppNamePrefix = "dbbliss.nvim ";

    /// <summary>
    /// Default for client_connection_check_interval (PG 14+, Linux/BSD/macOS servers). Without it the
    /// server keeps running a statement whose client died until the statement tries to send data.
    /// </summary>
    private const int DefaultConnectionCheckMs = 2000;

    public string Name => "postgres";

    public ICatalog? Catalog { get; } = new PostgresCatalog();

    public ISessionAdmin? Sessions { get; } = new PostgresSessions();

    public IManagement? Management { get; } = new PostgresManagement(ProcessRunner.Default);

    public async Task<IEngineSession> OpenAsync(ConnectionSpec spec, IMessageSink messages, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(spec.ConnectionString)
        {
            // A pooled connection would outlive "disconnect" and keep its transaction until reset.
            Pooling = false,
        };
        if (spec.Password is not null) builder.Password = spec.Password;
        // A user-supplied application_name is kept; such sessions cannot be swept.
        if (string.IsNullOrEmpty(builder.ApplicationName)) builder.ApplicationName = AppNamePrefix + instances.Id;

        var conn = new NpgsqlConnection(builder.ConnectionString);
        conn.Notice += (_, e) => messages.Message(e.Notice.Severity.ToLowerInvariant(), e.Notice.MessageText);
        try
        {
            await conn.OpenAsync(ct);
            var checkMs = spec.Options?["pg_client_connection_check_interval_ms"]?.GetValue<int>() ?? DefaultConnectionCheckMs;
            if (checkMs < 0)
            {
                throw new ArgumentException("options.pg_client_connection_check_interval_ms must be 0 (off) or a number of milliseconds above 0.");
            }
            if (checkMs > 0)
            {
                await ApplyConnectionCheckAsync(conn, checkMs, messages, ct);
            }
            await EndOrphansAsync(conn, instances, messages, ct);
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

    /// <summary>
    /// Ends sessions left behind by a backend on this machine that was killed hard. Only the server
    /// could have noticed the dead client, and on Windows it cannot (no client_connection_check_interval),
    /// so the query would run to its end and an open transaction keep its locks. Terminating does what
    /// the server would have done: the statement stops and the transaction rolls back. The user is told.
    /// </summary>
    private static async Task EndOrphansAsync(NpgsqlConnection conn, Instances instances, IMessageSink messages, CancellationToken ct)
    {
        try
        {
            var dead = instances.DeadIds();
            if (dead.Count == 0) return;
            var orphans = new List<(int Pid, string State, int? Seconds, string Query)>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT pid, application_name, coalesce(state, ''), extract(epoch FROM now() - query_start)::int, coalesce(left(query, 60), '') " +
                "FROM pg_stat_activity WHERE application_name LIKE @prefix AND pid <> pg_backend_pid()", conn))
            {
                cmd.Parameters.AddWithValue("prefix", AppNamePrefix + "%");
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    if (!dead.Contains(r.GetString(1)[AppNamePrefix.Length..])) continue;
                    orphans.Add((r.GetInt32(0), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.GetString(4)));
                }
            }
            foreach (var (pid, state, seconds, query) in orphans)
            {
                var what = $"pid {pid}, {(state.Length > 0 ? state : "state unknown")}{(seconds is { } s ? $", last statement started {s} s ago" : "")}: {query}";
                try
                {
                    await using var kill = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", conn);
                    kill.Parameters.AddWithValue("pid", pid);
                    var ended = await kill.ExecuteScalarAsync(ct) is true;
                    messages.Message("warning", ended
                        ? $"Ended a session left by a dbbliss backend that was killed ({what}). Any open transaction in it was rolled back."
                        : $"Could not end a session left by a dbbliss backend that was killed ({what}); it is already gone or not yours.");
                }
                catch (PostgresException ex)
                {
                    messages.Message("warning", $"Could not end a session left by a dbbliss backend that was killed ({what}): {ex.MessageText}");
                }
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidCastException)
        {
            messages.Message("warning", $"Could not check for sessions left by killed dbbliss backends: {ex.Message}");
        }
    }

    public ErrorInfo DescribeError(Exception ex)
    {
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        if (pg is not null)
        {
            // Position is a 1-based character offset into the statement; the backend maps it to a line.
            return new ErrorInfo(pg.MessageText, Code: pg.SqlState, SqlState: pg.SqlState,
                Severity: pg.Severity, Position: pg.Position > 0 ? pg.Position : null);
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

        public async Task<IReadOnlyList<QueryTable>> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 30 };
            AdoQuery.AddParameters(cmd, parameters);
            return await AdoQuery.ReadAllAsync(cmd, ct);
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
