using System.Text.Json.Nodes;
using Dbbliss.Backend.Engines;
using Dbbliss.Backend.Management;

namespace Dbbliss.Backend.Plans;

public static class PlannerSupport
{
    /// <summary>
    /// Ends the transaction an actual plan ran in. A rollback that cannot be sent (the connection broke) loses nothing:
    /// the session is closed right after, and the server rolls back what a closed session left open.
    /// </summary>
    public static async Task RollBackQuietlyAsync(IEngineSession session)
    {
        try
        {
            await session.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warn($"plan session {session.ServerSessionId}: rollback failed ({ex.Message}); closing the session rolls it back");
        }
    }

    /// <summary>The first cell of the first row of the first result set.</summary>
    public sealed class FirstCell : IResultSink
    {
        public string? Value { get; private set; }

        public ValueTask ResultSetAsync(int index, IReadOnlyList<ColumnInfo> columns) => ValueTask.CompletedTask;

        public ValueTask RowAsync(int index, JsonArray row)
        {
            if (index == 0 && Value is null && row.Count > 0 && row[0] is JsonValue v && v.TryGetValue<string>(out var s)) Value = s;
            return ValueTask.CompletedTask;
        }

        public ValueTask ResultSetDoneAsync(int index, long rows) => ValueTask.CompletedTask;
    }
}
