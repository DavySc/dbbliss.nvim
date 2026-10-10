using Dbbliss.Backend.Engines;
using Dbbliss.Backend.Sessions;
using Npgsql;

namespace Dbbliss.ProtocolTests;

/// <summary>
/// The session SQL of the two engines against a scripted session: what is sent, with which values,
/// and how the answers are read. The statements themselves run against real servers in the cancel suite.
/// </summary>
public static class SessionEngineTests
{
    /// <summary>Answers by what the SQL contains, and records every statement and its parameters.</summary>
    private sealed class Scripted(Func<string, IReadOnlyDictionary<string, object?>?, IReadOnlyList<QueryTable>> answer) : IEngineSession
    {
        public List<string> Sent { get; } = [];
        public List<IReadOnlyDictionary<string, object?>?> Parameters { get; } = [];

        public string ServerSessionId => "9";

        public Task<IReadOnlyList<QueryTable>> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
        {
            Sent.Add(sql);
            Parameters.Add(parameters);
            return Task.FromResult(answer(sql, parameters));
        }

        public Task<ExecuteSummary> ExecuteAsync(string sql, IResultSink sink, QueryControl control) => throw new NotSupportedException();
        public Task BeginTransactionAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task CommitAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task RollbackAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<TransactionState> GetTransactionStateAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly DateTime Started = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Unspecified);
    private static readonly string Token = SessionIds.FormatIdentity(Started);

    private static IReadOnlyList<QueryTable> OneRow() => [new QueryTable(["session_id"], [[55]])];

    private static IReadOnlyList<QueryTable> NoRows() => [new QueryTable(["session_id"], [])];

