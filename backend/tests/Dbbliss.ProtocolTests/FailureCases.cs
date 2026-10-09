using System.Text.Json.Nodes;
using Dbbliss.Backend.Rpc;

namespace Dbbliss.ProtocolTests;

/// <summary>
/// The backend when something around it fails: a server that does not answer, a session that will
/// not close, a stdout that is gone, a disk that is full. Each case checks that the failure is
/// bounded, reported, and does not stop the backend from doing the rest of its job.
/// </summary>
public static class FailureCases
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static async Task Completes(Harness h, string what)
    {
        try
        {
            await h.Backend.Completion.WaitAsync(Wait);
        }
        catch (TimeoutException)
        {
            throw new TestFailure($"the backend did not finish shutting down: {what}");
        }
    }

    // A query the server never lets go of and a session that never closes must not hold the
    // shutdown up beyond its bounds (HLR-TX-4).
    public static async Task ShutdownIsBounded()
    {
        await using var h = new Harness();
        h.Backend.ShutdownQueryWait = TimeSpan.FromMilliseconds(300);
        h.Backend.ShutdownCloseWait = TimeSpan.FromMilliseconds(300);
        var c = await h.ConnectAsync();
        await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q", ["sql"] = "STUCK" });
        await h.Server.StuckStarted.Task.WaitAsync(Wait);
        h.Server.DisposeHangs = new TaskCompletionSource();
        var started = DateTime.UtcNow;
        await h.ResultAsync("shutdown");
        await Completes(h, "a stuck query and a session that does not close");
        var took = DateTime.UtcNow - started;
        if (took > TimeSpan.FromSeconds(3)) throw new TestFailure($"shutdown took {took.TotalSeconds:0.0} s with waits of 0.3 s");
        if (h.Server.CancelRequests < 1) throw new TestFailure("the running query was not cancelled at shutdown");
        h.Server.DisposeHangs.SetResult();
        h.Server.StuckRelease.SetResult();
    }

    // One session that cannot be closed must not stop the others from being closed.
    public static async Task ShutdownClosesEverySessionEvenIfOneFails()
    {
        await using var h = new Harness();
        var first = await h.ConnectAsync();
        var second = await h.ConnectAsync();
        await h.ResultAsync("execute", new JsonObject { ["connection_id"] = second, ["query_id"] = "tx", ["sql"] = "BEGIN" });
        await h.NotificationAsync("query/done", "tx");
        h.Server.DisposeThrows = true;
        await h.ResultAsync("shutdown");
        await Completes(h, "a session whose close throws");
        if (h.Server.DisposeAttempts < 2) throw new TestFailure($"only {h.Server.DisposeAttempts} of 2 sessions were asked to close ({first}, {second})");
    }

    // Server messages: while a query runs they go with it, otherwise as connection messages.
    public static async Task ServerMessagesReachTheClient()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "n", ["sql"] = "NOTICE careful" });
        var message = await h.WaitForAsync(m => m["method"]?.GetValue<string>() == "query/message", "the query message");
        var p = message["params"]!;
        if (p["text"]?.GetValue<string>() != "careful" || p["severity"]?.GetValue<string>() != "info" || p["query_id"]?.GetValue<string>() != "n"
            || p["number"]?.GetValue<int>() != 1 || p["line"]?.GetValue<int>() != 1)
        {
            throw new TestFailure("query/message is wrong: " + message.ToJsonString());
        }
        var done = await h.NotificationAsync("query/done", "n");
        var all = h.Messages.ToList();
        if (all.IndexOf(message) > all.IndexOf(done)) throw new TestFailure("the message came after query/done");

        await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "late", ["sql"] = "LATENOTICE afterwards" });
        await h.NotificationAsync("query/done", "late");
        var late = await h.WaitForAsync(m => m["method"]?.GetValue<string>() == "connection/message", "the connection message");
        if (late["params"]?["connection_id"]?.GetValue<string>() != c || late["params"]?["text"]?.GetValue<string>() != "afterwards")
        {
            throw new TestFailure("connection/message is wrong: " + late.ToJsonString());
        }
    }

    // A transaction probe that fails, or an engine that cannot ask, counts as an open transaction.
    public static async Task UnknownTransactionStateBlocksDisconnect()
    {
        foreach (var statement in new[] { "PROBEFAIL", "PROBEUNKNOWN" })
        {
            await using var h = new Harness();
            var c = await h.ConnectAsync();
            await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q", ["sql"] = statement });
            var done = await h.NotificationAsync("query/done", "q");
            var state = done["params"]!["transaction"]?.GetValue<string>();
            if (state != "unknown") throw new TestFailure($"{statement}: query/done says {state}, expected unknown");
            var refused = await h.CallAsync("disconnect", new JsonObject { ["connection_id"] = c });
            if (refused["error"]?["code"]?.GetValue<int>() != RpcErrors.TransactionOpen) throw new TestFailure($"{statement}: disconnect was not refused: {refused.ToJsonString()}");
            var status = await h.ResultAsync("transaction/status", new JsonObject { ["connection_id"] = c });
            if (status["transaction"]?.GetValue<string>() != "unknown") throw new TestFailure($"{statement}: transaction/status says {status.ToJsonString()}");
            var closed = await h.ResultAsync("disconnect", new JsonObject { ["connection_id"] = c, ["rollback"] = true });
            if (closed["rolled_back"]?.GetValue<bool>() != true) throw new TestFailure($"{statement}: rollback = true did not report a rollback");
        }
    }

    // disconnect with rollback = true ends an open transaction and says so; without one it says it did not.
    public static async Task DisconnectWithRollbackSaysWhatItDid()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        await h.ResultAsync("transaction/begin", new JsonObject { ["connection_id"] = c });
        var closed = await h.ResultAsync("disconnect", new JsonObject { ["connection_id"] = c, ["rollback"] = true });
        if (closed["rolled_back"]?.GetValue<bool>() != true) throw new TestFailure("an open transaction was closed without saying so");
        var plain = await h.ConnectAsync();
        var none = await h.ResultAsync("disconnect", new JsonObject { ["connection_id"] = plain });
        if (none["rolled_back"]?.GetValue<bool>() != false) throw new TestFailure("a plain disconnect claimed a rollback");
        // A begin that the server refuses is reported as a database error, not as an internal one.
        var third = await h.ConnectAsync();
        h.Server.BeginThrows = true;
        var failed = await h.CallAsync("transaction/begin", new JsonObject { ["connection_id"] = third });
        if (failed["error"]?["code"]?.GetValue<int>() != RpcErrors.Database) throw new TestFailure("a failed begin: " + failed.ToJsonString());
    }

    // execute without a query id gets one; an export can overwrite; a rejected execute does not leave
    // an export file or a busy connection behind.
    public static async Task ExecuteOptions()
    {
        var dir = Directory.CreateTempSubdirectory("dbbliss-exec").FullName;
        try
        {
            await using var h = new Harness();
            var c = await h.ConnectAsync();
            var started = await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["sql"] = "SELECT 1" });
            var generated = started["query_id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(generated)) throw new TestFailure("no query id was generated");
            await h.NotificationAsync("query/done", generated);

            var path = Path.Combine(dir, "out.csv");
            await File.WriteAllTextAsync(path, "old");
            await h.ResultAsync("execute", new JsonObject
            {
                ["connection_id"] = c, ["query_id"] = "ow", ["sql"] = "ROWS 2",
                ["export"] = new JsonObject { ["path"] = path, ["overwrite"] = true },
            });
            var done = await h.NotificationAsync("query/done", "ow");
            if (done["params"]?["export"]?["rows"]?.GetValue<long>() != 2) throw new TestFailure("export rows: " + done.ToJsonString());
            if (!(await File.ReadAllTextAsync(path)).StartsWith("id,v,n,q\r\n1,", StringComparison.Ordinal)) throw new TestFailure("the export was not overwritten");

            // A duplicate query id with an export is refused, and the connection is free afterwards.
            await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "stuck", ["sql"] = "STUCK" });
            await h.Server.StuckStarted.Task.WaitAsync(Wait);
            var other = await h.ConnectAsync();
            var dup = await h.CallAsync("execute", new JsonObject
            {
                ["connection_id"] = other, ["query_id"] = "stuck", ["sql"] = "SELECT 1",
                ["export"] = new JsonObject { ["path"] = Path.Combine(dir, "dup.csv") },
            });
            if (dup["error"]?["code"]?.GetValue<int>() != RpcErrors.InvalidParams) throw new TestFailure("duplicate query id: " + dup.ToJsonString());
            h.Server.StuckRelease.SetResult();
            await h.NotificationAsync("query/done", "stuck");
            await h.ResultAsync("execute", new JsonObject { ["connection_id"] = other, ["query_id"] = "free", ["sql"] = "SELECT 1" });
            await h.NotificationAsync("query/done", "free");

            // A path that cannot be written: the execute fails and the connection is free.
            var bad = await h.CallAsync("execute", new JsonObject
            {
                ["connection_id"] = other, ["query_id"] = "bad", ["sql"] = "SELECT 1",
                ["export"] = new JsonObject { ["path"] = Path.Combine(dir, "no", "such", "dir.csv") },
            });
            if (bad["error"]?["code"]?.GetValue<int>() != RpcErrors.InvalidParams) throw new TestFailure("unwritable export path: " + bad.ToJsonString());
            await h.ResultAsync("execute", new JsonObject { ["connection_id"] = other, ["query_id"] = "free2", ["sql"] = "SELECT 1" });
            await h.NotificationAsync("query/done", "free2");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A disk that fills up while exporting ends the query as an error that names the file, and the
    // export is marked incomplete. /dev/full is a Linux device; elsewhere there is nothing to test with.
    public static async Task ExportWriteFailureIsReported()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/dev/full")) return;
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        await h.ResultAsync("execute", new JsonObject
        {
            ["connection_id"] = c, ["query_id"] = "q", ["sql"] = "ROWS 10",
            ["export"] = new JsonObject { ["path"] = "/dev/full", ["overwrite"] = true },
        });
        var done = (await h.NotificationAsync("query/done", "q"))["params"]!;
        if (done["status"]?.GetValue<string>() != "error") throw new TestFailure("a full disk ended as " + done.ToJsonString());
        if (done["export"]?["complete"]?.GetValue<bool>() != false) throw new TestFailure("a failed export was marked complete: " + done.ToJsonString());
        if (!(done["error"]?["message"]?.GetValue<string>() ?? "").Contains("/dev/full", StringComparison.Ordinal)) throw new TestFailure("the error does not name the file: " + done.ToJsonString());
    }

    // Rows that arrive after a cancel beyond the overflow cap are dropped, and counted in query/done:
    // delivered + truncated is every row the server produced.
    public static async Task RowsPastTheOverflowCapAreReported()
    {
        const int total = 300_000;
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        var stalled = h.Stdout.Arm("query/rows");
        await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q", ["sql"] = $"ROWS {total}" });
        await stalled.WaitAsync(Wait);
        // The writer is stalled, so even the answer to the cancel waits behind it.
        var cancel = h.Send("cancel", new JsonObject { ["query_id"] = "q" });
        await Task.Delay(2000); // the server goes on producing: the rows pile up behind the stalled writer
        h.Stdout.Release();
        await h.WaitForAsync(m => m["id"]?.GetValue<int>() == cancel && m["method"] is null, "the answer to the cancel");
        var done = (await h.NotificationAsync("query/done", "q"))["params"]!;
        var truncated = done["truncated_rows"]?.GetValue<long>() ?? 0;
        if (truncated <= 0) throw new TestFailure("no rows were reported truncated: " + done.ToJsonString());
        var delivered = h.Messages.Where(m => m["method"]?.GetValue<string>() == "query/rows").Sum(m => (long)m["params"]!["rows"]!.AsArray().Count);
        if (delivered + truncated != total) throw new TestFailure($"delivered {delivered} + truncated {truncated} != {total}");
    }

    // stdout gone: whichever message could not be written, the backend stops cleanly and does not throw.
    public static async Task BrokenStdoutEndsTheBackendCleanly()
    {
        // an answer that cannot be written
        await using (var h = new Harness())
        {
            h.Stdout.FailAfter("\"protocol\"");
            await h.ResultAsync("initialize");
            h.Send("initialize");
            await Completes(h, "a reply that could not be written");
        }
        // an error that cannot be written
        await using (var h = new Harness())
        {
            h.Stdout.FailAfter("\"protocol\"");
            await h.ResultAsync("initialize");
            h.SendRaw("{not json");
            await Completes(h, "an error that could not be written");
        }
        // query/done and rows that cannot be written
        await using (var h = new Harness())
        {
            var c = await h.ConnectAsync();
            h.Stdout.FailAfter("\"result\":{\"query_id\"");
            h.Send("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q", ["sql"] = "ROWS 3" });
            await Completes(h, "query/done that could not be written");
        }
        // the warning about an unacknowledged cancel that cannot be written
        await using (var h = new Harness())
        {
            h.Backend.CancelWarnAfter = TimeSpan.FromMilliseconds(300);
            h.Backend.ShutdownQueryWait = TimeSpan.FromMilliseconds(300);
            h.Backend.ShutdownCloseWait = TimeSpan.FromMilliseconds(300);
            var c = await h.ConnectAsync();
            await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q", ["sql"] = "STUCK" });
            await h.Server.StuckStarted.Task.WaitAsync(Wait);
            h.Stdout.FailAfter("\"cancel_sent\"");
            h.Send("cancel", new JsonObject { ["query_id"] = "q" });
            await Completes(h, "a cancel warning that could not be written");
            h.Server.StuckRelease.SetResult();
        }
    }

    // A catalog call that does not finish is cut off at the timeout and the user is told; one that is
    // stuck in the driver and ignores the cut-off makes the next call give up waiting, with its own
    // message; and after either the next call gets a fresh session (HLR-CAT-4).
    public static async Task CatalogTimeoutsAreReported()
    {
        await using var h = new Harness();
        h.Backend.CatalogTimeout = TimeSpan.FromMilliseconds(800);
        var c = await h.ConnectAsync();
        var first = await h.ResultAsync("catalog/describe", new JsonObject { ["connection_id"] = c, ["name"] = "t" });
        var firstSession = first["catalog_session"]!.GetValue<string>();

        // stuck in the driver: the next call waits for the lock, then gives up
        var deaf = h.Send("catalog/describe", new JsonObject { ["connection_id"] = c, ["name"] = "hangdeaf" });
        await Task.Delay(200);
        var waiting = await h.CallAsync("catalog/describe", new JsonObject { ["connection_id"] = c, ["name"] = "t" });
        if (waiting["error"]?["code"]?.GetValue<int>() != RpcErrors.Database
            || !waiting["error"]!["message"]!.GetValue<string>().Contains("Another catalog request", StringComparison.Ordinal))
        {
            throw new TestFailure("a catalog call behind a stuck one: " + waiting.ToJsonString());
        }
        h.Engine.FakeCatalog.Gate.SetResult();
        await h.WaitForAsync(m => m["id"]?.GetValue<int>() == deaf && m["method"] is null, "the stuck call, once it returns");

        // slow but cancellable: cut off, reported, and the session is replaced
        var slow = await h.CallAsync("catalog/describe", new JsonObject { ["connection_id"] = c, ["name"] = "hang" });
        if (slow["error"]?["code"]?.GetValue<int>() != RpcErrors.Database
            || !slow["error"]!["message"]!.GetValue<string>().Contains("did not finish", StringComparison.Ordinal))
        {
            throw new TestFailure("a catalog call that hangs: " + slow.ToJsonString());
        }
        var again = await h.ResultAsync("catalog/describe", new JsonObject { ["connection_id"] = c, ["name"] = "t" });
        if (again["catalog_session"]!.GetValue<string>() == firstSession) throw new TestFailure("the catalog session was not replaced after a timeout");
    }

    // A catalog session that cannot be opened is a database error; one that cannot be closed does not
    // stop the connection from being closed.
    public static async Task CatalogSessionFailures()
    {
        await using var h = new Harness();
        h.Server.OpensBeforeRefusing = 1;
        var c = await h.ConnectAsync();
        var refused = await h.CallAsync("catalog/describe", new JsonObject { ["connection_id"] = c, ["name"] = "t" });
        if (refused["error"]?["code"]?.GetValue<int>() != RpcErrors.Database) throw new TestFailure("a catalog session that cannot be opened: " + refused.ToJsonString());

        await using var g = new Harness();
        var d = await g.ConnectAsync();
        await g.ResultAsync("catalog/describe", new JsonObject { ["connection_id"] = d, ["name"] = "t" });
        g.Server.DisposeThrows = true;
        // closing the catalog session fails; disconnecting still has to close the user's session
        await g.CallAsync("disconnect", new JsonObject { ["connection_id"] = d });
        g.Server.DisposeThrows = false;
        var after = await g.CallAsync("disconnect", new JsonObject { ["connection_id"] = d });
        if (after["error"]?["code"]?.GetValue<int>() != RpcErrors.UnknownConnection) throw new TestFailure("the connection survived a failing close: " + after.ToJsonString());
    }
}
