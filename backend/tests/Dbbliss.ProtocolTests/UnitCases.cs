using System.Text.Json.Nodes;
using Dbbliss.Backend;
using Dbbliss.Backend.Catalog;
using Dbbliss.Backend.Engines;
using Dbbliss.Backend.Rpc;

namespace Dbbliss.ProtocolTests;

/// <summary>Small units whose edges decide behaviour the suites above rely on.</summary>
public static class UnitCases
{
    private static void Check(bool ok, string what)
    {
        if (!ok) throw new TestFailure(what);
    }

    // The cancel handle: one fire per request, a late registration fires at once, a finished query
    // is never cancelled, and a driver that throws on cancel does not take the backend with it.
    public static void QueryControl()
    {
        var fired = 0;
        var qc = new Dbbliss.Backend.Engines.QueryControl("q");
        qc.RefireProtocolCancel(); // nothing requested: nothing to re-send
        qc.SetProtocolCancel(() => fired++);
        Check(fired == 0, "registering a cancel fired it although none was requested");
        qc.RefireProtocolCancel();
        Check(fired == 0, "a re-send without a cancel request fired");

        Check(qc.Cancel(), "the first cancel was refused");
        Check(fired == 1 && qc.CancelRequested && qc.Token.IsCancellationRequested && qc.CancelRequestedAtMs is not null, "the first cancel did not fire once and mark the request");
        Check(qc.Cancel() && fired == 1, "a second cancel fired again or was refused");
        qc.RefireProtocolCancel();
        Check(fired == 2, "a re-send did not fire");
        qc.ClearProtocolCancel();
        qc.RefireProtocolCancel();
        Check(fired == 2, "a re-send fired a cleared cancel");

        qc.MarkFinished();
        Check(qc.Finished && !qc.Cancel(), "a finished query accepted a cancel");
        qc.SetProtocolCancel(() => fired++);
        qc.RefireProtocolCancel();
        Check(fired == 2, "a finished query was cancelled");
        qc.Dispose();

        // Registered after the cancel was requested (the race with query start): fires at once.
        var late = new Dbbliss.Backend.Engines.QueryControl("late");
        late.Cancel();
        var lateFired = 0;
        late.SetProtocolCancel(() => lateFired++);
        Check(lateFired == 1, "a cancel requested before registration did not fire at registration");

        // A cancel that throws is logged and does not escape.
        var angry = new Dbbliss.Backend.Engines.QueryControl("angry");
        angry.SetProtocolCancel(() => throw new InvalidOperationException("driver broke"));
        Check(angry.Cancel() && angry.Token.IsCancellationRequested, "a throwing protocol cancel stopped the cancel");
        angry.RefireProtocolCancel();
    }

    public static void IdentifierQuoting()
    {
        Check(ObjectNames.QuoteIdent("plain") == "\"plain\"", "plain identifier");
        Check(ObjectNames.QuoteIdent("a\"b") == "\"a\"\"b\"", "quote in identifier: " + ObjectNames.QuoteIdent("a\"b"));
        Check(ObjectNames.QuoteBracket("plain") == "[plain]", "plain bracket identifier");
        Check(ObjectNames.QuoteBracket("a]b") == "[a]]b]", "bracket in identifier: " + ObjectNames.QuoteBracket("a]b"));
        // What is quoted parses back to what it was.
        foreach (var name in new[] { "x", "with space", "dot.ted", "q\"uote", "br]acket", "[both]", "" })
        {
            if (name.Length == 0) continue;
            Check(ObjectNames.Parse(ObjectNames.QuoteIdent(name), brackets: false).Single() == name, "round trip of " + name);
            Check(ObjectNames.Parse(ObjectNames.QuoteBracket(name), brackets: true).Single() == name, "bracket round trip of " + name);
        }
    }

    public static void CredentialEdges()
    {
        // credman is the Windows Credential Manager: elsewhere it is refused with the way out.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                Credentials.Resolve(new JsonObject { ["credman"] = "target" });
                throw new TestFailure("credman was accepted on a platform without it");
            }
            catch (RpcException ex)
            {
                Check(ex.Code == RpcErrors.InvalidParams && ex.Message.Contains("pass", StringComparison.Ordinal), $"credman off Windows: {ex.Code} {ex.Message}");
            }
        }

        // A tool that does not answer is killed and reported (a passphrase prompt that nobody sees).
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var dir = Directory.CreateTempSubdirectory("dbbliss-tool-");
            var path = Environment.GetEnvironmentVariable("PATH");
            try
            {
                var script = Path.Combine(dir.FullName, "pass");
                File.WriteAllText(script, "#!/bin/sh\nsleep 30\n");
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Environment.SetEnvironmentVariable("PATH", dir.FullName + Path.PathSeparator + path);
                Credentials.ToolTimeout = TimeSpan.FromMilliseconds(500);
                var started = DateTime.UtcNow;
                try
                {
                    Credentials.Resolve(new JsonObject { ["pass"] = "x" });
                    throw new TestFailure("a tool that never answers gave a password");
                }
                catch (RpcException ex)
                {
                    Check(ex.Code == RpcErrors.CredentialNotFound && ex.Message.Contains("did not answer", StringComparison.Ordinal), $"tool timeout: {ex.Code} {ex.Message}");
                }
                Check(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "the tool timeout was not honoured");
            }
            finally
            {
                Credentials.ToolTimeout = TimeSpan.FromSeconds(60);
                Environment.SetEnvironmentVariable("PATH", path);
                dir.Delete(recursive: true);
            }
        }
    }

    // The instance registry lives in the user's state dir; if that cannot be used, the backend
    // still runs and sweeps nothing.
    public static void InstancesWithAnUnusableStateDir()
    {
        var dir = Directory.CreateTempSubdirectory("dbbliss-state-");
        try
        {
            // no instances directory yet: nothing is dead
            var fresh = new Instances(dir.FullName, id: "me");
            Check(fresh.DeadIds().Count == 0, "dead ids in an empty state dir");
            fresh.Prune();
            fresh.Unregister();

            // the state dir is a file: registering fails quietly
            var file = Path.Combine(dir.FullName, "file");
            File.WriteAllText(file, "not a directory");
            var broken = new Instances(file, id: "me");
            broken.Register();
            broken.Unregister();
            Check(broken.DeadIds().Count == 0, "dead ids when the state dir is a file");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // fetch on a query that streams without a window has no credit to grant; it says so by granting
    // nothing instead of failing.
    public static async Task FetchWithoutAWindow()
    {
        await using var h = new Harness();
        var c = await h.ConnectAsync();
        await h.ResultAsync("execute", new JsonObject { ["connection_id"] = c, ["query_id"] = "q", ["sql"] = "STUCK" });
        await h.Server.StuckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var granted = await h.ResultAsync("fetch", new JsonObject { ["query_id"] = "q", ["rows"] = 100 });
        Check(granted["state"]?.GetValue<string>() == "granted" && granted["granted"]?.GetValue<long>() == 0, "fetch without a window: " + granted.ToJsonString());
        h.Server.StuckRelease.SetResult();
        await h.NotificationAsync("query/done", "q");
    }
}
