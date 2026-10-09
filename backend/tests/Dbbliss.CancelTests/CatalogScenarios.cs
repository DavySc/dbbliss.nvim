using System.Text.Json.Nodes;

namespace Dbbliss.CancelTests;

/// <summary>Phase 2: the schema tree, object info and CREATE scripts, against a real server.</summary>
public sealed partial class Scenarios
{
    /// <summary>The table with a space in its name, quoted the way the engine quotes.</summary>
    private string OrderLine => profile.Engine == "sqlserver" ? profile.Qualify("dbbliss_cat.[Order Line]") : profile.Qualify("dbbliss_cat.\"Order Line\"");

    private static async Task<JsonArray> ChildrenAsync(BackendClient c, string connId, string? database = null, string? schema = null, string? folder = null)
    {
        var p = new JsonObject { ["connection_id"] = connId };
        if (database is not null) p["database"] = database;
        if (schema is not null) p["schema"] = schema;
        if (folder is not null) p["folder"] = folder;
        return (await c.RequestAsync("catalog/children", p))["nodes"]!.AsArray();
    }

    private static bool Names(JsonArray nodes, params string[] expected) =>
        expected.All(e => nodes.Any(n => n!["name"]!.GetValue<string>() == e));

    private static JsonObject Section(JsonObject info, string title) =>
        info["sections"]!.AsArray().Select(s => s!.AsObject()).First(s => s["title"]!.GetValue<string>() == title);

    private static int Rows(JsonObject info, string title) => Section(info, title)["rows"]!.AsArray().Count;

    private static string Column(JsonObject info, string title, string column)
    {
        var section = Section(info, title);
        var at = section["columns"]!.AsArray().Select(x => x!.GetValue<string>()).ToList().IndexOf(column);
        return string.Join(",", section["rows"]!.AsArray().Select(r => r![at]?.ToString()));
    }

    /// <summary>Runs a script through the backend as a user would: split, then each statement in order.</summary>
    private async Task RunScriptAsync(BackendClient c, string connId, string text, string tag)
    {
        var split = await c.RequestAsync("script/split", new JsonObject { ["engine"] = profile.Engine, ["text"] = text });
        var n = 0;
        foreach (var unit in split["statements"]!.AsArray())
        {
            var id = $"{tag}-{++n}";
            await Execute(c, connId, id, unit!["text"]!.GetValue<string>());
            var done = (await c.WaitDoneAsync(id, DoneTimeoutMs))["params"]!;
            if (done["status"]!.GetValue<string>() != "completed")
            {
                throw new InvalidOperationException($"script statement failed ({done["error"]?["message"]}): {unit["text"]!.GetValue<string>()[..Math.Min(80, unit["text"]!.GetValue<string>().Length)]}");
            }
        }
    }

