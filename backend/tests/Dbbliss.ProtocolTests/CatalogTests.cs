using System.Text.Json.Nodes;
using Dbbliss.Backend.Catalog;
using Dbbliss.Backend.Rpc;

namespace Dbbliss.ProtocolTests;

public static class CatalogTests
{
    private static void Names(string text, bool brackets, params string[] expected)
    {
        var parts = ObjectNames.Parse(text, brackets);
        if (!parts.SequenceEqual(expected)) throw new TestFailure($"{text}: got [{string.Join("|", parts)}], expected [{string.Join("|", expected)}]");
    }

    private static void BadName(string text, bool brackets)
    {
        try
        {
            ObjectNames.Parse(text, brackets);
        }
        catch (FormatException)
        {
            return;
        }
        throw new TestFailure($"\"{text}\" should not parse");
    }

    public static void NameParsing()
    {
        Names("customer", false, "customer");
        Names("public.customer", false, "public", "customer");
        Names("db.dbo.customer", true, "db", "dbo", "customer");
        Names("\"My Table\"", false, "My Table");
        Names("public.\"Order Line\"", false, "public", "Order Line");
        Names("\"a.b\".c", false, "a.b", "c");
        Names("\"say \"\"hi\"\"\"", false, "say \"hi\"");
        Names("[dbo].[Order Line]", true, "dbo", "Order Line");
        Names("[a]]b].c", true, "a]b", "c");
        Names("[x.y]", true, "x.y");
        Names("  spaced . name ", false, "spaced", "name");
        Names("[brackets]", false, "[brackets]");   // not quotes on PostgreSQL
        BadName("", false);
        BadName("a.", false);
        BadName(".a", false);
        BadName("a..b", false);
        BadName("\"unclosed", false);
        BadName("[unclosed", true);
        BadName("\"\"", false);
        BadName("\"a\"b", false);
    }

    private static JsonObject Children(Harness h, string c, JsonObject? extra = null)
    {
        var p = new JsonObject { ["connection_id"] = c };
        foreach (var (k, v) in extra ?? []) p[k] = v?.DeepClone();
        return p;
    }

    // The catalog uses a session of its own: it answers while the user's session is busy with a paused
    // query, and in an aborted transaction, which would refuse every catalog query.
    public static async Task IndependentOfTheUserSession()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        var userSession = h.Server.Open.Keys.Single();

        await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q1", ["sql"] = "ROWS 5000", ["window"] = 100 });
        await WaitUntilPaused(h, "q1");
        var busy = await h.CallAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q2", ["sql"] = "SELECT 1" });
        if (busy["error"]?["code"]?.GetValue<int>() != RpcErrors.ConnectionBusy) throw new TestFailure("the user's session should be busy");
        var roots = await h.ResultAsync("catalog/children", Children(h, c));
        if (roots["nodes"]![0]!["name"]!.GetValue<string>() != "db1") throw new TestFailure($"children: {roots.ToJsonString()}");
        await h.ResultAsync("cancel", new JsonObject { ["query_id"] = "q1" });
        await h.NotificationAsync("query/done", "q1");

        await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q3", ["sql"] = "BEGIN" });
        await h.NotificationAsync("query/done", "q3");
        await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q4", ["sql"] = "FAIL" });
        var done = (await h.NotificationAsync("query/done", "q4"))["params"]!;
        if (done["transaction"]!.GetValue<string>() != "aborted") throw new TestFailure("the user's transaction should be aborted");
        var info = await h.ResultAsync("catalog/describe", new JsonObject { ["connection_id"] = c, ["name"] = "public.customer" });
        if (info["sections"]!.AsArray().Count != 2) throw new TestFailure($"describe: {info.ToJsonString()}");

