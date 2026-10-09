using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Dbbliss.CancelTests;

/// <summary>Info: a control experiment that documents server behaviour; never fails the run.</summary>
public enum Outcome { Pass, Fail, Skip, Info }

public sealed record ScenarioResult(string Engine, string Scenario, Outcome Outcome, string Detail, long? ServerStopMs = null);

/// <summary>The Phase 0 scenarios. Every one checks the server from an observer connection.</summary>
public sealed partial class Scenarios(EngineProfile profile, Settings settings)
{
    private const int ExecutingTimeoutMs = 15000;
    private const int DoneTimeoutMs = 15000;
    /// <summary>How long the server gets to stop after a cancel (or a client death) before we call it a failure.</summary>
    private const int ServerStopTimeoutMs = 10000;

    private Dictionary<string, string> BackendEnv => new() { [profile.PasswordEnv] = Environment.GetEnvironmentVariable(profile.PasswordEnv) ?? "" };

    public IEnumerable<(string Name, Func<Task<ScenarioResult>> Run)> All()
    {
        // Verifies: HLR-CANCEL-1, HLR-CANCEL-2
        yield return ("sleep_cancel", () => CancelLongQuery("sleep_cancel", profile.SleepSql));
        foreach (var (name, sql) in profile.ExtraLongQueries) yield return (name, () => CancelLongQuery(name, sql));
        if (profile.HasCatalog)
        {
            // Verifies: HLR-CAT-1, HLR-CAT-2, HLR-CAT-3, HLR-CAT-5
            yield return ("catalog_browse_describe_script", CatalogBrowseDescribeScript);
            // Verifies: HLR-CAT-4
            yield return ("catalog_independent_of_user_session", CatalogIndependentOfUserSession);
        }
        // Verifies: HLR-SCRIPT-3
        yield return ("error_line_in_buffer", ErrorLineInBuffer);
        // Verifies: HLR-CANCEL-6, HLR-CANCEL-2
        yield return ("cancel_immediately", CancelImmediately);
        if (profile.BatchThenSleepSql is not null) yield return ("batch_cancel_keeps_results", BatchCancelKeepsResults);  // Verifies: HLR-CANCEL-3
        // Verifies: HLR-CANCEL-2, HLR-CANCEL-3
        yield return ("streaming_cancel", StreamingCancel);
        // Verifies: HLR-CANCEL-4, HLR-DATA-2
        yield return ("streaming_cancel_stalled_client", StreamingCancelStalledClient);
        // Verifies: HLR-CANCEL-4, HLR-PAGE-1, HLR-PAGE-2
        yield return ("paged_cancel", PagedCancel);
        // Verifies: HLR-EXPORT-1, HLR-CANCEL-2
        yield return ("export_cancel", ExportCancel);
        // Verifies: HLR-CANCEL-7
        yield return ("tx_cancel", () => TransactionCancel("tx_cancel", xactAbort: false));
        if (profile.Engine == "sqlserver") yield return ("tx_cancel_xact_abort", () => TransactionCancel("tx_cancel_xact_abort", xactAbort: true));  // Verifies: HLR-CANCEL-7
        if (profile.SupportsServerTransactionView)
        {
            // Verifies: HLR-TX-1, HLR-TX-2
            yield return ("tx_typed_begin", TypedBegin);
            // Verifies: HLR-TX-2
            yield return ("tx_typed_commit_after_api_begin", TypedCommitAfterApiBegin);
            // Verifies: HLR-TX-3
            yield return ("tx_aborted_commit_refused", AbortedCommitRefused);
        }
        if (profile is SqlServerProfile mssql) yield return ("sqlserver_set_options", () => SetOptions(mssql));  // Verifies: LLR-MSSQL-1
        if (profile.UnreachableConnectionString is not null)
        {
            // Verifies: LLR-WIRE-2
            yield return ("connect_failure", ConnectFailure);
            // Verifies: LLR-SESS-1
            yield return ("application_name", ApplicationName);
            // Verifies: HLR-TX-3
            yield return ("tx_misuse", TransactionMisuse);
        }
        // Verifies: LLR-PG-1
        if (profile.Engine == "postgres") yield return ("pg_session_settings", PgSessionSettings);
        // Verifies: HLR-LIFE-1, HLR-TX-4
        yield return ("backend_stdin_closed", () => BackendDeath("backend_stdin_closed", c => { c.CloseStdin(); return Task.CompletedTask; }));
        // Verifies: HLR-LIFE-1
        yield return ("backend_sigterm", BackendSigterm);
        // Verifies: HLR-LIFE-1, LLR-PG-1
        yield return ("backend_killed", () => BackendDeath("backend_killed", c => { c.Kill(); return Task.CompletedTask; }));
        if (profile.Engine == "postgres")
        {
            // Control experiment: shows why the backend enables client_connection_check_interval.
            // Verifies: LLR-PG-1
            yield return ("backend_killed_no_conncheck", async () =>
            {
                var r = await BackendDeath("backend_killed_no_conncheck",
                    c => { c.Kill(); return Task.CompletedTask; }, new JsonObject { ["pg_client_connection_check_interval_ms"] = 0 });
                return r with { Outcome = Outcome.Info, Detail = (r.ServerStopMs is null ? "server kept running (expected): " : "server stopped: ") + r.Detail };
            });
            // Verifies: HLR-LIFE-2
            yield return ("backend_killed_orphan_swept", OrphanSwept);
        }
        // Verifies: HLR-LIFE-1
        yield return ("nvim_quit", () => NvimDeath("nvim_quit", kill: false));
        // Verifies: HLR-LIFE-1
        yield return ("nvim_killed", () => NvimDeath("nvim_killed", kill: true));
    }

