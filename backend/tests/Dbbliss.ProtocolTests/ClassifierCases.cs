using Dbbliss.Backend.Scripts;

namespace Dbbliss.ProtocolTests;

/// <summary>
/// Read or write, by the statement's words. A write that is called a read gets past the prompt on a
/// prod connection, so the table leans on the ways a SELECT can change data.
/// </summary>
public static class ClassifierCases
{
    private const ScriptDialect Pg = ScriptDialect.Semicolon;
    private const ScriptDialect Ms = ScriptDialect.GoBatches;
    private const StatementKind R = StatementKind.Read;
    private const StatementKind W = StatementKind.Write;

    private static readonly (ScriptDialect Dialect, string Sql, StatementKind Expected)[] Cases =
    [
        (Pg, "select 1", R),
        (Pg, "  -- note\nSELECT * FROM t", R),
        (Pg, "values (1), (2)", R),
        (Pg, "with a as (select 1) select * from a", R),
        (Pg, "with a as (delete from t returning *) select * from a", W),
        (Pg, "with a as (insert into t values (1) returning *) select * from a", W),
        (Pg, "select * into t2 from t", W),
        (Pg, "select * from t for update", W),
        (Pg, "select 'update t set x = 1'", R),
        (Pg, "select \"update\" from t", R),
        (Pg, "select $$delete$$", R),
        (Pg, "insert into t values (1)", W),
        (Pg, "update t set x = 1", W),
        (Pg, "delete from t", W),
        (Pg, "merge into t using s on true when matched then delete", W),
        (Pg, "truncate t", W),
        (Pg, "create table t (id int)", W),
        (Pg, "drop table t", W),
        (Pg, "do $$ begin delete from t; end $$", W),
        (Pg, "call p()", W),
        (Pg, "vacuum", W),
        (Pg, "commit", W),
        (Pg, "begin", R),
        (Pg, "rollback", R),
        (Pg, "set search_path = x", R),
        (Pg, "show all", R),
        (Pg, "explain select 1", R),
        (Pg, "explain analyze delete from t", W),
        (Pg, "", R),
        (Ms, "select top 5 * from t with (nolock)", R),
        (Ms, "select * into #tmp from t", W),
        (Ms, "exec sp_who", W),
        (Ms, "execute p", W),
        (Ms, "use master", R),
        (Ms, "declare @x int = 1", R),
        (Ms, "print 'hello'", R),
        (Ms, "insert t values (1)", W),
        (Ms, "update t set x = 1", W),
        (Ms, "alter table t add c int", W),
    ];

    public static void Run()
    {
        var failures = Cases
            .Select(c => (c, actual: StatementClassifier.Classify(c.Sql, c.Dialect)))
            .Where(r => r.actual != r.c.Expected)
            .Select(r => $"{r.c.Dialect} {r.c.Sql.Replace("\n", "\\n")}: got {r.actual}, expected {r.c.Expected}")
            .ToList();
        if (failures.Count > 0) throw new TestFailure($"{failures.Count} of {Cases.Length} cases failed:\n  " + string.Join("\n  ", failures));
    }

    // Statements that change what completion and the schema tree list: erring toward true costs a reload,
    // erring toward false leaves a new table out of completion.
    private static readonly (ScriptDialect Dialect, string Sql, bool Expected)[] SchemaChangeCases =
    [
        (Pg, "create table t (id int)", true),
        (Pg, "CREATE OR REPLACE FUNCTION f() RETURNS int AS 'select 1' LANGUAGE sql", true),
        (Pg, "  -- make it\ncreate index i on t (id)", true),
        (Pg, "alter table t add column c int", true),
        (Pg, "drop table if exists t", true),
        (Pg, "select * into t2 from t", true),
        (Pg, "with a as (select 1 as x) select x into t3 from a", true),
        (Pg, "create table t2 as select 1", true),
        (Pg, "select 1", false),
        (Pg, "select 'create table t (id int)'", false),
        (Pg, "select \"drop\" from t", false),
        (Pg, "insert into t values (1)", false),
        (Pg, "update t set x = 1", false),
        (Pg, "delete from t", false),
        (Pg, "truncate t", false),
        (Pg, "begin", false),
        (Pg, "commit", false),
        (Pg, "", false),
        (Ms, "create table dbo.t (id int)", true),
        (Ms, "alter procedure p as select 1", true),
        (Ms, "drop view v", true),
        (Ms, "select * into #tmp from t", true),
        (Ms, "exec sp_rename 'dbo.t', 'u'", true),
        (Ms, "EXECUTE sp_rename @objname = 'dbo.t', @newname = 'u'", true),
        (Ms, "sp_rename 'dbo.t', 'u'", true),
        (Ms, "exec sp_who", false),
        (Ms, "exec p", false),
        (Ms, "select top 5 * from t", false),
        (Ms, "insert t values (1)", false),
        (Ms, "use master", false),
    ];

    public static void SchemaChanges()
    {
        var failures = SchemaChangeCases
            .Select(c => (c, actual: StatementClassifier.ChangesSchema(c.Sql, c.Dialect)))
            .Where(r => r.actual != r.c.Expected)
            .Select(r => $"{r.c.Dialect} {r.c.Sql.Replace("\n", "\\n")}: got {r.actual}, expected {r.c.Expected}")
            .ToList();
        if (failures.Count > 0) throw new TestFailure($"{failures.Count} of {SchemaChangeCases.Length} cases failed:\n  " + string.Join("\n  ", failures));
    }
}
