using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Dbbliss.CancelTests;

/// <summary>Info: a control experiment that documents server behaviour; never fails the run.</summary>
public enum Outcome { Pass, Fail, Skip, Info }

public sealed record ScenarioResult(string Engine, string Scenario, Outcome Outcome, string Detail, long? ServerStopMs = null);

/// <summary>The Phase 0 scenarios. Every one checks the server from an observer connection.</summary>
public sealed class Scenarios(EngineProfile profile, Settings settings)
{
    private const int ExecutingTimeoutMs = 15000;
    private const int DoneTimeoutMs = 15000;
    /// <summary>How long the server gets to stop after a cancel (or a client death) before we call it a failure.</summary>
    private const int ServerStopTimeoutMs = 10000;

    private Dictionary<string, string> BackendEnv => new() { [profile.PasswordEnv] = Environment.GetEnvironmentVariable(profile.PasswordEnv) ?? "" };

    public IEnumerable<(string Name, Func<Task<ScenarioResult>> Run)> All()
    {
        yield return ("sleep_cancel", () => CancelLongQuery("sleep_cancel", profile.SleepSql));
        foreach (var (name, sql) in profile.ExtraLongQueries) yield return (name, () => CancelLongQuery(name, sql));
        yield return ("cancel_immediately", CancelImmediately);
        if (profile.BatchThenSleepSql is not null) yield return ("batch_cancel_keeps_results", BatchCancelKeepsResults);
        yield return ("streaming_cancel", StreamingCancel);
        yield return ("streaming_cancel_stalled_client", StreamingCancelStalledClient);
        yield return ("tx_cancel", () => TransactionCancel("tx_cancel", xactAbort: false));
        if (profile.Engine == "sqlserver") yield return ("tx_cancel_xact_abort", () => TransactionCancel("tx_cancel_xact_abort", xactAbort: true));
        yield return ("backend_stdin_closed", () => BackendDeath("backend_stdin_closed", c => { c.CloseStdin(); return Task.CompletedTask; }));
        yield return ("backend_sigterm", BackendSigterm);
        yield return ("backend_killed", () => BackendDeath("backend_killed", c => { c.Kill(); return Task.CompletedTask; }));
        if (profile.Engine == "postgres")
        {
            // Control experiment: shows why the backend enables client_connection_check_interval.
            yield return ("backend_killed_no_conncheck", async () =>
            {
                var r = await BackendDeath("backend_killed_no_conncheck",
                    c => { c.Kill(); return Task.CompletedTask; }, new JsonObject { ["pg_client_connection_check_interval_ms"] = 0 });
                return r with { Outcome = Outcome.Info, Detail = (r.ServerStopMs is null ? "server kept running (expected): " : "server stopped: ") + r.Detail };
            });
        }
        yield return ("nvim_quit", () => NvimDeath("nvim_quit", kill: false));
        yield return ("nvim_killed", () => NvimDeath("nvim_killed", kill: true));
    }

    private ScenarioResult Result(string scenario, Outcome o, string detail, long? stopMs = null) =>
        new(profile.Engine, scenario, o, detail, stopMs);

    private async Task<(BackendClient Client, string ConnectionId, string Session)> StartAsync(JsonObject? options = null)
    {
        var client = BackendClient.Start(settings.BackendPath, BackendEnv);
        await client.RequestAsync("initialize");
        var conn = await client.RequestAsync("connect", new JsonObject
        {
            ["engine"] = profile.Engine,
            ["connection_string"] = profile.ConnectionString,
            // No password env set: integrated auth (e.g. SQL Server Express on the Windows CI runner).
            ["password"] = string.IsNullOrEmpty(Environment.GetEnvironmentVariable(profile.PasswordEnv)) ? null : new JsonObject { ["env"] = profile.PasswordEnv },
            ["options"] = options,
        });
        return (client, conn["connection_id"]!.GetValue<string>(), conn["server_session_id"]!.GetValue<string>());
    }

