using Dbbliss.Backend.Scripts;

namespace Dbbliss.ProtocolTests;

/// <summary>
/// The splitter's cases. A statement that splits wrongly sends the server half a statement, or runs
/// two as one, so the table covers every way a terminator can hide: strings, quoted identifiers,
/// comments, dollar quoting, atomic bodies, and a GO that is not alone on its line.
/// </summary>
public static class SplitterCases
{
    private const ScriptDialect Pg = ScriptDialect.Semicolon;
    private const ScriptDialect Ms = ScriptDialect.GoBatches;

    private sealed record Case(string Name, ScriptDialect Dialect, string Input, string[] Texts, int[]? Repeats = null, SourcePosition[]? Starts = null, SourcePosition[]? Ends = null);

    private static readonly Case[] Cases =
    [
        // PostgreSQL, semicolons
        new("pg two statements", Pg, "select 1; select 2;", ["select 1;", "select 2;"]),
        new("pg last without semicolon", Pg, "select 1;\n\nselect 2", ["select 1;", "select 2"]),
        new("pg semicolon in string", Pg, "select ';' ; select 2", ["select ';' ;", "select 2"]),
        new("pg doubled quote", Pg, "select 'it''s;'; select 2", ["select 'it''s;';", "select 2"]),
        new("pg backslash is plain in a plain string", Pg, "select 'a\\'; select 2", ["select 'a\\';", "select 2"]),
        new("pg backslash escapes in an E string", Pg, "select E'a\\';b'; select 2", ["select E'a\\';b';", "select 2"]),
        new("pg quoted identifier", Pg, "select \"a;b\" from t; select 2", ["select \"a;b\" from t;", "select 2"]),
        new("pg line comment", Pg, "select 1 -- ; here\n; select 2", ["select 1 -- ; here\n;", "select 2"]),
        new("pg nested block comment", Pg, "select /* ; /* nested ; */ ; */ 1; select 2", ["select /* ; /* nested ; */ ; */ 1;", "select 2"]),
        new("pg dollar quote", Pg, "select $$a;b$$; select 2", ["select $$a;b$$;", "select 2"]),
        new("pg tagged dollar quote", Pg, "do $f$ begin x; end $f$; select 1", ["do $f$ begin x; end $f$;", "select 1"]),
        new("pg dollar quote with another tag inside", Pg, "select $a$ $b$; $b$ $a$; select 2", ["select $a$ $b$; $b$ $a$;", "select 2"]),
        new("pg parameter is not a dollar quote", Pg, "select $1; select 2", ["select $1;", "select 2"]),
        new("pg stray semicolons and a trailing comment", Pg, ";;select 1;;\n-- tail\n", ["select 1;"]),
        new("pg unterminated string runs to the end", Pg, "select 1; select 'abc; x", ["select 1;", "select 'abc; x"]),
        new("pg unterminated block comment", Pg, "select 1; /* never closed; select 2", ["select 1;"]),
        new("pg empty", Pg, "  \n -- nothing\n", []),
        new("pg begin atomic body", Pg,
            "create function f() returns int language sql begin atomic select 1; select case when true then 1 else 2 end; end; select 2;",
            ["create function f() returns int language sql begin atomic select 1; select case when true then 1 else 2 end; end;", "select 2;"]),
        new("pg plain begin is a statement", Pg, "begin; select 1; commit;", ["begin;", "select 1;", "commit;"]),
        new("pg comments above a statement belong to it", Pg, "-- first\nselect 1;", ["-- first\nselect 1;"],
            Starts: [new(0, 0)]),
        new("pg a comment on the terminator's line trails the previous statement", Pg, "select 1; -- one\nselect 2;", ["select 1;", "select 2;"],
            Starts: [new(0, 0), new(1, 0)]),
        new("pg positions", Pg, "select 1;\n  select 2;", ["select 1;", "select 2;"],
            Starts: [new(0, 0), new(1, 2)], Ends: [new(0, 9), new(1, 11)]),
        new("pg crlf", Pg, "select 1;\r\nselect 2;\r\n", ["select 1;", "select 2;"], Starts: [new(0, 0), new(1, 0)]),
        new("pg multi-line statement", Pg, "select a,\n  b\nfrom t;\nselect 2;", ["select a,\n  b\nfrom t;", "select 2;"],
            Starts: [new(0, 0), new(3, 0)], Ends: [new(2, 7), new(3, 9)]),
        new("pg astral characters count as two columns", Pg, "select '😀'; select 2", ["select '😀';", "select 2"],
            Starts: [new(0, 0), new(0, 13)]),

        // SQL Server, GO batches
        new("ms go splits", Ms, "select 1\nGO\nselect 2", ["select 1", "select 2"], Starts: [new(0, 0), new(2, 0)]),
        new("ms semicolons do not split", Ms, "select 1; select 2\nGO", ["select 1; select 2"]),
        new("ms go is case-insensitive and may be indented", Ms, "select 1\n  go\nselect 2", ["select 1", "select 2"]),
        new("ms go count", Ms, "select 1\ngo 3\nselect 2", ["select 1", "select 2"], Repeats: [3, 1]),
        new("ms go with a comment", Ms, "select 1\nGO -- done\nselect 2", ["select 1", "select 2"]),
        new("ms go in a string", Ms, "select 'a\nGO\nb'", ["select 'a\nGO\nb'"]),
        new("ms go in a block comment", Ms, "/* x\nGO\n*/ select 1", ["/* x\nGO\n*/ select 1"]),
        new("ms go in a bracketed identifier", Ms, "select [a\nGO\nb]", ["select [a\nGO\nb]"]),
        new("ms go in a quoted identifier", Ms, "select \"a\nGO\nb\"", ["select \"a\nGO\nb\""]),
        new("ms go not alone on its line", Ms, "select 1 GO", ["select 1 GO"]),
        new("ms go with something after it", Ms, "select 1\nGO select 2", ["select 1\nGO select 2"]),
        new("ms go zero is not a go line", Ms, "select 1\nGO 0", ["select 1\nGO 0"]),
        new("ms leading and doubled go", Ms, "  GO  -- first\nselect 1\nGO\nGO\nselect 2\nGO\n", ["select 1", "select 2"]),
        new("ms bracketed identifier with ]]", Ms, "select [a]]b] from t\nGO\nselect 2", ["select [a]]b] from t", "select 2"]),
        new("ms comment-only batch dropped", Ms, "-- nothing\nGO\nselect 1", ["select 1"]),
        new("ms crlf", Ms, "select 1\r\nGO\r\nselect 2", ["select 1", "select 2"], Starts: [new(0, 0), new(2, 0)]),
        new("pg statement starting with a parenthesis", Pg, "(select 1); ('x')", ["(select 1);", "('x')"]),
        new("pg statement starting with a string", Pg, "'a' ; select 2", ["'a' ;", "select 2"]),
        new("pg line comment at the end of the input", Pg, "select 1 -- done", ["select 1"]),
        new("pg lone dollar at the end of the input", Pg, "select $", ["select $"]),
        new("pg dollar tag at the end of the input", Pg, "select $abc", ["select $abc"]),
        new("pg unterminated dollar quote runs to the end", Pg, "select $$abc; x", ["select $$abc; x"]),
        new("ms go with a count too large for an int", Ms, "select 1\nGO 99999999999", ["select 1\nGO 99999999999"]),
        new("ms go followed by a comment at the end of the input", Ms, "select 1\nGO -- done", ["select 1"]),
        new("ms square bracket is plain text on postgres", Pg, "select a[1]; select 2", ["select a[1];", "select 2"]),
    ];

