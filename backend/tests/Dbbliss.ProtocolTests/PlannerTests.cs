using System.Text.Json.Nodes;
using Dbbliss.Backend.Engines;
using Dbbliss.Backend.Management;
using Dbbliss.Backend.Plans;

namespace Dbbliss.ProtocolTests;

/// <summary>What the planners send and in which order, against a scripted session. The plans themselves come from the real servers in the cancel suite.</summary>
public static class PlannerTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static ManagementContext Context(ScriptedSession session) => new(new StubEngine(_ => session), new ConnectionSpec("x", null, null), "dev");

    private static Func<string, QueryControl, IResultSink, Task> Returns(string column, params string[] cells) => async (_, _, sink) =>
    {
        await sink.ResultSetAsync(0, [new ColumnInfo(column, "json")]);
        foreach (var cell in cells) await sink.RowAsync(0, new JsonArray(cell));
        await sink.ResultSetDoneAsync(0, cells.Length);
    };

    private static void Expect(IEnumerable<string> actual, IEnumerable<string> expected, string what)
    {
        if (!actual.SequenceEqual(expected)) throw new TestFailure($"{what}:\n  got      {string.Join(" | ", actual)}\n  expected {string.Join(" | ", expected)}");
    }

    private static async Task<T> Fails<T>(Func<Task> action, string what, string? contains = null) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T ex)
        {
            if (contains is not null && !ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)) throw new TestFailure($"{what}: \"{ex.Message}\" lacks \"{contains}\"");
            return ex;
        }
        throw new TestFailure($"{what}: expected {typeof(T).Name}");
    }

    // Verifies: HLR-PLAN-2, HLR-PLAN-1
    public static async Task PostgresEstimatedRunsNothing()
    {
        var session = new ScriptedSession(executeWithSink: Returns("QUERY PLAN", Fixture("pg_estimated.json")));
        var docs = await new PostgresPlanner().PlanAsync(Context(session), new PlanRequest("SELECT 1;", PlanMode.Estimated, false), CancellationToken.None);
        Expect(session.Events, ["sql: EXPLAIN (FORMAT JSON) SELECT 1;"], "an estimated plan is one EXPLAIN, no transaction");
        if (docs.Count != 1 || docs[0].Mode != PlanMode.Estimated || docs[0].Statement != "SELECT 1;" || docs[0].Root.Operator != "Hash Join") throw new TestFailure("plan: " + docs[0].Root.Operator);
        if (!session.Disposed) throw new TestFailure("the plan session was kept");
    }

    // Verifies: HLR-PLAN-2, HLR-PLAN-3
    public static async Task PostgresActualRunsInATransactionThatIsRolledBack()
    {
        var session = new ScriptedSession(executeWithSink: Returns("QUERY PLAN", Fixture("pg_actual.json")));
        var docs = await new PostgresPlanner().PlanAsync(Context(session), new PlanRequest("DELETE FROM t", PlanMode.Actual, true), CancellationToken.None);
        Expect(session.Events, ["begin", "sql: EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) DELETE FROM t", "rollback"], "an actual plan is run inside a transaction that is rolled back");
        if (docs[0].Mode != PlanMode.Actual || !session.Disposed) throw new TestFailure("mode or session");
        if (session.Events.Contains("commit")) throw new TestFailure("committed");

        // Failed: still rolled back, and the failure is the driver's.
        var failing = new ScriptedSession(execute: (_, _) => throw new InvalidOperationException("relation \"t\" does not exist"));
        await Fails<InvalidOperationException>(() => new PostgresPlanner().PlanAsync(Context(failing), new PlanRequest("DELETE FROM t", PlanMode.Actual, true), CancellationToken.None), "failure", "does not exist");
        Expect(failing.Events, ["begin", "sql: EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) DELETE FROM t", "rollback"], "a failed actual plan is rolled back too");
        if (!failing.Disposed) throw new TestFailure("the session was kept after a failure");

        // A rollback that cannot be sent does not lose the plan: closing the session ends the transaction on the server.
        var broken = new ScriptedSession(executeWithSink: Returns("QUERY PLAN", Fixture("pg_actual.json"))) { RollbackThrows = true };
        var kept = await new PostgresPlanner().PlanAsync(Context(broken), new PlanRequest("UPDATE t SET a = 1", PlanMode.Actual, true), CancellationToken.None);
        if (kept.Count != 1 || !broken.Disposed) throw new TestFailure("the plan is kept and the session closed even when the rollback fails");
    }

    // Verifies: HLR-PLAN-5, HLR-PLAN-3
    public static async Task PostgresCancelStopsTheStatementAndRollsBack()
    {
        var started = new TaskCompletionSource();
        var session = new ScriptedSession(execute: async (sql, control) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, control.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("canceling statement due to user request");
            }
        });
        using var cts = new CancellationTokenSource();
        var running = new PostgresPlanner().PlanAsync(Context(session), new PlanRequest("DELETE FROM t", PlanMode.Actual, true), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        await Fails<OperationCanceledException>(() => running, "cancel");
        if (session.ProtocolCancels < 1) throw new TestFailure("the protocol-level cancel was not used");
        Expect(session.Events, ["begin", "sql: EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) DELETE FROM t", "rollback"], "a cancelled plan is rolled back");
        if (!session.Disposed) throw new TestFailure("the session was kept");
    }

    // Verifies: HLR-PLAN-8
    public static async Task PostgresWithoutAPlanIsAFailure()
    {
        var none = new ScriptedSession();
        await Fails<OperationFailedException>(() => new PostgresPlanner().PlanAsync(Context(none), new PlanRequest("SELECT 1", PlanMode.Estimated, false), CancellationToken.None), "no rows", "no plan");
        var odd = new ScriptedSession(executeWithSink: Returns("QUERY PLAN", "this is not json"));
        await Fails<OperationFailedException>(() => new PostgresPlanner().PlanAsync(Context(odd), new PlanRequest("SELECT 1", PlanMode.Estimated, false), CancellationToken.None), "not json", "plan");
        if (!none.Disposed || !odd.Disposed) throw new TestFailure("sessions kept");
    }

    // Verifies: HLR-PLAN-2, HLR-PLAN-1
    public static async Task SqlServerEstimatedAsksForTheShowplanInsteadOfRunning()
    {
        var session = new ScriptedSession(executeWithSink: (sql, _, sink) => sql.StartsWith("SET", StringComparison.Ordinal)
            ? Task.CompletedTask
            : Returns("Microsoft SQL Server 2005 XML Showplan", Fixture("ss_estimated.xml"))(sql, null!, sink));
        var docs = await new SqlServerPlanner().PlanAsync(Context(session), new PlanRequest("SELECT 1", PlanMode.Estimated, false), CancellationToken.None);
        Expect(session.Events, ["sql: SET SHOWPLAN_XML ON", "sql: SELECT 1"], "estimated: the showplan setting, then the batch (which is then not run)");
        if (docs.Count != 1 || docs[0].Root.Operator != "Sort (TopN Sort)" || !session.Disposed) throw new TestFailure("plan");
    }

    // Verifies: HLR-PLAN-2, HLR-PLAN-3, HLR-PLAN-8
    public static async Task SqlServerActualRunsInATransactionAndKeepsOnlyThePlans()
    {
        var session = new ScriptedSession(executeWithSink: async (sql, _, sink) =>
        {
            if (sql.StartsWith("SET", StringComparison.Ordinal)) return;
            // The statement's own result set, then its plan, then a second statement's plan.
            await sink.ResultSetAsync(0, [new ColumnInfo("n", "int")]);
            for (var i = 0; i < 1000; i++) await sink.RowAsync(0, new JsonArray(i));
            await sink.ResultSetDoneAsync(0, 1000);
            await sink.ResultSetAsync(1, [new ColumnInfo("Microsoft SQL Server 2005 XML Showplan", "xml")]);
            await sink.RowAsync(1, new JsonArray(Fixture("ss_actual.xml")));
            await sink.ResultSetDoneAsync(1, 1);
            await sink.ResultSetAsync(2, [new ColumnInfo("Microsoft SQL Server 2005 XML Showplan", "xml")]);
            await sink.RowAsync(2, new JsonArray(Fixture("ss_actual.xml")));
            await sink.ResultSetDoneAsync(2, 1);
        });
        var docs = await new SqlServerPlanner().PlanAsync(Context(session), new PlanRequest("DELETE FROM t", PlanMode.Actual, true), CancellationToken.None);
        Expect(session.Events, ["begin", "sql: SET STATISTICS XML ON", "sql: DELETE FROM t", "rollback"], "actual: inside a transaction that is rolled back");
        if (docs.Count != 2 || docs.Any(d => d.Mode != PlanMode.Actual)) throw new TestFailure("one plan per statement, the result set discarded: " + docs.Count);
        if (!session.Disposed) throw new TestFailure("session kept");

        var failing = new ScriptedSession(executeWithSink: (sql, _, _) => sql.StartsWith("SET", StringComparison.Ordinal) ? Task.CompletedTask : throw new InvalidOperationException("Invalid object name 't'."));
        await Fails<InvalidOperationException>(() => new SqlServerPlanner().PlanAsync(Context(failing), new PlanRequest("DELETE FROM t", PlanMode.Actual, true), CancellationToken.None), "failure", "Invalid object");
        Expect(failing.Events, ["begin", "sql: SET STATISTICS XML ON", "sql: DELETE FROM t", "rollback"], "a failed actual plan is rolled back too");

        var noPlan = new ScriptedSession(executeWithSink: Returns("n", "1"));
        await Fails<OperationFailedException>(() => new SqlServerPlanner().PlanAsync(Context(noPlan), new PlanRequest("SELECT 1", PlanMode.Estimated, false), CancellationToken.None), "no showplan result set", "no plan");

        var broken = new ScriptedSession(executeWithSink: Returns("Microsoft SQL Server 2005 XML Showplan", Fixture("ss_actual.xml"))) { RollbackThrows = true };
        var kept = await new SqlServerPlanner().PlanAsync(Context(broken), new PlanRequest("UPDATE t SET a = 1", PlanMode.Actual, true), CancellationToken.None);
        if (kept.Count != 1) throw new TestFailure("a rollback that fails does not lose the plan");
    }

    // Verifies: HLR-PLAN-5, HLR-PLAN-3
    public static async Task SqlServerCancelStopsTheStatementAndRollsBack()
    {
        var started = new TaskCompletionSource();
        var session = new ScriptedSession(execute: async (sql, control) =>
        {
            if (sql.StartsWith("SET", StringComparison.Ordinal)) return;
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, control.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("Operation cancelled by user.");
            }
        });
        using var cts = new CancellationTokenSource();
        var running = new SqlServerPlanner().PlanAsync(Context(session), new PlanRequest("DELETE FROM t", PlanMode.Actual, true), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        await Fails<OperationCanceledException>(() => running, "cancel");
        if (session.ProtocolCancels < 1) throw new TestFailure("the protocol-level cancel was not used");
        if (session.Events.Last() != "rollback" || !session.Disposed) throw new TestFailure("rolled back and closed: " + string.Join(" | ", session.Events));
    }
}
