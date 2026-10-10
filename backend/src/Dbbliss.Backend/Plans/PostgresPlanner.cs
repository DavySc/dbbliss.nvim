using Dbbliss.Backend.Management;

namespace Dbbliss.Backend.Plans;

/// <summary>
/// EXPLAIN (FORMAT JSON) for an estimated plan, which runs nothing; EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) for an
/// actual one, which runs the statement, so inside a transaction of the plan session that is always rolled back.
/// </summary>
public sealed class PostgresPlanner : IPlanner
{
    public async Task<IReadOnlyList<PlanDocument>> PlanAsync(ManagementContext context, PlanRequest request, CancellationToken ct)
    {
        var actual = request.Mode == PlanMode.Actual;
        await using var session = await context.Engine.OpenAsync(context.Spec, new MessageLogSink(), ct);
        var sql = (actual ? "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " : "EXPLAIN (FORMAT JSON) ") + request.Sql;
        var plan = new PlannerSupport.FirstCell();
        if (actual) await session.BeginTransactionAsync(ct);
        try
        {
            await ControlledExecution.RunAsync(session, sql, plan, ct);
        }
        finally
        {
            if (actual) await PlannerSupport.RollBackQuietlyAsync(session);
        }
        return [PostgresPlanParser.Parse(plan.Value ?? throw new OperationFailedException("The server returned no plan."), request.Sql, request.Mode)];
    }
}