    private static Task<JsonObject> Execute(BackendClient c, string connectionId, string queryId, string sql) =>
        c.RequestAsync("execute", new JsonObject { ["connection_id"] = connectionId, ["query_id"] = queryId, ["sql"] = sql });

    /// <summary>Polls the observer until the session is executing. Proves the test is not vacuous.</summary>
    private async Task<ServerView> WaitExecutingAsync(System.Data.Common.DbConnection observer, string session)
    {
        var sw = Stopwatch.StartNew();
        ServerView view;
        do
        {
            view = await profile.ViewAsync(observer, session);
            if (view.Executing) return view;
            await Task.Delay(100);
        }
        while (sw.ElapsedMilliseconds < ExecutingTimeoutMs);
        throw new TimeoutException($"server never showed the query as executing: {view.Detail}");
    }

    /// <summary>Polls until the server no longer executes anything for the session; returns ms waited, or null on timeout.</summary>
    private async Task<(long? Ms, ServerView View)> WaitStoppedAsync(System.Data.Common.DbConnection observer, string session, Stopwatch since)
    {
        ServerView view;
        do
        {
            view = await profile.ViewAsync(observer, session);
            if (!view.Executing) return (since.ElapsedMilliseconds, view);
            await Task.Delay(50);
        }
        while (since.ElapsedMilliseconds < ServerStopTimeoutMs);
        return (null, view);
    }

    private async Task<ScenarioResult> CancelLongQuery(string name, string sql)
    {
        await using var observer = await profile.OpenObserverAsync();
        var (c, connId, session) = await StartAsync();
        await using var _ = c;
        await Execute(c, connId, "q", sql);
        await WaitExecutingAsync(observer, session);

        var sw = Stopwatch.StartNew();
        var cancel = await c.RequestAsync("cancel", new JsonObject { ["query_id"] = "q" });
        var done = await c.WaitDoneAsync("q", DoneTimeoutMs);
        var (stopMs, view) = await WaitStoppedAsync(observer, session, sw);

        var status = done["params"]!["status"]!.GetValue<string>();
        var ack = done["params"]!["cancel"]?["ack_ms"];
        var detail = $"cancel={cancel["state"]} done={status} ack={ack}ms server: {view.Detail}";
        var ok = status == "cancelled" && stopMs is not null;
        return Result(name, ok ? Outcome.Pass : Outcome.Fail, detail, stopMs);
    }

    /// <summary>
    /// Cancel sent right behind execute, before the statement is likely on the wire, where
    /// SqlCommand.Cancel / SQLCancel are no-ops. Repeated to catch the race.
    /// </summary>
    private async Task<ScenarioResult> CancelImmediately()
    {
        const int rounds = 20;
        await using var observer = await profile.OpenObserverAsync();
        var (c, connId, session) = await StartAsync();
        await using var _ = c;
        long worst = 0;
        for (var i = 0; i < rounds; i++)
        {
            var id = "q" + i;
            var sw = Stopwatch.StartNew();
            var exec = c.Send("execute", new JsonObject { ["connection_id"] = connId, ["query_id"] = id, ["sql"] = profile.SleepSql });
            var cancel = c.Send("cancel", new JsonObject { ["query_id"] = id });
            await Task.WhenAll(exec, cancel);
            var done = await c.WaitDoneAsync(id, DoneTimeoutMs);
            var status = done["params"]!["status"]!.GetValue<string>();
            var (stopMs, view) = await WaitStoppedAsync(observer, session, sw);
            if (status != "cancelled" || stopMs is null)
            {
                return Result("cancel_immediately", Outcome.Fail, $"round {i}: done={status} cancel={cancel.Result["result"]?["state"]} server: {view.Detail}");
            }
            worst = Math.Max(worst, stopMs.Value);
        }
        return Result("cancel_immediately", Outcome.Pass, $"{rounds} rounds, all cancelled; worst server stop {worst} ms", worst);
    }

