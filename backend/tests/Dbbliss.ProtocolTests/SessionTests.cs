using System.Text.Json.Nodes;
using Dbbliss.Backend.Rpc;
using Dbbliss.Backend.Sessions;

namespace Dbbliss.ProtocolTests;

public static class SessionTests
{
    private static JsonObject P(string c, string? id = null, string? identity = null)
    {
        var p = new JsonObject { ["connection_id"] = c };
        if (id is not null) p["id"] = id;
        if (identity is not null) p["identity"] = identity;
        return p;
    }

    private static int Code(JsonObject response) => response["error"]?["code"]?.GetValue<int>() ?? 0;

    private static void Expect(JsonObject response, int code, string what)
    {
        if (Code(response) != code) throw new TestFailure($"{what}: expected error {code}, got {response.ToJsonString()}");
    }

    private static ForeignSession Add(Harness h, string id, bool inTransaction = false)
    {
        var f = new ForeignSession(id, "t-" + id) { InTransaction = inTransaction };
        h.Server.Foreign[id] = f;
        return f;
    }

    // Verifies: HLR-ADM-1
    public static async Task ListShape()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        Add(h, "f1");
        Add(h, "f2", inTransaction: true);
        var result = await h.ResultAsync("sessions/list", P(c));
        if (result["can_cancel"]?.GetValue<bool>() != true) throw new TestFailure("can_cancel: " + result.ToJsonString());
        var rows = result["sessions"]!.AsArray();
        var f2 = rows.Single(r => r!["id"]!.GetValue<string>() == "f2")!;
        string[] keys = ["id", "identity", "user", "database", "host", "application", "state", "duration_ms", "wait", "blocked_by", "in_transaction", "query", "own"];
        foreach (var k in keys)
        {
            if (!f2.AsObject().ContainsKey(k)) throw new TestFailure($"row lacks {k}: {f2.ToJsonString()}");
        }
        if (f2["identity"]!.GetValue<string>() != "t-f2" || f2["blocked_by"]!.GetValue<string>() != "f1" || f2["in_transaction"]!.GetValue<bool>() != true
            || f2["duration_ms"]!.GetValue<long>() != 1500 || f2["user"]!.GetValue<string>() != "alice")
        {
            throw new TestFailure("row content: " + f2.ToJsonString());
        }
    }

    // Verifies: HLR-ADM-6
    public static async Task OwnSessionsAreMarkedAndRefused()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        var rows = (await h.ResultAsync("sessions/list", P(c)))["sessions"]!.AsArray();
        var own = rows.Where(r => r!["own"]!.GetValue<bool>()).Select(r => r!["id"]!.GetValue<string>()).Order().ToArray();
        var open = h.Server.Open.Keys.Order().ToArray();
        if (open.Length != 2 || !own.SequenceEqual(open)) throw new TestFailure($"own: [{string.Join(",", own)}], open sessions: [{string.Join(",", open)}]");
        foreach (var id in open)
        {
            foreach (var method in new[] { "sessions/terminate", "sessions/cancel" })
            {
                var response = await h.CallAsync(method, P(c, id, "own-" + id));
                Expect(response, RpcErrors.Session, $"{method} on own session {id}");
            }
        }
        if (h.Server.Open.Count != 2) throw new TestFailure("an own session was closed");
        if (h.Engine.FakeSessions.SessionsUsed.Count != 1) throw new TestFailure("the engine was asked to act on an own session");
    }

    // Verifies: HLR-ADM-2
    public static async Task WorksWhileTheUserSessionIsBusy()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        var userSession = h.Server.Open.Keys.Single();
        await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q1", ["sql"] = "ROWS 5000", ["window"] = 100 });
        await h.WaitForAsync(m => m["method"]?.GetValue<string>() == "query/paused", "query/paused");
        Add(h, "f1");
        var list = await h.ResultAsync("sessions/list", P(c));
        if (list["sessions"]!.AsArray().All(r => r!["id"]!.GetValue<string>() != "f1")) throw new TestFailure("no list while busy");
        var done = await h.ResultAsync("sessions/terminate", P(c, "f1", "t-f1"));
        if (done["done"]?.GetValue<bool>() != true) throw new TestFailure("terminate while busy: " + done.ToJsonString());
        var used = h.Engine.FakeSessions.SessionsUsed.Distinct().ToArray();
        if (used.Length != 1 || used[0] == userSession) throw new TestFailure($"used {string.Join(",", used)}, the user's session is {userSession}");
        await h.ResultAsync("cancel", new JsonObject { ["query_id"] = "q1" });
        await h.NotificationAsync("query/done", "q1");
    }

    // Verifies: HLR-ADM-3
    public static async Task CancelStopsTheStatementNotTheSession()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        var f = Add(h, "f1");
        var done = await h.ResultAsync("sessions/cancel", P(c, "f1", "t-f1"));
        if (done["done"]?.GetValue<bool>() != true || f.Cancelled != 1 || f.Terminated) throw new TestFailure($"cancel: {done.ToJsonString()}, cancelled {f.Cancelled}, terminated {f.Terminated}");
    }

    // Verifies: HLR-ADM-3
    public static async Task CancelRefusedWhereTheEngineCannot()
    {
        await using var h = new Harness();
        h.Server.CanCancelOthers = false;
        var c = await h.ConnectAsync();
        var f = Add(h, "f1");
        var list = await h.ResultAsync("sessions/list", P(c));
        if (list["can_cancel"]?.GetValue<bool>() != false) throw new TestFailure("can_cancel should be false");
        var response = await h.CallAsync("sessions/cancel", P(c, "f1", "t-f1"));
        Expect(response, RpcErrors.Session, "cancel without engine support");
        if (f.Cancelled != 0 || h.Engine.FakeSessions.SessionsUsed.Count != 1) throw new TestFailure("something was sent to the server");
    }

    // Verifies: HLR-ADM-4
    public static async Task TerminateEndsTheSession()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        Add(h, "f1");
        var other = Add(h, "f2");
        var done = await h.ResultAsync("sessions/terminate", P(c, "f1", "t-f1"));
        if (done["done"]?.GetValue<bool>() != true || h.Server.Foreign.ContainsKey("f1") || other.Terminated) throw new TestFailure("terminate: " + done.ToJsonString());
    }

    // Verifies: HLR-ADM-5
    public static async Task ActionsOnlyReachTheSessionThatWasSeen()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        var f = Add(h, "f1");
        foreach (var method in new[] { "sessions/cancel", "sessions/terminate" })
        {
            Expect(await h.CallAsync(method, P(c, "f1", "a-later-session")), RpcErrors.Session, method + " with another identity");
            Expect(await h.CallAsync(method, P(c, "f9", "t-f9")), RpcErrors.Session, method + " on an id that is gone");
        }
        if (f.Cancelled != 0 || f.Terminated) throw new TestFailure("a session that was not the one seen was acted on");
        // The user can fix it, so the catalog session stays.
        if (h.Server.Open.Count != 2) throw new TestFailure("a user error replaced the catalog session");
    }

    // Verifies: HLR-ADM-8
    public static async Task UnsignalledAndFailedActionsAreReported()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        var f = Add(h, "f1");
        f.Signals = false;
        foreach (var method in new[] { "sessions/cancel", "sessions/terminate" })
        {
            var r = await h.ResultAsync(method, P(c, "f1", "t-f1"));
            if (r["done"]?.GetValue<bool>() != false) throw new TestFailure($"{method} reported success for a signal that reached nothing: {r.ToJsonString()}");
        }
        var first = h.Server.Open.Keys.Order().ToArray();
        var crash = await h.CallAsync("sessions/terminate", P(c, "crash", "x"));
        Expect(crash, RpcErrors.Database, "driver failure");
        if (h.Server.Open.Count != 1) throw new TestFailure("the broken catalog session should be closed");
        await h.ResultAsync("sessions/list", P(c));
        if (h.Server.Open.Count != 2 || h.Server.Open.Keys.Order().SequenceEqual(first)) throw new TestFailure("a new catalog session was expected");
    }

    // Verifies: HLR-ADM-7
    public static async Task StatusReportsPresenceAndProgress()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        var f = Add(h, "f1", inTransaction: true);
        var before = await h.ResultAsync("sessions/status", P(c, "f1", "t-f1"));
        if (before["present"]?.GetValue<bool>() != true || before["detail"] is not null) throw new TestFailure("before: " + before.ToJsonString());
        await h.ResultAsync("sessions/terminate", P(c, "f1", "t-f1"));
        var rolling = await h.ResultAsync("sessions/status", P(c, "f1", "t-f1"));
        if (rolling["present"]?.GetValue<bool>() != true || rolling["detail"]?.GetValue<string>() != "SPID f1: rollback 40%") throw new TestFailure("rolling back: " + rolling.ToJsonString());
        var again = await h.ResultAsync("sessions/status", P(c, "f1", "t-f1"));
        if (again["detail"]?.GetValue<string>() != "SPID f1: rollback 40%") throw new TestFailure("a second status carries the message of the first: " + again.ToJsonString());
        h.Server.Foreign.TryRemove("f1", out _);
        var gone = await h.ResultAsync("sessions/status", P(c, "f1", "t-f1"));
        if (gone["present"]?.GetValue<bool>() != false) throw new TestFailure("gone: " + gone.ToJsonString());
        _ = f;
    }

    // Verifies: HLR-ADM-1, LLR-ADM-9
    public static async Task BadRequests()
    {
        await using var h = new Harness(new FakeEngine(new FakeServer(), "plain", hasSessions: false));
        var c = await h.ConnectAsync();
        Expect(await h.CallAsync("sessions/list", P("nope")), RpcErrors.UnknownConnection, "unknown connection");
        Expect(await h.CallAsync("sessions/terminate", P(c)), RpcErrors.InvalidParams, "no id");
        Expect(await h.CallAsync("sessions/terminate", P(c, "f1")), RpcErrors.InvalidParams, "no identity");
        Expect(await h.CallAsync("sessions/terminate", P(c, "bad", "x")), RpcErrors.Session, "an id the engine refuses");
        var plain = (await h.ResultAsync("connect", new JsonObject { ["engine"] = "plain", ["connection_string"] = "x" }))["connection_id"]!.GetValue<string>();
        var response = await h.CallAsync("sessions/list", P(plain));
        if (Code(response) != RpcErrors.InvalidParams || !response["error"]!["message"]!.GetValue<string>().Contains("no session support", StringComparison.Ordinal))
        {
            throw new TestFailure("an engine without session support: " + response.ToJsonString());
        }
    }

    // Verifies: LLR-ADM-9
    public static void IdsArePlainIntegers()
    {
        if (SessionIds.ParseInt("1234") != 1234) throw new TestFailure("1234");
        foreach (var bad in new[] { "", " ", "12 ", "1; SHUTDOWN", "-5", "0x10", "99999999999", "1.5", "५" })
        {
            try
            {
                SessionIds.ParseInt(bad);
            }
            catch (SessionActionException)
            {
                continue;
            }
            throw new TestFailure($"\"{bad}\" should not be a session id");
        }
    }

    // Verifies: HLR-ADM-5
    public static void IdentityTokensRoundTrip()
    {
        var utc = new DateTime(2026, 10, 10, 8, 47, 21, DateTimeKind.Utc).AddTicks(1234567);
        var token = SessionIds.FormatIdentity(utc);
        if (SessionIds.ParseIdentity(token) != utc || SessionIds.ParseIdentity(token).Kind != DateTimeKind.Utc) throw new TestFailure("utc: " + token);
        var local = new DateTime(2026, 10, 10, 8, 47, 21, 3, DateTimeKind.Unspecified);
        if (SessionIds.ParseIdentity(SessionIds.FormatIdentity(local)) != local) throw new TestFailure("unspecified");
        if (SessionIds.ParseIdentity(SessionIds.FormatIdentity(new DateTimeOffset(utc))) != utc) throw new TestFailure("offset");
        foreach (var bad in new[] { "", "yesterday", "2026-10-10" })
        {
            try
            {
                SessionIds.ParseIdentity(bad);
            }
            catch (SessionActionException)
            {
                continue;
            }
            throw new TestFailure($"\"{bad}\" should not be an identity");
        }
        try
        {
            SessionIds.FormatIdentity(null);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new TestFailure("a missing start time should be refused");
    }

    // Verifies: HLR-ADM-3
    public static async Task SqlServerCannotCancelAnotherSession()
    {
        var admin = new SqlServerSessions();
        if (admin.CanCancel) throw new TestFailure("SQL Server cannot cancel another session's statement");
        try
        {
            await admin.CancelAsync(null!, new SessionTarget("55", "x"), CancellationToken.None);
        }
        catch (SessionActionException)
        {
            return;
        }
        throw new TestFailure("cancel should be refused");
    }
}
