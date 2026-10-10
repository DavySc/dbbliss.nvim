using Dbbliss.Backend.Catalog;
using Dbbliss.Backend.Engines;
using Npgsql;

namespace Dbbliss.Backend.Sessions;

/// <summary>
/// PostgreSQL sessions from pg_stat_activity. An action matches its target by pid and backend_start in
/// the same statement that signals it, so a pid the server has reused is never touched.
/// </summary>
public sealed class PostgresSessions : ISessionAdmin
{
    public bool CanCancel => true;

    // Aliases are quoted: "user" is reserved. Duration: running time of an active statement, else time in the current state.
    private const string ListSql = """
        SELECT a.pid::text AS "id", a.backend_start AS "identity", a.usename AS "user", a.datname AS "database",
               COALESCE(host(a.client_addr), a.client_hostname) AS "host", a.application_name AS "application",
               COALESCE(a.state, 'unknown') AS "state",
               (EXTRACT(EPOCH FROM (now() - CASE WHEN a.state = 'active' THEN a.query_start ELSE a.state_change END)) * 1000)::bigint AS "duration_ms",
               CASE WHEN a.state = 'active' AND a.wait_event IS NOT NULL THEN a.wait_event_type || ': ' || a.wait_event END AS "wait",
               (pg_blocking_pids(a.pid))[1]::text AS "blocked_by",
               (a.xact_start IS NOT NULL) AS "in_transaction",
               a.query AS "query"
        FROM pg_stat_activity a
        WHERE a.backend_type = 'client backend'
        ORDER BY a.pid
        """;

    private const string Match = """
        FROM pg_stat_activity a
        WHERE a.pid = @pid AND a.backend_start = @started AND a.backend_type = 'client backend' AND a.pid <> pg_backend_pid()
        """;

    public async Task<IReadOnlyList<ServerSession>> ListAsync(IEngineSession session, CancellationToken ct)
    {
        var table = await CatalogHelpers.First(session, ListSql, null, ct);
        return table.Rows.Select(r => new ServerSession(
            Id: Str(r[0])!, Identity: SessionIds.FormatIdentity(r[1]), User: Str(r[2]), Database: Str(r[3]), Host: Str(r[4]),
            Application: Str(r[5]), State: Str(r[6])!, DurationMs: r[7] is null ? null : Convert.ToInt64(r[7], System.Globalization.CultureInfo.InvariantCulture),
            Wait: Str(r[8]), BlockedBy: Str(r[9]), InTransaction: CatalogHelpers.Bool(r[10]), Query: Str(r[11]))).ToList();
    }

    public Task<bool> CancelAsync(IEngineSession session, SessionTarget target, CancellationToken ct) =>
        SignalAsync(session, "pg_cancel_backend", target, ct);

    public Task<bool> TerminateAsync(IEngineSession session, SessionTarget target, CancellationToken ct) =>
        SignalAsync(session, "pg_terminate_backend", target, ct);

    public async Task<SessionStatus> StatusAsync(IEngineSession session, SessionTarget target, MessageLog messages, CancellationToken ct)
    {
        var table = await CatalogHelpers.First(session, "SELECT 1 AS \"present\" " + Match, Parameters(target), ct);
        return new SessionStatus(table.Rows.Count > 0, null);
    }

    private static async Task<bool> SignalAsync(IEngineSession session, string function, SessionTarget target, CancellationToken ct)
    {
        try
        {
            // The function name is one of two constants above.
            var table = await CatalogHelpers.First(session, $"SELECT {function}(a.pid) AS \"done\" " + Match, Parameters(target), ct);
            return table.Rows.Count > 0
                ? CatalogHelpers.Bool(table.Rows[0][0])
                : throw new SessionActionException($"Session {target.Id} no longer exists, or is not the one you saw; refresh the list.");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new SessionActionException(ex.MessageText);
        }
    }

    private static Dictionary<string, object?> Parameters(SessionTarget target) => new()
    {
        ["pid"] = SessionIds.ParseInt(target.Id),
        ["started"] = SessionIds.ParseIdentity(target.Identity),
    };

    private static string? Str(object? v) => CatalogHelpers.Str(v);
}
