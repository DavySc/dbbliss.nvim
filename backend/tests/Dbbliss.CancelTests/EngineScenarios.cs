using System.Text.Json.Nodes;

namespace Dbbliss.CancelTests;

/// <summary>What the engines do at the edges of a session: refusing to connect, naming themselves, misuse of transactions, settings.</summary>
public sealed partial class Scenarios
{
    /// <summary>Runs a statement and returns the first value of its first row, as text.</summary>
    private async Task<string?> FirstValueAsync(BackendClient c, string connId, string queryId, string sql)
    {
        await Execute(c, connId, queryId, sql);
        var rows = await c.WaitNotificationAsync(n => n["method"]?.GetValue<string>() == "query/rows" && n["params"]?["query_id"]?.GetValue<string>() == queryId, DoneTimeoutMs);
        var done = (await c.WaitDoneAsync(queryId, DoneTimeoutMs))["params"]!;
        if (done["status"]!.GetValue<string>() != "completed") throw new InvalidOperationException($"{sql} did not complete: {done["error"]?["message"]}");
        return rows["params"]!["rows"]![0]![0]?.ToString();
    }

    private (List<string> Steps, Action<string, bool> Step, Func<bool> Ok) Steps()
    {
        var steps = new List<string>();
        var ok = true;
        return (steps, (what, good) => { steps.Add(what + (good ? "" : " (WRONG)")); ok &= good; }, () => ok);
    }

    /// <summary>A refused connection is a database error carrying the driver's message; the backend stays usable.</summary>
    private async Task<ScenarioResult> ConnectFailure()
    {
        var (steps, step, ok) = Steps();
        await using var client = BackendClient.Start(settings.BackendPath, BackendEnv);
        await client.RequestAsync("initialize");
        try
        {
            await client.RequestAsync("connect", new JsonObject
            {
                ["engine"] = profile.Engine,
                ["connection_string"] = profile.UnreachableConnectionString,
            }, timeoutMs: 60000);
            step("connected to nothing", false);
        }
        catch (BackendErrorException ex)
        {
            var code = ex.Error["code"]?.GetValue<int>();
            step($"error {code}: {ex.Message.Split('\n')[0]}", code == 1005 && ex.Message.Length > 0);
        }
        step("backend still answers", (await client.RequestAsync("initialize"))["protocol"] is not null);
        return Result("connect_failure", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }

    /// <summary>Sessions name themselves for the DBA; a name the user chose is kept.</summary>
    private async Task<ScenarioResult> ApplicationName()
    {
        var (steps, step, ok) = Steps();
        var (c, conn, _) = await StartAsync();
        await using (c)
        {
            var name = await FirstValueAsync(c, conn, "a1", profile.ApplicationNameSql!);
            step($"default \"{name}\"", name is not null && (profile.Engine == "postgres" ? name.StartsWith("dbbliss.nvim ", StringComparison.Ordinal) : name == "dbbliss.nvim"));
        }
        var (c2, conn2, _) = await StartAsync(connectionString: profile.ConnectionString.TrimEnd(';') + ";Application Name=my-own-tool");
        await using (c2)
        {
            var mine = await FirstValueAsync(c2, conn2, "a2", profile.ApplicationNameSql!);
            step($"user's \"{mine}\"", mine == "my-own-tool");
        }
        return Result("application_name", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }

    /// <summary>A transaction cannot be opened twice or committed when none is open: both are refused with a message, and rollback of nothing is harmless.</summary>
    private async Task<ScenarioResult> TransactionMisuse()
    {
        var (steps, step, ok) = Steps();
        var (c, conn, _) = await StartAsync();
        await using var _c = c;

        async Task<string?> Refused(string method)
        {
            try
            {
                await c.RequestAsync(method, new JsonObject { ["connection_id"] = conn });
                return null;
            }
            catch (BackendErrorException ex)
            {
                return ex.Message;
            }
        }

        var noneCommit = await Refused("transaction/commit");
        step($"commit with none open: {noneCommit}", noneCommit is not null && noneCommit.Contains("No open transaction", StringComparison.Ordinal));
        step("rollback with none open is harmless", await Refused("transaction/rollback") is null);
        step("begin", await Refused("transaction/begin") is null);
        var twice = await Refused("transaction/begin");
        step($"second begin: {twice}", twice is not null && twice.Contains("already open", StringComparison.Ordinal));
        step("commit", await Refused("transaction/commit") is null);
        var again = await Refused("transaction/commit");
        step("second commit refused", again is not null);
        return Result("tx_misuse", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }

    /// <summary>
    /// client_connection_check_interval is set on every session unless turned off; a negative value is
    /// refused. Servers on Windows have no such setting at all (every non-zero value is rejected, with a warning).
    /// </summary>
    private async Task<ScenarioResult> PgSessionSettings()
    {
        var (steps, step, ok) = Steps();
        foreach (var (ms, expected) in new (int?, string)[] { (null, "2s"), (5000, "5s"), (0, "0") })
        {
            var options = ms is null ? null : new JsonObject { ["pg_client_connection_check_interval_ms"] = ms };
            var (c, conn, _) = await StartAsync(options);
            await using var _c = c;
            var value = await FirstValueAsync(c, conn, "s", "SHOW client_connection_check_interval");
            var want = OperatingSystem.IsWindows() ? "0" : expected;
            step($"option {(ms is null ? "default" : ms.ToString())}: {value}", value == want);
        }
        // A value that makes no sense is refused at connect, not ignored.
        try
        {
            var (bad, _, _) = await StartAsync(new JsonObject { ["pg_client_connection_check_interval_ms"] = -5 });
            await bad.DisposeAsync();
            step("a negative interval: connected", false);
        }
        catch (BackendErrorException ex)
        {
            step($"a negative interval: refused ({ex.Message})", ex.Message.Contains("pg_client_connection_check_interval_ms", StringComparison.Ordinal));
        }
        return Result("pg_session_settings", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }

    /// <summary>A failed statement inside a TRY with XACT_ABORT ON dooms the transaction (XACT_STATE -1): it is reported as aborted, refuses commit, and rolls back.</summary>
    private async Task<ScenarioResult> SqlServerUncommittable()
    {
        var (steps, step, ok) = Steps();
        var (c, conn, _) = await StartAsync();
        await using var _c = c;
        await Execute(c, conn, "doom", "SET XACT_ABORT ON; BEGIN TRANSACTION; BEGIN TRY SELECT 1/0; END TRY BEGIN CATCH SELECT 1 AS caught; END CATCH");
        var done = (await c.WaitDoneAsync("doom", DoneTimeoutMs))["params"]!;
        step($"after the failed statement: {done["transaction"]}", done["transaction"]?.GetValue<string>() == "aborted");
        string? message = null;
        try
        {
            await c.RequestAsync("transaction/commit", new JsonObject { ["connection_id"] = conn });
        }
        catch (BackendErrorException ex)
        {
            message = ex.Message;
        }
        step($"commit: {message}", message is not null && message.Contains("uncommittable", StringComparison.Ordinal));
        await c.RequestAsync("transaction/rollback", new JsonObject { ["connection_id"] = conn });
        var status = await c.RequestAsync("transaction/status", new JsonObject { ["connection_id"] = conn });
        step($"after rollback: {status["transaction"]}", status["transaction"]?.GetValue<string>() == "none");
        return Result("sqlserver_uncommittable_transaction", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }
}
