using System.Text.Json.Nodes;
using Dbbliss.Backend.Engines;
using Dbbliss.Backend.Management;

namespace Dbbliss.Backend.Plans;

/// <summary>
/// SET SHOWPLAN_XML ON for an estimated plan (the batch is then not run); SET STATISTICS XML ON for an actual one (it is,
/// so inside a transaction of the plan session that is always rolled back, and its own result sets are thrown away).
/// </summary>
public sealed class SqlServerPlanner : IPlanner
{
    private const string ShowplanColumn = "Microsoft SQL Server 2005 XML Showplan";

    public async Task<IReadOnlyList<PlanDocument>> PlanAsync(ManagementContext context, PlanRequest request, CancellationToken ct)
    {
        var actual = request.Mode == PlanMode.Actual;
        await using var session = await context.Engine.OpenAsync(context.Spec, new MessageLogSink(), ct);
        var plans = new ShowplanSink();
        if (actual) await session.BeginTransactionAsync(ct);
        try
        {
            await ControlledExecution.RunAsync(session, actual ? "SET STATISTICS XML ON" : "SET SHOWPLAN_XML ON", new ControlledExecution.DiscardRows(), ct);
            await ControlledExecution.RunAsync(session, request.Sql, plans, ct);
        }
        finally
        {
            if (actual) await PlannerSupport.RollBackQuietlyAsync(session);
        }
        if (plans.Xml.Count == 0) throw new OperationFailedException("The server returned no plan.");
        return plans.Xml.SelectMany(x => SqlServerPlanParser.Parse(x, request.Mode)).ToList();
    }

    /// <summary>Keeps the showplan result sets (one column of XML) and counts the rest away.</summary>
    private sealed class ShowplanSink : IResultSink
    {
        private bool _showplan;

        public List<string> Xml { get; } = [];

        public ValueTask ResultSetAsync(int index, IReadOnlyList<ColumnInfo> columns)
        {
            _showplan = columns.Count == 1 && columns[0].Name.StartsWith(ShowplanColumn, StringComparison.Ordinal);
            return ValueTask.CompletedTask;
        }

        public ValueTask RowAsync(int index, JsonArray row)
        {
            if (_showplan && row.Count > 0 && row[0] is JsonValue v && v.TryGetValue<string>(out var xml)) Xml.Add(xml);
            return ValueTask.CompletedTask;
        }

        public ValueTask ResultSetDoneAsync(int index, long rows) => ValueTask.CompletedTask;
    }
}
