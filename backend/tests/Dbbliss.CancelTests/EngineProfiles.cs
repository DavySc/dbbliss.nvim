using System.Data.Common;
using System.Data.Odbc;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Dbbliss.CancelTests;

/// <summary>What the server says about a session, read from a second, independent connection.</summary>
public sealed record ServerView(bool Executing, string? TransactionState, string Detail);

/// <summary>
/// Per-engine SQL and the observer: a separate connection opened directly with the driver, used to
/// check what the server is doing, independent of anything the backend reports.
/// </summary>
public abstract class EngineProfile
{
    public required string ConnectionString { get; init; }
    public required string PasswordEnv { get; init; }

    public abstract string Engine { get; }
    public abstract string SleepSql { get; }
    public abstract string StreamingSql { get; }

    /// <summary>A batch whose first statement returns one row and whose second sleeps; null if the engine has no batches.</summary>
    public virtual string? BatchThenSleepSql => null;

    /// <summary>Extra long-running statement types worth cancelling, beyond sleep and streaming.</summary>
    public virtual IReadOnlyList<(string Name, string Sql)> ExtraLongQueries => [];

    public virtual bool SupportsServerTransactionView => true;

    /// <summary>Transaction control typed by the user as ordinary SQL.</summary>
    public virtual string TypedBeginSql => "BEGIN";
    public virtual string TypedCommitSql => "COMMIT";

    protected string Password => Environment.GetEnvironmentVariable(PasswordEnv) ?? "";

    public abstract Task<DbConnection> OpenObserverAsync();

    /// <summary>Phase 2: creates the objects the catalog scenarios look at (schema dbbliss_cat). Empty: the engine has no catalog.</summary>
    public virtual Task CreateCatalogFixtureAsync(DbConnection observer) => Task.CompletedTask;

    public virtual Task DropCatalogFixtureAsync(DbConnection observer) => Task.CompletedTask;

    public virtual bool HasCatalog => false;

    /// <summary>The database holding the fixture.</summary>
    public virtual Task<string> CatalogDatabaseAsync(DbConnection observer) => Task.FromResult("");

    /// <summary>A name as the user would type it to reach a fixture object (SQL Server: through the database).</summary>
    public virtual string Qualify(string schemaAndName) => schemaAndName;

    /// <summary>A connection string that reaches nothing, so connecting fails quickly (port 1 on this machine).</summary>
    public virtual string? UnreachableConnectionString => null;

    /// <summary>A statement that returns the session's application name as its first value.</summary>
    public virtual string? ApplicationNameSql => null;

    /// <summary>Statements that drop the scripted fixture objects, dependants first.</summary>
    public virtual IReadOnlyList<string> DropScriptedObjectsSql => [];

    /// <summary>Overload identity of dbbliss_cat.add_one as the tree reports it (PostgreSQL only).</summary>
    public virtual string? AddOneIdentity => null;

    public abstract Task<ServerView> ViewAsync(DbConnection observer, string serverSessionId);

    /// <summary>Phase 3: the server's id of the connection's own session.</summary>
    public virtual string ServerIdSql => throw new NotSupportedException();

    public async Task<string> ServerIdAsync(DbConnection c) =>
        Convert.ToString((await FirstRowAsync(c, ServerIdSql)).Values[0], System.Globalization.CultureInfo.InvariantCulture)!;

    // ---- Phase 4: scratch databases for backup and drop ------------------------------------------

    /// <summary>The connection string of this profile, pointed at another database.</summary>
    public virtual string ScratchConnectionString(string database) => throw new NotSupportedException();

    public virtual Task<DbConnection> OpenObserverAsync(string database) => throw new NotSupportedException();

    /// <summary>
    /// Creates <paramref name="database"/> afresh with bkp_probe (id, pad; <paramref name="rows"/> rows), parent and child
    /// (a foreign key), the view v_probe, the function fn_probe and the procedure pr_probe.
    /// </summary>
    public virtual Task CreateScratchAsync(DbConnection observer, string database, int rows) => throw new NotSupportedException();

    public virtual Task DropScratchAsync(DbConnection observer, string database) => throw new NotSupportedException();

    public virtual Task<bool> DatabaseExistsAsync(DbConnection observer, string database) => throw new NotSupportedException();

