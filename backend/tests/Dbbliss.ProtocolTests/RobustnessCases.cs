using System.Text.Json.Nodes;
using Dbbliss.Backend.Rpc;

namespace Dbbliss.ProtocolTests;

/// <summary>
/// What the backend does with requests that are wrong: it answers with an error that names the
/// problem and keeps serving. Nothing here needs a database.
/// </summary>
public static class RobustnessCases
{
    private static int Code(JsonObject response) => response["error"]?["code"]?.GetValue<int>() ?? throw new TestFailure($"expected an error, got {response.ToJsonString()}");

    private static void Expect(JsonObject response, int code, string what)
    {
        var actual = response["error"]?["code"]?.GetValue<int>();
        if (actual != code) throw new TestFailure($"{what}: expected error {code}, got {response.ToJsonString()}");
        if (string.IsNullOrWhiteSpace(response["error"]?["message"]?.GetValue<string>())) throw new TestFailure($"{what}: the error has no message");
    }

    public static async Task MalformedRequests()
    {
        await using var h = new Harness();

        h.SendRaw("{not json");
        Expect(await h.WaitForAsync(m => m["error"]?["code"]?.GetValue<int>() == RpcErrors.ParseError, "a parse error"), RpcErrors.ParseError, "invalid JSON");

        h.SendRaw("[1,2]");
        Expect(await h.WaitForAsync(m => m["error"]?["code"]?.GetValue<int>() == RpcErrors.InvalidRequest && m["id"] is null, "an invalid request error"),
            RpcErrors.InvalidRequest, "JSON that is not an object");

        h.SendRaw("{\"jsonrpc\":\"2.0\",\"id\":7}");
        var missing = await h.WaitForAsync(m => m["id"]?.GetValue<int>() == 7, "the response to a request without a method");
        Expect(missing, RpcErrors.InvalidRequest, "a request without a method");

        h.SendRaw("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"no/such/method\"}");
        var unknown = await h.WaitForAsync(m => m["id"]?.GetValue<int>() == 8, "the response to an unknown method");
        Expect(unknown, RpcErrors.MethodNotFound, "an unknown method");
        if (!(unknown["error"]!["message"]!.GetValue<string>().Contains("no/such/method", StringComparison.Ordinal))) throw new TestFailure("the error does not name the method");

        // Blank lines are ignored, and a request without an id is performed but not answered.
        h.SendRaw("");
        h.SendRaw("   ");
        h.SendRaw("{\"method\":\"initialize\"}");
        var errorsBefore = h.Messages.Count(m => m["error"] is not null);

        // The backend still serves, and nothing was written for the blank lines or the notification.
        var init = await h.ResultAsync("initialize");
        if (init["protocol"] is null) throw new TestFailure("initialize returned no protocol version");
        var all = h.Messages;
        if (all.Count(m => m["error"] is not null) != errorsBefore) throw new TestFailure("a blank line or a notification produced an error");
        if (all.Any(m => m["result"] is not null && m["id"] is null)) throw new TestFailure("a request without an id was answered");
    }

    public static async Task InvalidParameters()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();

        Expect(await h.CallAsync("connect", new JsonObject { ["engine"] = "nope", ["connection_string"] = "x" }), RpcErrors.InvalidParams, "unknown engine");
        Expect(await h.CallAsync("connect", new JsonObject { ["connection_string"] = "x" }), RpcErrors.InvalidParams, "connect without an engine");
        Expect(await h.CallAsync("execute", new JsonObject { ["connection_id"] = "c999", ["sql"] = "SELECT 1" }), RpcErrors.UnknownConnection, "unknown connection");
        Expect(await h.CallAsync("execute", new JsonObject { ["connection_id"] = c }), RpcErrors.InvalidParams, "execute without sql");
        Expect(await h.CallAsync("execute", new JsonObject { ["connection_id"] = c, ["sql"] = "SELECT 1", ["window"] = 0 }), RpcErrors.InvalidParams, "window 0");
        Expect(await h.CallAsync("execute", new JsonObject
        {
            ["connection_id"] = c, ["sql"] = "SELECT 1", ["window"] = 10,
            ["export"] = new JsonObject { ["path"] = Path.Combine(Path.GetTempPath(), "never.csv") },
        }), RpcErrors.InvalidParams, "an export with a window");
        Expect(await h.CallAsync("fetch", new JsonObject { ["query_id"] = "q" }), RpcErrors.InvalidParams, "fetch without rows");
        Expect(await h.CallAsync("fetch", new JsonObject { ["query_id"] = "q", ["rows"] = 0 }), RpcErrors.InvalidParams, "fetch 0 rows");
        Expect(await h.CallAsync("script/split", new JsonObject { ["engine"] = "fake" }), RpcErrors.InvalidParams, "script/split without text");
        Expect(await h.CallAsync("script/split", new JsonObject { ["engine"] = "nope", ["text"] = "SELECT 1" }), RpcErrors.InvalidParams, "script/split with an unknown engine");

        // A request about a query that is not running is answered, not an error.
        foreach (var method in new[] { "cancel", "fetch" })
        {
            var result = await h.ResultAsync(method, new JsonObject { ["query_id"] = "gone", ["rows"] = 5 });
            if (result["state"]?.GetValue<string>() != "not_running") throw new TestFailure($"{method} of a query that is not running: {result.ToJsonString()}");
        }

        // A query id may be used once at a time.
        await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q", ["sql"] = "STUCK" });
        await h.Server.StuckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var other = await h.ConnectAsync();
        Expect(await h.CallAsync("execute", new JsonObject { ["connection_id"] = other, ["query_id"] = "q", ["sql"] = "SELECT 1" }),
            RpcErrors.InvalidParams, "a query id that is in use");
        h.Server.StuckRelease.SetResult();
        await h.NotificationAsync("query/done", "q");

        // A parameter of the wrong type is an error response too, and the backend goes on.
        Expect(await h.CallAsync("execute", new JsonObject { ["connection_id"] = c, ["sql"] = "SELECT 1", ["window"] = "ten" }), RpcErrors.Internal, "a window that is not a number");
        await h.ResultAsync("initialize");
    }

    public static async Task ConnectFailureIsADatabaseError()
    {
        await using var h = new Harness();
        var response = await h.CallAsync("connect", new JsonObject { ["engine"] = "fake", ["connection_string"] = "refuse" });
        Expect(response, RpcErrors.Database, "a refused connection");
        if (!response["error"]!["message"]!.GetValue<string>().Contains("connection refused", StringComparison.Ordinal)) throw new TestFailure("the server's message is not passed on");
        if (!h.Server.Open.IsEmpty) throw new TestFailure("a failed connect left a session open");
        await h.ConnectAsync();
    }

    public static async Task RequestsAreRefusedWhileShuttingDown()
    {
        await using var h = new Harness();
        await h.ResultAsync("shutdown");
        await h.Backend.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Expect(await h.CallAsync("initialize"), RpcErrors.ShuttingDown, "a request after shutdown");
        Expect(await h.CallAsync("connect", new JsonObject { ["engine"] = "fake", ["connection_string"] = "x" }), RpcErrors.ShuttingDown, "connect after shutdown");
        var again = await h.CallAsync("shutdown");
        if (again["error"] is not null) throw new TestFailure($"a second shutdown was refused: {again.ToJsonString()}");
    }
}
