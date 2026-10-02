using System.Text.Json.Nodes;
using System.Threading.Channels;
using Dbbliss.Backend.Engines;

namespace Dbbliss.Backend.Rpc;

/// <summary>
/// Turns a query's rows into paged notifications.
///
/// The query thread never writes to stdout itself: it hands pages to a pump task. Only a bounded
/// number of row pages may be in flight; when Neovim stops reading, the query thread waits for a
/// slot, and that wait is cancellable. This matters for SQL Server: a query thread stuck on a full
/// stdout pipe stops draining the TDS socket, and the server never processes the attention.
///
/// Rows already received are always delivered, also when the query is cancelled or fails.
/// </summary>
public sealed class ResultStreamer : IResultSink, IAsyncDisposable
{
    private const int PageSize = 500;
    private const int MaxPagesInFlight = 8;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly Output _output;
    private readonly string _queryId;
    private readonly CancellationToken _queryToken;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _slots = new(MaxPagesInFlight, MaxPagesInFlight);
    private readonly Channel<(string Method, JsonObject Params, bool IsPage)> _queue =
        Channel.CreateUnbounded<(string, JsonObject, bool)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _timerStop = new();
    private readonly Task _pump;
    private readonly Task _timer;
    private JsonArray _page = [];
    private int _pageSet = -1;

    public ResultStreamer(Output output, string queryId, CancellationToken queryToken)
    {
        _output = output;
        _queryId = queryId;
        _queryToken = queryToken;
        _pump = Task.Run(PumpAsync);
        _timer = Task.Run(TimerAsync);
    }

    public async ValueTask ResultSetAsync(int index, IReadOnlyList<ColumnInfo> columns, CancellationToken ct)
    {
        var cols = new JsonArray();
        foreach (var c in columns) cols.Add(new JsonObject { ["name"] = c.Name, ["type"] = c.Type });
        await WithLockAsync(async () =>
        {
            await FlushLockedAsync(ct);
            Enqueue("query/resultset", new JsonObject { ["result_set"] = index, ["columns"] = cols });
        }, ct);
    }

    public async ValueTask RowAsync(int index, JsonArray row, CancellationToken ct)
    {
        await WithLockAsync(async () =>
        {
            _pageSet = index;
            _page.Add(row);
            if (_page.Count >= PageSize) await FlushLockedAsync(ct);
        }, ct);
    }

    public async ValueTask ResultSetDoneAsync(int index, long rows, CancellationToken ct)
    {
        await WithLockAsync(async () =>
        {
            await FlushLockedAsync(ct);
            Enqueue("query/resultset_done", new JsonObject { ["result_set"] = index, ["rows"] = rows });
        }, ct);
    }

    /// <summary>
    /// Server message (NOTICE/PRINT/...), raised synchronously from driver events on the query thread.
    /// Flushes pending rows first to keep the order; never drops the message.
    /// </summary>
    public void Message(string severity, string text, int? number, int? line)
    {
        var msg = new JsonObject { ["severity"] = severity, ["text"] = text, ["number"] = number, ["line"] = line };
        try
        {
            WithLockAsync(async () =>
            {
                await FlushLockedAsync(_queryToken);
                Enqueue("query/message", msg);
            }, _queryToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Cancelled while waiting for the client: deliver it anyway, possibly ahead of buffered rows.
            Enqueue("query/message", msg);
        }
    }

    /// <summary>Flushes buffered rows and waits until everything is written (or stdout is gone).</summary>
    public async Task CompleteAsync()
    {
        await _timerStop.CancelAsync();
        await _timer;
        await WithLockAsync(() => FlushLockedAsync(CancellationToken.None), CancellationToken.None);
        _queue.Writer.TryComplete();
        await _pump;
    }

    private void Enqueue(string method, JsonObject @params, bool isPage = false)
    {
        @params["query_id"] = _queryId;
        _queue.Writer.TryWrite((method, @params, isPage));
    }

    private async Task FlushLockedAsync(CancellationToken ct)
    {
        if (_page.Count == 0) return;
        await WaitAsync(_slots, ct);
        var page = _page;
        _page = [];
        Enqueue("query/rows", new JsonObject { ["result_set"] = _pageSet, ["rows"] = page }, isPage: true);
    }

    private async Task PumpAsync()
    {
        var broken = false;
        await foreach (var (method, @params, isPage) in _queue.Reader.ReadAllAsync())
        {
            if (!broken)
            {
                try
                {
                    await _output.NotifyAsync(method, @params);
                }
                catch (IOException)
                {
                    broken = true; // Output.Broken has started the shutdown; keep draining.
                }
            }
            if (isPage) _slots.Release();
        }
    }

    private async Task TimerAsync()
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_timerStop.Token, _queryToken);
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(linked.Token))
            {
                await WithLockAsync(() => FlushLockedAsync(linked.Token), linked.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Takes the semaphore without consulting the token when it is free: SemaphoreSlim.WaitAsync
    /// throws on a cancelled token even then, which would drop rows the server already sent.
    /// The token only interrupts a wait that would really block.
    /// </summary>
    private static Task WaitAsync(SemaphoreSlim semaphore, CancellationToken ct) =>
        semaphore.Wait(0) ? Task.CompletedTask : semaphore.WaitAsync(ct);

    private async Task WithLockAsync(Func<Task> action, CancellationToken ct)
    {
        await WaitAsync(_lock, ct);
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
        _timerStop.Dispose();
        _lock.Dispose();
        _slots.Dispose();
    }
}
