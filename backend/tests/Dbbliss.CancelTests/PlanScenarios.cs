using System.Text.Json.Nodes;

namespace Dbbliss.CancelTests;

/// <summary>Phase 5: plans against a real server.</summary>
public sealed partial class Scenarios
{
    private static JsonObject PlanParams(string conn, string sql, string mode, bool confirm = false)
    {
        var p = new JsonObject { ["connection_id"] = conn, ["sql"] = sql, ["mode"] = mode };
        if (confirm) p["confirm_execute"] = true;
        return p;
    }

    private static async Task<(string Id, JsonObject Done)> PlanAsync(BackendClient c, string conn, string sql, string mode, bool confirm = false)
    {
        var id = (await c.RequestAsync("plan/start", PlanParams(conn, sql, mode, confirm)))["plan_id"]!.GetValue<string>();
        return (id, (await c.WaitNotificationAsync(n => IsPlanDone(n, id), 120000))["params"]!.AsObject());
    }

    private static bool IsPlanDone(JsonObject n, string id) => n["method"]?.GetValue<string>() == "plan/done" && n["params"]?["plan_id"]?.GetValue<string>() == id;

    private static IEnumerable<JsonObject> PlanNodes(JsonNode node)
    {
        yield return node.AsObject();
        foreach (var child in node["children"]!.AsArray())
        {
            foreach (var n in PlanNodes(child!)) yield return n;
        }
    }