    /// <summary>1 when the object exists in the scratch database the connection is in.</summary>
    public virtual string ObjectExistsSql(string name) => throw new NotSupportedException();

    /// <summary>Restores the backup file into a new database and returns the row count of bkp_probe there.</summary>
    public virtual Task<long> RestoreAndCountAsync(DbConnection observer, string backupPath, string newDatabase) => throw new NotSupportedException();

    public async Task<bool> ExistsAsync(DbConnection scratch, string name) =>
        Convert.ToInt64((await FirstRowAsync(scratch, ObjectExistsSql(name))).Values[0], System.Globalization.CultureInfo.InvariantCulture) != 0;

    /// <summary>A login that can see other sessions but may not signal them; null where the engine has no sessions admin.</summary>
    public virtual Task<LimitedUser?> CreateLimitedUserAsync(DbConnection observer) => Task.FromResult<LimitedUser?>(null);

    public abstract string CreateProbeTableSql(string table);
    public abstract string DropProbeTableSql(string table);
    public virtual string InsertProbeSql(string table) => $"INSERT INTO {table} (id) VALUES (1)";

    public async Task ExecAsync(DbConnection observer, string sql)
    {
        await using var cmd = observer.CreateCommand();
        cmd.CommandText = sql;
        // Fixtures can be large (a scratch database for a backup that takes a while): no 30 s default.
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> CountAsync(DbConnection observer, string table)
    {
        await using var cmd = observer.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    protected static async Task<(bool HasRow, object?[] Values)> FirstRowAsync(DbConnection c, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (false, []);
        var values = new object?[r.FieldCount];
        for (var i = 0; i < values.Length; i++) values[i] = r.IsDBNull(i) ? null : r.GetValue(i);
        return (true, values);
    }
}

/// <param name="ConnectionString">Carries its own password.</param>
public sealed record LimitedUser(string ConnectionString, Func<Task> DropAsync);

public sealed class PostgresProfile : EngineProfile
{
    /// <summary>The folder with pg_dump and pg_restore when they are not on PATH (DBBLISS_TEST_PG_BIN).</summary>
    public static string? ToolFolder => Environment.GetEnvironmentVariable("DBBLISS_TEST_PG_BIN") is { Length: > 0 } f ? f : null;

    private string Tool(string name) => ToolFolder is { } f ? Path.Combine(f, OperatingSystem.IsWindows() ? name + ".exe" : name) : name;

    public override string ScratchConnectionString(string database) => new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;

    public override async Task<DbConnection> OpenObserverAsync(string database)
    {
        var b = new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false, ApplicationName = "dbbliss-observer", Database = database };
        if (Password.Length > 0) b.Password = Password;
        var c = new NpgsqlConnection(b.ConnectionString);
        await c.OpenAsync();
        return c;
    }

    public override async Task CreateScratchAsync(DbConnection observer, string database, int rows)
    {
        await DropScratchAsync(observer, database);
        await ExecAsync(observer, "CREATE DATABASE " + database);
        await using var c = await OpenObserverAsync(database);
        await ExecAsync(c, "CREATE TABLE bkp_probe (id int PRIMARY KEY, pad text)");
        await ExecAsync(c, $"INSERT INTO bkp_probe SELECT g, repeat(md5(g::text), 3) FROM generate_series(1, {rows}) g");
        await ExecAsync(c, "CREATE TABLE parent (id int PRIMARY KEY)");
        await ExecAsync(c, "CREATE TABLE child (id int PRIMARY KEY, parent_id int NOT NULL REFERENCES parent(id))");
        await ExecAsync(c, "CREATE VIEW v_probe AS SELECT id FROM bkp_probe");
        await ExecAsync(c, "CREATE FUNCTION fn_probe(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a + 1 $$");
        await ExecAsync(c, "CREATE PROCEDURE pr_probe() LANGUAGE sql AS $$ SELECT 1 $$");
    }

    public override Task DropScratchAsync(DbConnection observer, string database) => ExecAsync(observer, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");

    public override async Task<bool> DatabaseExistsAsync(DbConnection observer, string database) =>
        Convert.ToInt64((await FirstRowAsync(observer, "SELECT count(*) FROM pg_database WHERE datname = @d", ("d", database))).Values[0], System.Globalization.CultureInfo.InvariantCulture) != 0;

    public override string ObjectExistsSql(string name) =>
        $"SELECT (to_regclass('public.{name}') IS NOT NULL OR EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace s ON s.oid = p.pronamespace WHERE s.nspname = 'public' AND p.proname = '{name}'))::int";

    public override async Task<long> RestoreAndCountAsync(DbConnection observer, string backupPath, string newDatabase)
    {
        await DropScratchAsync(observer, newDatabase);
        await ExecAsync(observer, "CREATE DATABASE " + newDatabase);
        var b = new NpgsqlConnectionStringBuilder(ConnectionString);
        var psi = new System.Diagnostics.ProcessStartInfo(Tool("pg_restore")) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "--no-owner", "--no-password", "--host=" + b.Host, "--port=" + b.Port, "--username=" + b.Username, "--dbname=" + newDatabase, backupPath }) psi.ArgumentList.Add(a);
        if (Password.Length > 0) psi.Environment["PGPASSWORD"] = Password;
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException("pg_restore: " + err);
        await using var c = await OpenObserverAsync(newDatabase);
        return await CountAsync(c, "bkp_probe");
    }

