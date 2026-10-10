using System.Text.Json.Nodes;

namespace Dbbliss.Backend.Plans;

public enum PlanMode
{
    /// <summary>The planner's guess; the statement is not run.</summary>
    Estimated,

    /// <summary>The statement is run (inside a transaction that is rolled back) and what happened is measured.</summary>
    Actual,
}

/// <param name="Sql">One statement (PostgreSQL) or one batch (SQL Server).</param>
/// <param name="ConfirmExecute">The caller says the user agreed that an actual plan runs this statement.</param>
public sealed record PlanRequest(string Sql, PlanMode Mode, bool ConfirmExecute);

/// <summary>One operator of a plan. Costs, rows and times are the server's units; null where the server gives none.</summary>
public sealed class PlanNode
{
    public int Id { get; init; }
    public required string Operator { get; init; }

    /// <summary>What it works on and how: relation, index, join type, predicate.</summary>
    public string? Detail { get; init; }

    public double? EstimatedRows { get; init; }

    /// <summary>The cost of this node and everything below it.</summary>
    public double? Cost { get; init; }

    /// <summary>Rows this node produced in all, over all its loops (actual plans).</summary>
    public double? ActualRows { get; init; }

    public double? Loops { get; init; }

    /// <summary>Time of this node and everything below it, in milliseconds, over all its loops (actual plans).</summary>
    public double? TimeMs { get; init; }

    /// <summary>The node's own cost / time: its figure minus its children's, never below 0.</summary>
    public double? SelfCost { get; set; }
    public double? SelfTimeMs { get; set; }

    /// <summary>Pages touched, as the engine counts them (PostgreSQL BUFFERS), for the viewer to show.</summary>
    public string? Buffers { get; init; }

    public List<string> Warnings { get; } = [];
    public List<PlanNode> Children { get; } = [];

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["id"] = Id,
            ["operator"] = Operator,
            ["detail"] = Detail,
            ["estimated_rows"] = EstimatedRows,
            ["cost"] = Cost,
            ["actual_rows"] = ActualRows,
            ["loops"] = Loops,
            ["time_ms"] = TimeMs,
            ["buffers"] = Buffers,
            ["self_cost"] = SelfCost,
            ["self_time_ms"] = SelfTimeMs,
            ["warnings"] = new JsonArray(Warnings.Select(w => (JsonNode)w).ToArray()),
            ["children"] = new JsonArray(Children.Select(c => (JsonNode)c.ToJson()).ToArray()),
        };
        return o;
    }
}

/// <param name="Totals">Whole-plan figures by name (planning_ms, execution_ms, ...); a missing one is not in the map.</param>
/// <param name="Notes">Things the engine says about the whole plan, such as a missing index.</param>
/// <param name="Raw">The engine's own plan text (JSON, XML), for saving or opening elsewhere.</param>
public sealed record PlanDocument(string Engine, PlanMode Mode, string Statement, PlanNode Root, IReadOnlyDictionary<string, double> Totals, IReadOnlyList<string> Notes, string Raw)
{
    public int? HottestId { get; init; }

    public JsonObject ToJson() => new()
    {
        ["engine"] = Engine,
        ["mode"] = Mode == PlanMode.Actual ? "actual" : "estimated",
        ["statement"] = Statement,
        ["root"] = Root.ToJson(),
        ["hottest"] = HottestId,
        ["totals"] = new JsonObject(Totals.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        ["notes"] = new JsonArray(Notes.Select(n => (JsonNode)n).ToArray()),
        ["raw"] = Raw,
    };
}

public static class PlanMath
{
    /// <summary>Fills in every node's own cost and time and returns the id of the hottest node.</summary>
    public static int? Annotate(PlanNode root)
    {
        AnnotateOwn(root);
        var all = new List<PlanNode>();
        Collect(root, all);
        // Time decides when there is any (an actual plan); the cost decides otherwise. The first node in plan order wins a tie.
        var byTime = Hottest(all, n => n.SelfTimeMs);
        return (byTime ?? Hottest(all, n => n.SelfCost))?.Id;
    }

    private static void Collect(PlanNode n, List<PlanNode> into)
    {
        into.Add(n);
        foreach (var c in n.Children) Collect(c, into);
    }

    private static PlanNode? Hottest(List<PlanNode> nodes, Func<PlanNode, double?> own)
    {
        PlanNode? best = null;
        foreach (var n in nodes)
        {
            if (own(n) is { } v && (best is null || v > own(best)!.Value)) best = n;
        }
        return best;
    }

    /// <summary>A node's time, or when the server gave it none (a Compute Scalar), what the nodes below it took.</summary>
    private static double? EffectiveTime(PlanNode n)
    {
        if (n.TimeMs is { } t) return t;
        var below = n.Children.Select(EffectiveTime).Where(x => x is not null).ToList();
        return below.Count == 0 ? null : below.Sum(x => x!.Value);
    }

    private static void AnnotateOwn(PlanNode n)
    {
        foreach (var c in n.Children) AnnotateOwn(c);
        n.SelfCost = n.Cost is { } cost ? Math.Max(0, cost - n.Children.Sum(c => c.Cost ?? 0)) : null;
        n.SelfTimeMs = n.TimeMs is { } time ? Math.Max(0, time - n.Children.Sum(c => EffectiveTime(c) ?? 0)) : null;
    }
}
