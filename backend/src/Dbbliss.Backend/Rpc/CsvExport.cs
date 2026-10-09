using System.Text;
using System.Text.Json.Nodes;
using Dbbliss.Backend.Engines;

namespace Dbbliss.Backend.Rpc;

/// <summary>
/// Writes the first result set of a query straight to a CSV file, so an export of any size never
/// passes through Neovim. Like every sink it never throws because of a cancel (rows the server sent
/// must not be dropped by unwinding the driver's reader); a cancelled or failed export leaves the
/// file as far as it got, and query/done says so (<see cref="Complete"/> is false then).
///
/// RFC 4180 quoting, UTF-8 without a BOM, CRLF line ends, NULL as an empty field. Other result sets
/// are not exported; <see cref="IgnoredResultSets"/> counts them so the client can say so.
/// </summary>
public sealed class CsvExport : IResultSink, IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private int _first = -1;

    public string Path { get; }

    /// <summary>Data rows written.</summary>
    public long Rows { get; private set; }

    public int IgnoredResultSets { get; private set; }

    public bool Complete { get; set; }

    /// <summary>Fails if the file exists and <paramref name="overwrite"/> is false.</summary>
    public CsvExport(string path, bool overwrite)
    {
        if (!System.IO.Path.IsPathRooted(path))
        {
            throw new RpcException(RpcErrors.InvalidParams, "Export path must be absolute.");
        }
        Path = path;
        FileStream stream;
        try
        {
            stream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
        }
        catch (IOException) when (!overwrite && File.Exists(path))
        {
            throw new RpcException(RpcErrors.InvalidParams, $"{path} already exists.", new JsonObject { ["exists"] = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            throw new RpcException(RpcErrors.InvalidParams, $"Cannot write {path}: {ex.Message}");
        }
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n" };
    }

    public async ValueTask ResultSetAsync(int index, IReadOnlyList<ColumnInfo> columns)
    {
        if (_first < 0) _first = index;
        if (index != _first)
        {
            IgnoredResultSets++;
            return;
        }
        await _writer.WriteLineAsync(string.Join(',', columns.Select(c => Field(c.Name))));
    }

    public async ValueTask RowAsync(int index, JsonArray row)
    {
        if (index != _first) return;
        var line = new StringBuilder();
        for (var i = 0; i < row.Count; i++)
        {
            if (i > 0) line.Append(',');
            line.Append(Field(row[i]));
        }
        await _writer.WriteLineAsync(line.ToString());
        Rows++;
    }

    public ValueTask ResultSetDoneAsync(int index, long rows) => ValueTask.CompletedTask;

    internal static string Field(JsonNode? value)
    {
        var text = value switch
        {
            null => "",
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => value.ToJsonString(),
        };
        return Field(text);
    }

    private static string Field(string text) =>
        text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
    }
}
