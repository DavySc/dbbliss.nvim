using System.Text.Json.Nodes;

namespace Dbbliss.CancelTests;

/// <summary>Phase 4: backup, restore and drop against a real server.</summary>
public sealed partial class Scenarios
{
    private const string Scratch = "dbbliss_bkp";
    private const string Restored = "dbbliss_bkp_restored";
    private const int BackupTimeoutMs = 180000;

    private async Task<string> BackupPathAsync(BackendClient c, string conn, string stem)
    {
        if (profile.Engine == "postgres")
        {
            var dir = Path.Combine(Path.GetTempPath(), "dbbliss-bkp-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, stem + ".dump");
        }
        var directory = (await c.RequestAsync("backup/defaults", new JsonObject { ["connection_id"] = conn }))["directory"]?.GetValue<string>()
            ?? throw new InvalidOperationException("the server has no default backup folder");
        return directory.TrimEnd('/', '\\') + (directory.Contains('\\', StringComparison.Ordinal) ? "\\" : "/") + stem + ".bak";
    }

    private static JsonObject BackupParams(string conn, string database, string path, bool overwrite = false)
    {
        var p = new JsonObject { ["connection_id"] = conn, ["database"] = database, ["path"] = path };
        if (overwrite) p["overwrite"] = true;
        return p;
    }

    private static bool IsDone(JsonObject n, string id) => n["method"]?.GetValue<string>() == "backup/done" && n["params"]?["backup_id"]?.GetValue<string>() == id;

    private static bool IsProgress(JsonObject n, string id) => n["method"]?.GetValue<string>() == "backup/progress" && n["params"]?["backup_id"]?.GetValue<string>() == id;

    /// <summary>Starts a backup and waits for its end.</summary>
    private static async Task<(string Id, JsonObject Done)> BackupAsync(BackendClient c, string conn, string database, string path, bool overwrite = false)
    {
        var id = (await c.RequestAsync("backup/start", BackupParams(conn, database, path, overwrite)))["backup_id"]!.GetValue<string>();
        return (id, (await c.WaitNotificationAsync(n => IsDone(n, id), BackupTimeoutMs))["params"]!.AsObject());
    }

    /// <summary>True when no backup of ours is running on the server any more (pg_dump's session, a BACKUP request).</summary>
    private async Task<bool> NoBackupRunningAsync(System.Data.Common.DbConnection observer, string database, int waitMs = 15000)
    {
        var sql = profile.Engine == "postgres"
            ? "SELECT count(*) FROM pg_stat_activity WHERE application_name = 'pg_dump' AND datname = @d"
            : "SELECT count(*) FROM sys.dm_exec_requests WHERE command LIKE 'BACKUP%' AND database_id = DB_ID(@d)";
        for (var waited = 0; waited < waitMs; waited += 200)
        {
            var (_, v) = await FirstRowAsync2(observer, sql, database);
            if (Convert.ToInt64(v[0], System.Globalization.CultureInfo.InvariantCulture) == 0) return true;
            await Task.Delay(200);
        }
        return false;
    }

    private static async Task<(bool, object?[])> FirstRowAsync2(System.Data.Common.DbConnection c, string sql, string database)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var p = cmd.CreateParameter();
        p.ParameterName = "d";
        p.Value = database;
        cmd.Parameters.Add(p);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (false, []);
        var values = new object?[r.FieldCount];
        for (var i = 0; i < values.Length; i++) values[i] = r.IsDBNull(i) ? null : r.GetValue(i);
        return (true, values);
    }

