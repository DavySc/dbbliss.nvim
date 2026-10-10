using System.Collections.Concurrent;
using Dbbliss.Backend.Management;
using Dbbliss.Backend.Plans;

namespace Dbbliss.ProtocolTests;

/// <summary>
/// Plans without a server. The statement steers it: "SELECT fail" fails, "SELECT hang" runs until cancelled, "SELECT empty" answers
/// with no plan at all; anything else gets a two-node plan.
/// </summary>
public sealed class FakePlanner : IPlanner
{
    public ConcurrentQueue<PlanRequest> Requests { get; } = new();
    public int Cancelled;

    public async Task<IReadOnlyList<PlanDocument>> PlanAsync(ManagementContext context, PlanRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        if (request.Sql == "SELECT fail") throw new OperationFailedException("relation \"nope\" does not exist");
        if (request.Sql == "SELECT crash") throw new InvalidOperationException("driver exploded");
        if (request.Sql == "SELECT hang")
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Cancelled);
                throw;
            }
        }
        if (request.Sql == "SELECT empty") return [];
        var root = new PlanNode { Id = 0, Operator = "Hash Join", Cost = 10, EstimatedRows = 100 };
        root.Children.Add(new PlanNode { Id = 1, Operator = "Seq Scan", Cost = 8, EstimatedRows = 100, Detail = "on t" });
        var hottest = PlanMath.Annotate(root);
        return [new PlanDocument("fake", request.Mode, request.Sql, root, new Dictionary<string, double> { ["cost"] = 10 }, ["a note"], "RAW") { HottestId = hottest }];
    }
}
