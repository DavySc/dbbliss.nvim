using System.Globalization;
using Dbbliss.Backend.Catalog;
using Dbbliss.Backend.Engines;
using Microsoft.Data.SqlClient;

namespace Dbbliss.Backend.Sessions;

/// <summary>
/// SQL Server sessions from sys.dm_exec_sessions with the session's current request. SQL Server has no
/// statement-only cancel for another session: KILL ends the session and rolls back its transaction.
/// KILL takes no parameter, so it is built from an integer that <see cref="SessionIds.ParseInt"/> checked.
/// </summary>
public sealed class SqlServerSessions : ISessionAdmin
{
    /// <summary>KILL errors the user can fix: 6102 permission, 6104 own process, 6106 no such process, 6107 not a user process.</summary>
    private static readonly int[] UserErrors = [6102, 6104, 6106, 6107];

    /// <summary>"Status report cannot be obtained. Rollback operation for Process ID n is not in progress."</summary>
    private const int NoRollback = 6120;

    public bool CanCancel => false;

    // One request per session (TOP (1)): MARS can have several, and a join would repeat the session.
    private const string ListSql = """
        SELECT CAST(s.session_id AS varchar(12)) AS [id], s.login_time AS [identity], s.login_name AS [user],
               DB_NAME(COALESCE(r.database_id, s.database_id)) AS [database], s.host_name AS [host], s.program_name AS [application],
               CASE WHEN r.session_id IS NOT NULL THEN r.status WHEN s.status = 'sleeping' THEN 'idle' ELSE s.status END AS [state],
               CASE WHEN r.session_id IS NOT NULL THEN DATEDIFF_BIG(ms, r.start_time, GETDATE())
                    ELSE DATEDIFF_BIG(ms, s.last_request_end_time, GETDATE()) END AS [duration_ms],
               r.wait_type AS [wait],
               CASE WHEN r.blocking_session_id > 0 THEN CAST(r.blocking_session_id AS varchar(12)) END AS [blocked_by],
               CAST(CASE WHEN s.open_transaction_count > 0 THEN 1 ELSE 0 END AS bit) AS [in_transaction],
               LEFT(t.[text], 4000) AS [query]
        FROM sys.dm_exec_sessions s
        OUTER APPLY (SELECT TOP (1) r1.* FROM sys.dm_exec_requests r1 WHERE r1.session_id = s.session_id ORDER BY r1.start_time) r
        LEFT JOIN sys.dm_exec_connections c ON c.session_id = s.session_id
        OUTER APPLY sys.dm_exec_sql_text(COALESCE(r.sql_handle, c.most_recent_sql_handle)) t
        WHERE s.is_user_process = 1
        ORDER BY s.session_id
        """;

    private const string Match = """
        SELECT s.session_id FROM sys.dm_exec_sessions s
        WHERE s.session_id = @id AND s.login_time = @started AND s.is_user_process = 1 AND s.session_id <> @@SPID
        """;

    public async Task<IReadOnlyList<ServerSession>> ListAsync(IEngineSession session, CancellationToken ct)
    {
        var table = await CatalogHelpers.First(session, ListSql, null, ct);
        return table.Rows.Select(r => new ServerSession(
            Id: CatalogHelpers.Str(r[0])!, Identity: SessionIds.FormatIdentity(r[1]), User: CatalogHelpers.Str(r[2]), Database: CatalogHelpers.Str(r[3]),
            Host: CatalogHelpers.Str(r[4]), Application: CatalogHelpers.Str(r[5]), State: CatalogHelpers.Str(r[6])!,
            DurationMs: r[7] is null ? null : Convert.ToInt64(r[7], CultureInfo.InvariantCulture), Wait: CatalogHelpers.Str(r[8]),
            BlockedBy: CatalogHelpers.Str(r[9]), InTransaction: CatalogHelpers.Bool(r[10]), Query: CatalogHelpers.Str(r[11]))).ToList();
    }

    public Task<bool> CancelAsync(IEngineSession session, SessionTarget target, CancellationToken ct) =>
        throw new SessionActionException("SQL Server cannot cancel another session's statement without ending the session; terminate it instead.");

    public async Task<bool> TerminateAsync(IEngineSession session, SessionTarget target, CancellationToken ct)
    {
        var id = await RequirePresentAsync(session, target, ct);
        await GuardAsync(() => session.QueryAsync("KILL " + id.ToString(CultureInfo.InvariantCulture), null, ct));
        return true;
    }

    public async Task<SessionStatus> StatusAsync(IEngineSession session, SessionTarget target, MessageLog messages, CancellationToken ct)
    {
        var table = await CatalogHelpers.First(session, Match, Parameters(target), ct);
        if (table.Rows.Count == 0) return new SessionStatus(false, null);
        var id = SessionIds.ParseInt(target.Id);
        // Only what this call makes the server say counts.
        messages.Drain();
        try
        {
            await GuardAsync(() => session.QueryAsync("KILL " + id.ToString(CultureInfo.InvariantCulture) + " WITH STATUSONLY", null, ct));
        }
        catch (SqlException ex) when (ex.Number == NoRollback)
        {
            return new SessionStatus(true, null);
        }
        var said = messages.Drain();
        return new SessionStatus(true, said.Count == 0 ? null : string.Join("\n", said));
    }

    private static async Task<int> RequirePresentAsync(IEngineSession session, SessionTarget target, CancellationToken ct)
    {
        var table = await CatalogHelpers.First(session, Match, Parameters(target), ct);
        return table.Rows.Count > 0
            ? SessionIds.ParseInt(target.Id)
            : throw new SessionActionException($"Session {target.Id} no longer exists, or is not the one you saw; refresh the list.");
    }

    /// <summary>Errors of KILL that the user can fix are reported as such; the rest are driver failures.</summary>
    private static async Task GuardAsync(Func<Task> kill)
    {
        try
        {
            await kill();
        }
        catch (SqlException ex) when (UserErrors.Contains(ex.Number))
        {
            throw new SessionActionException(ex.Message);
        }
    }

    private static Dictionary<string, object?> Parameters(SessionTarget target) => new()
    {
        ["id"] = SessionIds.ParseInt(target.Id),
        ["started"] = SessionIds.ParseIdentity(target.Identity),
    };
}
