using System.Text.Json.Nodes;
using Dbbliss.Backend;
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
    ("execute_right_after_query_done", "lease released too late", ExecuteRightAfterQueryDone),
    ("execute_right_after_transaction_call", "lease released too late", ExecuteRightAfterTransactionCall),
    ("instances_dead_ids", "", InstancesDeadIds),
    ("instances_prune", "", InstancesPrune),
    ("splitter_cases", "", () => { SplitterCases.Run(); return Task.CompletedTask; }),
    ("script_split_rpc", "", ScriptSplitRpc),
    ("error_line_in_buffer", "", ErrorLineInBuffer),
    ("paging_pauses_and_fetch_resumes", "", PagingPausesAndFetchResumes),
    ("paused_query_cancels_without_loss", "", PausedQueryCancelsWithoutLoss),
    ("paging_without_window_streams_all", "", PagingWithoutWindowStreamsAll),
    ("export_writes_csv", "", ExportWritesCsv),
    ("export_refuses_existing_file", "", ExportRefusesExistingFile),
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

// A client may send its next request the moment it reads query/done. The connection must be free
// by then. Hold the backend inside the query/done write, send execute, then let the write finish.
static async Task ExecuteRightAfterQueryDone()
{
    await using var h = new Harness();
    var c = await h.ConnectAsync();
    var held = h.Stdout.Arm("\"query/done\"");
    await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q1", ["sql"] = "SELECT 1" });
    await held.WaitAsync(TimeSpan.FromSeconds(5));
    await h.NotificationAsync("query/done", "q1");
    var next = h.CallAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q2", ["sql"] = "SELECT 1" });
    await Task.Delay(200); // the request is read and dispatched while the write is held
    h.Stdout.Release();
    var response = await next;
    if (response["error"] is not null) throw new TestFailure($"execute right after query/done was refused: {response["error"]!.ToJsonString()}");
}

// The same for the response to a transaction call.
static async Task ExecuteRightAfterTransactionCall()
{
    await using var h = new Harness();
    var c = await h.ConnectAsync();
    var held = h.Stdout.Arm("\"transaction\":\"active\"");
    var beginId = h.Send("transaction/begin", new JsonObject { ["connection_id"] = c });
    await held.WaitAsync(TimeSpan.FromSeconds(5));
    await h.WaitForAsync(m => m["id"]?.GetValue<int>() == beginId, "response to transaction/begin");
    var next = h.CallAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q1", ["sql"] = "SELECT 1" });
    await Task.Delay(200);
    h.Stdout.Release();
    var response = await next;
    if (response["error"] is not null) throw new TestFailure($"execute right after the begin response was refused: {response["error"]!.ToJsonString()}");
}

// The registry that decides which sessions the orphan sweep may end (decision 19). A false "dead"
// would terminate a live backend's session, so every way of being alive is checked.
static Task InstancesDeadIds()
{
    var dir = Directory.CreateTempSubdirectory("dbbliss-instances-");
    try
    {
        var instancesDir = Path.Combine(dir.FullName, "instances");
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var selfStart = self.StartTime.ToUniversalTime();
        const int NoSuchPid = 0x7FFFFFF0;
        Instances.Write(instancesDir, "gone", NoSuchPid, DateTime.UtcNow);
        Instances.Write(instancesDir, "alive", self.Id, selfStart);
        Instances.Write(instancesDir, "reused", self.Id, selfStart.AddHours(-1));
        Instances.Write(instancesDir, "me", NoSuchPid, DateTime.UtcNow);
        File.WriteAllText(Path.Combine(instancesDir, "garbage.json"), "{ not json");

        var dead = new Instances(dir.FullName, id: "me").DeadIds();
        var expected = new HashSet<string> { "gone", "reused" };
        if (!dead.SetEquals(expected)) throw new TestFailure($"dead ids: {string.Join(",", dead.Order())}, expected gone,reused");
        return Task.CompletedTask;
    }
    finally
    {
        dir.Delete(recursive: true);
    }
}

static Task InstancesPrune()
{
    var dir = Directory.CreateTempSubdirectory("dbbliss-instances-");
    try
    {
        var instancesDir = Path.Combine(dir.FullName, "instances");
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        const int NoSuchPid = 0x7FFFFFF0;
        Instances.Write(instancesDir, "old_dead", NoSuchPid, DateTime.UtcNow);
        Instances.Write(instancesDir, "new_dead", NoSuchPid, DateTime.UtcNow);
        Instances.Write(instancesDir, "old_alive", self.Id, self.StartTime.ToUniversalTime());
        var old = DateTime.UtcNow - Instances.KeepDead - TimeSpan.FromDays(1);
        File.SetLastWriteTimeUtc(Path.Combine(instancesDir, "old_dead.json"), old);
        File.SetLastWriteTimeUtc(Path.Combine(instancesDir, "old_alive.json"), old);

        new Instances(dir.FullName).Prune();
        var left = Directory.GetFiles(instancesDir).Select(Path.GetFileNameWithoutExtension).Order().ToArray();
        if (!left.SequenceEqual(["new_dead", "old_alive"])) throw new TestFailure($"left after prune: {string.Join(",", left)}");
        return Task.CompletedTask;
    }
    finally
    {
        dir.Delete(recursive: true);
    }
}

