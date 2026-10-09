using System.Text.Json.Nodes;
using System.Threading.Channels;
using Dbbliss.Backend.Engines;

namespace Dbbliss.Backend.Rpc;

/// <summary>
/// Turns a query's rows into paged notifications. Modelled in spec/tla/QueryLifecycle.tla
/// (configuration Proposed); the comments name the corresponding model actions.
///
/// The query thread never writes to stdout itself: it hands pages to a pump task. Before a cancel,
/// at most <see cref="MaxPagesInFlight"/> row pages may be in flight; when Neovim stops reading,
/// the query thread waits for a slot (Block). That wait must end on a cancel: a query thread that
/// stops reading the socket keeps SQL Server blocked in ASYNC_NETWORK_IO, where it never sees the
/// attention.
///
/// After a cancel, backpressure no longer applies (HandOverflow / CancelWake): rows are queued
/// without a slot so the driver keeps draining the socket and the server can finish. What is
/// still in flight after a cancel is bounded by socket buffers, so this is bounded memory in
/// practice; past <see cref="OverflowRowCap"/> rows are dropped and reported as truncated.
/// Nothing is ever dropped silently: never throw away rows by unwinding the driver's reader.
///
/// Pull paging (<see cref="Grant"/>): the client allows a number of rows at a time. When they are
/// used up the query thread flushes what it has, tells the client (query/paused) and waits, exactly
/// like a wait for a page slot: the driver stops reading, the socket fills and the server is paused
/// by TCP backpressure. A cancel ends the wait (CancelWake) and the rest of the result drains
/// through the overflow path as before, so a paused query is cancelled like any other. A client
/// that never fetches costs a paused query, not memory.
/// </summary>
public sealed class ResultStreamer : IResultSink, IAsyncDisposable
{
    private const int PageSize = 500;
    private const int MaxPagesInFlight = 8;
    /// <summary>Slot-less rows allowed in flight after a cancel before rows are dropped (reported).</summary>
    public const int OverflowRowCap = 200_000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly Output _output;
    private readonly string _queryId;
    private readonly CancellationToken _queryToken;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _slots = new(MaxPagesInFlight, MaxPagesInFlight);
    private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private readonly PeriodicTimer _tick = new(FlushInterval);
    private readonly Task _pump;
    private readonly Task _timer;
    private readonly SemaphoreSlim? _credit;
    private long _accepted;
    private long _granted;
    private bool _pauseAnnounced;
    private JsonArray _page = [];
    private int _pageSet = -1;
    private long _overflowRows;
    private long _truncatedRows;

    private readonly record struct Item(string Method, JsonObject Params, bool HoldsSlot, int OverflowRows);

    /// <param name="window">Rows the client allows up front; null for no limit (no pull paging).</param>
    public ResultStreamer(Output output, string queryId, CancellationToken queryToken, long? window = null)
    {
        _output = output;
        _queryId = queryId;
        _queryToken = queryToken;
        if (window is { } w)
        {
            _credit = new SemaphoreSlim(0, int.MaxValue);
            Grant(w);
        }
        _pump = Task.Run(PumpAsync);
        _timer = Task.Run(TimerAsync);
    }

    /// <summary>Rows dropped because the post-cancel overflow cap was reached. Reported in query/done.</summary>
    public long TruncatedRows => Interlocked.Read(ref _truncatedRows);

    public async ValueTask ResultSetAsync(int index, IReadOnlyList<ColumnInfo> columns)
    {
        var cols = new JsonArray();
        foreach (var c in columns) cols.Add(new JsonObject { ["name"] = c.Name, ["type"] = c.Type });
        await WithLockAsync(async () =>
        {
            await FlushLockedAsync();
            Enqueue(new Item("query/resultset", new JsonObject { ["result_set"] = index, ["columns"] = cols }, false, 0));
        });
    }

    /// <summary>Allows <paramref name="rows"/> more rows to be sent. Returns the total allowed so far.</summary>
    public long Grant(long rows)
    {
        if (_credit is null || rows <= 0) return Interlocked.Read(ref _granted);
        var granted = Interlocked.Add(ref _granted, rows);
        while (rows > 0)
        {
            var step = (int)Math.Min(rows, 1 << 30);
            _credit.Release(step);
            rows -= step;
        }
        return granted;
    }

    public async ValueTask RowAsync(int index, JsonArray row)
    {
        await WithLockAsync(async () =>
        {
            if (_pageSet != index) await FlushLockedAsync();
            _pageSet = index;
            await AcquireCreditAsync();
            _page.Add(row);
            if (_page.Count >= PageSize) await FlushLockedAsync();
        });
    }