    /// <summary>
    /// The server holds the first statement's result in its send buffer while the second sleeps;
    /// it arrives together with the cancel error. It must still reach the client.
    /// </summary>
    private async Task<ScenarioResult> BatchCancelKeepsResults()
    {
        await using var observer = await profile.OpenObserverAsync();
        var (c, connId, session) = await StartAsync();
        await using var _ = c;
        await Execute(c, connId, "q", profile.BatchThenSleepSql!);
        await WaitExecutingAsync(observer, session);
        var sw = Stopwatch.StartNew();
        await c.RequestAsync("cancel", new JsonObject { ["query_id"] = "q" });
        var done = await c.WaitDoneAsync("q", DoneTimeoutMs);
        var (stopMs, view) = await WaitStoppedAsync(observer, session, sw);
        var status = done["params"]!["status"]!.GetValue<string>();
        var rows = c.RowsReceived("q");
        var detail = $"done={status} first_result_rows={rows} server: {view.Detail}";
        return Result("batch_cancel_keeps_results", status == "cancelled" && rows == 1 && stopMs is not null ? Outcome.Pass : Outcome.Fail, detail, stopMs);
    }

    private async Task<ScenarioResult> StreamingCancel()
    {
        await using var observer = await profile.OpenObserverAsync();
        var (c, connId, session) = await StartAsync();
        await using var _ = c;
        await Execute(c, connId, "q", profile.StreamingSql);
        await c.WaitNotificationAsync(n => n["method"]?.GetValue<string>() == "query/rows", ExecutingTimeoutMs);
        await Task.Delay(500); // let it stream for a bit

        var sw = Stopwatch.StartNew();
        await c.RequestAsync("cancel", new JsonObject { ["query_id"] = "q" });
        var done = await c.WaitDoneAsync("q", DoneTimeoutMs);
        var (stopMs, view) = await WaitStoppedAsync(observer, session, sw);
        var status = done["params"]!["status"]!.GetValue<string>();
        var rows = c.RowsReceived("q");
        var detail = $"done={status} rows_before_cancel={rows} ack={done["params"]!["cancel"]?["ack_ms"]}ms server: {view.Detail}";
        return Result("streaming_cancel", status == "cancelled" && stopMs is not null && rows > 0 ? Outcome.Pass : Outcome.Fail, detail, stopMs);
    }

    /// <summary>
    /// Neovim stops reading (busy, frozen): the backend blocks writing rows to a full pipe.
    /// The cancel must still stop the server while the client is stalled.
    /// </summary>
    private async Task<ScenarioResult> StreamingCancelStalledClient()
    {
        await using var observer = await profile.OpenObserverAsync();
        var (c, connId, session) = await StartAsync();
        await using var _ = c;
        await Execute(c, connId, "q", profile.StreamingSql);
        await c.WaitNotificationAsync(n => n["method"]?.GetValue<string>() == "query/rows", ExecutingTimeoutMs);
        c.PauseReading();
        await Task.Delay(3000); // pipe fills, backend blocks in write

        var sw = Stopwatch.StartNew();
        var cancelResponse = c.Send("cancel", new JsonObject { ["query_id"] = "q" });
        var (stopMs, view) = await WaitStoppedAsync(observer, session, sw);
        c.ResumeReading();
        await cancelResponse.WaitAsync(TimeSpan.FromMilliseconds(DoneTimeoutMs));
        var done = await c.WaitDoneAsync("q", DoneTimeoutMs * 2);
        var status = done["params"]!["status"]!.GetValue<string>();
        var detail = $"done={status} server stopped while client stalled: {(stopMs is null ? "NO" : "yes")} server: {view.Detail}";
        return Result("streaming_cancel_stalled_client", status == "cancelled" && stopMs is not null ? Outcome.Pass : Outcome.Fail, detail, stopMs);
    }

