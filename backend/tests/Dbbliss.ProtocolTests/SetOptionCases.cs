using System.Text.Json.Nodes;
using Dbbliss.Backend.Engines;

namespace Dbbliss.ProtocolTests;

/// <summary>
/// The SET batch a new SQL Server session gets. Names and values are pasted into SQL, so the table
/// leans on what must be refused; a bad entry has to fail the connect, never be skipped.
/// </summary>
public static class SetOptionCases
{
    private static JsonObject Opts(params (string Name, JsonNode? Value)[] entries)
    {
        var set = new JsonObject();
        foreach (var (n, v) in entries) set[n] = v;
        return new JsonObject { ["mssql_set_options"] = set };
    }

    private static string[] Statements(JsonObject? options) =>
        SqlServerEngine.BuildSetBatch(options)!.Split("; ");

    public static void Run()
    {
        // Defaults: ARITHABORT ON whether or not there are options at all.
        Equal("SET ARITHABORT ON", SqlServerEngine.BuildSetBatch(null), "no options");
        Equal("SET ARITHABORT ON", SqlServerEngine.BuildSetBatch(new JsonObject()), "empty options");
        Equal("SET ARITHABORT ON", SqlServerEngine.BuildSetBatch(new JsonObject { ["pg_client_connection_check_interval_ms"] = 0 }), "unrelated options");
        Equal("SET ARITHABORT ON", SqlServerEngine.BuildSetBatch(Opts()), "empty mssql_set_options");

        // An entry with the same name replaces the default, whatever its case; the value is kept as written.
        Equal("SET ARITHABORT OFF", SqlServerEngine.BuildSetBatch(Opts(("arithabort", "OFF"))), "override, lower-case name");
        Equal("SET ARITHABORT off", SqlServerEngine.BuildSetBatch(Opts(("ARITHABORT", "off"))), "override keeps value case");
        Equal("SET ARITHABORT OFF", SqlServerEngine.BuildSetBatch(Opts(("ARITHABORT", false))), "boolean false");
        Equal("SET ARITHABORT ON", SqlServerEngine.BuildSetBatch(Opts(("ARITHABORT", true))), "boolean true");

        // Added options come after the default; numbers and identifiers pass.
        SameSet(["SET ARITHABORT ON", "SET DEADLOCK_PRIORITY -5"], Statements(Opts(("DEADLOCK_PRIORITY", -5))), "number");
        SameSet(["SET ARITHABORT ON", "SET LANGUAGE us_english"], Statements(Opts(("language", "us_english"))), "name upper-cased, value not");
        SameSet(["SET ARITHABORT ON", "SET DATEFIRST 1", "SET LOCK_TIMEOUT 5000"],
            Statements(Opts(("DATEFIRST", 1), ("LOCK_TIMEOUT", "5000"))), "two added options");

        // Refused: injection and anything that is not a plain NAME value.
        Refused(Opts(("ARITHABORT", "ON; DROP TABLE x")), "semicolon in value");
        Refused(Opts(("ARITHABORT", "ON\nSELECT 1")), "newline in value");
        Refused(Opts(("ARITHABORT", "ON OFF")), "space in value");
        Refused(Opts(("ARITHABORT", "ON--")), "comment in value");
        Refused(Opts(("ARITHABORT", "--")), "bare comment as value");
        Refused(Opts(("ARITHABORT", "/*")), "block comment as value");
        Refused(Opts(("DEADLOCK_PRIORITY", "-")), "bare minus");
        Refused(Opts(("DEADLOCK_PRIORITY", "5-3")), "minus inside value");
        Refused(Opts(("ARITHABORT", "")), "empty value");
        Refused(Opts(("ARITHABORT; DROP TABLE x", "ON")), "semicolon in name");
        Refused(Opts(("ARITH ABORT", "ON")), "space in name");
        Refused(Opts(("", "ON")), "empty name");
        Refused(Opts(("ARITHABORT", null)), "null value");
        Refused(Opts(("ARITHABORT", new JsonObject())), "object value");
        Refused(Opts(("ARITHABORT", new JsonArray())), "array value");
        Refused(Opts(("DEADLOCK_PRIORITY", 1.5)), "fractional number");

        // A mssql_set_options that is not a table is a mistake to report, not to skip.
        Refused(new JsonObject { ["mssql_set_options"] = "ARITHABORT OFF" }, "string instead of table");
        Refused(new JsonObject { ["mssql_set_options"] = new JsonArray("ARITHABORT") }, "array instead of table");
        Refused(new JsonObject { ["mssql_set_options"] = true }, "boolean instead of table");
    }

    private static void Equal(string expected, string? actual, string what)
    {
        if (actual != expected) throw new TestFailure($"{what}: expected \"{expected}\", got \"{actual}\"");
    }

    private static void SameSet(string[] expected, string[] actual, string what)
    {
        if (!expected.OrderBy(x => x).SequenceEqual(actual.OrderBy(x => x), StringComparer.Ordinal))
        {
            throw new TestFailure($"{what}: expected [{string.Join(" | ", expected)}], got [{string.Join(" | ", actual)}]");
        }
    }

    private static void Refused(JsonObject options, string what)
    {
        try
        {
            var batch = SqlServerEngine.BuildSetBatch(options);
            throw new TestFailure($"{what}: accepted, batch \"{batch}\"");
        }
        catch (ArgumentException)
        {
        }
    }
}