    public override string ServerIdSql => "SELECT pg_backend_pid()";

    public override async Task<LimitedUser?> CreateLimitedUserAsync(DbConnection observer)
    {
        await ExecAsync(observer, "DROP ROLE IF EXISTS dbbliss_limited");
        await ExecAsync(observer, "CREATE ROLE dbbliss_limited LOGIN PASSWORD 'limited-Pw1'");
        // Sees every session (without it other users' rows are blank), but may not signal them.
        await ExecAsync(observer, "GRANT pg_read_all_stats TO dbbliss_limited");
        var b = new NpgsqlConnectionStringBuilder(ConnectionString) { Username = "dbbliss_limited", Password = "limited-Pw1" };
        return new LimitedUser(b.ConnectionString, () => ExecAsync(observer, "DROP ROLE IF EXISTS dbbliss_limited"));
    }

    public override string? UnreachableConnectionString => "Host=127.0.0.1;Port=1;Username=nobody;Database=none;Timeout=3";
    public override string? ApplicationNameSql => "SHOW application_name";
    public override string Engine => "postgres";
    public override string SleepSql => "SELECT pg_sleep(60)";
    public override string? BatchThenSleepSql => "SELECT 1 AS a; SELECT pg_sleep(60)";
    // SRF in the select list streams (ProjectSet); in FROM it would materialize first.
    public override string StreamingSql => "SELECT generate_series(1, 100000000) AS n, repeat('x', 200) AS pad";

    public override bool HasCatalog => true;

    public override string? AddOneIdentity => "a integer";

    private static readonly string[] CatalogFixture =
    [
        "DROP SCHEMA IF EXISTS dbbliss_cat CASCADE",
        "CREATE SCHEMA dbbliss_cat",
        """
        CREATE TABLE dbbliss_cat.customer (
            id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            email text NOT NULL UNIQUE,
            name varchar(100) DEFAULT 'anonymous',
            created timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT customer_name_not_blank CHECK (length(name) > 0))
        """,
        "COMMENT ON TABLE dbbliss_cat.customer IS 'People who buy things'",
        """
        CREATE TABLE dbbliss_cat."Order Line" (
            order_id integer NOT NULL,
            line integer NOT NULL,
            customer_id integer NOT NULL REFERENCES dbbliss_cat.customer(id) ON DELETE CASCADE,
            qty numeric(10,2) NOT NULL DEFAULT 1,
            total numeric GENERATED ALWAYS AS (qty * 2) STORED,
            PRIMARY KEY (order_id, line))
        """,
        "CREATE INDEX order_line_customer ON dbbliss_cat.\"Order Line\" (customer_id, qty DESC) WHERE qty > 0",
        "CREATE VIEW dbbliss_cat.customer_names AS SELECT id, name FROM dbbliss_cat.customer",
        "CREATE FUNCTION dbbliss_cat.add_one(a integer) RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT a + 1 $$",
        "CREATE FUNCTION dbbliss_cat.add_one(a numeric, b numeric) RETURNS numeric LANGUAGE sql AS $$ SELECT a + b $$",
        "CREATE PROCEDURE dbbliss_cat.noop() LANGUAGE sql AS $$ SELECT 1 $$",
        "CREATE FUNCTION dbbliss_cat.touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$",
        "CREATE TRIGGER customer_touch BEFORE UPDATE ON dbbliss_cat.customer FOR EACH ROW EXECUTE FUNCTION dbbliss_cat.touch()",
    ];