    private ScenarioResult Result(string scenario, Outcome o, string detail, long? stopMs = null) =>
        new(profile.Engine, scenario, o, detail, stopMs);

    private async Task<(BackendClient Client, string ConnectionId, string Session)> StartAsync(JsonObject? options = null, string? stateDir = null, string? connectionString = null)
    {
        var env = BackendEnv;
        if (stateDir is not null) env["DBBLISS_STATE_DIR"] = stateDir;
        var client = BackendClient.Start(settings.BackendPath, env);
        await client.RequestAsync("initialize");
        var conn = await client.RequestAsync("connect", new JsonObject
        {
            ["engine"] = profile.Engine,
            ["connection_string"] = connectionString ?? profile.ConnectionString,
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
    /// Phase 1 M2: a query with a row window sends the window and pauses; the server is held by TCP
    /// backpressure, not finished. The cancel must stop it from there.
    /// </summary>
    private async Task<ScenarioResult> PagedCancel()
    {
        await using var observer = await profile.OpenObserverAsync();
        var (c, connId, session) = await StartAsync();
        await using var _ = c;
        const int window = 1000;
        await c.RequestAsync("execute", new JsonObject { ["connection_id"] = connId, ["query_id"] = "q", ["sql"] = profile.StreamingSql, ["window"] = window });
        await c.WaitNotificationAsync(n => n["method"]?.GetValue<string>() == "query/paused", ExecutingTimeoutMs);
        await Task.Delay(1500); // a query that was only pretending to pause would keep sending
        var rowsWhilePaused = c.RowsReceived("q");
        var stillRunning = (await profile.ViewAsync(observer, session)).Executing;

        var sw = Stopwatch.StartNew();
        await c.RequestAsync("cancel", new JsonObject { ["query_id"] = "q" });
        var done = await c.WaitDoneAsync("q", DoneTimeoutMs);
        var (stopMs, view) = await WaitStoppedAsync(observer, session, sw);
        var status = done["params"]!["status"]!.GetValue<string>();
        var ok = status == "cancelled" && stopMs is not null && rowsWhilePaused == window && stillRunning;
        return Result("paged_cancel", ok ? Outcome.Pass : Outcome.Fail,
            $"done={status} rows_while_paused={rowsWhilePaused} (window {window}) server_busy_while_paused={stillRunning} server: {view.Detail}", stopMs);
    }

    /// <summary>
    /// Phase 1 M2: an export of a huge result goes to a file without Neovim, and a cancel stops it
    /// with the file as far as it got, reported as incomplete.
    /// </summary>
    private async Task<ScenarioResult> ExportCancel()
    {
        await using var observer = await profile.OpenObserverAsync();
        var (c, connId, session) = await StartAsync();
        await using var _ = c;
        var dir = Directory.CreateTempSubdirectory("dbbliss-export-");
        var path = Path.Combine(dir.FullName, "out.csv");
        try
        {
            await c.RequestAsync("execute", new JsonObject
            {
                ["connection_id"] = connId, ["query_id"] = "q", ["sql"] = profile.StreamingSql, ["export"] = new JsonObject { ["path"] = path },
            });
            await Task.Delay(1500);
            var sizeBefore = new FileInfo(path).Length;

            var sw = Stopwatch.StartNew();
            await c.RequestAsync("cancel", new JsonObject { ["query_id"] = "q" });
            var done = (await c.WaitDoneAsync("q", DoneTimeoutMs))["params"]!;
            var (stopMs, view) = await WaitStoppedAsync(observer, session, sw);
            var status = done["status"]!.GetValue<string>();
            var export = done["export"];
            var complete = export?["complete"]?.GetValue<bool>();
            var sentToClient = c.RowsReceived("q");
            var ok = status == "cancelled" && stopMs is not null && complete == false && sizeBefore > 0 && sentToClient == 0;
            return Result("export_cancel", ok ? Outcome.Pass : Outcome.Fail,
                $"done={status} file_bytes_before_cancel={sizeBefore} rows_written={export?["rows"]} complete={complete} rows_to_client={sentToClient} server: {view.Detail}", stopMs);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
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

    private static bool ServerHasTransaction(ServerView v) => v.TransactionState is "active" or "aborted" or "open";

    /// <summary>
    /// Phase 1 M1: a failing statement's error is reported on its line in the source buffer. The
    /// statement starts on buffer line 10 (0-based) and fails on its second line, so the buffer line
    /// is 12 (1-based). SQL Server reports a line, and it is the line the failing statement starts on
    /// (not the line of the bad name inside it), so there the failing statement is a second one.
    /// PostgreSQL reports a character offset the backend converts.
    /// </summary>
    private async Task<ScenarioResult> ErrorLineInBuffer()
    {
        const string name = "error_line_in_buffer";
        var (c, connId, _) = await StartAsync();
        await using var _c = c;
        await c.RequestAsync("execute", new JsonObject
        {
            ["connection_id"] = connId,
            ["query_id"] = "e1",
            ["sql"] = profile.Engine == "sqlserver"
                ? "SELECT 1\nSELECT * FROM dbbliss_no_such_table_m1"
                : "SELECT 1\nFROM dbbliss_no_such_table_m1",
            ["line_offset"] = 10,
        });
        var done = (await c.WaitDoneAsync("e1", DoneTimeoutMs))["params"]!;
        var error = done["error"];
        var line = error?["line"]?.GetValue<int>();
        var bufferLine = error?["buffer_line"]?.GetValue<int>();
        var ok = done["status"]?.GetValue<string>() == "error" && line == 2 && bufferLine == 12;
        return Result(name, ok ? Outcome.Pass : Outcome.Fail, $"status={done["status"]}, line={line}, buffer_line={bufferLine} (expected error, 2, 12)");
    }

    private async Task<string?> RunToDoneAsync(BackendClient c, string connId, string queryId, string sql)
    {
        await Execute(c, connId, queryId, sql);
        var done = await c.WaitDoneAsync(queryId, DoneTimeoutMs);
        return done["params"]!["transaction"]?.GetValue<string>();
    }

    private static async Task<string> TxCallAsync(BackendClient c, string connId, string op)
    {
        try
        {
            var r = await c.RequestAsync("transaction/" + op, new JsonObject { ["connection_id"] = connId });
            return r["transaction"]!.GetValue<string>();
        }
        catch (BackendErrorException ex)
        {
            return "error " + ex.Error["code"];
        }
    }

    /// <summary>
    /// spec/quint/client.qnt, bug 1: a BEGIN typed as SQL must be reported, must block a plain
    /// disconnect, and the API rollback must end it.
    /// </summary>
    /// <summary>New SQL Server sessions get SSMS's ARITHABORT ON; <c>mssql_set_options</c> overrides and extends it.</summary>
    private async Task<ScenarioResult> SetOptions(SqlServerProfile mssql)
    {
        const string name = "sqlserver_set_options";
        await using var observer = await mssql.OpenObserverAsync();
        var steps = new List<string>();
        var ok = true;

        async Task Check(string what, JsonObject? options, bool arithAbort, int deadlockPriority)
        {
            var (c, _, session) = await StartAsync(options);
            await using var __ = c;
            var got = await mssql.SessionSettingsAsync(observer, session);
            var good = got == (arithAbort, deadlockPriority);
            steps.Add($"{what}: arithabort={got.ArithAbort} deadlock_priority={got.DeadlockPriority}" + (good ? "" : " (WRONG)"));
            ok &= good;
        }

        await Check("default", null, arithAbort: true, deadlockPriority: 0);
        await Check("ARITHABORT off", new JsonObject { ["mssql_set_options"] = new JsonObject { ["arithabort"] = "OFF" } }, false, 0);
        await Check("extra option", new JsonObject { ["mssql_set_options"] = new JsonObject { ["DEADLOCK_PRIORITY"] = "LOW" } }, true, -5);

        try
        {
            var (c, _, _) = await StartAsync(new JsonObject { ["mssql_set_options"] = new JsonObject { ["ARITHABORT"] = "ON; DROP TABLE x" } });
            await c.DisposeAsync();
            steps.Add("injection: connected (WRONG)");
            ok = false;
        }
        catch (BackendErrorException)
        {
            steps.Add("injection: refused");
        }

        // Well-formed but unknown to the server: the connect must fail, not carry on without it.
        try
        {
            var (c, _, _) = await StartAsync(new JsonObject { ["mssql_set_options"] = new JsonObject { ["NOT_A_SET_OPTION"] = "ON" } });
            await c.DisposeAsync();
            steps.Add("unknown option: connected (WRONG)");
            ok = false;
        }
        catch (BackendErrorException)
        {
            steps.Add("unknown option: refused by the server");
        }
        return Result(name, ok ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }

    private async Task<ScenarioResult> TypedBegin()
    {
        const string name = "tx_typed_begin";
        await using var observer = await profile.OpenObserverAsync();
        var (c, connId, session) = await StartAsync();
        await using var _ = c;
        var steps = new List<string>();
        var ok = true;
        void Step(string what, bool good)
        {
            steps.Add(what + (good ? "" : " (WRONG)"));
            ok &= good;
        }

        var reported = await RunToDoneAsync(c, connId, "b", profile.TypedBeginSql);
        var server = await profile.ViewAsync(observer, session);
        Step($"after typed begin: backend={reported} server={server.TransactionState}", reported == "active" && ServerHasTransaction(server));

        string disconnect;
        try
        {
            await c.RequestAsync("disconnect", new JsonObject { ["connection_id"] = connId, ["rollback"] = false });
            disconnect = "accepted";
        }
        catch (BackendErrorException ex)
        {
            disconnect = "error " + ex.Error["code"];
        }
        server = await profile.ViewAsync(observer, session);
        Step($"plain disconnect: {disconnect}, server={server.TransactionState}", disconnect == "error 1003" && ServerHasTransaction(server));

        if (disconnect != "accepted")
        {
            var rollback = await TxCallAsync(c, connId, "rollback");
            server = await profile.ViewAsync(observer, session);
            Step($"api rollback: {rollback}, server={server.TransactionState}", rollback == "none" && !ServerHasTransaction(server));
        }
        return Result(name, ok ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }

    /// <summary>A transaction opened by the API and ended by typed SQL leaves the connection usable.</summary>
    private async Task<ScenarioResult> TypedCommitAfterApiBegin()
    {
        const string name = "tx_typed_commit_after_api_begin";
        await using var observer = await profile.OpenObserverAsync();
        var (c, connId, session) = await StartAsync();
        await using var _ = c;
        var begin = await TxCallAsync(c, connId, "begin");
        var commitReported = await RunToDoneAsync(c, connId, "c", profile.TypedCommitSql);
        var server = await profile.ViewAsync(observer, session);
        var begin2 = await TxCallAsync(c, connId, "begin");
        var commit2 = await TxCallAsync(c, connId, "commit");
        var after = await profile.ViewAsync(observer, session);
        var ok = begin == "active" && commitReported == "none" && !ServerHasTransaction(server)
                 && begin2 == "active" && commit2 == "none" && !ServerHasTransaction(after);
        return Result(name, ok ? Outcome.Pass : Outcome.Fail,
            $"begin={begin} typed_commit={commitReported} server={server.TransactionState} begin_again={begin2} commit={commit2} server_after={after.TransactionState}");
    }

    /// <summary>
    /// After an error, commit is refused and rollback works. PostgreSQL keeps the transaction open but
    /// aborted (a COMMIT there would silently roll it back). SQL Server rolls it back with the batch:
    /// an uncommittable transaction never outlives the batch that doomed it.
    /// </summary>
    private async Task<ScenarioResult> AbortedCommitRefused()
    {
        const string name = "tx_aborted_commit_refused";
        await using var observer = await profile.OpenObserverAsync();
        var (c, connId, session) = await StartAsync();
        await using var _ = c;
        if (profile.Engine == "sqlserver") await RunToDoneAsync(c, connId, "xa", "SET XACT_ABORT OFF");
        var begin = await TxCallAsync(c, connId, "begin");
        var failSql = profile.Engine == "sqlserver" ? "SELECT CONVERT(int, 'x')" : "SELECT 1/0";
        var failed = await RunToDoneAsync(c, connId, "f", failSql);
        var commit = await TxCallAsync(c, connId, "commit");
        var server = await profile.ViewAsync(observer, session);
        var rollback = await TxCallAsync(c, connId, "rollback");
        var after = await profile.ViewAsync(observer, session);
        var ok = begin == "active" && commit.StartsWith("error", StringComparison.Ordinal) && rollback == "none" && !ServerHasTransaction(after);
        return Result(name, ok ? Outcome.Pass : Outcome.Fail,
            $"begin={begin} after_error={failed} commit={commit} server={server.TransactionState} rollback={rollback} server_after={after.TransactionState}");
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
        if (stopMs is null && exited && profile.Engine == "postgres" && await WarnedNoConnectionCheckAsync(c))
        {
            // A hard kill gives the backend no chance to cancel. Only the server can notice the dead
            // client, through client_connection_check_interval, which a PostgreSQL server on Windows
            // does not support. Nothing on our side can stop the query, so the requirement is that
            // the user was told at connect.
            return Result(name, Outcome.Info,
                "server kept running: it cannot detect a dead client (no client_connection_check_interval); the backend warned at connect, " +
                "and the next connect ends the session (backend_killed_orphan_swept). " + detail);
        }
        return Result(name, stopMs is not null && exited ? Outcome.Pass : Outcome.Fail, detail, stopMs);
    }

    /// <summary>
    /// Decision 19: a backend killed hard leaves its query running when the server cannot detect the
    /// dead client (a Windows PostgreSQL server; here reproduced by turning the connection check off).
    /// The next backend's connect must end that session and say so.
    /// </summary>
    private async Task<ScenarioResult> OrphanSwept()
    {
        const string name = "backend_killed_orphan_swept";
        var stateDir = Directory.CreateTempSubdirectory("dbbliss-state-");
        try
        {
            await using var observer = await profile.OpenObserverAsync();
            // A live backend on the same machine: the sweep must leave its running query alone.
            var (live, connLive, sessionLive) = await StartAsync(stateDir: stateDir.FullName);
            await using var _live = live;
            await Execute(live, connLive, "live", profile.SleepSql);
            await WaitExecutingAsync(observer, sessionLive);
            var (a, connA, sessionA) = await StartAsync(new JsonObject { ["pg_client_connection_check_interval_ms"] = 0 }, stateDir.FullName);
            await using (a)
            {
                await Execute(a, connA, "q", profile.SleepSql);
                await WaitExecutingAsync(observer, sessionA);
                a.Kill();
                await a.WaitExitAsync(10000);
            }
            await Task.Delay(500);
            var before = await profile.ViewAsync(observer, sessionA);
            if (!before.Executing) return Result(name, Outcome.Fail, "precondition failed, the query stopped without the sweep: " + before.Detail);

            var sw = Stopwatch.StartNew();
            var (b, _, _) = await StartAsync(stateDir: stateDir.FullName);
            await using var _ = b;
            string? warning = null;
            try
            {
                var n = await b.WaitNotificationAsync(m => m["method"]?.GetValue<string>() == "connection/message"
                    && (m["params"]?["text"]?.GetValue<string>() ?? "").Contains($"pid {sessionA},", StringComparison.Ordinal), 5000);
                warning = n["params"]!["text"]!.GetValue<string>();
            }
            catch (TimeoutException)
            {
            }
            ServerView view;
            do
            {
                view = await profile.ViewAsync(observer, sessionA);
                if (view.Detail == "session gone") break;
                await Task.Delay(50);
            }
            while (sw.ElapsedMilliseconds < ServerStopTimeoutMs);
            var gone = view.Detail == "session gone";
            var liveView = await profile.ViewAsync(observer, sessionLive);
            var ok = gone && warning is not null && liveView.Executing;
            return Result(name, ok ? Outcome.Pass : Outcome.Fail,
                $"orphan {(gone ? "ended" : "still there: " + view.Detail)}; live backend's query {(liveView.Executing ? "untouched" : "WRONGLY ENDED: " + liveView.Detail)}; " +
                $"user told: {warning ?? "nothing"}", gone ? sw.ElapsedMilliseconds : null);
        }
        finally
        {
            try
            {
                stateDir.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<bool> WarnedNoConnectionCheckAsync(BackendClient c)
    {
        try
        {
            await c.WaitNotificationAsync(n => n["method"]?.GetValue<string>() == "connection/message"
                && n["params"]?["severity"]?.GetValue<string>() == "warning"
                && (n["params"]?["text"]?.GetValue<string>() ?? "").Contains("client_connection_check_interval", StringComparison.Ordinal), 1000);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
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