    public async ValueTask ResultSetDoneAsync(int index, long rows)
    {
        await WithLockAsync(async () =>
        {
            await FlushLockedAsync();
            Enqueue(new Item("query/resultset_done", new JsonObject { ["result_set"] = index, ["rows"] = rows }, false, 0));
        });
    }

    /// <summary>
    /// Server message (NOTICE/PRINT/...), raised synchronously from driver events on the query thread.
    /// Flushes pending rows first to keep the order.
    /// </summary>
    public void Message(string severity, string text, int? number, int? line)
    {
        var msg = new JsonObject { ["severity"] = severity, ["text"] = text, ["number"] = number, ["line"] = line };
        WithLockAsync(async () =>
        {
            await FlushLockedAsync();
            Enqueue(new Item("query/message", msg, false, 0));
        }).GetAwaiter().GetResult();
    }

    /// <summary>Flushes buffered rows and waits until everything is written (or stdout is gone).</summary>
    public async Task CompleteAsync()
    {
        _tick.Dispose();
        await _timer;
        await WithLockAsync(FlushLockedAsync);
        _queue.Writer.TryComplete();
        await _pump;
    }

    private void Enqueue(Item item)
    {
        item.Params["query_id"] = _queryId;
        _queue.Writer.TryWrite(item);
    }

    /// <summary>
    /// Takes one row's credit. Out of credit: hand over what is held, tell the client once, and wait
    /// for a grant. A cancel ends the wait and rows go on without credit (CancelWake), so the
    /// server can finish; those rows are not dropped.
    /// </summary>
    private async Task AcquireCreditAsync()
    {
        if (_credit is null) return;
        _accepted++;
        if (_credit.Wait(0))
        {
            _pauseAnnounced = false;
            return;
        }
        if (_queryToken.IsCancellationRequested) return;
        await FlushLockedAsync();
        if (!_pauseAnnounced)
        {
            _pauseAnnounced = true;
            Enqueue(new Item("query/paused", new JsonObject { ["rows_sent"] = _accepted - 1 }, false, 0));
        }
        try
        {
            await _credit.WaitAsync(_queryToken);
            _pauseAnnounced = false;
        }
        catch (OperationCanceledException)
        {
            // CancelWake: carry on without credit.
        }
    }

    /// <summary>
    /// Hands the current page to the pump. Waits for a slot (Block) unless the query was cancelled,
    /// in which case the page bypasses backpressure (HandOverflow / CancelWake). Never throws on cancel.
    /// </summary>
    private async Task FlushLockedAsync()
    {
        if (_page.Count == 0) return;
        var hasSlot = _slots.Wait(0);
        if (!hasSlot && !_queryToken.IsCancellationRequested)
        {
            try
            {
                await _slots.WaitAsync(_queryToken);
                hasSlot = true;
            }
            catch (OperationCanceledException)
            {
                // CancelWake: fall through to overflow.
            }
        }
        var page = _page;
        _page = [];
        var overflow = 0;
        if (!hasSlot)
        {
            if (Interlocked.Read(ref _overflowRows) + page.Count > OverflowRowCap)
            {
                Interlocked.Add(ref _truncatedRows, page.Count);
                return;
            }
            overflow = page.Count;
            Interlocked.Add(ref _overflowRows, overflow);
        }
        Enqueue(new Item("query/rows", new JsonObject { ["result_set"] = _pageSet, ["rows"] = page }, hasSlot, overflow));
    }

    /// <summary>Pump: one notification at a time, in order. Writes to a dead Neovim are skipped.</summary>
    private async Task PumpAsync()
    {
        var broken = false;
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            if (!broken)
            {
                try
                {
                    await _output.NotifyAsync(item.Method, item.Params);
                }
                catch (IOException)
                {
                    broken = true; // Output.Broken has started the shutdown; keep draining.
                }
            }
            if (item.HoldsSlot) _slots.Release();
            if (item.OverflowRows > 0) Interlocked.Add(ref _overflowRows, -item.OverflowRows);
        }
    }

    /// <summary>Flushes a slow trickle of rows. Only when a slot is free: the timer never waits while holding the lock.</summary>
    private async Task TimerAsync()
    {
        // Disposing the timer ends the wait with false: no exception is needed to stop the loop.
        while (await _tick.WaitForNextTickAsync())
        {
            await WithLockAsync(() =>
            {
                if (_page.Count > 0 && _slots.Wait(0))
                {
                    _slots.Release();
                    return FlushLockedAsync();
                }
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>
    /// The lock is only ever held across a slot wait that a cancel ends, so waiting for it needs no
    /// token. (A cancellable lock wait would unwind the query thread and drop rows.)
    /// </summary>
    private async Task WithLockAsync(Func<Task> action)
    {
        await _lock.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CompleteAsync();
        _lock.Dispose();
        _slots.Dispose();
        _credit?.Dispose();
    }
}
