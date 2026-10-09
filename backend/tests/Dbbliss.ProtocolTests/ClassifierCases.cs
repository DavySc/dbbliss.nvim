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
}
