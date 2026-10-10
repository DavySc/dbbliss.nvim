using System.Text.Json.Nodes;
using Dbbliss.Backend.Engines;

namespace Dbbliss.Backend.Catalog;

/// <summary>
/// Where in the browser tree a listing is asked for: nothing set is the root (databases), a database
/// lists its schemas, a schema lists its folders (tables, views, ...), a folder lists its objects.
/// </summary>
public sealed record CatalogPath(string? Database = null, string? Schema = null, string? Folder = null);

/// <param name="Kind">database | schema | folder | table | view | function | procedure</param>
/// <param name="Identity">What tells overloads apart (a function's argument types); null otherwise.</param>
/// <param name="System">A system database or schema: the tree hides it unless asked.</param>
/// <param name="Browsable">False for a database this connection cannot look into (PostgreSQL: any but its own).</param>
public sealed record CatalogNode(
    string Name, string Kind, bool Expandable, string? Folder = null, string? Identity = null, bool System = false,
    bool Browsable = true, string? Detail = null)
{
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["kind"] = Kind,
        ["expandable"] = Expandable,
        ["folder"] = Folder,
        ["identity"] = Identity,
        ["system"] = System,
        ["browsable"] = Browsable,
        ["detail"] = Detail,
    };
}

/// <summary>
/// An object to describe or script: either text as typed in a buffer (<see cref="Typed"/>, possibly
/// qualified, resolved by the server's own rules), or an exact one from the tree.
/// </summary>
public sealed record ObjectRequest(
    string? Typed, string? Database, string? Schema, string? Name, string? Kind, string? Identity);

/// <summary>A table of information: the info buffer is a list of these, so Lua knows nothing of the engine.</summary>
/// <param name="Text">The rows are lines of text (a definition), one per row in the first column: shown as is, not as a table.</param>
public sealed record InfoSection(string Title, IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool Text = false)
{
    public static InfoSection OfText(string title, string text) =>
        new(title, ["text"], text.Replace("\r\n", "\n").Split('\n').Select(l => new object?[] { l }).ToList(), Text: true);

    public JsonObject ToJson()
    {
        var rows = new JsonArray();
        foreach (var row in Rows)
        {
            var r = new JsonArray();
            foreach (var v in row) r.Add(ValueConverter.Convert(v));
            rows.Add(r);
        }
        return new JsonObject
        {
            ["title"] = Title,
            ["columns"] = new JsonArray(Columns.Select(c => (JsonNode)c).ToArray()),
            ["rows"] = rows,
            ["text"] = Text,
        };
    }
}

public sealed record ObjectInfo(string Title, string Kind, IReadOnlyList<InfoSection> Sections)
{
    public JsonObject ToJson() => new()
    {
        ["title"] = Title,
        ["kind"] = Kind,
        ["sections"] = new JsonArray(Sections.Select(s => (JsonNode)s.ToJson()).ToArray()),
    };
}

/// <summary>One name for completion: a table, view, function or procedure.</summary>
/// <param name="Kind">table | view | function | procedure</param>
public sealed record NameEntry(string Schema, string Name, string Kind)
{
    public JsonObject ToJson() => new() { ["schema"] = Schema, ["name"] = Name, ["kind"] = Kind };
}

/// <summary>The names of a database for completion. <paramref name="Truncated"/>: there were more than the limit and the rest is not here.</summary>
public sealed record CatalogNames(IReadOnlyList<NameEntry> Items, bool Truncated);

public sealed record ColumnEntry(string Name, string Type)
{
    public JsonObject ToJson() => new() { ["name"] = Name, ["type"] = Type };
}

/// <summary>The columns of one table or view, with the name the server resolved it to.</summary>
public sealed record TableColumns(string Schema, string Name, IReadOnlyList<ColumnEntry> Columns);

/// <summary>A catalog operation the user can fix: the name matched nothing, or matched too much.</summary>
public sealed class CatalogException(string message) : Exception(message);

/// <summary>
/// Catalog queries for one engine. They run on a session of their own (never the user's), so an
/// aborted transaction or a long-running query on the user's connection does not stop the tree or an
/// object lookup. Catalog SQL never uses DISTINCT or GROUP BY to remove duplicates: a duplicate means
/// a wrong join, which is fixed with the join, EXISTS, or a window function.
/// </summary>
public interface ICatalog
{
    /// <summary>Called once on a new catalog session (PostgreSQL: makes it read-only).</summary>
    Task PrepareAsync(IEngineSession session, CancellationToken ct);

    Task<IReadOnlyList<CatalogNode>> ChildrenAsync(IEngineSession session, CatalogPath path, CancellationToken ct);

    Task<ObjectInfo> DescribeAsync(IEngineSession session, ObjectRequest request, CancellationToken ct);

    /// <summary>A CREATE script for the object.</summary>
    Task<string> ScriptAsync(IEngineSession session, ObjectRequest request, CancellationToken ct);

    /// <summary>
    /// The tables, views, functions and procedures of a database (null: the session's own), for completion: at most
    /// <paramref name="limit"/>, in schema and name order, <see cref="CatalogNames.Truncated"/> set when there were more.
    /// System schemas only when asked.
    /// </summary>
    Task<CatalogNames> NamesAsync(IEngineSession session, string? database, bool includeSystem, int limit, CancellationToken ct);

    /// <summary>The columns of a table or view the server resolves (<see cref="CatalogException"/> when it is none).</summary>
    Task<TableColumns> ColumnsAsync(IEngineSession session, ObjectRequest request, CancellationToken ct);
}