    /// <summary>
    /// Cancel inside an open transaction. Passes when the backend's reported transaction state
    /// matches the server's, and when nothing was committed after rollback.
    /// </summary>
    private async Task<ScenarioResult> TransactionCancel(string name, bool xactAbort)
    {
        await using var observer = await profile.OpenObserverAsync();
        var table = settings.QualifyProbeTable("dbbliss_probe_" + Guid.NewGuid().ToString("N")[..8]);
        await profile.ExecAsync(observer, profile.CreateProbeTableSql(table));
        try
        {
            var (c, connId, session) = await StartAsync();
            await using var _ = c;
            if (xactAbort)
            {
                await Execute(c, connId, "xa", "SET XACT_ABORT ON");
                await c.WaitDoneAsync("xa", DoneTimeoutMs);
            }
            await c.RequestAsync("transaction/begin", new JsonObject { ["connection_id"] = connId });
            await Execute(c, connId, "ins", profile.InsertProbeSql(table));
            var insDone = await c.WaitDoneAsync("ins", DoneTimeoutMs);
            if (insDone["params"]!["status"]!.GetValue<string>() != "completed")
            {
                return Result(name, Outcome.Fail, "insert failed: " + insDone["params"]!["error"]?.ToJsonString());
            }

            await Execute(c, connId, "q", profile.SleepSql);
            await WaitExecutingAsync(observer, session);
            await c.RequestAsync("cancel", new JsonObject { ["query_id"] = "q" });
            var done = await c.WaitDoneAsync("q", DoneTimeoutMs);
            var status = done["params"]!["status"]!.GetValue<string>();
            var reported = done["params"]!["transaction"]?.GetValue<string>();
            var statusCall = await c.RequestAsync("transaction/status", new JsonObject { ["connection_id"] = connId });
            var reportedAgain = statusCall["transaction"]!.GetValue<string>();
            var server = await profile.ViewAsync(observer, session);

            bool consistent;
            if (!profile.SupportsServerTransactionView)
            {
                consistent = true; // DB2 for i: verified manually, see docs/db2i-checklist.md
            }
            else if (profile.Engine == "sqlserver")
            {
                // The observer sees open/none; XACT_STATE (active vs uncommittable) is only visible in-session.
                consistent = (reported != "none") == (server.TransactionState == "open") && reported == reportedAgain;
            }
            else
            {
                consistent = reported == server.TransactionState && reported == reportedAgain;
            }

            // A data-modification probe: the insert must not be visible to others before rollback...
            // Only PostgreSQL reads past the uncommitted row without blocking (MVCC, READ COMMITTED).
            var visibleBefore = profile.Engine == "postgres" ? await profile.CountAsync(observer, table) : -1;
            await c.RequestAsync("transaction/rollback", new JsonObject { ["connection_id"] = connId });
            // ...and must be gone after it. Nothing may have been committed by the cancel.
            var visibleAfter = await profile.CountAsync(observer, table);
            var after = await profile.ViewAsync(observer, session);

            var detail = $"done={status} backend_tx={reported}/{reportedAgain} server_tx={server.TransactionState} " +
                         $"rows_visible_before_rollback={(visibleBefore < 0 ? "n/a" : visibleBefore)} after_rollback={visibleAfter} server_after={after.TransactionState}";
            var ok = status == "cancelled" && consistent && visibleAfter == 0 && visibleBefore <= 0
                     && (after.TransactionState is null or "none");
            return Result(name, ok ? Outcome.Pass : Outcome.Fail, detail);
        }
        finally
        {
            await profile.ExecAsync(observer, profile.DropProbeTableSql(table));
        }
    }

    private async Task<ScenarioResult> BackendDeath(string name, Func<BackendClient, Task> kill, JsonObject? options = null)
    {
        await using var observer = await profile.OpenObserverAsync();
        var (c, connId, session) = await StartAsync(options);
        await using var _ = c;
        await Execute(c, connId, "q", profile.SleepSql);
        await WaitExecutingAsync(observer, session);

        var sw = Stopwatch.StartNew();
        await kill(c);
        var (stopMs, view) = await WaitStoppedAsync(observer, session, sw);
        var exited = await c.WaitExitAsync(10000);
        var detail = $"backend exited={exited} server: {view.Detail}";
        return Result(name, stopMs is not null && exited ? Outcome.Pass : Outcome.Fail, detail, stopMs);
    }