    /// <summary>True when a statement that contains <paramref name="marker"/> is running on a session other than the observer's.</summary>
    private async Task<bool> StatementRunningAsync(System.Data.Common.DbConnection observer, string marker)
    {
        var sql = profile.Engine == "postgres"
            ? "SELECT count(*) FROM pg_stat_activity WHERE state = 'active' AND pid <> pg_backend_pid() AND query LIKE @m"
            : "SELECT count(*) FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t WHERE r.session_id <> @@SPID AND t.text LIKE @m";
        var (_, v) = await FirstRowAsync3(observer, sql, "%" + marker + "%");
        return Convert.ToInt64(v[0], System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<(bool, object?[])> FirstRowAsync3(System.Data.Common.DbConnection c, string sql, string value)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var p = cmd.CreateParameter();
        p.ParameterName = "m";
        p.Value = value;
        cmd.Parameters.Add(p);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (false, []);
        var values = new object?[r.FieldCount];
        for (var i = 0; i < values.Length; i++) values[i] = r.IsDBNull(i) ? null : r.GetValue(i);
        return (true, values);
    }

    /// <summary>
    /// Estimated and actual plans of real statements: a tree with figures, the hottest node, and, for an actual plan of an
    /// INSERT, an UPDATE and a DELETE, the statement run (its scan saw the rows) and then rolled back (the table is as it was).
    /// </summary>
    private async Task<ScenarioResult> PlansEstimatedAndActual()
    {
        var (steps, step, ok) = Steps();
        const int rows = 20000;
        await using var observer = await profile.OpenObserverAsync();
        await profile.CreateScratchAsync(observer, Scratch, rows);
        try
        {
            var (c, conn, _) = await StartAsync(connectionString: profile.ScratchConnectionString(Scratch));
            await using var _c = c;
            await using var scratch = await profile.OpenObserverAsync(Scratch);
            var schema = profile.Engine == "postgres" ? "public." : "dbo.";
            var join = $"SELECT p.id, q.pad FROM {schema}bkp_probe p JOIN {schema}bkp_probe q ON q.id = p.id WHERE p.id < 100";

            var (_, est) = await PlanAsync(c, conn, join, "estimated");
            step($"estimated: {est["status"]} {est["error"]}", est["status"]!.GetValue<string>() == "completed");
            var plan = est["plans"]!.AsArray()[0]!;
            var nodes = PlanNodes(plan["root"]!).ToList();
            step($"estimated tree of {nodes.Count} nodes", nodes.Count >= 2 && nodes.All(n => !string.IsNullOrEmpty(n["operator"]!.GetValue<string>())));
            step("estimated: costs and rows, no actual figures", nodes.All(n => n["cost"] is not null) && nodes.All(n => n["actual_rows"] is null && n["time_ms"] is null));
            step("estimated: a hottest node", plan["hottest"] is not null && nodes.Any(n => n["id"]!.GetValue<int>() == plan["hottest"]!.GetValue<int>()));
            step("raw plan kept", plan["raw"]!.GetValue<string>().Length > 50 && plan["mode"]!.GetValue<string>() == "estimated");

            var (_, act) = await PlanAsync(c, conn, join, "actual");
            step($"actual: {act["status"]} {act["error"]}", act["status"]!.GetValue<string>() == "completed");
            var actual = act["plans"]!.AsArray()[0]!;
            var anodes = PlanNodes(actual["root"]!).ToList();
            step("actual: rows and time on the nodes", anodes.Any(n => n["actual_rows"] is not null) && anodes.Any(n => n["time_ms"] is not null));
            step("actual: the join produced the 99 rows (ids 1..99)", anodes.Any(n => n["actual_rows"]?.GetValue<double>() == 99));
            step("actual: a hottest node with an own time", actual["hottest"] is not null && anodes.Any(n => n["id"]!.GetValue<int>() == actual["hottest"]!.GetValue<int>() && n["self_time_ms"] is not null));

            // DML: the estimate runs nothing, the actual refuses without the word, and with it runs and is rolled back.
            var statements = new[]
            {
                $"INSERT INTO {schema}parent SELECT id + 1000000 FROM {schema}bkp_probe",
                $"UPDATE {schema}bkp_probe SET pad = 'changed'",
                $"DELETE FROM {schema}bkp_probe",
            };
            foreach (var dml in statements)
            {
                var kind = dml.Split(' ')[0];
                var (_, e) = await PlanAsync(c, conn, dml, "estimated");
                step($"{kind} estimated is a plan", e["status"]!.GetValue<string>() == "completed");
                step($"{kind} actual is refused without the word", await ErrorCodeAsync(c.RequestAsync("plan/start", PlanParams(conn, dml, "actual"))) == 1009);
                var (_, a) = await PlanAsync(c, conn, dml, "actual", confirm: true);
                var dmlNodes = a["status"]!.GetValue<string>() == "completed" ? PlanNodes(a["plans"]![0]!["root"]!).ToList() : [];
                step($"{kind} actual ran ({a["status"]} {a["error"]}): a node saw {rows} rows", dmlNodes.Any(n => n["actual_rows"]?.GetValue<double>() == rows));
            }
            var (_, remaining) = await FirstRowAsync2(scratch, $"SELECT (SELECT count(*) FROM {schema}bkp_probe), (SELECT count(*) FROM {schema}parent), (SELECT count(*) FROM {schema}bkp_probe WHERE pad = 'changed')", "x");
            step($"nothing was kept: {rows} rows, 0 parents, 0 changed ({string.Join(",", remaining)})", Convert.ToInt64(remaining[0], System.Globalization.CultureInfo.InvariantCulture) == rows
                && Convert.ToInt64(remaining[1], System.Globalization.CultureInfo.InvariantCulture) == 0 && Convert.ToInt64(remaining[2], System.Globalization.CultureInfo.InvariantCulture) == 0);

            var (_, broken) = await PlanAsync(c, conn, "SELECT * FROM table_that_is_not_there", "estimated");
            step($"a statement the server refuses fails with its words: {broken["error"]}", broken["status"]!.GetValue<string>() == "failed" && broken["error"]!.GetValue<string>().Contains("table_that_is_not_there", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await profile.DropScratchAsync(observer, Scratch);
        }
        return Result("plans_estimated_and_actual", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }

    /// <summary>
    /// A long actual plan of a statement that changes data: cancel stops it on the server and what it changed is rolled back;
    /// it blocks the operations it should; closing the backend ends it too.
    /// </summary>
    private async Task<ScenarioResult> PlanCancelAndShutdown()
    {
        var (steps, step, ok) = Steps();
        await using var observer = await profile.OpenObserverAsync();
        await profile.CreateScratchAsync(observer, Scratch, 1000);
        try
        {
            var (c, conn, _) = await StartAsync(connectionString: profile.ScratchConnectionString(Scratch));
            await using var _c = c;
            await using var scratch = await profile.OpenObserverAsync(Scratch);
            // Changes every row, then waits: whatever was changed must not survive the cancel.
            var slow = profile.Engine == "postgres"
                ? "WITH u AS (UPDATE public.bkp_probe SET pad = 'plan_cancel_probe' RETURNING id) SELECT count(*) FROM u, pg_sleep(60)"
                : "UPDATE dbo.bkp_probe SET pad = 'plan_cancel_probe'; WAITFOR DELAY '00:01:00'";
            var id = (await c.RequestAsync("plan/start", PlanParams(conn, slow, "actual", confirm: true)))["plan_id"]!.GetValue<string>();
            var seen = false;
            for (var waited = 0; waited < 20000 && !seen; waited += 200)
            {
                seen = await StatementRunningAsync(observer, "plan_cancel_probe");
                if (!seen) await Task.Delay(200);
            }
            step("the statement is running on the server", seen);
            step("a second plan is refused as busy", await ErrorCodeAsync(c.RequestAsync("plan/start", PlanParams(conn, "SELECT 1", "estimated"))) == 1002);
            step("a disconnect is refused as busy", await ErrorCodeAsync(c.RequestAsync("disconnect", new JsonObject { ["connection_id"] = conn, ["rollback"] = true })) == 1002);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await c.RequestAsync("plan/cancel", new JsonObject { ["plan_id"] = id });
            var done = (await c.WaitNotificationAsync(n => IsPlanDone(n, id), 60000))["params"]!;
            step($"cancelled in {sw.ElapsedMilliseconds} ms ({done["status"]})", done["status"]!.GetValue<string>() == "cancelled");
            var stopped = false;
            for (var waited = 0; waited < 15000 && !stopped; waited += 200)
            {
                stopped = !await StatementRunningAsync(observer, "plan_cancel_probe");
                if (!stopped) await Task.Delay(200);
            }
            step("the statement is gone from the server", stopped);
            var (_, changed) = await FirstRowAsync2(scratch, $"SELECT count(*) FROM {(profile.Engine == "postgres" ? "public." : "dbo.")}bkp_probe WHERE pad = 'plan_cancel_probe'", "x");
            step($"what it changed was rolled back ({changed[0]} rows changed)", Convert.ToInt64(changed[0], System.Globalization.CultureInfo.InvariantCulture) == 0);

            // Closing the backend mid-plan.
            var id2 = (await c.RequestAsync("plan/start", PlanParams(conn, slow, "actual", confirm: true)))["plan_id"]!.GetValue<string>();
            seen = false;
            for (var waited = 0; waited < 20000 && !seen; waited += 200)
            {
                seen = await StatementRunningAsync(observer, "plan_cancel_probe");
                if (!seen) await Task.Delay(200);
            }
            step("the second statement is running", seen && id2 != id);
            c.CloseStdin();
            step("the backend exits", await c.WaitExitAsync(30000));
            stopped = false;
            for (var waited = 0; waited < 15000 && !stopped; waited += 200)
            {
                stopped = !await StatementRunningAsync(observer, "plan_cancel_probe");
                if (!stopped) await Task.Delay(200);
            }
            step("the statement is gone after shutdown", stopped);
            var (_, again) = await FirstRowAsync2(scratch, $"SELECT count(*) FROM {(profile.Engine == "postgres" ? "public." : "dbo.")}bkp_probe WHERE pad = 'plan_cancel_probe'", "x");
            step("and nothing it changed is left", Convert.ToInt64(again[0], System.Globalization.CultureInfo.InvariantCulture) == 0);
        }
        finally
        {
            await profile.DropScratchAsync(observer, Scratch);
        }
        return Result("plan_cancel_and_shutdown", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }
}
