using System.Data.Common;
using System.Text.Json.Nodes;

namespace Dbbliss.CancelTests;

/// <summary>Phase 3: listing, cancelling and ending other sessions, against a real server.</summary>
public sealed partial class Scenarios
{
    private const string OldIdentity = "2000-01-01T00:00:00.0000000Z";

    /// <summary>Runs a statement on a connection of the test's own; returns what it failed with, or null.</summary>
    private static async Task<Exception?> RunAsync(DbConnection c, string sql)
    {
        try
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<bool> FinishesAsync(Task task, int ms = 10000) => await Task.WhenAny(task, Task.Delay(ms)) == task;

    private static JsonObject? Row(JsonObject list, string id) =>
        list["sessions"]!.AsArray().Select(r => r!.AsObject()).FirstOrDefault(r => r["id"]!.GetValue<string>() == id);

    private static async Task<int?> ErrorCodeAsync(Task<JsonObject> request)
    {
        try
        {
            await request;
            return null;
        }
        catch (BackendErrorException ex)
        {
            return ex.Error["code"]?.GetValue<int>();
        }
    }

    private static JsonObject Target(string conn, string id, string identity) => new() { ["connection_id"] = conn, ["id"] = id, ["identity"] = identity };

    /// <summary>
    /// Another session runs a long statement. The list shows it with its query; the connection's own
    /// sessions are marked and refused; a wrong identity reaches nothing; cancel stops the statement and
    /// leaves the session on PostgreSQL, and is refused on SQL Server, where only KILL exists.
    /// </summary>
    private async Task<ScenarioResult> SessionsListAndCancel()
    {
        var (steps, step, ok) = Steps();
        await using var observer = await profile.OpenObserverAsync();
        await using var victim = await profile.OpenObserverAsync();
        var victimId = await profile.ServerIdAsync(victim);
        var (c, conn, userSession) = await StartAsync();
        await using var _c = c;
        var running = RunAsync(victim, profile.SleepSql);
        await WaitExecutingAsync(observer, victimId);

        var list = await c.RequestAsync("sessions/list", new JsonObject { ["connection_id"] = conn });
        var row = Row(list, victimId);
        step("the running session is listed", row is not null);
        if (row is null) return Result("sessions_list_cancel", Outcome.Fail, string.Join("; ", steps));
        var state = row["state"]!.GetValue<string>();
        // Each engine's own word: PostgreSQL "active", SQL Server "suspended" for a WAITFOR.
        step($"state {state}", state is "active" or "running" or "suspended");
        step($"duration {row["duration_ms"]}", row["duration_ms"]?.GetValue<long>() >= 0);
        step("its query is shown", row["query"]?.GetValue<string>()?.Contains(profile.SleepSql, StringComparison.OrdinalIgnoreCase) == true);
        step("it has a user and an identity", !string.IsNullOrEmpty(row["user"]?.GetValue<string>()) && !string.IsNullOrEmpty(row["identity"]?.GetValue<string>()));
        var catalogSession = list["catalog_session"]!.GetValue<string>();
        var own = list["sessions"]!.AsArray().Where(r => r!["own"]!.GetValue<bool>()).Select(r => r!["id"]!.GetValue<string>()).Order().ToArray();
        step($"own sessions [{string.Join(",", own)}]", own.SequenceEqual(new[] { userSession, catalogSession }.Order()));
        step("the victim is not marked own", !row["own"]!.GetValue<bool>());
        step("can_cancel", list["can_cancel"]!.GetValue<bool>() == (profile.Engine == "postgres"));

        var identity = row["identity"]!.GetValue<string>();
        step("own session refused", await ErrorCodeAsync(c.RequestAsync("sessions/terminate", Target(conn, userSession, "x"))) == 1008);
        step("wrong identity refused (cancel)", await ErrorCodeAsync(c.RequestAsync("sessions/cancel", Target(conn, victimId, OldIdentity))) is 1008);
        step("wrong identity refused (terminate)", await ErrorCodeAsync(c.RequestAsync("sessions/terminate", Target(conn, victimId, OldIdentity))) == 1008);
        step("garbage id refused", await ErrorCodeAsync(c.RequestAsync("sessions/terminate", Target(conn, "1; SHUTDOWN", identity))) == 1008);
        step("the victim still runs", (await profile.ViewAsync(observer, victimId)).Executing);

        if (profile.Engine == "postgres")
        {
            var done = await c.RequestAsync("sessions/cancel", Target(conn, victimId, identity));
            step("cancel reported done", done["done"]!.GetValue<bool>());
            step("the statement ended", await FinishesAsync(running));
            var failure = running.IsCompletedSuccessfully ? running.Result : null;
            step($"with query_canceled ({failure?.GetType().Name})", failure is Npgsql.PostgresException { SqlState: "57014" });
            step("the session lives", await RunAsync(victim, "SELECT 1") is null);
        }
        else
        {
            step("cancel refused", await ErrorCodeAsync(c.RequestAsync("sessions/cancel", Target(conn, victimId, identity))) == 1008);
            step("the victim still runs after the refusal", (await profile.ViewAsync(observer, victimId)).Executing);
            var done = await c.RequestAsync("sessions/terminate", Target(conn, victimId, identity));
            step("terminate reported done", done["done"]!.GetValue<bool>());
            step("the statement ended", await FinishesAsync(running));
        }
        return Result("sessions_list_cancel", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }

    /// <summary>
    /// A session with an uncommitted insert is ended: the server rolls the insert back (nothing is
    /// committed), the connection is dead, and status reports it gone.
    /// </summary>
    private async Task<ScenarioResult> SessionsTerminateRollsBack()
    {
        var (steps, step, ok) = Steps();
        await using var observer = await profile.OpenObserverAsync();
        await using var victim = await profile.OpenObserverAsync();
        var table = settings.QualifyProbeTable("dbbliss_probe_" + Guid.NewGuid().ToString("N")[..8]);
        await profile.ExecAsync(observer, profile.CreateProbeTableSql(table));
        try
        {
            var victimId = await profile.ServerIdAsync(victim);
            var (c, conn, _) = await StartAsync();
            await using var _c = c;
            await profile.ExecAsync(victim, profile.TypedBeginSql);
            await profile.ExecAsync(victim, profile.InsertProbeSql(table));

            var list = await c.RequestAsync("sessions/list", new JsonObject { ["connection_id"] = conn });
            var row = Row(list, victimId);
            step("listed with an open transaction", row?["in_transaction"]?.GetValue<bool>() == true);
            if (row is null) return Result("sessions_terminate_rolls_back", Outcome.Fail, string.Join("; ", steps));
            var identity = row["identity"]!.GetValue<string>();
            var before = await c.RequestAsync("sessions/status", Target(conn, victimId, identity));
            step("status before: present", before["present"]!.GetValue<bool>());
            step($"status before: no rollback in progress ({before["detail"]})", before["detail"] is null);

            var done = await c.RequestAsync("sessions/terminate", Target(conn, victimId, identity));
            step("terminate reported done", done["done"]!.GetValue<bool>());
            var gone = false;
            for (var waited = 0; waited < 10000 && !gone; waited += 100)
            {
                gone = !(await c.RequestAsync("sessions/status", Target(conn, victimId, identity)))["present"]!.GetValue<bool>();
                if (!gone) await Task.Delay(100);
            }
            step("status reports the session gone", gone);
            step("nothing was committed", await profile.CountAsync(observer, table) == 0);
            step("the victim's connection is dead", await RunAsync(victim, "SELECT 1") is not null);
            step("terminating it again is refused", await ErrorCodeAsync(c.RequestAsync("sessions/terminate", Target(conn, victimId, identity))) == 1008);
        }
        finally
        {
            await profile.ExecAsync(observer, profile.DropProbeTableSql(table));
        }
        return Result("sessions_terminate_rolls_back", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }

    /// <summary>
    /// SQL Server only: a session with a large uncommitted insert is killed, and status shows the
    /// server's rollback progress (KILL ... WITH STATUSONLY) while it lasts.
    /// </summary>
    private async Task<ScenarioResult> SessionsRollbackProgress()
    {
        var (steps, step, ok) = Steps();
        await using var observer = await profile.OpenObserverAsync();
        await using var victim = await profile.OpenObserverAsync();
        var table = settings.QualifyProbeTable("dbbliss_probe_" + Guid.NewGuid().ToString("N")[..8]);
        await profile.ExecAsync(observer, profile.CreateProbeTableSql(table));
        try
        {
            var victimId = await profile.ServerIdAsync(victim);
            var (c, conn, _) = await StartAsync();
            await using var _c = c;
            await profile.ExecAsync(victim, profile.TypedBeginSql);
            await profile.ExecAsync(victim, $"INSERT INTO {table} (id) SELECT TOP (4000000) 1 FROM sys.all_columns a CROSS JOIN sys.all_columns b");
            var row = Row(await c.RequestAsync("sessions/list", new JsonObject { ["connection_id"] = conn }), victimId);
            step("listed", row is not null);
            if (row is null) return Result("sessions_rollback_progress", Outcome.Fail, string.Join("; ", steps));
            var identity = row["identity"]!.GetValue<string>();
            await c.RequestAsync("sessions/terminate", Target(conn, victimId, identity));
            string? progress = null;
            var present = true;
            for (var waited = 0; waited < 30000 && present; waited += 50)
            {
                var status = await c.RequestAsync("sessions/status", Target(conn, victimId, identity));
                present = status["present"]!.GetValue<bool>();
                progress ??= status["detail"]?.GetValue<string>();
                if (present) await Task.Delay(50);
            }
            step($"progress seen while rolling back ({progress})", progress?.Contains("rollback", StringComparison.OrdinalIgnoreCase) == true);
            step("the session is gone at the end", !present);
            step("nothing was committed", await profile.CountAsync(observer, table) == 0);
        }
        finally
        {
            await profile.ExecAsync(observer, profile.DropProbeTableSql(table));
        }
        return Result("sessions_rollback_progress", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }

    /// <summary>
    /// A login that may look at sessions but not signal them: the server's refusal reaches the user
    /// (code 1008, the server's words), the victim is untouched, and the connection stays usable.
    /// </summary>
    private async Task<ScenarioResult> SessionsLimitedUser()
    {
        var (steps, step, ok) = Steps();
        await using var observer = await profile.OpenObserverAsync();
        var limited = await profile.CreateLimitedUserAsync(observer);
        if (limited is null) return Result("sessions_limited_user", Outcome.Skip, "no limited login for this engine");
        try
        {
            await using var victim = await profile.OpenObserverAsync();
            var victimId = await profile.ServerIdAsync(victim);
            var running = RunAsync(victim, profile.SleepSql);
            await WaitExecutingAsync(observer, victimId);
            async Task EndAsync()
            {
                await profile.ExecAsync(observer, profile.Engine == "postgres" ? $"SELECT pg_terminate_backend({victimId})" : $"KILL {victimId}");
                await FinishesAsync(running);
            }
            BackendClient c;
            string conn;
            try
            {
                (c, conn, _) = await StartAsync(connectionString: limited.ConnectionString, passwordFromEnv: false);
            }
            catch (BackendErrorException ex) when (profile.Engine == "sqlserver")
            {
                // SQL Server on the Windows runner may allow Windows logins only.
                await EndAsync();
                return Result("sessions_limited_user", Outcome.Skip, "SQL login refused: " + ex.Message.Split('\n')[0]);
            }
            {
                await using var _c = c;
                var list = await c.RequestAsync("sessions/list", new JsonObject { ["connection_id"] = conn });
                var row = Row(list, victimId);
                step("the limited login sees the session", row is not null);
                if (row is not null)
                {
                    var identity = row["identity"]!.GetValue<string>();
                    JsonObject? error = null;
                    try
                    {
                        await c.RequestAsync("sessions/terminate", Target(conn, victimId, identity));
                    }
                    catch (BackendErrorException ex)
                    {
                        error = ex.Error;
                    }
                    step($"terminate refused with 1008: {error?["message"]}", error?["code"]?.GetValue<int>() == 1008);
                    step("the victim still runs", (await profile.ViewAsync(observer, victimId)).Executing);
                }
                step("the connection still answers", (await c.RequestAsync("sessions/list", new JsonObject { ["connection_id"] = conn }))["sessions"] is JsonArray);
            }
            await EndAsync();
            step("cleanup ended the victim", running.IsCompleted);
        }
        finally
        {
            await limited.DropAsync();
        }
        return Result("sessions_limited_user", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }
}
