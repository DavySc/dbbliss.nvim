using System.Text.Json.Nodes;
using Dbbliss.Backend.Rpc;

namespace Dbbliss.ProtocolTests;

/// <summary>catalog/names and catalog/columns: what completion reads, through the catalog session.</summary>
public static class CompletionTests
{
    private static int Code(JsonObject response) => response["error"]?["code"]?.GetValue<int>() ?? 0;

    public static async Task NamesAreListedThroughTheCatalogSession()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        var r = await h.ResultAsync("catalog/names", new JsonObject { ["connection_id"] = c });
        var names = r["names"]!.AsArray();
        if (names.Count != 3 || names[0]!["schema"]!.GetValue<string>() != "public" || names[0]!["name"]!.GetValue<string>() != "orders" || names[0]!["kind"]!.GetValue<string>() != "table")
        {
            throw new TestFailure("names: " + r.ToJsonString());
        }
        if (r["truncated"]!.GetValue<bool>()) throw new TestFailure("three names are not truncated");
        if (r["limit"]!.GetValue<int>() != 20000) throw new TestFailure("the default limit is 20 000: " + r.ToJsonString());
        if (h.Engine.FakeCatalog.LastNames is not (null, false, 20000)) throw new TestFailure("defaults passed on: " + h.Engine.FakeCatalog.LastNames);
        var userSession = h.Server.Open.Keys.Order().First();
        if (r["catalog_session"]!.GetValue<string>() == userSession) throw new TestFailure("completion used the user's session");

        await h.ResultAsync("catalog/names", new JsonObject { ["connection_id"] = c, ["database"] = "other", ["include_system"] = true });
        if (h.Engine.FakeCatalog.LastNames is not ("other", true, 20000)) throw new TestFailure("database and include_system passed on: " + h.Engine.FakeCatalog.LastNames);
    }

    public static async Task NamesSayWhenTheyStoppedAtTheLimit()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        var r = await h.ResultAsync("catalog/names", new JsonObject { ["connection_id"] = c, ["limit"] = 2 });
        if (r["names"]!.AsArray().Count != 2 || !r["truncated"]!.GetValue<bool>() || r["limit"]!.GetValue<int>() != 2)
        {
            throw new TestFailure("two of three: " + r.ToJsonString());
        }
        var capped = await h.ResultAsync("catalog/names", new JsonObject { ["connection_id"] = c, ["limit"] = 500000 });
        if (capped["limit"]!.GetValue<int>() != 20000) throw new TestFailure("a larger limit is capped at 20 000: " + capped.ToJsonString());
        foreach (var bad in new JsonNode?[] { 0, -5, "many" })
        {
            var response = await h.CallAsync("catalog/names", new JsonObject { ["connection_id"] = c, ["limit"] = bad });
            if (Code(response) != RpcErrors.InvalidParams) throw new TestFailure($"limit {bad?.ToJsonString()}: {response.ToJsonString()}");
        }
    }

    public static async Task ColumnsOfATableTheServerResolves()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        var r = await h.ResultAsync("catalog/columns", new JsonObject { ["connection_id"] = c, ["name"] = "orders" });
        var columns = r["columns"]!.AsArray();
        if (r["schema"]!.GetValue<string>() != "public" || r["name"]!.GetValue<string>() != "orders"
            || columns.Count != 2 || columns[0]!["name"]!.GetValue<string>() != "id" || columns[0]!["type"]!.GetValue<string>() != "integer")
        {
            throw new TestFailure("columns: " + r.ToJsonString());
        }
        var missing = await h.CallAsync("catalog/columns", new JsonObject { ["connection_id"] = c, ["name"] = "missing" });
        if (Code(missing) != RpcErrors.Catalog) throw new TestFailure("a name the server does not know is a catalog error: " + missing.ToJsonString());
        var noName = await h.CallAsync("catalog/columns", new JsonObject { ["connection_id"] = c });
        if (Code(noName) != RpcErrors.InvalidParams) throw new TestFailure("a request without a name: " + noName.ToJsonString());
    }

    public static async Task FailuresAndMissingSupportAreReported()
    {
        await using (var h = new Harness())
        {
            var c = await h.ConnectAsync();
            await h.ResultAsync("catalog/names", new JsonObject { ["connection_id"] = c });
            var crash = await h.CallAsync("catalog/names", new JsonObject { ["connection_id"] = c, ["database"] = "crash" });
            if (Code(crash) != RpcErrors.Database) throw new TestFailure("a driver failure: " + crash.ToJsonString());
            if (h.Server.Open.Count != 1) throw new TestFailure("the broken catalog session should be closed");
            await h.ResultAsync("catalog/names", new JsonObject { ["connection_id"] = c });
            var unknown = await h.CallAsync("catalog/names", new JsonObject { ["connection_id"] = "nope" });
            if (Code(unknown) != RpcErrors.UnknownConnection) throw new TestFailure("unknown connection");
        }
        await using (var h = new Harness(new FakeEngine(new FakeServer(), "plain", hasCatalog: false)))
        {
            var c = (await h.ResultAsync("connect", new JsonObject { ["engine"] = "plain", ["connection_string"] = "x" }))["connection_id"]!.GetValue<string>();
            foreach (var method in new[] { "catalog/names", "catalog/columns" })
            {
                var response = await h.CallAsync(method, new JsonObject { ["connection_id"] = c, ["name"] = "t" });
                if (Code(response) != RpcErrors.InvalidParams || !response["error"]!["message"]!.GetValue<string>().Contains("no catalog support", StringComparison.Ordinal))
                {
                    throw new TestFailure($"{method} on an engine without a catalog: {response.ToJsonString()}");
                }
            }
        }
    }
}
