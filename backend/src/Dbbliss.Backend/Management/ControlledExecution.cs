using Dbbliss.Backend.Engines;

namespace Dbbliss.Backend.Management;

/// <summary>
/// Runs a statement on a session of its own with the protocol-level cancel as the way to stop it (the attention of
/// TDS, PostgreSQL's CancelRequest), re-sent until the statement ends as for the user's queries: a cancel before the
/// statement is on the wire does nothing.
/// </summary>
public static class ControlledExecution
{
    private static readonly TimeSpan[] RefireDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1)];

    /// <summary>Cancelling <paramref name="ct"/> stops the statement and ends in <see cref="OperationCanceledException"/>, whatever the driver threw.</summary>
    public static async Task RunAsync(IEngineSession session, string sql, IResultSink sink, CancellationToken ct)
    {
        using var control = new QueryControl("operation");
        using var _ = ct.Register(() => control.Cancel());
        using var stopWatching = new CancellationTokenSource();
        var watching = Task.Run(async () =>
        {
            try
            {
                using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, stopWatching.Token);
                await Task.Delay(Timeout.Infinite, either.Token);
            }
            catch (OperationCanceledException)
            {
            }
            for (var attempt = 0; !stopWatching.IsCancellationRequested; attempt++)
            {
                try
                {
                    await Task.Delay(RefireDelays[Math.Min(attempt, RefireDelays.Length - 1)], stopWatching.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                control.RefireProtocolCancel();
            }
        });
        try
        {
            await session.ExecuteAsync(sql, sink, control);
        }
        catch (Exception) when (control.CancelRequested)
        {
            throw new OperationCanceledException(ct);
        }
        finally
        {
            control.MarkFinished();
            await stopWatching.CancelAsync();
            await watching;
        }
    }

    /// <summary>A sink for statements whose rows nobody wants.</summary>
    public sealed class DiscardRows : IResultSink
    {
        public ValueTask ResultSetAsync(int index, IReadOnlyList<ColumnInfo> columns) => ValueTask.CompletedTask;
        public ValueTask RowAsync(int index, System.Text.Json.Nodes.JsonArray row) => ValueTask.CompletedTask;
        public ValueTask ResultSetDoneAsync(int index, long rows) => ValueTask.CompletedTask;
    }
}