    public static void Run()
    {
        var failures = new List<string>();
        foreach (var c in Cases)
        {
            var units = ScriptSplitter.Split(c.Input, c.Dialect);
            var texts = units.Select(u => u.Text).ToArray();
            if (!texts.SequenceEqual(c.Texts))
            {
                failures.Add($"{c.Name}: got [{string.Join(" | ", texts.Select(Show))}], expected [{string.Join(" | ", c.Texts.Select(Show))}]");
                continue;
            }
            if (c.Repeats is not null && !units.Select(u => u.Repeat).SequenceEqual(c.Repeats))
            {
                failures.Add($"{c.Name}: repeats [{string.Join(",", units.Select(u => u.Repeat))}], expected [{string.Join(",", c.Repeats)}]");
            }
            if (c.Starts is not null && !units.Select(u => u.Start).SequenceEqual(c.Starts))
            {
                failures.Add($"{c.Name}: starts [{string.Join(",", units.Select(u => u.Start))}], expected [{string.Join(",", c.Starts)}]");
            }
            if (c.Ends is not null && !units.Select(u => u.End).SequenceEqual(c.Ends))
            {
                failures.Add($"{c.Name}: ends [{string.Join(",", units.Select(u => u.End))}], expected [{string.Join(",", c.Ends)}]");
            }
        }
        if (failures.Count > 0) throw new TestFailure($"{failures.Count} of {Cases.Length} cases failed:\n  " + string.Join("\n  ", failures));
    }

    private static string Show(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");
}