        var used = h.Engine.FakeCatalog.SessionsUsed.Distinct().ToArray();
        if (used.Length != 1 || used[0] == userSession) throw new TestFailure($"catalog used {string.Join(",", used)}, the user's session is {userSession}");
        if (h.Server.Open.Count != 2) throw new TestFailure($"expected the user's session and one catalog session, got {h.Server.Open.Count}");
    }

    private static async Task WaitUntilPaused(Harness h, string queryId)
    {
        await h.WaitForAsync(m => m["method"]?.GetValue<string>() == "query/paused" && m["params"]?["query_id"]?.GetValue<string>() == queryId, "query/paused");
    }

    // Both sessions end with the connection, on disconnect and on shutdown.
    public static async Task CatalogSessionEndsWithTheConnection()
    {
        await using (var h = new Harness())
        {
            var c = await h.ConnectAsync();
            await h.ResultAsync("catalog/children", Children(h, c));
            if (h.Server.Open.Count != 2) throw new TestFailure("no catalog session was opened");
            await h.ResultAsync("disconnect", new JsonObject { ["connection_id"] = c, ["rollback"] = false });
            if (!h.Server.Open.IsEmpty) throw new TestFailure($"sessions left after disconnect: {string.Join(",", h.Server.Open.Keys)}");
        }
        await using (var h = new Harness())
        {
            var c = await h.ConnectAsync();
            await h.ResultAsync("catalog/children", Children(h, c));
            await h.CloseAsync();
            if (!h.Server.Open.IsEmpty) throw new TestFailure($"sessions left after shutdown: {string.Join(",", h.Server.Open.Keys)}");
        }
    }

    // A name that matches nothing is the user's to fix (1007); a driver failure replaces the session.
    public static async Task Errors()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        var missing = await h.CallAsync("catalog/describe", new JsonObject { ["connection_id"] = c, ["name"] = "missing" });
        if (missing["error"]?["code"]?.GetValue<int>() != RpcErrors.Catalog) throw new TestFailure($"missing: {missing.ToJsonString()}");
        if (h.Server.Open.Count != 2) throw new TestFailure("a user error should keep the catalog session");
        var first = h.Server.Open.Keys.Order().ToArray();

        var crash = await h.CallAsync("catalog/describe", new JsonObject { ["connection_id"] = c, ["name"] = "crash" });
        if (crash["error"]?["code"]?.GetValue<int>() != RpcErrors.Database) throw new TestFailure($"crash: {crash.ToJsonString()}");
        if (h.Server.Open.Count != 1) throw new TestFailure("the broken catalog session should be closed");

        var again = await h.ResultAsync("catalog/describe", new JsonObject { ["connection_id"] = c, ["name"] = "ok" });
        if (again["title"]!.GetValue<string>() != "table ok") throw new TestFailure("the catalog did not recover");
        if (h.Server.Open.Count != 2 || h.Server.Open.Keys.Order().SequenceEqual(first)) throw new TestFailure("a new catalog session was expected");

        var unknown = await h.CallAsync("catalog/describe", new JsonObject { ["connection_id"] = "nope", ["name"] = "x" });
        if (unknown["error"]?["code"]?.GetValue<int>() != RpcErrors.UnknownConnection) throw new TestFailure("unknown connection");
        var noName = await h.CallAsync("catalog/describe", new JsonObject { ["connection_id"] = c });
        if (noName["error"]?["code"]?.GetValue<int>() != RpcErrors.InvalidParams) throw new TestFailure("a request without a name");
    }

    public static async Task DescribeAndScriptShapes()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        var info = await h.ResultAsync("catalog/describe", new JsonObject { ["connection_id"] = c, ["name"] = "t" });
        var columns = info["sections"]![0]!;
        if (columns["title"]!.GetValue<string>() != "Columns" || columns["text"]!.GetValue<bool>()) throw new TestFailure("columns section");
        if (columns["rows"]![1]![1] is not null) throw new TestFailure("a NULL cell must stay null");
        var definition = info["sections"]![1]!;
        if (!definition["text"]!.GetValue<bool>() || definition["rows"]!.AsArray().Count != 2) throw new TestFailure("definition section");
        var script = await h.ResultAsync("catalog/script", new JsonObject { ["connection_id"] = c, ["object"] = "t", ["schema"] = "s", ["kind"] = "table" });
        if (script["text"]!.GetValue<string>() != "CREATE TABLE t ();\n") throw new TestFailure($"script: {script.ToJsonString()}");
        var tree = await h.ResultAsync("catalog/children", Children(h, c, new JsonObject { ["database"] = "d", ["schema"] = "s", ["folder"] = "table" }));
        if (tree["nodes"]![0]!["name"]!.GetValue<string>() != "under d/s/table") throw new TestFailure($"tree: {tree.ToJsonString()}");
    }
}