    private static async Task Throws<T>(Func<Task> action, string what) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new TestFailure(what + ": expected " + typeof(T).Name);
    }

    // Verifies: HLR-ADM-4, HLR-ADM-5, LLR-ADM-9
    public static async Task SqlServerKillIsBuiltFromTheCheckedInteger()
    {
        var admin = new SqlServerSessions();
        var session = new Scripted((sql, _) => sql.Contains("dm_exec_sessions", StringComparison.Ordinal) ? OneRow() : []);
        var done = await admin.TerminateAsync(session, new SessionTarget("55", Token), CancellationToken.None);
        if (!done) throw new TestFailure("terminate should report done");
        if (session.Sent.Count != 2 || session.Sent[1] != "KILL 55") throw new TestFailure("sent: " + string.Join(" | ", session.Sent));
        if (!session.Sent[0].Contains("login_time = @started", StringComparison.Ordinal)) throw new TestFailure("the match ignores the identity: " + session.Sent[0]);
        var p = session.Parameters[0]!;
        if (!Equals(p["id"], 55) || !Equals(p["started"], Started)) throw new TestFailure("the match was not bound to the id and the identity");
        if (session.Sent[0].Contains("55", StringComparison.Ordinal)) throw new TestFailure("the id was pasted into the match query");

        var nobody = new Scripted((_, _) => NoRows());
        await Throws<SessionActionException>(() => admin.TerminateAsync(nobody, new SessionTarget("55", Token), CancellationToken.None), "no matching session");
        if (nobody.Sent.Count != 1) throw new TestFailure("KILL was sent for a session that did not match: " + string.Join(" | ", nobody.Sent));

        var untouched = new Scripted((_, _) => OneRow());
        await Throws<SessionActionException>(() => admin.TerminateAsync(untouched, new SessionTarget("55; SHUTDOWN", Token), CancellationToken.None), "injected id");
        await Throws<SessionActionException>(() => admin.TerminateAsync(untouched, new SessionTarget("55", "yesterday"), CancellationToken.None), "bad identity");
        if (untouched.Sent.Count != 0) throw new TestFailure("something was sent for a bad request");
    }

    // Verifies: HLR-ADM-7
    public static async Task SqlServerStatusReadsOnlyThisCallsMessages()
    {
        var admin = new SqlServerSessions();
        var log = new MessageLog();
        log.Message("info", "stale message from an earlier call");
        var session = new Scripted((sql, _) =>
        {
            if (sql.Contains("dm_exec_sessions", StringComparison.Ordinal)) return OneRow();
            if (sql == "KILL 55 WITH STATUSONLY") log.Message("info", "SPID 55: transaction rollback in progress.");
            return [];
        });
        var status = await admin.StatusAsync(session, new SessionTarget("55", Token), log, CancellationToken.None);
        if (!status.Present || status.Detail != "SPID 55: transaction rollback in progress.") throw new TestFailure($"status: {status}");

        var quiet = await admin.StatusAsync(new Scripted((sql, _) => sql.Contains("dm_exec_sessions", StringComparison.Ordinal) ? OneRow() : []),
            new SessionTarget("55", Token), log, CancellationToken.None);
        if (!quiet.Present || quiet.Detail is not null) throw new TestFailure($"nothing said, nothing shown: {quiet}");

        var gone = new Scripted((_, _) => NoRows());
        var absent = await admin.StatusAsync(gone, new SessionTarget("55", Token), log, CancellationToken.None);
        if (absent.Present || gone.Sent.Count != 1) throw new TestFailure("a session that is gone needs no KILL ... STATUSONLY");
    }

    // Verifies: HLR-ADM-3, HLR-ADM-5, HLR-ADM-8
    public static async Task PostgresSignalsAreMatchedAndReported()
    {
        var admin = new PostgresSessions();
        var utc = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
        var target = new SessionTarget("4242", SessionIds.FormatIdentity(utc));

        var signalled = new Scripted((_, _) => [new QueryTable(["done"], [[true]])]);
        if (!await admin.CancelAsync(signalled, target, CancellationToken.None)) throw new TestFailure("cancel should report done");
        if (!signalled.Sent[0].Contains("pg_cancel_backend", StringComparison.Ordinal) || !signalled.Sent[0].Contains("backend_start = @started", StringComparison.Ordinal))
        {
            throw new TestFailure("cancel SQL: " + signalled.Sent[0]);
        }
        var p = signalled.Parameters[0]!;
        if (!Equals(p["pid"], 4242) || !Equals(p["started"], utc)) throw new TestFailure("parameters not bound");

        var ended = new Scripted((_, _) => [new QueryTable(["done"], [[true]])]);
        await admin.TerminateAsync(ended, target, CancellationToken.None);
        if (!ended.Sent[0].Contains("pg_terminate_backend", StringComparison.Ordinal)) throw new TestFailure("terminate SQL: " + ended.Sent[0]);

        var nothing = new Scripted((_, _) => [new QueryTable(["done"], [[false]])]);
        if (await admin.TerminateAsync(nothing, target, CancellationToken.None)) throw new TestFailure("a signal that reached nothing is not done");

        await Throws<SessionActionException>(() => admin.CancelAsync(new Scripted((_, _) => NoRows()), target, CancellationToken.None), "no matching session");
        await Throws<SessionActionException>(() => admin.CancelAsync(new Scripted((_, _) => []), target, CancellationToken.None), "no result set at all");
        await Throws<SessionActionException>(() => admin.TerminateAsync(signalled, new SessionTarget("4242; SELECT 1", target.Identity), CancellationToken.None), "injected pid");

        var denied = new Scripted((_, _) => throw new PostgresException("permission denied to terminate process", "ERROR", "ERROR", PostgresErrorCodes.InsufficientPrivilege));
        await Throws<SessionActionException>(() => admin.TerminateAsync(denied, target, CancellationToken.None), "permission denied");
        var broken = new Scripted((_, _) => throw new PostgresException("boom", "ERROR", "ERROR", PostgresErrorCodes.InternalError));
        await Throws<PostgresException>(() => admin.TerminateAsync(broken, target, CancellationToken.None), "other server errors are not hidden");

        var present = await admin.StatusAsync(new Scripted((_, _) => [new QueryTable(["present"], [[1]])]), target, new MessageLog(), CancellationToken.None);
        var absent = await admin.StatusAsync(new Scripted((_, _) => [new QueryTable(["present"], [])]), target, new MessageLog(), CancellationToken.None);
        if (!present.Present || absent.Present || present.Detail is not null) throw new TestFailure("status");
    }

    // Verifies: HLR-ADM-1
    public static async Task ListsAreReadIntoRows()
    {
        var utc = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
        string[] columns = ["id", "identity", "user", "database", "host", "application", "state", "duration_ms", "wait", "blocked_by", "in_transaction", "query"];
        object?[] full = ["12", utc, "alice", "sales", "10.0.0.7", "psql", "active", 1500L, "Lock: relation", "9", true, "SELECT 1"];
        object?[] sparse = ["13", utc, null, null, null, null, "idle", null, null, null, false, null];
        var session = new Scripted((_, _) => [new QueryTable(columns, [full, sparse])]);
        foreach (var admin in new ISessionAdmin[] { new PostgresSessions(), new SqlServerSessions() })
        {
            var rows = await admin.ListAsync(session, CancellationToken.None);
            if (rows.Count != 2) throw new TestFailure($"{admin.GetType().Name}: {rows.Count} rows");
            var a = rows[0];
            if (a.Id != "12" || a.Identity != SessionIds.FormatIdentity(utc) || a.User != "alice" || a.DurationMs != 1500 || a.Wait != "Lock: relation"
                || a.BlockedBy != "9" || !a.InTransaction || a.Query != "SELECT 1" || a.State != "active")
            {
                throw new TestFailure($"{admin.GetType().Name}: {a}");
            }
            var b = rows[1];
            if (b.User is not null || b.DurationMs is not null || b.InTransaction || b.Query is not null) throw new TestFailure($"{admin.GetType().Name} sparse: {b}");
        }
    }
}
