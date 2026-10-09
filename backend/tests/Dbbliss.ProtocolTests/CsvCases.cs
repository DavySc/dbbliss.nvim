using System.Text;
using System.Text.Json.Nodes;
using Dbbliss.Backend.Engines;
using Dbbliss.Backend.Rpc;

namespace Dbbliss.ProtocolTests;

/// <summary>The CSV a backend export writes: RFC 4180 quoting, no BOM, CRLF, NULL as an empty field.</summary>
public static class CsvCases
{
    private static readonly ColumnInfo[] Header = [new("id", "int"), new("note, with \"quotes\"", "text")];

    public static async Task Run()
    {
        var dir = Directory.CreateTempSubdirectory("dbbliss-csv").FullName;
        try
        {
            await Fields(Path.Combine(dir, "fields.csv"));
            await Sets(Path.Combine(dir, "sets.csv"));
            await Paths(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task Fields(string path)
    {
        await using (var csv = new CsvExport(path, overwrite: false))
        {
            await csv.ResultSetAsync(0, Header);
            await csv.RowAsync(0, new JsonArray(1, "plain"));
            await csv.RowAsync(0, new JsonArray(2, null));                       // NULL: empty field
            await csv.RowAsync(0, new JsonArray(3, ""));                         // empty string: also empty
            await csv.RowAsync(0, new JsonArray(4, "a,b"));
            await csv.RowAsync(0, new JsonArray(5, "say \"hi\""));
            await csv.RowAsync(0, new JsonArray(6, "line1\nline2"));
            await csv.RowAsync(0, new JsonArray(7, "cr\rhere"));
            await csv.RowAsync(0, new JsonArray(8, " padded "));                  // spaces alone need no quotes
            await csv.RowAsync(0, new JsonArray(9, "h\u00e9llo \u2603 \U0001F600")); // UTF-8
            await csv.RowAsync(0, new JsonArray(true, 1.5));                     // non-strings use their JSON text
            await csv.RowAsync(0, new JsonArray(11, new JsonArray(1, 2)));       // a nested value is its JSON text, quoted for the comma
            await csv.ResultSetDoneAsync(0, 10);
            if (csv.Rows != 11) throw new TestFailure($"rows written: {csv.Rows}");
        }
        var bytes = await File.ReadAllBytesAsync(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) throw new TestFailure("the file starts with a BOM");
        var expected = string.Join("\r\n",
            "id,\"note, with \"\"quotes\"\"\"",
            "1,plain",
            "2,",
            "3,",
            "4,\"a,b\"",
            "5,\"say \"\"hi\"\"\"",
            "6,\"line1\nline2\"",
            "7,\"cr\rhere\"",
            "8, padded ",
            "9,h\u00e9llo \u2603 \U0001F600",
            "true,1.5",
            "11,\"[1,2]\"") + "\r\n";
        var actual = new UTF8Encoding(false, true).GetString(bytes);
        if (actual != expected) throw new TestFailure($"csv differs:\nexpected {Show(expected)}\nactual   {Show(actual)}");
    }

    // Only the first result set is exported; the others are counted, not written.
    private static async Task Sets(string path)
    {
        await using (var csv = new CsvExport(path, overwrite: false))
        {
            await csv.ResultSetAsync(0, [new("a", "int")]);
            await csv.RowAsync(0, new JsonArray(1));
            await csv.ResultSetAsync(1, [new("b", "int")]);
            await csv.RowAsync(1, new JsonArray(2));
            await csv.ResultSetAsync(2, [new("c", "int")]);
            await csv.RowAsync(2, new JsonArray(3));
            if (csv.Rows != 1 || csv.IgnoredResultSets != 2) throw new TestFailure($"rows {csv.Rows}, ignored {csv.IgnoredResultSets}");
        }
        var text = await File.ReadAllTextAsync(path);
        if (text != "a\r\n1\r\n") throw new TestFailure($"other result sets leaked into the file: {Show(text)}");
    }

    private static async Task Paths(string dir)
    {
        var path = Path.Combine(dir, "exists.csv");
        await File.WriteAllTextAsync(path, "keep");
        Refused(() => new CsvExport(path, overwrite: false), "an existing file without overwrite");
        if (await File.ReadAllTextAsync(path) != "keep") throw new TestFailure("a refused export changed the file");
        await using (var csv = new CsvExport(path, overwrite: true))
        {
            await csv.ResultSetAsync(0, [new("a", "int")]);
        }
        if (await File.ReadAllTextAsync(path) != "a\r\n") throw new TestFailure("overwrite did not replace the file");
        Refused(() => new CsvExport("relative.csv", overwrite: true), "a relative path");
        Refused(() => new CsvExport(Path.Combine(dir, "no", "such", "dir.csv"), overwrite: true), "a missing directory");
    }

    private static void Refused(Func<CsvExport> make, string what)
    {
        try
        {
            make().DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (RpcException)
        {
            return;
        }
        throw new TestFailure($"{what} was accepted");
    }

    private static string Show(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");
}
