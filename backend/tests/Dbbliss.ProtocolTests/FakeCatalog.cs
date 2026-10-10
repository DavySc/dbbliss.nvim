using Dbbliss.Backend.Catalog;
using Dbbliss.Backend.Engines;

namespace Dbbliss.ProtocolTests;

/// <summary>
/// A catalog that answers without a database. Names steer it: "missing" is a CatalogException (the
/// user can fix it), "crash" a driver-style failure (the catalog session must be replaced). It keeps
/// the session it was called with, so tests can see which session the backend used.
/// </summary>
public sealed class FakeCatalog : ICatalog
{
    public List<string> SessionsUsed { get; } = [];

    /// <summary>"hangdeaf" does not return until this is set and ignores cancellation, like a driver stuck in a call.</summary>
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task PrepareAsync(IEngineSession session, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<CatalogNode>> ChildrenAsync(IEngineSession session, CatalogPath path, CancellationToken ct)
    {
        SessionsUsed.Add(session.ServerSessionId);
        IReadOnlyList<CatalogNode> nodes = path.Database is null
            ? [new CatalogNode("db1", "database", true)]
            : [new CatalogNode($"under {path.Database}/{path.Schema}/{path.Folder}", "table", false)];
        return Task.FromResult(nodes);
    }

    public async Task<ObjectInfo> DescribeAsync(IEngineSession session, ObjectRequest request, CancellationToken ct)
    {
        SessionsUsed.Add(session.ServerSessionId);
        Steer(request);
        if ((request.Typed ?? request.Name) == "hang") await Task.Delay(Timeout.Infinite, ct);
        if ((request.Typed ?? request.Name) == "hangdeaf") await Gate.Task;
        return (new ObjectInfo($"table {request.Typed ?? request.Name}", "table",
            [new InfoSection("Columns", ["name", "type"], [["id", "integer"], ["note", null]]), InfoSection.OfText("Definition", "line 1\nline 2")]));
    }

    public Task<string> ScriptAsync(IEngineSession session, ObjectRequest request, CancellationToken ct)
    {
        SessionsUsed.Add(session.ServerSessionId);
        Steer(request);
        return Task.FromResult($"CREATE TABLE {request.Typed ?? request.Name} ();\n");
    }

    /// <summary>What the last names request asked for, so a test sees what the backend passed on.</summary>
    public (string? Database, bool IncludeSystem, int Limit)? LastNames { get; private set; }

    public Task<CatalogNames> NamesAsync(IEngineSession session, string? database, bool includeSystem, int limit, CancellationToken ct)
    {
        SessionsUsed.Add(session.ServerSessionId);
        LastNames = (database, includeSystem, limit);
        if (database == "crash") throw new InvalidOperationException("the driver broke");
        var all = new List<NameEntry> { new("public", "orders", "table"), new("public", "order_view", "view"), new("sales", "fn_total", "function") };
        if (includeSystem) all.Add(new NameEntry("pg_catalog", "pg_class", "table"));
        return Task.FromResult(new CatalogNames(all.Take(limit).ToList(), all.Count > limit));
    }

    public Task<TableColumns> ColumnsAsync(IEngineSession session, ObjectRequest request, CancellationToken ct)
    {
        SessionsUsed.Add(session.ServerSessionId);
        Steer(request);
        return Task.FromResult(new TableColumns("public", request.Typed ?? request.Name!, [new ColumnEntry("id", "integer"), new ColumnEntry("note", "text")]));
    }

    private static void Steer(ObjectRequest request)
    {
        var name = request.Typed ?? request.Name;
        if (name == "missing") throw new CatalogException("Nothing named missing.");
        if (name == "crash") throw new InvalidOperationException("the driver broke");
    }
}