    /// <summary>Whether the backup file is on disk (this machine for PostgreSQL, the server for SQL Server).</summary>
    private async Task<bool> BackupFileExistsAsync(System.Data.Common.DbConnection observer, string path)
    {
        if (profile.Engine == "postgres") return File.Exists(path);
        await using var cmd = observer.CreateCommand();
        cmd.CommandText = "EXEC master.sys.xp_fileexist @p";
        var p = cmd.CreateParameter();
        p.ParameterName = "@p";
        p.Value = path;
        cmd.Parameters.Add(p);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() && Convert.ToInt64(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static void CleanUp(string path)
    {
        try
        {
            if (Path.GetFileName(Path.GetDirectoryName(path))?.StartsWith("dbbliss-bkp-", StringComparison.Ordinal) == true) Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// A backup of a scratch database is verified by the backend and then restored by the test into another
    /// database, which holds the same rows; SQL Server's msdb says it is copy-only and checksummed. A second
    /// backup to the same file is refused unless overwriting is asked for.
    /// </summary>
    private async Task<ScenarioResult> BackupRestoreRoundTrip()
    {
        var (steps, step, ok) = Steps();
        const int rows = 20000;
        await using var observer = await profile.OpenObserverAsync();
        await profile.CreateScratchAsync(observer, Scratch, rows);
        var (c, conn, _) = await StartAsync();
        await using var _c = c;
        var path = await BackupPathAsync(c, conn, "roundtrip-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var (id, done) = await BackupAsync(c, conn, Scratch, path);
            step($"status {done["status"]} {done["error"]}", done["status"]!.GetValue<string>() == "completed" && done["verified"]!.GetValue<bool>());
            step("the file is named", done["path"]!.GetValue<string>().Replace('\\', '/').EndsWith(path.Replace('\\', '/')[path.Replace('\\', '/').LastIndexOf('/')..], StringComparison.Ordinal));
            var progress = c.Notifications(n => IsProgress(n, id));
            var phases = progress.Select(n => n["params"]!["phase"]!.GetValue<string>()).Distinct().ToArray();
            step($"progress phases [{string.Join(",", phases)}]", profile.Engine == "postgres" ? phases.Contains("dump") && phases.Contains("verify") : phases.Contains("backup") && phases.Contains("verify"));
            if (profile.Engine == "sqlserver")
            {
                step("percent progress", progress.Any(n => n["params"]!["percent"] is not null));
                var (_, facts) = await FirstRowAsync2(observer,
                    "SELECT bs.is_copy_only, bs.has_backup_checksums FROM msdb.dbo.backupset bs JOIN msdb.dbo.backupmediafamily mf ON mf.media_set_id = bs.media_set_id WHERE mf.physical_device_name = @d", path);
                step($"msdb: copy-only and checksummed ({string.Join(",", facts)})", facts.Length == 2 && Convert.ToInt32(facts[0], System.Globalization.CultureInfo.InvariantCulture) == 1 && Convert.ToInt32(facts[1], System.Globalization.CultureInfo.InvariantCulture) == 1);
            }
            else
            {
                step($"size {done["bytes"]} = file", done["bytes"]!.GetValue<long>() == new FileInfo(path).Length && new FileInfo(path).Length > 0);
            }
            var restored = await profile.RestoreAndCountAsync(observer, path, Restored);
            step($"restored copy holds {restored} rows", restored == rows);

            var refusal = await ErrorCodeAsync(c.RequestAsync("backup/start", BackupParams(conn, Scratch, path)));
            step("a second backup to the same file is refused", refusal == 1009);
            var (_, again) = await BackupAsync(c, conn, Scratch, path, overwrite: true);
            step("overwriting is done when asked", again["status"]!.GetValue<string>() == "completed");
        }
        finally
        {
            await profile.DropScratchAsync(observer, Scratch);
            await profile.DropScratchAsync(observer, Restored);
            CleanUp(path);
        }
        return Result("backup_restore_round_trip", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }

    /// <summary>
    /// A long backup: refuses a second backup, a drop and a disconnect; cancel ends the tool on the server's
    /// side and leaves no partial file; and closing the backend's stdin mid-backup ends it too.
    /// </summary>
    private async Task<ScenarioResult> BackupCancelAndShutdown()
    {
        var (steps, step, ok) = Steps();
        await using var observer = await profile.OpenObserverAsync();
        await profile.CreateScratchAsync(observer, Scratch, profile.Engine == "postgres" ? 2000000 : 1500000);
        var (c, conn, _) = await StartAsync();
        await using var _c = c;
        var path = await BackupPathAsync(c, conn, "cancel-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var id = (await c.RequestAsync("backup/start", BackupParams(conn, Scratch, path)))["backup_id"]!.GetValue<string>();
            await c.WaitNotificationAsync(n => IsProgress(n, id) && (profile.Engine == "postgres" ? n["params"]!["text"]!.GetValue<string>().Contains("contents of table", StringComparison.Ordinal) : n["params"]!["percent"] is not null), 60000);
            step("a second backup is refused as busy", await ErrorCodeAsync(c.RequestAsync("backup/start", BackupParams(conn, Scratch, path + "2"))) == 1002);
            step("a drop is refused as busy", await ErrorCodeAsync(c.RequestAsync("management/drop", new JsonObject
            {
                ["connection_id"] = conn, ["kind"] = "table", ["database"] = Scratch, ["schema"] = profile.Engine == "postgres" ? "public" : "dbo", ["name"] = "child", ["confirm_name"] = "child",
            })) == 1002);
            step("a disconnect is refused as busy", await ErrorCodeAsync(c.RequestAsync("disconnect", new JsonObject { ["connection_id"] = conn, ["rollback"] = true })) == 1002);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await c.RequestAsync("backup/cancel", new JsonObject { ["backup_id"] = id });
            var done = (await c.WaitNotificationAsync(n => IsDone(n, id), 60000))["params"]!;
            step($"cancelled in {sw.ElapsedMilliseconds} ms (status {done["status"]})", done["status"]!.GetValue<string>() == "cancelled" && !done["verified"]!.GetValue<bool>());
            step("the tool is gone from the server", await NoBackupRunningAsync(observer, Scratch));
            if (profile.Engine == "postgres") step("no partial file", !File.Exists(path));
            else step("done names the partial file the server may have left", done["note"]?.GetValue<string>()?.Contains(path, StringComparison.Ordinal) == true);

            // Shutdown mid-backup: the tool must not outlive the backend.
            var path2 = profile.Engine == "postgres" ? path : path.Replace("cancel-", "shutdown-", StringComparison.Ordinal);
            var id2 = (await c.RequestAsync("backup/start", BackupParams(conn, Scratch, path2)))["backup_id"]!.GetValue<string>();
            await c.WaitNotificationAsync(n => IsProgress(n, id2) && (profile.Engine == "postgres" ? n["params"]!["text"]!.GetValue<string>().Contains("contents of table", StringComparison.Ordinal) : n["params"]!["percent"] is not null), 60000);
            c.CloseStdin();
            step("the backend exits", await c.WaitExitAsync(30000));
            step("the tool is gone from the server after shutdown", await NoBackupRunningAsync(observer, Scratch));
            if (profile.Engine == "postgres") step("no partial file after shutdown", !File.Exists(path2));
        }
        finally
        {
            await profile.DropScratchAsync(observer, Scratch);
            CleanUp(path);
        }
        return Result("backup_cancel_and_shutdown", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }

    /// <summary>Refusals that come before anything runs: a tool that is not there (PostgreSQL), a database that does not exist.</summary>
    private async Task<ScenarioResult> BackupRefusals()
    {
        var (steps, step, ok) = Steps();
        await using var observer = await profile.OpenObserverAsync();
        if (profile.Engine == "postgres")
        {
            var (c, conn, _) = await StartAsync(options: new JsonObject { ["pg_dump"] = Path.Combine(Path.GetTempPath(), "dbbliss-no-such-folder") });
            await using var _c = c;
            var path = await BackupPathAsync(c, conn, "x");
            try
            {
                await c.RequestAsync("backup/start", BackupParams(conn, "postgres", path));
                step("a missing pg_dump was accepted", false);
            }
            catch (BackendErrorException ex)
            {
                step($"missing pg_dump: {ex.Error["code"]} {ex.Message.Split('\n')[0]}", ex.Error["code"]?.GetValue<int>() == 1009 && ex.Message.Contains("pg_dump", StringComparison.Ordinal));
            }
            CleanUp(path);
        }
        {
            var (c, conn, _) = await StartAsync();
            await using var _c = c;
            var path = await BackupPathAsync(c, conn, "nodb");
            step("a database that does not exist is refused", profile.Engine == "sqlserver"
                ? await ErrorCodeAsync(c.RequestAsync("backup/start", BackupParams(conn, "dbbliss_no_such_db", path))) == 1009
                : true);   // pg_dump itself fails for PostgreSQL; covered below
            if (profile.Engine == "postgres")
            {
                var (_, done) = await BackupAsync(c, conn, "dbbliss_no_such_db", path);
                step($"pg_dump on a missing database fails with its words: {done["error"]}", done["status"]!.GetValue<string>() == "failed" && done["error"]!.GetValue<string>().Contains("dbbliss_no_such_db", StringComparison.Ordinal) && !File.Exists(path));
            }
            CleanUp(path);
        }
        return Result("backup_refusals", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }

    /// <summary>
    /// Drops with the typed name, on a real server: objects of every kind, a name with quote characters, the
    /// server's refusal when something depends on the object or a session is in the database, and the prod rule.
    /// </summary>
    private async Task<ScenarioResult> DropObjectsAndDatabases()
    {
        var (steps, step, ok) = Steps();
        const string second = "dbbliss_drop";
        await using var observer = await profile.OpenObserverAsync();
        await profile.CreateScratchAsync(observer, Scratch, 100);
        await profile.CreateScratchAsync(observer, second, 10);
        var schema = profile.Engine == "postgres" ? "public" : "dbo";
        JsonObject Drop(string conn, string kind, string database, string name, string? confirm = null, string? identity = null, string? backup = null)
        {
            var p = new JsonObject { ["connection_id"] = conn, ["kind"] = kind, ["database"] = database, ["name"] = name, ["confirm_name"] = confirm ?? name };
            if (kind != "database") p["schema"] = schema;
            if (identity is not null) p["identity"] = identity;
            if (backup is not null) p["backup_id"] = backup;
            return p;
        }
        try
        {
            var (c, conn, _) = await StartAsync(connectionString: profile.ScratchConnectionString(Scratch));
            await using var _c = c;
            await using var scratch = await profile.OpenObserverAsync(Scratch);

            step("a wrong typed name is refused", await ErrorCodeAsync(c.RequestAsync("management/drop", Drop(conn, "view", Scratch, "v_probe", confirm: "V_PROBE"))) == 1009 && await profile.ExistsAsync(scratch, "v_probe"));
            await c.RequestAsync("management/drop", Drop(conn, "view", Scratch, "v_probe"));
            step("the view is dropped", !await profile.ExistsAsync(scratch, "v_probe"));
            await c.RequestAsync("management/drop", Drop(conn, "function", Scratch, "fn_probe", identity: profile.Engine == "postgres" ? "a integer" : null));
            step("the function is dropped", !await profile.ExistsAsync(scratch, "fn_probe"));
            await c.RequestAsync("management/drop", Drop(conn, "procedure", Scratch, "pr_probe"));
            step("the procedure is dropped", !await profile.ExistsAsync(scratch, "pr_probe"));

            step("a table is not a view", await ErrorCodeAsync(c.RequestAsync("management/drop", Drop(conn, "view", Scratch, "bkp_probe"))) == 1009 && await profile.ExistsAsync(scratch, "bkp_probe"));
            string? dependsMessage = null;
            try
            {
                await c.RequestAsync("management/drop", Drop(conn, "table", Scratch, "parent"));
            }
            catch (BackendErrorException ex)
            {
                dependsMessage = ex.Message;
            }
            step($"a table other objects depend on is not dropped ({dependsMessage?.Split('\n')[0]})", dependsMessage is not null && await profile.ExistsAsync(scratch, "parent"));
            await c.RequestAsync("management/drop", Drop(conn, "table", Scratch, "child"));
            await c.RequestAsync("management/drop", Drop(conn, "table", Scratch, "parent"));
            step("without the dependency it is dropped", !await profile.ExistsAsync(scratch, "child") && !await profile.ExistsAsync(scratch, "parent"));

            // Names with quote characters stay inside the statement.
            var weird = profile.Engine == "postgres" ? "we\"ird; DROP TABLE bkp_probe; --" : "we]ird; DROP TABLE bkp_probe; --";
            var quoted = profile.Engine == "postgres" ? "\"we\"\"ird; DROP TABLE bkp_probe; --\"" : "[we]]ird; DROP TABLE bkp_probe; --]";
            await profile.ExecAsync(scratch, $"CREATE TABLE {quoted} (id int)");
            await c.RequestAsync("management/drop", Drop(conn, "table", Scratch, weird));
            step("an awkward name is dropped as itself, nothing else", await profile.ExistsAsync(scratch, "bkp_probe") && await profile.CountAsync(scratch, "bkp_probe") == 100);

            // Databases, from a connection to the main database.
            var (c2, conn2, _) = await StartAsync();
            await using var _c2 = c2;
            var inside = await profile.OpenObserverAsync(second);
            string? inUse = null;
            try
            {
                await c2.RequestAsync("management/drop", Drop(conn2, "database", second, second));
            }
            catch (BackendErrorException ex)
            {
                inUse = ex.Message;
            }
            step($"a database with a session in it is not dropped ({inUse?.Split('\n')[0]})", inUse is not null && await profile.DatabaseExistsAsync(observer, second));
            await inside.DisposeAsync();
            await c2.RequestAsync("management/drop", Drop(conn2, "database", second, second));
            step("without the session it is dropped", !await profile.DatabaseExistsAsync(observer, second));

            // The prod rule.
            await profile.CreateScratchAsync(observer, second, 10);
            foreach (var tag in new string?[] { null, "prod" })
            {
                var (c3, conn3, _) = await StartAsync(tag: tag);
                await using var _c3 = c3;
                step($"env {tag ?? "(none)"}: no backup, no drop", await ErrorCodeAsync(c3.RequestAsync("management/drop", Drop(conn3, "database", second, second))) == 1009 && await profile.DatabaseExistsAsync(observer, second));
                if (tag == "prod")
                {
                    var path = await BackupPathAsync(c3, conn3, "prod-" + Guid.NewGuid().ToString("N")[..8]);
                    try
                    {
                        var (backupId, done) = await BackupAsync(c3, conn3, second, path);
                        step("the backup made first completed", done["status"]!.GetValue<string>() == "completed");
                        await c3.RequestAsync("management/drop", Drop(conn3, "database", second, second, backup: backupId));
                        step("with a verified backup the prod drop goes through", !await profile.DatabaseExistsAsync(observer, second));
                    }
                    finally
                    {
                        CleanUp(path);
                    }
                }
            }
        }
        finally
        {
            await profile.DropScratchAsync(observer, Scratch);
            await profile.DropScratchAsync(observer, second);
        }
        return Result("drop_objects_and_databases", ok() ? Outcome.Pass : Outcome.Fail, string.Join("; ", steps));
    }
}
