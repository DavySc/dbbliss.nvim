using System.Diagnostics;

namespace Dbbliss.Backend.Engines;

/// <summary>
/// Cancellation handle for one running query. The engine registers how to cancel at the
/// protocol level (TDS attention, PG CancelRequest, SQLCancel); <see cref="Cancel"/> fires it from
/// whichever thread the cancel request arrives on. The token stops our own loops and waits.
/// </summary>
public sealed class QueryControl : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Action? _protocolCancel;
    private bool _cancelRequested;
    private bool _finished;

    public QueryControl(string queryId) => QueryId = queryId;

    public string QueryId { get; }
    public CancellationToken Token => _cts.Token;
    public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
    public long? CancelRequestedAtMs { get; private set; }

    public bool CancelRequested
    {
        get { lock (_gate) return _cancelRequested; }
    }

    public bool Finished
    {
        get { lock (_gate) return _finished; }
    }

    /// <summary>
    /// Registers the driver's protocol-level cancel. If a cancel was already requested
    /// (it raced with query start), it fires immediately.
    /// </summary>
    public void SetProtocolCancel(Action cancel)
    {
        bool fireNow;
        lock (_gate)
        {
            _protocolCancel = cancel;
            fireNow = _cancelRequested && !_finished;
        }
        if (fireNow) Fire(cancel);
    }

    public void ClearProtocolCancel()
    {
        lock (_gate) _protocolCancel = null;
    }

    /// <returns>false if the query had already finished.</returns>
    public bool Cancel()
    {
        Action? action;
        lock (_gate)
        {
            if (_finished) return false;
            if (_cancelRequested) return true;
            _cancelRequested = true;
            CancelRequestedAtMs = Elapsed.ElapsedMilliseconds;
            action = _protocolCancel;
        }
        // Protocol cancel first: that is what actually stops the server.
        if (action is not null) Fire(action);
        _cts.Cancel();
        return true;
    }

    /// <summary>
    /// Re-sends the protocol cancel. Covers the race where the first cancel reached the driver
    /// before the statement was on the wire, where the driver's cancel is a no-op.
    /// </summary>
    public void RefireProtocolCancel()
    {
        Action? action;
        lock (_gate)
        {
            if (_finished || !_cancelRequested) return;
            action = _protocolCancel;
        }
        if (action is not null) Fire(action);
    }

    public void MarkFinished()
    {
        lock (_gate)
        {
            _finished = true;
            _protocolCancel = null;
        }
    }

    private void Fire(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn($"query {QueryId}: protocol cancel threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose() => _cts.Dispose();
}