    public override async Task CreateCatalogFixtureAsync(DbConnection observer)
    {
        foreach (var sql in CatalogFixture) await ExecAsync(observer, sql);
    }

    public override Task DropCatalogFixtureAsync(DbConnection observer) => ExecAsync(observer, "DROP SCHEMA IF EXISTS dbbliss_cat CASCADE");

    public override async Task<string> CatalogDatabaseAsync(DbConnection observer) =>
        (await FirstRowAsync(observer, "SELECT current_database()")).Values[0]?.ToString() ?? "";

    public override IReadOnlyList<string> DropScriptedObjectsSql =>
    [
        "DROP PROCEDURE dbbliss_cat.noop()",
        "DROP FUNCTION dbbliss_cat.add_one(integer)",
        "DROP VIEW dbbliss_cat.customer_names",
        "DROP TABLE dbbliss_cat.\"Order Line\"",
        "DROP TABLE dbbliss_cat.customer",
    ];

    public override async Task<DbConnection> OpenObserverAsync()
    {
        var b = new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false, ApplicationName = "dbbliss-observer" };
        if (Password.Length > 0) b.Password = Password;
        var c = new NpgsqlConnection(b.ConnectionString);
        await c.OpenAsync();
        return c;
    }

    public override async Task<ServerView> ViewAsync(DbConnection observer, string pid)
    {
        var (has, v) = await FirstRowAsync(observer,
            "SELECT state, wait_event, left(query, 60) FROM pg_stat_activity WHERE pid = @pid",
            ("pid", int.Parse(pid, System.Globalization.CultureInfo.InvariantCulture)));
        if (!has) return new ServerView(false, "none", "session gone");
        var state = (string?)v[0] ?? "";
        var tx = state switch
        {
            "idle in transaction" => "active",
            "idle in transaction (aborted)" => "aborted",
            "idle" => "none",
            _ => null,
        };
        return new ServerView(state == "active", tx, $"state={state} wait={v[1]} query={v[2]}");
    }

    public override string CreateProbeTableSql(string t) => $"CREATE TABLE {t} (id int)";
    public override string DropProbeTableSql(string t) => $"DROP TABLE IF EXISTS {t}";
}

