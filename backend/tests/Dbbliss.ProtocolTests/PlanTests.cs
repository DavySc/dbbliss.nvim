using System.Text.Json.Nodes;
using Dbbliss.Backend.Rpc;

namespace Dbbliss.ProtocolTests;

public static class PlanTests
{
    private static JsonObject P(string c, string sql, string mode = "estimated", bool confirm = false)
    {
        var p = new JsonObject { ["connection_id"] = c, ["sql"] = sql, ["mode"] = mode };
        if (confirm) p["confirm_execute"] = true;
        return p;
    }

    private static int Code(JsonObject response) => response["error"]?["code"]?.GetValue<int>() ?? 0;

    private static void Expect(JsonObject response, int code, string what)
    {
        if (Code(response) != code) throw new TestFailure($"{what}: expected error {code}, got {response.ToJsonString()}");
    }

    private static async Task<string> StartAsync(Harness h, JsonObject p) => (await h.ResultAsync("plan/start", p))["plan_id"]!.GetValue<string>();

    private static Task<JsonObject> DoneAsync(Harness h, string id) =>
        h.WaitForAsync(m => m["method"]?.GetValue<string>() == "plan/done" && m["params"]?["plan_id"]?.GetValue<string>() == id, "plan/done for " + id);

    // Verifies: HLR-PLAN-1, HLR-PLAN-7
    public static async Task APlanComesBackAsOneNotification()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        var id = await StartAsync(h, P(c, "SELECT 1"));
        var done = (await DoneAsync(h, id))["params"]!;
        if (done["status"]!.GetValue<string>() != "completed" || done["connection_id"]!.GetValue<string>() != c) throw new TestFailure("done: " + done.ToJsonString());
        var plan = done["plans"]!.AsArray().Single()!;
        if (plan["engine"]!.GetValue<string>() != "fake" || plan["mode"]!.GetValue<string>() != "estimated" || plan["statement"]!.GetValue<string>() != "SELECT 1"
            || plan["raw"]!.GetValue<string>() != "RAW" || plan["hottest"]!.GetValue<int>() != 1 || plan["notes"]![0]!.GetValue<string>() != "a note" || plan["totals"]!["cost"]!.GetValue<double>() != 10)
        {
            throw new TestFailure("plan: " + plan.ToJsonString());
        }
        var root = plan["root"]!;
        if (root["operator"]!.GetValue<string>() != "Hash Join" || root["children"]!.AsArray().Count != 1 || root["children"]![0]!["detail"]!.GetValue<string>() != "on t"
            || root["children"]![0]!["self_cost"]!.GetValue<double>() != 8 || root["self_cost"]!.GetValue<double>() != 2)
        {
            throw new TestFailure("root: " + root.ToJsonString());
        }
        await Task.Delay(200);
        if (h.Messages.Count(m => m["method"]?.GetValue<string>() == "plan/done") != 1) throw new TestFailure("done not exactly once");
        var request = h.Engine.FakePlanner.Requests.Single();
        if (request.Mode != Dbbliss.Backend.Plans.PlanMode.Estimated || request.Sql != "SELECT 1") throw new TestFailure("the planner got " + request);
    }

    // Verifies: HLR-PLAN-4
    public static async Task AnActualPlanOfAWriteNeedsTheCallersWord()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        // Estimated plans run nothing: no word needed.
        await DoneAsync(h, await StartAsync(h, P(c, "DELETE FROM t")));
        // Actual plans run the statement: a read goes, a write is refused until the caller says the user agreed.
        await DoneAsync(h, await StartAsync(h, P(c, "SELECT * FROM t", "actual")));
        var before = h.Engine.FakePlanner.Requests.Count;
        foreach (var sql in new[] { "DELETE FROM t", "UPDATE t SET a = 1", "INSERT INTO t VALUES (1)", "WITH x AS (DELETE FROM t RETURNING *) SELECT * FROM x", "SELECT * INTO u FROM t", "CALL p()", "EXEC p", "DROP TABLE t" })
        {
            var refused = await h.CallAsync("plan/start", P(c, sql, "actual"));
            Expect(refused, RpcErrors.Management, "an actual plan of: " + sql);
            if (!refused["error"]!["message"]!.GetValue<string>().Contains("run", StringComparison.Ordinal)) throw new TestFailure("the refusal says why: " + refused.ToJsonString());
        }
        if (h.Engine.FakePlanner.Requests.Count != before) throw new TestFailure("the planner ran something that was refused");
        var id = await StartAsync(h, P(c, "DELETE FROM t", "actual", confirm: true));
        await DoneAsync(h, id);
        var last = h.Engine.FakePlanner.Requests.Last();
        if (!last.ConfirmExecute || last.Mode != Dbbliss.Backend.Plans.PlanMode.Actual) throw new TestFailure("the planner is told: " + last);
    }

    // Verifies: HLR-PLAN-5, HLR-PLAN-8
    public static async Task FailuresAndCancelsAreReported()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        var failed = (await DoneAsync(h, await StartAsync(h, P(c, "SELECT fail"))))["params"]!;
        if (failed["status"]!.GetValue<string>() != "failed" || !failed["error"]!.GetValue<string>().Contains("does not exist", StringComparison.Ordinal) || failed["plans"]!.AsArray().Count != 0) throw new TestFailure("failed: " + failed.ToJsonString());
        var crashed = (await DoneAsync(h, await StartAsync(h, P(c, "SELECT crash"))))["params"]!;
        if (crashed["status"]!.GetValue<string>() != "failed" || !crashed["error"]!.GetValue<string>().Contains("driver exploded", StringComparison.Ordinal)) throw new TestFailure("crashed: " + crashed.ToJsonString());
        var empty = (await DoneAsync(h, await StartAsync(h, P(c, "SELECT empty"))))["params"]!;
        if (empty["status"]!.GetValue<string>() != "failed" || !empty["error"]!.GetValue<string>().Contains("no plan", StringComparison.OrdinalIgnoreCase)) throw new TestFailure("no plan is a failure, not an empty plan: " + empty.ToJsonString());

        var hung = await StartAsync(h, P(c, "SELECT hang", "actual"));
        var state = await h.ResultAsync("plan/cancel", new JsonObject { ["plan_id"] = hung });
        if (state["state"]!.GetValue<string>() != "cancelling") throw new TestFailure("cancel: " + state.ToJsonString());
        var done = (await DoneAsync(h, hung))["params"]!;
        if (done["status"]!.GetValue<string>() != "cancelled" || h.Engine.FakePlanner.Cancelled != 1) throw new TestFailure("cancelled: " + done.ToJsonString());
        if ((await h.ResultAsync("plan/cancel", new JsonObject { ["plan_id"] = hung }))["state"]!.GetValue<string>() != "finished") throw new TestFailure("a finished plan");
        Expect(await h.CallAsync("plan/cancel", new JsonObject { ["plan_id"] = "nope" }), RpcErrors.InvalidParams, "unknown plan");
        // A backup id is not a plan id.
        var backup = (await h.ResultAsync("backup/start", new JsonObject { ["connection_id"] = c, ["database"] = "app", ["path"] = "/b/x" }))["backup_id"]!.GetValue<string>();
        Expect(await h.CallAsync("plan/cancel", new JsonObject { ["plan_id"] = backup }), RpcErrors.InvalidParams, "a backup is not a plan");
    }

    // Verifies: HLR-PLAN-6
    public static async Task APlanIsAnOperationOfItsConnection()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        var id = await StartAsync(h, P(c, "SELECT hang", "actual"));
        Expect(await h.CallAsync("plan/start", P(c, "SELECT 1")), RpcErrors.ConnectionBusy, "second plan");
        Expect(await h.CallAsync("backup/start", new JsonObject { ["connection_id"] = c, ["database"] = "app", ["path"] = "/b/x" }), RpcErrors.ConnectionBusy, "backup during a plan");
        Expect(await h.CallAsync("management/drop", new JsonObject { ["connection_id"] = c, ["kind"] = "table", ["database"] = "app", ["schema"] = "public", ["name"] = "t", ["confirm_name"] = "t" }), RpcErrors.ConnectionBusy, "drop during a plan");
        Expect(await h.CallAsync("disconnect", new JsonObject { ["connection_id"] = c, ["rollback"] = true }), RpcErrors.ConnectionBusy, "disconnect during a plan");
        await h.ResultAsync("plan/cancel", new JsonObject { ["plan_id"] = id });
        await DoneAsync(h, id);
        await h.ResultAsync("disconnect", new JsonObject { ["connection_id"] = c, ["rollback"] = false });

        // Shutdown cancels a running plan and waits for it.
        await using var h2 = new Harness();
        var c2 = await h2.ConnectAsync("dev");
        await StartAsync(h2, P(c2, "SELECT hang", "actual"));
        await Task.Delay(300);
        await h2.CloseAsync();
        if (h2.Engine.FakePlanner.Cancelled != 1) throw new TestFailure("shutdown did not cancel the plan");
    }

    // Verifies: HLR-PLAN-1
    public static async Task BadRequests()
    {
        await using var h = new Harness(new FakeEngine(new FakeServer(), "plain", hasPlanner: false));
        var c = await h.ConnectAsync("dev");
        Expect(await h.CallAsync("plan/start", P("nope", "SELECT 1")), RpcErrors.UnknownConnection, "unknown connection");
        Expect(await h.CallAsync("plan/start", new JsonObject { ["connection_id"] = c, ["mode"] = "estimated" }), RpcErrors.InvalidParams, "no sql");
        Expect(await h.CallAsync("plan/start", P(c, "SELECT 1", "later")), RpcErrors.InvalidParams, "unknown mode");
        var noMode = P(c, "SELECT 1");
        noMode.Remove("mode");
        Expect(await h.CallAsync("plan/start", noMode), RpcErrors.InvalidParams, "no mode");
        var plain = (await h.ResultAsync("connect", new JsonObject { ["engine"] = "plain", ["connection_string"] = "x", ["env"] = "dev" }))["connection_id"]!.GetValue<string>();
        var response = await h.CallAsync("plan/start", P(plain, "SELECT 1"));
        if (Code(response) != RpcErrors.InvalidParams || !response["error"]!["message"]!.GetValue<string>().Contains("no plan support", StringComparison.Ordinal)) throw new TestFailure("an engine without plans: " + response.ToJsonString());
        // After all the refusals the connection is free.
        await DoneAsync(h, await StartAsync(h, P(c, "SELECT 1")));
    }
}
