using System.Text.Json.Nodes;
using Dbbliss.Backend.Rpc;
using Dbbliss.ProtocolTests;

// Protocol tests: the properties of spec/quint/client.qnt, checked against the real Backend with a
// fake engine. No database needed.
//
//   dotnet run --project backend/tests/Dbbliss.ProtocolTests -- [--test name,...] [--verbose]
//
// --verbose echoes the backend's log (stderr).

var only = args.SkipWhile(a => a != "--test").Skip(1).FirstOrDefault()?.Split(',');
var gate = StderrGate.Install(echo: args.Contains("--verbose"));

var tests = new (string Name, string Bug, Func<Task> Run)[]
{
    ("query_reports_server_state", "", QueryReportsServerState),
    ("api_begin_blocks_disconnect", "", ApiBeginBlocksDisconnect),
    ("shutdown_closes_every_session", "", ShutdownClosesEverySession),
    ("typed_begin_blocks_disconnect", "1: NoSilentRollback", TypedBeginBlocksDisconnect),
    ("no_stale_report_after_rollback", "2: HonestTxView", NoStaleReportAfterRollback),
};

var failed = 0;
var ran = 0;
foreach (var (name, bug, run) in tests)
{
    if (only is not null && !only.Contains(name)) continue;
    ran++;
    Console.Write($"{name,-34} ");
    try
    {
        await run();
        Console.WriteLine("PASS");
    }
    catch (Exception ex)
    {
        failed++;
        gate.Release();
        var detail = ex is TestFailure ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
        Console.WriteLine($"FAIL  {(bug.Length > 0 ? $"[bug {bug}] " : "")}{detail}");
    }
}
Console.WriteLine();
Console.WriteLine($"{ran} tests, {failed} failed");
return failed == 0 && ran > 0 ? 0 : 1;

// The probe after every statement reports what the server has, including typed SQL.
static async Task QueryReportsServerState()
{
    await using var h = new Harness();
    var c = await h.ConnectAsync();
    foreach (var (sql, expected) in new[] { ("BEGIN", "active"), ("FAIL", "aborted"), ("ROLLBACK", "none") })
    {
        var q = "q-" + sql;
        await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = q, ["sql"] = sql });
        var done = await h.NotificationAsync("query/done", q);
        var reported = done["params"]!["transaction"]?.GetValue<string>();
        if (reported != expected) throw new TestFailure($"after {sql}: query/done says {reported}, server has {expected}");
    }
}

// Control for typed_begin_blocks_disconnect: the guard works for a transaction the API opened.
static async Task ApiBeginBlocksDisconnect()
{
    await using var h = new Harness();
    var c = await h.ConnectAsync();
    await h.ResultAsync("transaction/begin", new JsonObject { ["connection_id"] = c });
    var response = await h.CallAsync("disconnect", new JsonObject { ["connection_id"] = c, ["rollback"] = false });
    ExpectError(response, RpcErrors.TransactionOpen, "disconnect without rollback");
    if (!h.Server.ClosedWithTransaction.IsEmpty) throw new TestFailure("the session was closed with its transaction");
}

// NoOrphanSession, backend side: shutdown closes every session it opened.
static async Task ShutdownClosesEverySession()
{
    await using var h = new Harness();
    var c1 = await h.ConnectAsync();
    await h.ConnectAsync();
    await h.ResultAsync("transaction/begin", new JsonObject { ["connection_id"] = c1 });
    await h.CloseAsync();
    if (!h.Server.Open.IsEmpty) throw new TestFailure($"sessions still open after shutdown: {string.Join(", ", h.Server.Open.Keys)}");
}

// Bug 1 (README): a BEGIN typed as SQL must block a plain disconnect the same way an API begin
// does. Today the guard reads the engine's own transaction object, which only knows API begins,
// so the disconnect goes through and closing the session silently rolls the transaction back.
static async Task TypedBeginBlocksDisconnect()
{
    await using var h = new Harness();
    var c = await h.ConnectAsync();
    await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q1", ["sql"] = "BEGIN" });
    await h.NotificationAsync("query/done", "q1");
    var response = await h.CallAsync("disconnect", new JsonObject { ["connection_id"] = c, ["rollback"] = false });
    if (!h.Server.ClosedWithTransaction.IsEmpty)
    {
        throw new TestFailure("disconnect without rollback closed a session with an open transaction (silent rollback)");
    }
    ExpectError(response, RpcErrors.TransactionOpen, "disconnect without rollback");
}

// Bug 2 (README): the lease is released before query/done is written. Hold the query between the
// two (at its "query … completed" log line, which sits in that window), run a rollback on the same
// connection, then let query/done out. The last transaction state the client hears must be what
// the server has. Today the rollback's "none" arrives first and the stale "active" last.
static async Task NoStaleReportAfterRollback()
{
    await using var h = new Harness();
    var c = await h.ConnectAsync();
    var held = StderrGate.Current.Arm("query q1 completed");
    await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q1", ["sql"] = "BEGIN" });
    if (await Task.WhenAny(held, Task.Delay(TimeSpan.FromSeconds(5))) != held)
    {
        throw new TestFailure("the query never logged \"query q1 completed\"; the hook this test relies on moved");
    }
    var rollbackId = h.Send("transaction/rollback", new JsonObject { ["connection_id"] = c });
    await h.WaitForAsync(m => m["id"]?.GetValue<int>() == rollbackId, "response to transaction/rollback");
    StderrGate.Current.Release();
    await h.NotificationAsync("query/done", "q1");

    var view = ClientView.Transactions(h.Messages, new Dictionary<int, string> { [rollbackId] = c }, new Dictionary<string, string> { ["q1"] = c });
    var server = h.Server.Open.Values.Single().State.ToString().ToLowerInvariant();
    if (view[c] != server) throw new TestFailure($"the client's last report says {view[c]}, the server has {server}");
}

static void ExpectError(JsonObject response, int code, string what)
{
    var actual = response["error"]?["code"]?.GetValue<int>();
    if (actual != code) throw new TestFailure($"{what}: expected error {code}, got {response.ToJsonString()}");
}
