using Dbbliss.Backend.Management;

namespace Dbbliss.Backend.Plans;

/// <summary>Plans for one engine, on a session of its own.</summary>
public interface IPlanner
{
    /// <summary>
    /// Gets the plan(s) of the statement. An actual plan runs it inside a transaction that is rolled back whatever
    /// happens. Cancelling the token stops the statement with the protocol-level cancel. Output without a plan is
    /// an <see cref="OperationFailedException"/>.
    /// </summary>
    Task<IReadOnlyList<PlanDocument>> PlanAsync(ManagementContext context, PlanRequest request, CancellationToken ct);
}