// script/split needs an engine but no connection, and positions are 0-based.
static async Task ScriptSplitRpc()
{
    await using var h = new Harness();
    var result = await h.ResultAsync("script/split", new JsonObject { ["engine"] = "fake", ["text"] = "select 1;\n  select 2;" });
    var statements = result["statements"]!.AsArray();
    if (statements.Count != 2) throw new TestFailure($"expected 2 statements, got {result.ToJsonString()}");
    var second = statements[1]!;
    if (second["text"]!.GetValue<string>() != "select 2;"
        || second["start"]!["line"]!.GetValue<int>() != 1 || second["start"]!["col"]!.GetValue<int>() != 2
        || second["end"]!["line"]!.GetValue<int>() != 1 || second["end"]!["col"]!.GetValue<int>() != 11
        || second["repeat"]!.GetValue<int>() != 1)
    {
        throw new TestFailure($"second statement is wrong: {second.ToJsonString()}");
    }
    ExpectError(await h.CallAsync("script/split", new JsonObject { ["engine"] = "nope", ["text"] = "" }), RpcErrors.InvalidParams, "unknown engine");
    ExpectError(await h.CallAsync("script/split", new JsonObject { ["engine"] = "fake" }), RpcErrors.InvalidParams, "missing text");
}

// An error's place in the source buffer: the statement's first line plus the engine's line. The
// engine here reports a character offset, as PostgreSQL does, including after an astral character.
static async Task ErrorLineInBuffer()
{
    await using var h = new Harness();
    var c = await h.ConnectAsync();
    foreach (var (sql, offset, line, bufferLine) in new[] { ("select 1,\n2,\nPOSFAIL", 10, 3, 13), ("select '😀'\n, POSFAIL", 0, 2, 2), ("POSFAIL", 4, 1, 5) })
    {
        var q = "q" + offset;
        await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = q, ["sql"] = sql, ["line_offset"] = offset });
        var error = (await h.NotificationAsync("query/done", q))["params"]!["error"];
        if (error?["line"]?.GetValue<int>() != line || error["buffer_line"]?.GetValue<int>() != bufferLine)
        {
            throw new TestFailure($"{sql.Replace("\n", "\\n")} at offset {offset}: expected line {line} and buffer line {bufferLine}, got {error?.ToJsonString()}");
        }
    }
}

static int RowsReceived(Harness h, string queryId) =>
    h.Messages.Where(m => m["method"]?.GetValue<string>() == "query/rows" && m["params"]?["query_id"]?.GetValue<string>() == queryId)
        .Sum(m => m["params"]!["rows"]!.AsArray().Count);

static async Task<JsonObject> WaitPausedAsync(Harness h, string queryId, int count)
{
    var seen = 0;
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    while (DateTime.UtcNow < deadline)
    {
        var paused = h.Messages.Where(m => m["method"]?.GetValue<string>() == "query/paused" && m["params"]?["query_id"]?.GetValue<string>() == queryId).ToArray();
        seen = paused.Length;
        if (seen >= count) return paused[count - 1];
        await Task.Delay(20);
    }
    throw new TestFailure($"expected {count} query/paused for {queryId}, saw {seen}");
}

// Pull paging: the query sends its window and pauses; each fetch lets more through; nothing is lost
// and query/done comes only after the last row.
static async Task PagingPausesAndFetchResumes()
{
    await using var h = new Harness();
    var c = await h.ConnectAsync();
    await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q1", ["sql"] = "ROWS 3000", ["window"] = 1000 });
    var paused = await WaitPausedAsync(h, "q1", 1);
    if (paused["params"]!["rows_sent"]!.GetValue<long>() != 1000) throw new TestFailure($"paused after {paused["params"]!["rows_sent"]} rows, expected 1000");
    await Task.Delay(300);
    if (RowsReceived(h, "q1") != 1000) throw new TestFailure($"{RowsReceived(h, "q1")} rows arrived while paused, expected 1000");
    if (h.Messages.Any(m => m["method"]?.GetValue<string>() == "query/done")) throw new TestFailure("query/done arrived while paused");

    var granted = await h.ResultAsync("fetch", new JsonObject { ["query_id"] = "q1", ["rows"] = 1000 });
    if (granted["state"]?.GetValue<string>() != "granted") throw new TestFailure($"fetch: {granted.ToJsonString()}");
    await WaitPausedAsync(h, "q1", 2);
    await Task.Delay(300);
    if (RowsReceived(h, "q1") != 2000) throw new TestFailure($"{RowsReceived(h, "q1")} rows after the first fetch, expected 2000");

    await h.ResultAsync("fetch", new JsonObject { ["query_id"] = "q1", ["rows"] = 5000 });
    var done = await h.NotificationAsync("query/done", "q1");
    if (done["params"]!["status"]!.GetValue<string>() != "completed") throw new TestFailure($"status {done["params"]!["status"]}");
    if (RowsReceived(h, "q1") != 3000) throw new TestFailure($"{RowsReceived(h, "q1")} rows in the end, expected 3000");
    var late = await h.ResultAsync("fetch", new JsonObject { ["query_id"] = "q1", ["rows"] = 10 });
    if (late["state"]?.GetValue<string>() != "not_running") throw new TestFailure($"fetch after the end: {late.ToJsonString()}");
}

