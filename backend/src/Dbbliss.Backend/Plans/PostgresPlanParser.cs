using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dbbliss.Backend.Management;

namespace Dbbliss.Backend.Plans;

/// <summary>Reads PostgreSQL's <c>EXPLAIN (FORMAT JSON)</c>: an array holding one object with a "Plan" tree.</summary>
public static class PostgresPlanParser
{
    public static PlanDocument Parse(string json, string statement, PlanMode mode)
    {
        JsonNode? document;
        try
        {
            document = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new OperationFailedException("The plan the server returned could not be read as JSON: " + ex.Message);
        }
        var first = document is JsonArray { Count: > 0 } array ? array[0] as JsonObject : null;
        if (first?["Plan"] is not JsonObject plan) throw new OperationFailedException("The server returned no plan.");

        var nextId = 0;
        var root = Node(plan, ref nextId);
        var totals = new Dictionary<string, double>();
        if (root.Cost is { } cost) totals["cost"] = cost;
        if (Number(first["Planning Time"]) is { } planning) totals["planning_ms"] = planning;
        if (Number(first["Execution Time"]) is { } execution) totals["execution_ms"] = execution;
        return new PlanDocument("postgres", mode, statement, root, totals, [], json) { HottestId = PlanMath.Annotate(root) };
    }

    private static double? Number(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static PlanNode Node(JsonObject o, ref int nextId)
    {
        var loops = Number(o["Actual Loops"]);
        var node = new PlanNode
        {
            Id = nextId++,
            Operator = Text(o, "Node Type") ?? "?",
            Detail = Detail(o),
            EstimatedRows = Number(o["Plan Rows"]),
            Cost = Number(o["Total Cost"]),
            // Rows and time are reported per loop; the node's worth in all is the product.
            ActualRows = Number(o["Actual Rows"]) is { } rows ? rows * (loops ?? 1) : null,
            Loops = loops,
            TimeMs = Number(o["Actual Total Time"]) is { } time ? time * (loops ?? 1) : null,
            Buffers = Buffers(o),
        };
        Warn(o, node.Warnings);
        if (o["Plans"] is JsonArray children)
        {
            foreach (var child in children.OfType<JsonObject>()) node.Children.Add(Node(child, ref nextId));
        }
        return node;
    }

    private static string? Detail(JsonObject o)
    {
        var parts = new List<string>();
        if (Text(o, "Relation Name") is { } relation)
        {
            var qualified = Text(o, "Schema") is { } schema ? schema + "." + relation : relation;
            var alias = Text(o, "Alias");
            parts.Add("on " + qualified + (alias is not null && alias != relation ? " " + alias : ""));
        }
        if (Text(o, "Index Name") is { } index) parts.Add("index " + index);
        if (Text(o, "Join Type") is { } join) parts.Add(join + " join");
        foreach (var (key, label) in new[] { ("Index Cond", "index cond"), ("Recheck Cond", "recheck cond"), ("Hash Cond", "hash cond"), ("Merge Cond", "merge cond"), ("Join Filter", "join filter"), ("Filter", "filter") })
        {
            if (Text(o, key) is not { } condition) continue;
            var removed = key == "Filter" ? Number(o["Rows Removed by Filter"]) : null;
            parts.Add($"{label}: {condition}" + (removed is > 0 ? $" ({removed.Value.ToString("0", CultureInfo.InvariantCulture)} removed)" : ""));
        }
        if (o["Sort Key"] is JsonArray { Count: > 0 } keys) parts.Add("sort by " + string.Join(", ", keys.Select(k => k?.GetValue<string>())));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string? Buffers(JsonObject o)
    {
        var parts = new List<string>();
        foreach (var (kind, label) in new[] { ("Shared", "shared"), ("Local", "local"), ("Temp", "temp") })
        {
            foreach (var what in new[] { "Hit", "Read", "Dirtied", "Written" })
            {
                if (Number(o[$"{kind} {what} Blocks"]) is > 0 and var n) parts.Add($"{label} {what.ToLowerInvariant()}={n.ToString("0", CultureInfo.InvariantCulture)}");
            }
        }
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>What a node says about its own trouble: work that went to disk, workers that did not start.</summary>
    private static void Warn(JsonObject o, List<string> warnings)
    {
        if (Text(o, "Sort Space Type") == "Disk") warnings.Add($"sort spilled to disk ({Number(o["Sort Space Used"])?.ToString("0", CultureInfo.InvariantCulture) ?? "?"} kB)");
        if (Number(o["Hash Batches"]) is > 1 and var batches) warnings.Add($"hash in {batches.ToString("0", CultureInfo.InvariantCulture)} batches (spilled to disk)");
        if (Number(o["Disk Usage"]) is > 0 and var disk) warnings.Add($"hash aggregate spilled to disk ({disk.ToString("0", CultureInfo.InvariantCulture)} kB)");
        if (Number(o["Workers Planned"]) is { } planned && Number(o["Workers Launched"]) is { } launched && launched < planned)
        {
            warnings.Add($"launched {launched.ToString("0", CultureInfo.InvariantCulture)} of {planned.ToString("0", CultureInfo.InvariantCulture)} planned workers");
        }
    }
}
