using System.Text.Json.Nodes;

namespace Dbbliss.CancelTests;

/// <summary>Phase 5: the names and columns completion reads, from a real server.</summary>
public sealed partial class Scenarios
{
    /// <summary>
    /// catalog/names lists the tables, views, functions and procedures of the database in schema/name order, leaves the system
    /// schemas out unless asked, says when it stopped at the limit; catalog/columns gives the columns of a table or view.
    /// </summary>
    private async Task<ScenarioResult> CompletionNamesAndColumns()
    {
        var (steps, step, ok) = Steps();
        await using var observer = await profile.OpenObserverAsync();
        await profile.CreateScratchAsync(observer, Scratch, 10);
        try
        {
            var (c, conn, _) = await StartAsync(connectionString: profile.ScratchConnectionString(Scratch));
            await using var _c = c;
            var schema = profile.Engine == "postgres" ? "public" : "dbo";
            var fn = profile.Engine == "postgres"
                ? "CREATE FUNCTION public.completion_probe_fn() RETURNS int LANGUAGE sql AS 'SELECT 1'"
                : "CREATE FUNCTION dbo.completion_probe_fn() RETURNS int AS BEGIN RETURN 1 END";
            var proc = profile.Engine == "postgres"
                ? "CREATE PROCEDURE public.completion_probe_proc() LANGUAGE sql AS 'SELECT 1'"
                : "CREATE PROCEDURE dbo.completion_probe_proc AS SELECT 1";
            await using (var scratch = await profile.OpenObserverAsync(Scratch))
            {
                await profile.ExecAsync(scratch, fn);
                await profile.ExecAsync(scratch, proc);
            }

            var names = await c.RequestAsync("catalog/names", new JsonObject { ["connection_id"] = conn });
            var items = names["names"]!.AsArray().Select(n => (Schema: n!["schema"]!.GetValue<string>(), Name: n["name"]!.GetValue<string>(), Kind: n["kind"]!.GetValue<string>())).ToList();
            bool Has(string name, string kind) => items.Any(i => i.Name == name && i.Kind == kind && i.Schema == schema);
            step($"tables, view, function, procedure are listed ({items.Count} names)",
                Has("bkp_probe", "table") && Has("parent", "table") && Has("v_probe", "view") && Has("completion_probe_fn", "function") && Has("completion_probe_proc", "procedure"));
            step("system schemas are left out", items.All(i => i.Schema is not ("pg_catalog" or "information_schema" or "sys" or "INFORMATION_SCHEMA")));
            step("grouped by schema", items.Select(i => i.Schema).Distinct().Count() == items.Select(i => i.Schema).Aggregate(new List<string>(), (l, x) => { if (l.Count == 0 || l[^1] != x) l.Add(x); return l; }).Count);
            step("not truncated", !names["truncated"]!.GetValue<bool>());

            var withSystem = await c.RequestAsync("catalog/names", new JsonObject { ["connection_id"] = conn, ["include_system"] = true });
            step($"include_system adds system objects ({withSystem["names"]!.AsArray().Count} names)", withSystem["names"]!.AsArray().Count > items.Count);

            var limited = await c.RequestAsync("catalog/names", new JsonObject { ["connection_id"] = conn, ["limit"] = 2 });
            step("a limit stops the list and says so", limited["names"]!.AsArray().Count == 2 && limited["truncated"]!.GetValue<bool>());
            var exact = await c.RequestAsync("catalog/names", new JsonObject { ["connection_id"] = conn, ["limit"] = items.Count });
            step("a list of exactly the limit is not truncated", exact["names"]!.AsArray().Count == items.Count && !exact["truncated"]!.GetValue<bool>());

            var columns = await c.RequestAsync("catalog/columns", new JsonObject { ["connection_id"] = conn, ["name"] = "bkp_probe" });
            var cols = columns["columns"]!.AsArray().Select(x => (Name: x!["name"]!.GetValue<string>(), Type: x["type"]!.GetValue<string>())).ToList();
            step($"columns of a table in column order ({string.Join(", ", cols.Select(x => x.Name + " " + x.Type))})",
                cols.Count == 2 && cols[0].Name == "id" && cols[0].Type.StartsWith("int", StringComparison.OrdinalIgnoreCase) && cols[1].Name == "pad");
            step("resolved to its schema and name", columns["schema"]!.GetValue<string>() == schema && columns["name"]!.GetValue<string>() == "bkp_probe");
            var view = await c.RequestAsync("catalog/columns", new JsonObject { ["connection_id"] = conn, ["name"] = "v_probe" });
            step("columns of a view", view["columns"]!.AsArray().Count == 1 && view["columns"]![0]!["name"]!.GetValue<string>() == "id");
            var qualified = await c.RequestAsync("catalog/columns", new JsonObject { ["connection_id"] = conn, ["name"] = $"{schema}.parent" });
            step("a qualified name", qualified["name"]!.GetValue<string>() == "parent");
            step("a name the server does not know is a catalog error", await ErrorCodeAsync(c.RequestAsync("catalog/columns", new JsonObject { ["connection_id"] = conn, ["name"] = "no_such_table_here" })) == 1007);
            step("a function has no columns to complete", await ErrorCodeAsync(c.RequestAsync("catalog/columns", new JsonObject { ["connection_id"] = conn, ["name"] = "completion_probe_fn" })) == 1007);
        }
        finally
        {
            await profile.DropScratchAsync(observer, Scratch);
        }
        return Result("completion_names_and_columns", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }
}
