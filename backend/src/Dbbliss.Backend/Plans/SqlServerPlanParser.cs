using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Dbbliss.Backend.Management;

namespace Dbbliss.Backend.Plans;

/// <summary>Reads SQL Server's showplan XML (SET SHOWPLAN_XML / SET STATISTICS XML): one plan per statement that has one.</summary>
public static class SqlServerPlanParser
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private static readonly XName RelOp = Ns + "RelOp";

    public static IReadOnlyList<PlanDocument> Parse(string xml, PlanMode mode)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new OperationFailedException("The plan the server returned could not be read as XML: " + ex.Message);
        }
        var plans = new List<PlanDocument>();
        foreach (var statement in document.Descendants().Where(e => e.Name.Namespace == Ns && e.Name.LocalName.StartsWith("Stmt", StringComparison.Ordinal)))
        {
            if (statement.Element(Ns + "QueryPlan") is not { } queryPlan || queryPlan.Element(RelOp) is not { } top) continue;
            var nextId = 0;
            var root = Node(top, ref nextId);
            var totals = new Dictionary<string, double>();
            Total(totals, "statement_cost", statement.Attribute("StatementSubTreeCost"));
            Total(totals, "statement_est_rows", statement.Attribute("StatementEstRows"));
            Total(totals, "compile_ms", queryPlan.Attribute("CompileTime"));
            Total(totals, "dop", queryPlan.Attribute("DegreeOfParallelism"));
            Total(totals, "memory_grant_kb", queryPlan.Attribute("MemoryGrant"));
            var notes = new List<string>();
            foreach (var index in queryPlan.Descendants(Ns + "MissingIndexGroup")) notes.Add(MissingIndex(index));
            if (queryPlan.Element(Ns + "Warnings") is { } warnings) notes.AddRange(WarningTexts(warnings));
            plans.Add(new PlanDocument("sqlserver", mode, statement.Attribute("StatementText")?.Value ?? "", root, totals, notes, document.ToString(SaveOptions.DisableFormatting))
            {
                HottestId = PlanMath.Annotate(root),
            });
        }
        return plans.Count > 0 ? plans : throw new OperationFailedException("The server returned no plan.");
    }

    private static void Total(Dictionary<string, double> totals, string name, XAttribute? a)
    {
        if (a is not null && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) totals[name] = v;
    }

    private static double? Number(XElement e, string attribute) =>
        e.Attribute(attribute) is { } a && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>The nearest RelOp above an element, which is the operator it belongs to.</summary>
    private static bool Belongs(XElement e, XElement relOp) => ReferenceEquals(e.Ancestors(RelOp).FirstOrDefault(), relOp);

    private static PlanNode Node(XElement relOp, ref int nextId)
    {
        var id = (int?)Number(relOp, "NodeId") ?? nextId;
        nextId = Math.Max(nextId, id + 1);
        var physical = relOp.Attribute("PhysicalOp")?.Value ?? "?";
        var logical = relOp.Attribute("LogicalOp")?.Value;
        var threads = relOp.Element(Ns + "RunTimeInformation")?.Elements(Ns + "RunTimeCountersPerThread").ToList() ?? [];
        var node = new PlanNode
        {
            Id = id,
            Operator = logical is null || logical == physical ? physical : $"{physical} ({logical})",
            Detail = Detail(relOp),
            EstimatedRows = Number(relOp, "EstimateRows"),
            Cost = Number(relOp, "EstimatedTotalSubtreeCost"),
            // Several threads each did a share: the rows add up, the time is the longest any of them took.
            ActualRows = threads.Count == 0 ? null : threads.Sum(t => Number(t, "ActualRows") ?? 0),
            Loops = threads.Any(t => t.Attribute("ActualExecutions") is not null) ? threads.Sum(t => Number(t, "ActualExecutions") ?? 0) : null,
            TimeMs = threads.Count == 0 ? null : threads.Max(t => Number(t, "ActualElapsedms") ?? 0),
        };
        if (relOp.Element(Ns + "Warnings") is { } warnings) node.Warnings.AddRange(WarningTexts(warnings));
        foreach (var child in relOp.Descendants(RelOp).Where(d => Belongs(d, relOp))) node.Children.Add(Node(child, ref nextId));
        return node;
    }

    private static string? Detail(XElement relOp)
    {
        var parts = new List<string>();
        if (relOp.Descendants(Ns + "Object").FirstOrDefault(o => Belongs(o, relOp)) is { } obj)
        {
            var table = string.Join(".", new[] { obj.Attribute("Schema")?.Value, obj.Attribute("Table")?.Value }.Where(x => !string.IsNullOrEmpty(x)));
            if (table.Length > 0) parts.Add(table + (obj.Attribute("Alias")?.Value is { Length: > 0 } alias ? " as " + alias : ""));
            if (obj.Attribute("Index")?.Value is { Length: > 0 } index) parts.Add("index " + index);
        }
        var predicate = relOp.Descendants(Ns + "Predicate").FirstOrDefault(p => Belongs(p, relOp))?.Descendants(Ns + "ScalarOperator").FirstOrDefault()?.Attribute("ScalarString")?.Value;
        if (!string.IsNullOrEmpty(predicate)) parts.Add("where " + predicate);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string Humanize(string pascal)
    {
        var sb = new StringBuilder();
        foreach (var c in pascal)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>The flags (attributes that are true) and the findings (child elements) of a Warnings element.</summary>
    private static IEnumerable<string> WarningTexts(XElement warnings)
    {
        foreach (var a in warnings.Attributes())
        {
            if (a.Value is "1" or "true" or "True") yield return Humanize(a.Name.LocalName);
        }
        foreach (var e in warnings.Elements())
        {
            yield return e.Name.LocalName switch
            {
                "SpillToTempDb" => "spill to tempdb" + (e.Attribute("SpillLevel") is { } level ? $" (level {level.Value})" : ""),
                "PlanAffectingConvert" => $"plan-affecting convert ({e.Attribute("ConvertIssue")?.Value}): {e.Attribute("Expression")?.Value}",
                var other => Humanize(other),
            };
        }
    }

    private static string MissingIndex(XElement group)
    {
        var index = group.Element(Ns + "MissingIndex")!;
        var columns = index.Elements(Ns + "ColumnGroup").ToList();
        string Names(Func<string, bool> usage) => string.Join(", ", columns.Where(g => usage(g.Attribute("Usage")?.Value ?? "")).SelectMany(g => g.Elements(Ns + "Column")).Select(c => c.Attribute("Name")?.Value));
        var keys = Names(u => u != "INCLUDE");
        var include = Names(u => u == "INCLUDE");
        return $"missing index (impact {group.Attribute("Impact")?.Value}%): {index.Attribute("Schema")?.Value}.{index.Attribute("Table")?.Value} ({keys})" + (include.Length > 0 ? $" include ({include})" : "");
    }
}
