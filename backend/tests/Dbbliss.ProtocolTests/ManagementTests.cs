using System.Text.Json.Nodes;
using Dbbliss.Backend.Engines;
using Dbbliss.Backend.Management;
using Dbbliss.Backend.Rpc;

namespace Dbbliss.ProtocolTests;

public static class ManagementTests
{
    private static JsonObject Backup(string c, string path, string database = "app", bool overwrite = false)
    {
        var p = new JsonObject { ["connection_id"] = c, ["database"] = database, ["path"] = path };
        if (overwrite) p["overwrite"] = true;
        return p;
    }

    private static JsonObject Drop(string c, string name = "t", string kind = "table", string database = "app", string? confirm = null, string? backup = null)
    {
        var p = new JsonObject { ["connection_id"] = c, ["kind"] = kind, ["database"] = database, ["schema"] = "public", ["name"] = name, ["confirm_name"] = confirm ?? name };
        if (backup is not null) p["backup_id"] = backup;
        return p;
    }

    private static int Code(JsonObject response) => response["error"]?["code"]?.GetValue<int>() ?? 0;

    private static void Expect(JsonObject response, int code, string what)
    {
        if (Code(response) != code) throw new TestFailure($"{what}: expected error {code}, got {response.ToJsonString()}");
    }

    private static async Task<string> StartAsync(Harness h, string c, string path, string database = "app")
    {
        var result = await h.ResultAsync("backup/start", Backup(c, path, database));
        return result["backup_id"]!.GetValue<string>();
    }

    private static Task<JsonObject> DoneAsync(Harness h, string id) =>
        h.WaitForAsync(m => m["method"]?.GetValue<string>() == "backup/done" && m["params"]?["backup_id"]?.GetValue<string>() == id, "backup/done for " + id);

    private static IEnumerable<(int Index, JsonObject Message)> Events(Harness h, string id) =>
        h.Messages.Select((m, i) => (i, m)).Where(x => x.m["method"]?.GetValue<string>() is "backup/progress" or "backup/done" && x.m["params"]?["backup_id"]?.GetValue<string>() == id);