public sealed class SqlServerProfile : EngineProfile
{
    public override string ScratchConnectionString(string database) => new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = database }.ConnectionString;

    public override async Task<DbConnection> OpenObserverAsync(string database)
    {
        var c = await OpenObserverAsync();
        c.ChangeDatabase(database);
        return c;
    }

    public override async Task CreateScratchAsync(DbConnection observer, string database, int rows)
    {
        await DropScratchAsync(observer, database);
        await ExecAsync(observer, "CREATE DATABASE " + database);
        await using var c = await OpenObserverAsync(database);
        await ExecAsync(c, "CREATE TABLE dbo.bkp_probe (id int NOT NULL PRIMARY KEY, pad varchar(200))");
        await ExecAsync(c, $"INSERT INTO dbo.bkp_probe SELECT TOP ({rows}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)), REPLICATE(CONVERT(varchar(36), NEWID()), 5) FROM sys.all_columns a CROSS JOIN sys.all_columns b");
        await ExecAsync(c, "CREATE TABLE dbo.parent (id int NOT NULL PRIMARY KEY)");
        await ExecAsync(c, "CREATE TABLE dbo.child (id int NOT NULL PRIMARY KEY, parent_id int NOT NULL REFERENCES dbo.parent(id))");
        await ExecAsync(c, "CREATE VIEW dbo.v_probe AS SELECT id FROM dbo.bkp_probe");
        await ExecAsync(c, "CREATE FUNCTION dbo.fn_probe(@a int) RETURNS int AS BEGIN RETURN @a + 1 END");
        await ExecAsync(c, "CREATE PROCEDURE dbo.pr_probe AS SELECT 1");
    }

    public override Task DropScratchAsync(DbConnection observer, string database) => ExecAsync(observer,
        $"USE master; IF DB_ID('{database}') IS NOT NULL BEGIN ALTER DATABASE {database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {database}; END");

    public override async Task<bool> DatabaseExistsAsync(DbConnection observer, string database) =>
        Convert.ToInt64((await FirstRowAsync(observer, "SELECT count(*) FROM sys.databases WHERE name = @d", ("@d", database))).Values[0], System.Globalization.CultureInfo.InvariantCulture) != 0;

    public override string ObjectExistsSql(string name) => $"SELECT CASE WHEN OBJECT_ID(N'dbo.{name}') IS NULL THEN 0 ELSE 1 END";

    public override async Task<long> RestoreAndCountAsync(DbConnection observer, string backupPath, string newDatabase)
    {
        await DropScratchAsync(observer, newDatabase);
        var p = backupPath.Replace("'", "''", StringComparison.Ordinal);
        var dataDir = Convert.ToString((await FirstRowAsync(observer, "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(512))")).Values[0], System.Globalization.CultureInfo.InvariantCulture)!;
        var files = new List<(string Logical, string Physical)>();
        await using (var cmd = observer.CreateCommand())
        {
            cmd.CommandText = $"RESTORE FILELISTONLY FROM DISK = N'{p}'";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) files.Add((r.GetString(0), r.GetString(1)));
        }
        var sep = dataDir.Contains('\\', StringComparison.Ordinal) ? "\\" : "/";
        var moves = string.Join(", ", files.Select((f, i) => $"MOVE N'{f.Logical.Replace("'", "''", StringComparison.Ordinal)}' TO N'{dataDir.TrimEnd('/', '\\')}{sep}{newDatabase}_{i}{Path.GetExtension(f.Physical)}'"));
        await ExecAsync(observer, $"RESTORE DATABASE {newDatabase} FROM DISK = N'{p}' WITH {moves}, REPLACE");
        return await CountAsync(observer, newDatabase + ".dbo.bkp_probe");
    }

    public override string ServerIdSql => "SELECT @@SPID";

    public override async Task<LimitedUser?> CreateLimitedUserAsync(DbConnection observer)
    {
        await ExecAsync(observer, "IF SUSER_ID('dbbliss_limited') IS NOT NULL DROP LOGIN dbbliss_limited");
        await ExecAsync(observer, "CREATE LOGIN dbbliss_limited WITH PASSWORD = 'Limited-Pw1-x', CHECK_POLICY = OFF");
        await ExecAsync(observer, "GRANT VIEW SERVER STATE TO dbbliss_limited");
        var b = new SqlConnectionStringBuilder(ConnectionString) { IntegratedSecurity = false, UserID = "dbbliss_limited", Password = "Limited-Pw1-x" };
        return new LimitedUser(b.ConnectionString, () => ExecAsync(observer, "IF SUSER_ID('dbbliss_limited') IS NOT NULL DROP LOGIN dbbliss_limited"));
    }

    public override string? UnreachableConnectionString => "Server=127.0.0.1,1;User Id=nobody;TrustServerCertificate=true;Connect Timeout=3";
    public override string? ApplicationNameSql => "SELECT APP_NAME()";
    public override string Engine => "sqlserver";
    public override string TypedBeginSql => "BEGIN TRANSACTION";
    public override string TypedCommitSql => "COMMIT TRANSACTION";
    public override string SleepSql => "WAITFOR DELAY '00:01:00'";
    public override string? BatchThenSleepSql => "SELECT 1 AS a; WAITFOR DELAY '00:01:00'";
    public override string StreamingSql =>
        "SELECT TOP (100000000) a.object_id, REPLICATE('x', 200) AS pad FROM sys.all_columns a CROSS JOIN sys.all_columns b CROSS JOIN sys.all_columns c";

    public override bool HasCatalog => true;

    // The fixture lives in a database of its own, so the scenarios also reach another database than
    // the connection's (the connection string names none: master).
    private const string CatalogDb = "dbbliss_cattest";

    private static readonly string[] CatalogFixture =
    [
        "CREATE SCHEMA dbbliss_cat",
        """
        CREATE TABLE dbbliss_cat.customer (
            id int IDENTITY(1,1) NOT NULL CONSTRAINT pk_customer PRIMARY KEY,
            email nvarchar(200) NOT NULL CONSTRAINT uq_customer_email UNIQUE,
            name nvarchar(100) NULL CONSTRAINT df_customer_name DEFAULT N'anonymous',
            created datetime2(3) NOT NULL CONSTRAINT df_customer_created DEFAULT SYSUTCDATETIME(),
            CONSTRAINT ck_customer_name CHECK (LEN(name) > 0))
        """,
        """
        CREATE TABLE dbbliss_cat.[Order Line] (
            order_id int NOT NULL,
            line int NOT NULL,
            customer_id int NOT NULL CONSTRAINT fk_orderline_customer FOREIGN KEY REFERENCES dbbliss_cat.customer(id) ON DELETE CASCADE,
            qty decimal(10,2) NOT NULL CONSTRAINT df_orderline_qty DEFAULT 1,
            total AS (qty * 2) PERSISTED,
            CONSTRAINT pk_orderline PRIMARY KEY (order_id, line))
        """,
        "CREATE INDEX ix_orderline_customer ON dbbliss_cat.[Order Line] (customer_id, qty DESC) INCLUDE (line) WHERE qty > 0",
        "CREATE VIEW dbbliss_cat.customer_names AS SELECT id, name FROM dbbliss_cat.customer",
        "CREATE FUNCTION dbbliss_cat.add_one(@a int) RETURNS int AS BEGIN RETURN @a + 1 END",
        "CREATE PROCEDURE dbbliss_cat.noop AS SELECT 1",
        "CREATE TRIGGER dbbliss_cat.customer_touch ON dbbliss_cat.customer AFTER UPDATE AS SET NOCOUNT ON",
    ];

    public override async Task CreateCatalogFixtureAsync(DbConnection observer)
    {
        await DropCatalogFixtureAsync(observer);
        await ExecAsync(observer, "CREATE DATABASE " + CatalogDb);
        observer.ChangeDatabase(CatalogDb);
        foreach (var sql in CatalogFixture) await ExecAsync(observer, sql);
        observer.ChangeDatabase("master");
    }

    public override Task DropCatalogFixtureAsync(DbConnection observer) => ExecAsync(observer,
        $"USE master; IF DB_ID('{CatalogDb}') IS NOT NULL BEGIN ALTER DATABASE {CatalogDb} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {CatalogDb}; END");

    public override Task<string> CatalogDatabaseAsync(DbConnection observer) => Task.FromResult(CatalogDb);

    public override string Qualify(string schemaAndName) => CatalogDb + "." + schemaAndName;

    public override IReadOnlyList<string> DropScriptedObjectsSql =>
    [
        $"USE {CatalogDb}; DROP PROCEDURE dbbliss_cat.noop",
        $"USE {CatalogDb}; DROP FUNCTION dbbliss_cat.add_one",
        $"USE {CatalogDb}; DROP VIEW dbbliss_cat.customer_names",
        $"USE {CatalogDb}; DROP TABLE dbbliss_cat.[Order Line]",
        $"USE {CatalogDb}; DROP TABLE dbbliss_cat.customer",
    ];

    public override async Task<DbConnection> OpenObserverAsync()
    {
        var b = new SqlConnectionStringBuilder(ConnectionString) { Pooling = false, ApplicationName = "dbbliss-observer" };
        if (Password.Length > 0) b.Password = Password;
        var c = new SqlConnection(b.ConnectionString);
        await c.OpenAsync();
        return c;
    }

    public override async Task<ServerView> ViewAsync(DbConnection observer, string spid)
    {
        var id = short.Parse(spid, System.Globalization.CultureInfo.InvariantCulture);
        var (hasSession, s) = await FirstRowAsync(observer,
            "SELECT s.open_transaction_count, s.status FROM sys.dm_exec_sessions s WHERE s.session_id = @spid",
            ("@spid", id));
        if (!hasSession) return new ServerView(false, "none", "session gone");
        var (hasRequest, r) = await FirstRowAsync(observer,
            "SELECT r.status, r.command, r.wait_type FROM sys.dm_exec_requests r WHERE r.session_id = @spid",
            ("@spid", id));
        var openTx = Convert.ToInt32(s[0], System.Globalization.CultureInfo.InvariantCulture);
        var detail = hasRequest ? $"request status={r[0]} command={r[1]} wait={r[2]}" : $"no request; session status={s[1]}";
        return new ServerView(hasRequest, openTx > 0 ? "open" : "none", detail + $" open_tx={openTx}");
    }

    public override string CreateProbeTableSql(string t) => $"CREATE TABLE {t} (id int)";
    public override string DropProbeTableSql(string t) => $"DROP TABLE IF EXISTS {t}";

    /// <summary>The session's ARITHABORT and DEADLOCK_PRIORITY as the server reports them.</summary>
    public async Task<(bool ArithAbort, int DeadlockPriority)> SessionSettingsAsync(DbConnection observer, string spid)
    {
        var (has, v) = await FirstRowAsync(observer,
            "SELECT arithabort, deadlock_priority FROM sys.dm_exec_sessions WHERE session_id = @spid",
            ("@spid", short.Parse(spid, System.Globalization.CultureInfo.InvariantCulture)));
        if (!has) throw new InvalidOperationException("session gone");
        return (Convert.ToBoolean(v[0], System.Globalization.CultureInfo.InvariantCulture), Convert.ToInt32(v[1], System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// DB2 for i. UNVERIFIED: written against the IBM documentation for QSYS2.ACTIVE_JOB_INFO and
/// QSYS2.JOB_NAME; must be run against a real system before any result is trusted.
/// </summary>
public sealed class Db2iProfile : EngineProfile
{
    public override string Engine => "db2i";
    public override string SleepSql => "CALL QSYS2.QCMDEXC('DLYJOB DLY(60)')";
    public override string StreamingSql =>
        "WITH t(n) AS (SELECT 1 FROM SYSIBM.SYSDUMMY1 UNION ALL SELECT n + 1 FROM t WHERE n < 100000000) SELECT n, REPEAT('x', 200) FROM t";

    // A CPU-bound statement inside the SQL engine, in case SQLCancel behaves differently there than in a CL command.
    public override IReadOnlyList<(string Name, string Sql)> ExtraLongQueries =>
    [
        ("cpu_cancel", "WITH t(n) AS (SELECT 1 FROM SYSIBM.SYSDUMMY1 UNION ALL SELECT n + 1 FROM t WHERE n < 2000000000) SELECT COUNT(*) FROM t"),
    ];

    public override bool SupportsServerTransactionView => false;

    public override async Task<DbConnection> OpenObserverAsync()
    {
        var cs = Password.Length == 0 ? ConnectionString : ConnectionString.TrimEnd(';') + ";PWD={" + Password.Replace("}", "}}", StringComparison.Ordinal) + "};";
        var c = new OdbcConnection(cs);
        await Task.Run(c.Open);
        return c;
    }

    public override async Task<ServerView> ViewAsync(DbConnection observer, string jobName)
    {
        // jobName is number/user/name; JOB_NAME_FILTER takes the name part only.
        var shortName = jobName.Split('/').Last();
        var (has, v) = await FirstRowAsync(observer,
            "SELECT SQL_STATEMENT_STATUS, JOB_STATUS, FUNCTION, LEFT(SQL_STATEMENT_TEXT, 60) " +
            "FROM TABLE(QSYS2.ACTIVE_JOB_INFO(JOB_NAME_FILTER => ?, DETAILED_INFO => 'ALL')) X WHERE JOB_NAME = ?",
            ("@p1", shortName), ("@p2", jobName));
        if (!has) return new ServerView(false, null, "job not active");
        var status = (v[0] as string)?.Trim();
        return new ServerView(status == "ACTIVE", null, $"sql_status={status} job_status={v[1]} function={v[2]} stmt={v[3]}");
    }

    public override string CreateProbeTableSql(string t) => $"CREATE TABLE {t} (id INT)";
    public override string DropProbeTableSql(string t) => $"DROP TABLE {t}";
}
