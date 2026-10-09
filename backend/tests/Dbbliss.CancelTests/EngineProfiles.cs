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

    public abstract string CreateProbeTableSql(string table);
    public abstract string DropProbeTableSql(string table);
    public virtual string InsertProbeSql(string table) => $"INSERT INTO {table} (id) VALUES (1)";

    public async Task ExecAsync(DbConnection observer, string sql)
    {
        await using var cmd = observer.CreateCommand();
        cmd.CommandText = sql;
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

public sealed class PostgresProfile : EngineProfile
{
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