    // Verifies: HLR-MGT-1, HLR-MGT-5
    public static async Task BackupReportsProgressAndEndsOnce()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        var id = await StartAsync(h, c, "/b/app.dump");
        var done = (await DoneAsync(h, id))["params"]!;
        if (done["status"]!.GetValue<string>() != "completed" || done["path"]!.GetValue<string>() != "/b/app.dump" || done["bytes"]!.GetValue<long>() != 1234
            || done["detail"]!.GetValue<string>() != "fake 1.0" || done["verified"]!.GetValue<bool>() != true)
        {
            throw new TestFailure("done: " + done.ToJsonString());
        }
        await Task.Delay(200);
        var events = Events(h, id).ToList();
        var progress = events.Where(e => e.Message["method"]!.GetValue<string>() == "backup/progress").ToList();
        if (progress.Count != 2 || progress[0].Message["params"]!["phase"]!.GetValue<string>() != "dump" || progress[0].Message["params"]!["percent"]!.GetValue<int>() != 30
            || progress[1].Message["params"]!["phase"]!.GetValue<string>() != "verify")
        {
            throw new TestFailure("progress: " + string.Join(" | ", progress.Select(p => p.Message.ToJsonString())));
        }
        if (events.Count(e => e.Message["method"]!.GetValue<string>() == "backup/done") != 1) throw new TestFailure("done more than once");
        if (progress.Any(p => p.Index > events.Single(e => e.Message["method"]!.GetValue<string>() == "backup/done").Index)) throw new TestFailure("progress after done");
    }

    // Verifies: HLR-MGT-5
    public static async Task FailuresAreReportedWithTheirReason()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        var id = await StartAsync(h, c, "fail");
        var done = (await DoneAsync(h, id))["params"]!;
        if (done["status"]!.GetValue<string>() != "failed" || done["verified"]!.GetValue<bool>() || !done["error"]!.GetValue<string>().Contains("boom", StringComparison.Ordinal))
        {
            throw new TestFailure("done: " + done.ToJsonString());
        }
    }

    // Verifies: HLR-MGT-6
    public static async Task AnEngineThatCannotRemoveTheFileSaysItMayRemain()
    {
        await using var h = new Harness();
        h.Engine.FakeManagement.RemovesPartialFile = false;
        var c = await h.ConnectAsync("dev");
        var failed = (await DoneAsync(h, await StartAsync(h, c, "fail")))["params"]!;
        if (failed["note"]?.GetValue<string>() is not { } note || !note.Contains("fail", StringComparison.Ordinal) || !note.Contains("partial file", StringComparison.Ordinal)) throw new TestFailure("failed: " + failed.ToJsonString());
        var hung = await StartAsync(h, c, "hang");
        await h.ResultAsync("backup/cancel", new JsonObject { ["backup_id"] = hung });
        if ((await DoneAsync(h, hung))["params"]!["note"] is null) throw new TestFailure("a cancelled backup says nothing about its file");
        var good = (await DoneAsync(h, await StartAsync(h, c, "/b/ok.dump")))["params"]!;
        if (good["note"] is not null) throw new TestFailure("a completed backup has a note: " + good.ToJsonString());
        h.Engine.FakeManagement.RemovesPartialFile = true;
        var removed = (await DoneAsync(h, await StartAsync(h, c, "fail")))["params"]!;
        if (removed["note"] is not null) throw new TestFailure("an engine that removes the file says it may remain");
    }

    // Verifies: HLR-MGT-6
    public static async Task CancelStopsTheOperation()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        var id = await StartAsync(h, c, "hang");
        await h.WaitForAsync(m => m["method"]?.GetValue<string>() == "backup/progress", "first progress");
        var state = await h.ResultAsync("backup/cancel", new JsonObject { ["backup_id"] = id });
        if (state["state"]!.GetValue<string>() != "cancelling") throw new TestFailure("cancel: " + state.ToJsonString());
        var done = (await DoneAsync(h, id))["params"]!;
        if (done["status"]!.GetValue<string>() != "cancelled" || done["verified"]!.GetValue<bool>() || h.Engine.FakeManagement.Cancelled != 1) throw new TestFailure("done: " + done.ToJsonString());
        var again = await h.ResultAsync("backup/cancel", new JsonObject { ["backup_id"] = id });
        if (again["state"]!.GetValue<string>() != "finished") throw new TestFailure("a finished backup: " + again.ToJsonString());
        Expect(await h.CallAsync("backup/cancel", new JsonObject { ["backup_id"] = "nope" }), RpcErrors.InvalidParams, "unknown backup");
    }

    // Verifies: HLR-MGT-3, HLR-MGT-4
    public static async Task RefusalsComeBeforeAnythingRuns()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        Expect(await h.CallAsync("backup/start", Backup(c, "exists")), RpcErrors.Management, "existing file");
        var missing = await h.CallAsync("backup/start", Backup(c, "notool"));
        Expect(missing, RpcErrors.Management, "missing tool");
        if (!missing["error"]!["message"]!.GetValue<string>().Contains("pg_dump", StringComparison.Ordinal)) throw new TestFailure("the message names the tool: " + missing.ToJsonString());
        await Task.Delay(200);
        if (h.Messages.Any(m => m["method"]?.GetValue<string>() is "backup/progress" or "backup/done")) throw new TestFailure("a refused backup reported progress");
        // The refusals left the connection free, and overwrite = true lifts the first one.
        var id = (await h.ResultAsync("backup/start", Backup(c, "exists", overwrite: true)))["backup_id"]!.GetValue<string>();
        if ((await DoneAsync(h, id))["params"]!["status"]!.GetValue<string>() != "completed") throw new TestFailure("overwrite");
    }

    // Verifies: HLR-MGT-7, HLR-MGT-11
    public static async Task OperationsAreExclusivePerConnection()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        var id = await StartAsync(h, c, "hang");
        Expect(await h.CallAsync("backup/start", Backup(c, "second")), RpcErrors.ConnectionBusy, "second backup");
        Expect(await h.CallAsync("management/drop", Drop(c)), RpcErrors.ConnectionBusy, "drop during backup");
        Expect(await h.CallAsync("disconnect", new JsonObject { ["connection_id"] = c, ["rollback"] = true }), RpcErrors.ConnectionBusy, "disconnect during backup");
        if (h.Server.Open.IsEmpty) throw new TestFailure("the sessions were closed");
        await h.ResultAsync("backup/cancel", new JsonObject { ["backup_id"] = id });
        await DoneAsync(h, id);
        await h.ResultAsync("management/drop", Drop(c));
        await h.ResultAsync("disconnect", new JsonObject { ["connection_id"] = c, ["rollback"] = false });
    }

    // Verifies: HLR-MGT-7
    public static async Task ShutdownCancelsRunningOperations()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        await StartAsync(h, c, "hang");
        await h.WaitForAsync(m => m["method"]?.GetValue<string>() == "backup/progress", "first progress");
        await h.CloseAsync();
        if (h.Engine.FakeManagement.Cancelled != 1 || !h.Engine.FakeManagement.HangEnded.Task.IsCompleted) throw new TestFailure("the running backup was not cancelled and awaited by the shutdown");
    }

    // Verifies: HLR-MGT-8
    public static async Task DropNeedsTheTypedName()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        Expect(await h.CallAsync("management/drop", Drop(c, confirm: "T")), RpcErrors.Management, "case differs");
        Expect(await h.CallAsync("management/drop", Drop(c, confirm: "")), RpcErrors.InvalidParams, "empty confirmation");
        var noConfirm = Drop(c);
        noConfirm.Remove("confirm_name");
        Expect(await h.CallAsync("management/drop", noConfirm), RpcErrors.InvalidParams, "no confirmation");
        Expect(await h.CallAsync("management/drop", Drop(c, kind: "index")), RpcErrors.InvalidParams, "unknown kind");
        if (!h.Engine.FakeManagement.Drops.IsEmpty) throw new TestFailure("something was dropped without the name");
        var ok = await h.ResultAsync("management/drop", Drop(c));
        if (ok["dropped"]?.GetValue<bool>() != true || h.Engine.FakeManagement.Drops.Single() != "table app.public.t") throw new TestFailure("drop: " + ok.ToJsonString());
        var db = Drop(c, name: "app", kind: "database");
        await h.ResultAsync("management/drop", db);
        if (!h.Engine.FakeManagement.Drops.Contains("database app.public.app")) throw new TestFailure("a database drop");
        Expect(await h.CallAsync("management/drop", Drop(c, name: "app", kind: "database", confirm: "other")), RpcErrors.Management, "database name");
    }

    private static async Task<string> BackupDoneAsync(Harness h, string c, string database)
    {
        var id = await StartAsync(h, c, "/b/x.dump", database);
        await DoneAsync(h, id);
        return id;
    }

    // Verifies: HLR-MGT-9, LLR-MGT-14
    public static async Task ProdDropNeedsAFreshVerifiedBackup()
    {
        await using var h = new Harness();
        foreach (var env in new string?[] { "prod", null, "weird" })
        {
            var c = await h.ConnectAsync(env);
            Expect(await h.CallAsync("management/drop", Drop(c)), RpcErrors.Management, $"env {env ?? "(none)"} without a backup");
            Expect(await h.CallAsync("management/drop", Drop(c, backup: "nope")), RpcErrors.Management, "unknown backup id");
            var failed = await StartAsync(h, c, "fail");
            await DoneAsync(h, failed);
            Expect(await h.CallAsync("management/drop", Drop(c, backup: failed)), RpcErrors.Management, "failed backup");
            var hung = await StartAsync(h, c, "hang");
            await h.ResultAsync("backup/cancel", new JsonObject { ["backup_id"] = hung });
            await DoneAsync(h, hung);
            Expect(await h.CallAsync("management/drop", Drop(c, backup: hung)), RpcErrors.Management, "cancelled backup");
            var other = await BackupDoneAsync(h, c, "other");
            Expect(await h.CallAsync("management/drop", Drop(c, backup: other)), RpcErrors.Management, "backup of another database");
            var good = await BackupDoneAsync(h, c, "app");
            await h.ResultAsync("management/drop", Drop(c, backup: good));
        }
        // A backup made on another connection does not count.
        var c1 = await h.ConnectAsync("prod");
        var c2 = await h.ConnectAsync("prod");
        var theirs = await BackupDoneAsync(h, c1, "app");
        Expect(await h.CallAsync("management/drop", Drop(c2, backup: theirs)), RpcErrors.Management, "backup of another connection");
        // Other environments need none.
        foreach (var env in new[] { "dev", "test" })
        {
            await h.ResultAsync("management/drop", Drop(await h.ConnectAsync(env)));
        }
    }

    // Verifies: HLR-MGT-9
    public static async Task StaleBackupsDoNotCount()
    {
        await using var h = new Harness();
        h.Backend.Operations.DropBackupMaxAge = TimeSpan.FromMilliseconds(300);
        var c = await h.ConnectAsync("prod");
        var id = await BackupDoneAsync(h, c, "app");
        await Task.Delay(600);
        Expect(await h.CallAsync("management/drop", Drop(c, backup: id)), RpcErrors.Management, "a backup older than the limit");
    }

    // Verifies: HLR-MGT-10
    public static async Task DropErrorsReachTheUser()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        var inUse = await h.CallAsync("management/drop", Drop(c, name: "inuse"));
        Expect(inUse, RpcErrors.Database, "driver failure");
        if (!inUse["error"]!["message"]!.GetValue<string>().Contains("accessed by other users", StringComparison.Ordinal)) throw new TestFailure("the server's words: " + inUse.ToJsonString());
        Expect(await h.CallAsync("management/drop", Drop(c, name: "refuse")), RpcErrors.Management, "user-fixable");
        // The connection is free again after both.
        await h.ResultAsync("management/drop", Drop(c));
    }

    // Verifies: LLR-MGT-13
    public static async Task BackupDefaultsComeFromTheEngine()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        var result = await h.ResultAsync("backup/defaults", new JsonObject { ["connection_id"] = c });
        if (result["directory"]!.GetValue<string>() != "/var/backups") throw new TestFailure("defaults: " + result.ToJsonString());
    }

    // Verifies: HLR-MGT-1, LLR-MGT-14
    public static async Task BadRequests()
    {
        await using var h = new Harness(new FakeEngine(new FakeServer(), "plain", hasManagement: false));
        var c = await h.ConnectAsync("dev");
        Expect(await h.CallAsync("backup/start", Backup("nope", "x")), RpcErrors.UnknownConnection, "unknown connection");
        Expect(await h.CallAsync("backup/start", new JsonObject { ["connection_id"] = c, ["database"] = "app" }), RpcErrors.InvalidParams, "no path");
        Expect(await h.CallAsync("backup/start", new JsonObject { ["connection_id"] = c, ["path"] = "x" }), RpcErrors.InvalidParams, "no database");
        Expect(await h.CallAsync("management/drop", new JsonObject { ["connection_id"] = c, ["kind"] = "table", ["name"] = "t", ["confirm_name"] = "t" }), RpcErrors.InvalidParams, "no database for a drop");
        var plain = (await h.ResultAsync("connect", new JsonObject { ["engine"] = "plain", ["connection_string"] = "x", ["env"] = "dev" }))["connection_id"]!.GetValue<string>();
        foreach (var (method, p) in new[] { ("backup/start", Backup(plain, "x")), ("management/drop", Drop(plain)), ("backup/defaults", new JsonObject { ["connection_id"] = plain }) })
        {
            var response = await h.CallAsync(method, p);
            if (Code(response) != RpcErrors.InvalidParams || !response["error"]!["message"]!.GetValue<string>().Contains("no management support", StringComparison.Ordinal))
            {
                throw new TestFailure($"{method} on an engine without management: {response.ToJsonString()}");
            }
        }
    }

    // Verifies: HLR-MGT-5
    public static async Task AnUnexpectedFailureIsReportedNotLost()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        var done = (await DoneAsync(h, await StartAsync(h, c, "crash")))["params"]!;
        if (done["status"]!.GetValue<string>() != "failed" || !done["error"]!.GetValue<string>().Contains("driver exploded", StringComparison.Ordinal)) throw new TestFailure("done: " + done.ToJsonString());
        // The connection is free again.
        await h.ResultAsync("management/drop", Drop(c));
    }

    // Verifies: HLR-MGT-7
    public static async Task ShutdownWaitsForARunningDropAndEndsIt()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync("dev");
        var id = h.Send("management/drop", Drop(c, name: "hang"));
        await Task.Delay(300);
        await h.CloseAsync();
        var response = await h.WaitForAsync(m => m["id"]?.GetValue<int>() == id && m["method"] is null, "the drop's answer");
        Expect(response, RpcErrors.ShuttingDown, "a drop ended by shutdown");
    }

    // Verifies: HLR-MGT-7
    public static async Task ShutdownIsBoundedWhenAToolDoesNotStop()
    {
        await using var h = new Harness();
        h.Backend.Operations.ShutdownWait = TimeSpan.FromMilliseconds(300);
        var c = await h.ConnectAsync("dev");
        await StartAsync(h, c, "stubborn");
        await h.WaitForAsync(m => m["method"]?.GetValue<string>() == "backup/progress", "first progress");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await h.CloseAsync();
        h.Engine.FakeManagement.StubbornRelease.SetResult();
        if (sw.Elapsed > TimeSpan.FromSeconds(8)) throw new TestFailure($"shutdown waited {sw.Elapsed.TotalSeconds:0.0} s for a tool that does not stop");
    }

    // Verifies: HLR-MGT-7, HLR-MGT-11
    public static async Task TheManagerGuardsItsOwnRaces()
    {
        var output = new Output(Stream.Null);
        var manager = new OperationManager(output);
        var management = new FakeManagement();
        var target = new OperationTarget("c1", new ManagementContext(null!, new ConnectionSpec("x", null, null), "dev"), management);

        // A connection that is being closed takes no new operation; one that is running keeps it from closing.
        var (_, start) = await manager.StartBackupAsync(target, new BackupRequest("app", "hang", false));
        start();
        if (manager.TryBeginClose("c1")) throw new TestFailure("a connection with a running operation was closed");
        manager.Cancel("b1", "backup");
        for (var i = 0; i < 100 && !manager.TryBeginClose("c1"); i++) await Task.Delay(50);
        try
        {
            await manager.StartBackupAsync(target, new BackupRequest("app", "/b/x", false));
            throw new TestFailure("an operation started on a closing connection");
        }
        catch (RpcException ex) when (ex.Code == RpcErrors.UnknownConnection)
        {
        }

        // After shutdown began nothing starts.
        var other = new OperationTarget("c2", target.Context, management);
        await manager.ShutdownAsync();
        try
        {
            await manager.StartBackupAsync(other, new BackupRequest("app", "/b/x", false));
            throw new TestFailure("an operation started after shutdown");
        }
        catch (RpcException ex) when (ex.Code == RpcErrors.ShuttingDown)
        {
        }
    }

    private sealed class BrokenStdout : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("the pipe is closed");
        public override void Flush() => throw new IOException("the pipe is closed");
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // Verifies: HLR-MGT-7
    public static async Task AnOperationEndsEvenWhenTheReportCannotBeWritten()
    {
        var manager = new OperationManager(new Output(new BrokenStdout()));
        var target = new OperationTarget("c1", new ManagementContext(null!, new ConnectionSpec("x", null, null), "dev"), new FakeManagement());
        var (_, start) = await manager.StartBackupAsync(target, new BackupRequest("app", "/b/x", false));
        start();
        for (var i = 0; i < 100 && !manager.TryBeginClose("c1"); i++) await Task.Delay(50);
        if (!manager.TryBeginClose("c1")) throw new TestFailure("the connection stayed busy after its operation ended because stdout was gone");
    }
}