    /// <summary>
    /// The tree lists what the fixture holds, object info shows it, and a script run again after the
    /// objects are dropped gives the same script back (so the script is a real CREATE, not a sketch).
    /// </summary>
    private async Task<ScenarioResult> CatalogBrowseDescribeScript()
    {
        const string name = "catalog_browse_describe_script";
        await using var observer = await profile.OpenObserverAsync();
        await profile.CreateCatalogFixtureAsync(observer);
        try
        {
            var (c, connId, _) = await StartAsync();
            await using var _c = c;
            var steps = new List<string>();
            var ok = true;
            void Step(string what, bool good)
            {
                steps.Add(what + (good ? "" : " (WRONG)"));
                ok &= good;
            }
            var db = await profile.CatalogDatabaseAsync(observer);

            Step("databases", Names(await ChildrenAsync(c, connId), db));
            Step("schemas", Names(await ChildrenAsync(c, connId, db), "dbbliss_cat"));
            var folders = await ChildrenAsync(c, connId, db, "dbbliss_cat");
            Step("folders", Names(folders, "Tables", "Views", "Functions", "Procedures"));
            var tables = await ChildrenAsync(c, connId, db, "dbbliss_cat", "table");
            Step("tables", Names(tables, "customer", "Order Line"));
            Step("views", Names(await ChildrenAsync(c, connId, db, "dbbliss_cat", "view"), "customer_names"));
            var functions = await ChildrenAsync(c, connId, db, "dbbliss_cat", "function");
            Step("functions", Names(functions, "add_one"));
            Step("procedures", Names(await ChildrenAsync(c, connId, db, "dbbliss_cat", "procedure"), "noop"));

            var customer = await c.RequestAsync("catalog/describe", new JsonObject { ["connection_id"] = connId, ["name"] = profile.Qualify("dbbliss_cat.customer") });
            Step($"customer columns [{Column(customer, "Columns", "name")}]", Column(customer, "Columns", "name") == "id,email,name,created");
            Step($"customer indexes ({Rows(customer, "Indexes")})", Rows(customer, "Indexes") >= 2);
            Step($"customer constraints ({Rows(customer, "Constraints")})", Rows(customer, "Constraints") >= 3);
            Step($"customer referenced by ({Rows(customer, "Referenced by")})", Rows(customer, "Referenced by") == 1);
            Step($"customer triggers ({Rows(customer, "Triggers")})", Rows(customer, "Triggers") == 1);

            var line = await c.RequestAsync("catalog/describe", new JsonObject { ["connection_id"] = connId, ["name"] = OrderLine });
            Step($"order line columns ({Rows(line, "Columns")})", Rows(line, "Columns") == 5);
            Step($"order line foreign keys ({Rows(line, "Foreign keys")})", Rows(line, "Foreign keys") == 1);
            Step($"order line indexes ({Rows(line, "Indexes")})", Rows(line, "Indexes") >= 2);

            var addOne = functions.First(n => n!["name"]!.GetValue<string>() == "add_one" && (profile.AddOneIdentity is null || n["identity"]?.GetValue<string>() == profile.AddOneIdentity))!;
            var fn = await c.RequestAsync("catalog/describe", new JsonObject
            {
                ["connection_id"] = connId, ["database"] = db, ["schema"] = "dbbliss_cat", ["object"] = "add_one", ["kind"] = "function",
                ["identity"] = addOne["identity"]?.GetValue<string>(),
            });
            Step("function by tree node", fn["title"]!.GetValue<string>().Contains("add_one", StringComparison.Ordinal));

            string? missingCode = null;
            try
            {
                await c.RequestAsync("catalog/describe", new JsonObject { ["connection_id"] = connId, ["name"] = profile.Qualify("dbbliss_cat.no_such_thing") });
            }
            catch (BackendErrorException ex)
            {
                missingCode = ex.Error["code"]?.ToString();
            }
            Step($"missing object -> error {missingCode}", missingCode == "1007");

            // Script, drop, run the scripts, script again.
            var objects = new (string Typed, JsonObject? Node)[]
            {
                (profile.Qualify("dbbliss_cat.customer"), null),
                (OrderLine, null),
                (profile.Qualify("dbbliss_cat.customer_names"), null),
                ("", new JsonObject { ["database"] = db, ["schema"] = "dbbliss_cat", ["object"] = "add_one", ["kind"] = "function", ["identity"] = addOne["identity"]?.GetValue<string>() }),
                (profile.Qualify("dbbliss_cat.noop"), null),
            };
            async Task<string[]> ScriptAll()
            {
                var texts = new List<string>();
                foreach (var (typed, node) in objects)
                {
                    var p = node?.DeepClone().AsObject() ?? new JsonObject { ["name"] = typed };
                    p["connection_id"] = connId;
                    texts.Add((await c.RequestAsync("catalog/script", p))["text"]!.GetValue<string>());
                }
                return [.. texts];
            }
            var first = await ScriptAll();
            Step("scripts are CREATE statements", first.All(t => t.Contains("CREATE", StringComparison.OrdinalIgnoreCase)));
            foreach (var drop in profile.DropScriptedObjectsSql) await profile.ExecAsync(observer, drop);
            for (var i = 0; i < first.Length; i++) await RunScriptAsync(c, connId, first[i], "s" + i);
            var second = await ScriptAll();
            for (var i = 0; i < first.Length; i++) Step($"round trip {i}", first[i] == second[i]);
            if (!ok)
            {
                for (var i = 0; i < first.Length; i++)
                {
                    if (first[i] != second[i]) steps.Add($"--- script {i} before:\n{first[i]}\n--- after:\n{second[i]}");
                }
            }
            return Result(name, ok ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
        }
        finally
        {
            await profile.DropCatalogFixtureAsync(observer);
        }
    }

    /// <summary>
    /// The catalog has a session of its own: it answers while the user's session is held by a paused
    /// result and while the user's transaction is open (PostgreSQL: aborted), and that session ends
    /// with the connection.
    /// </summary>
    private async Task<ScenarioResult> CatalogIndependentOfUserSession()
    {
        const string name = "catalog_independent_of_user_session";
        await using var observer = await profile.OpenObserverAsync();
        var (c, connId, userSession) = await StartAsync();
        await using var _c = c;
        var steps = new List<string>();
        var ok = true;
        void Step(string what, bool good)
        {
            steps.Add(what + (good ? "" : " (WRONG)"));
            ok &= good;
        }

        await c.RequestAsync("execute", new JsonObject { ["connection_id"] = connId, ["query_id"] = "p", ["sql"] = profile.StreamingSql, ["window"] = 100 });
        await c.WaitNotificationAsync(n => n["method"]?.GetValue<string>() == "query/paused", ExecutingTimeoutMs);
        var paused = await c.RequestAsync("catalog/children", new JsonObject { ["connection_id"] = connId });
        Step("catalog answers while the user's result is paused", paused["nodes"]!.AsArray().Count > 0);
        var catalogSession = paused["catalog_session"]!.GetValue<string>();
        Step("it is another session", catalogSession != userSession);
        await c.RequestAsync("cancel", new JsonObject { ["query_id"] = "p" });
        await c.WaitDoneAsync("p", DoneTimeoutMs);

        await Execute(c, connId, "b", profile.TypedBeginSql);
        await c.WaitDoneAsync("b", DoneTimeoutMs);
        if (profile.Engine == "postgres")
        {
            await Execute(c, connId, "f", "SELECT 1/0");
            var failed = (await c.WaitDoneAsync("f", DoneTimeoutMs))["params"]!["transaction"]?.GetValue<string>();
            Step($"user's transaction is {failed}", failed == "aborted");
        }
        var inTx = await c.RequestAsync("catalog/children", new JsonObject { ["connection_id"] = connId });
        Step("catalog answers in the user's open transaction", inTx["nodes"]!.AsArray().Count > 0);
        Step("same catalog session", inTx["catalog_session"]!.GetValue<string>() == catalogSession);

        var before = await profile.ViewAsync(observer, catalogSession);
        Step($"catalog session exists on the server ({before.Detail})", !before.Detail.Contains("session gone", StringComparison.Ordinal));
        await c.RequestAsync("disconnect", new JsonObject { ["connection_id"] = connId, ["rollback"] = true });
        var after = await profile.ViewAsync(observer, catalogSession);
        Step($"catalog session ended with the connection ({after.Detail})", after.Detail.Contains("session gone", StringComparison.Ordinal));
        return Result(name, ok ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }
}
