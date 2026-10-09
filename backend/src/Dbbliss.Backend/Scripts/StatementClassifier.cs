namespace Dbbliss.Backend.Scripts;

/// <summary>What a statement can do to the database, as far as its text shows.</summary>
public enum StatementKind
{
    /// <summary>Reads data or only touches the session (SELECT, SHOW, SET, BEGIN, ROLLBACK, ...).</summary>
    Read,

    /// <summary>Anything else, or anything that might change data: asked about on a prod connection.</summary>
    Write,
}

/// <summary>
/// Sorts statements into read and write by their words, per dialect. Conservative: a statement is
/// Read only when its first word says so and no word in it changes data, so a data-modifying CTE,
/// SELECT ... INTO and SELECT ... FOR UPDATE count as writes. What it cannot see (a SELECT that
/// calls a function with side effects) it cannot catch.
/// </summary>
public static class StatementClassifier
{
    private static readonly HashSet<string> QueryStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "with", "values", "table", "show", "explain", "describe", "desc",
    };

    private static readonly HashSet<string> SessionOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "set", "reset", "use", "begin", "start", "rollback", "savepoint", "print", "declare", "waitfor",
    };

    private static readonly HashSet<string> DataWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "insert", "update", "delete", "merge", "into", "call", "exec", "execute", "truncate", "drop", "alter", "create",
    };

    public static StatementKind Classify(string text, ScriptDialect dialect)
    {
        var words = ScriptSplitter.Words(text, dialect);
        if (words.Count == 0) return StatementKind.Read;
        var first = words[0];
        if (SessionOnly.Contains(first)) return StatementKind.Read;
        if (!QueryStarts.Contains(first)) return StatementKind.Write;
        return words.Any(DataWords.Contains) ? StatementKind.Write : StatementKind.Read;
    }
}