// A paused query is cancelled like any other: the cancel ends the credit wait, the rest drains, and
// every row the server sent is either delivered or reported as truncated (NoSilentLoss).
static async Task PausedQueryCancelsWithoutLoss()
{
    await using var h = new Harness();
    var c = await h.ConnectAsync();
    const int total = 50_000;
    await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q1", ["sql"] = $"ROWS {total}", ["window"] = 500 });
    await WaitPausedAsync(h, "q1", 1);
    await h.ResultAsync("cancel", new JsonObject { ["query_id"] = "q1" });
    var done = (await h.NotificationAsync("query/done", "q1"))["params"]!;
    var status = done["status"]!.GetValue<string>();
    var truncated = done["truncated_rows"]?.GetValue<long>() ?? 0;
    var received = RowsReceived(h, "q1");
    // The fake finishes its n rows whatever the cancel, so the status can also be "completed" when
    // the cancel arrives after the last row; either way no row may vanish.
    if (status is not ("cancelled" or "completed")) throw new TestFailure($"status {status}");
    if (received + truncated != total) throw new TestFailure($"{received} delivered + {truncated} truncated != {total} sent (silent loss)");
}

// No window: today's behaviour, everything streams.
static async Task PagingWithoutWindowStreamsAll()
{
    await using var h = new Harness();
    var c = await h.ConnectAsync();
    await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q1", ["sql"] = "ROWS 5000" });
    await h.NotificationAsync("query/done", "q1");
    if (RowsReceived(h, "q1") != 5000) throw new TestFailure($"{RowsReceived(h, "q1")} rows, expected 5000");
    if (h.Messages.Any(m => m["method"]?.GetValue<string>() == "query/paused")) throw new TestFailure("a query without a window paused");
}

static string TempCsv() => Path.Combine(Directory.CreateTempSubdirectory("dbbliss-export-").FullName, "out.csv");

// The backend writes the file; NULL is empty, quotes are doubled, and rows never go through Neovim.
static async Task ExportWritesCsv()
{
    await using var h = new Harness();
    var c = await h.ConnectAsync();
    var path = TempCsv();
    try
    {
        await h.ResultAsync("execute", new JsonObject
        {
            ["connection_id"] = c, ["query_id"] = "q1", ["sql"] = "ROWS 3", ["export"] = new JsonObject { ["path"] = path },
        });
        var done = (await h.NotificationAsync("query/done", "q1"))["params"]!;
        var export = done["export"];
        if (export?["complete"]?.GetValue<bool>() != true || export["rows"]?.GetValue<long>() != 3) throw new TestFailure($"export info: {export?.ToJsonString()}");
        if (RowsReceived(h, "q1") != 0) throw new TestFailure("an export sent rows to the client");
        var text = File.ReadAllText(path);
        const string expected = "id,v,n,q\r\n1,v1,,\"q\"\"x\"\r\n2,v2,,\"q\"\"x\"\r\n3,v3,,\"q\"\"x\"\r\n";
        if (text != expected) throw new TestFailure($"file is {text.Replace("\r", "\\r").Replace("\n", "\\n")}");
    }
    finally
    {
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}

static async Task ExportRefusesExistingFile()
{
    await using var h = new Harness();
    var c = await h.ConnectAsync();
    var path = TempCsv();
    try
    {
        File.WriteAllText(path, "precious");
        var refused = await h.CallAsync("execute", new JsonObject
        {
            ["connection_id"] = c, ["query_id"] = "q1", ["sql"] = "ROWS 1", ["export"] = new JsonObject { ["path"] = path },
        });
        ExpectError(refused, RpcErrors.InvalidParams, "export over an existing file");
        if (File.ReadAllText(path) != "precious") throw new TestFailure("the existing file was changed");
        // The refusal released the connection.
        await h.ResultAsync("execute", new JsonObject
        {
            ["connection_id"] = c, ["query_id"] = "q2", ["sql"] = "ROWS 1", ["export"] = new JsonObject { ["path"] = path, ["overwrite"] = true },
        });
        await h.NotificationAsync("query/done", "q2");
        if (File.ReadAllText(path) != "id,v,n,q\r\n1,v1,,\"q\"\"x\"\r\n") throw new TestFailure("overwrite did not write the export");
    }
    finally
    {
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}

static void ExpectError(JsonObject response, int code, string what)
{
    var actual = response["error"]?["code"]?.GetValue<int>();
    if (actual != code) throw new TestFailure($"{what}: expected error {code}, got {response.ToJsonString()}");
}