    private Task<ScenarioResult> BackendSigterm()
    {
        if (OperatingSystem.IsWindows())
        {
            return Task.FromResult(Result("backend_sigterm", Outcome.Skip, "no SIGTERM on Windows; covered by stdin close and VimLeavePre"));
        }
        return BackendDeath("backend_sigterm", async c =>
        {
            using var kill = Process.Start("kill", ["-TERM", c.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            await kill.WaitForExitAsync();
        });
    }

    /// <summary>Real Neovim with the real plugin: :qa! (VimLeavePre runs) or a hard kill (backend sees stdin EOF).</summary>
    private async Task<ScenarioResult> NvimDeath(string name, bool kill)
    {
        if (settings.NvimPath is null) return Result(name, Outcome.Skip, "nvim not found");
        await using var observer = await profile.OpenObserverAsync();
        var dir = Directory.CreateTempSubdirectory("dbbliss-nvim-");
        var outFile = Path.Combine(dir.FullName, "out.txt");
        var quitFile = Path.Combine(dir.FullName, "quit");
        var psi = new ProcessStartInfo(settings.NvimPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
                 {
                     "--headless", "--clean", "--cmd", $"set rtp^={settings.PluginRoot.Replace('\\', '/')}",
                     "-c", $"luafile {Path.Combine(settings.PluginRoot, "tests", "nvim", "phase0_driver.lua").Replace('\\', '/')}",
                 })
        {
            psi.ArgumentList.Add(a);
        }
        psi.Environment["DBBLISS_BACKEND"] = settings.BackendPath;
        psi.Environment["DBBLISS_TEST_ENGINE"] = profile.Engine;
        psi.Environment["DBBLISS_TEST_CS"] = profile.ConnectionString;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(profile.PasswordEnv))) psi.Environment["DBBLISS_TEST_PW_ENV"] = profile.PasswordEnv;
        psi.Environment[profile.PasswordEnv] = Environment.GetEnvironmentVariable(profile.PasswordEnv) ?? "";
        psi.Environment["DBBLISS_TEST_SQL"] = profile.SleepSql;
        psi.Environment["DBBLISS_TEST_OUT"] = outFile;
        psi.Environment["DBBLISS_TEST_QUIT_FILE"] = quitFile;
        using var nvim = Process.Start(psi)!;
        try
        {
            var (session, backendPid) = await WaitForDriverAsync(outFile);
            await WaitExecutingAsync(observer, session);

            var sw = Stopwatch.StartNew();
            if (kill) nvim.Kill(entireProcessTree: false);
            else await File.WriteAllTextAsync(quitFile, "");
            var (stopMs, view) = await WaitStoppedAsync(observer, session, sw);
            var nvimExited = await WaitAsync(nvim, 10000);
            var backendGone = await WaitPidGoneAsync(backendPid, 10000);
            var detail = $"nvim exited={nvimExited} backend exited={backendGone} server: {view.Detail}";
            return Result(name, stopMs is not null && nvimExited && backendGone ? Outcome.Pass : Outcome.Fail, detail, stopMs);
        }
        finally
        {
            if (!nvim.HasExited) nvim.Kill(entireProcessTree: true);
            try
            {
                dir.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<(string Session, int BackendPid)> WaitForDriverAsync(string outFile)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20000)
        {
            if (File.Exists(outFile))
            {
                var lines = await File.ReadAllLinesAsync(outFile);
                var err = lines.FirstOrDefault(l => l.StartsWith("error=", StringComparison.Ordinal));
                if (err is not null) throw new InvalidOperationException("nvim driver: " + err);
                var session = lines.FirstOrDefault(l => l.StartsWith("session=", StringComparison.Ordinal));
                var pid = lines.FirstOrDefault(l => l.StartsWith("backend_pid=", StringComparison.Ordinal));
                if (session is not null && pid is not null)
                {
                    return (session["session=".Length..], int.Parse(pid["backend_pid=".Length..], System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("nvim driver did not connect within 20 s");
    }

    private static async Task<bool> WaitAsync(Process p, int ms)
    {
        try
        {
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(ms));
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitPidGoneAsync(int pid, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            await Task.Delay(100);
        }
        return false;
    }
}
