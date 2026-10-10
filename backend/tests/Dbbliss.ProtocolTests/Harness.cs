using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Dbbliss.Backend.Engines;
using Dbbliss.Backend.Rpc;

namespace Dbbliss.ProtocolTests;

public sealed class TestFailure(string message) : Exception(message);

/// <summary>
/// The real <see cref="Backend.Backend"/> in-process, speaking JSON-RPC over anonymous pipes, with
/// <see cref="FakeEngine"/> as its only engine. Every message the backend writes is kept in order.
/// </summary>
public sealed class Harness : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly AnonymousPipeServerStream _stdin = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _stdout = new(PipeDirection.In);
    private readonly StreamWriter _writer;
    private readonly List<JsonObject> _messages = [];
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly Task _run;
    private readonly Task _read;
    private int _nextId;

    public FakeServer Server { get; } = new();

    public FakeEngine Engine { get; }

    public Backend.Backend Backend { get; }

    /// <summary>The backend's stdout. Can hold the backend right after it has written a chosen message.</summary>
    public HoldingStream Stdout { get; }

    public Harness(params IEngine[] extraEngines)
    {
        var backendIn = new AnonymousPipeClientStream(PipeDirection.In, _stdin.ClientSafePipeHandle);
        Stdout = new HoldingStream(new AnonymousPipeClientStream(PipeDirection.Out, _stdout.ClientSafePipeHandle));
        Engine = new FakeEngine(Server);
        Backend = new Backend.Backend(new Output(Stdout), [Engine, .. extraEngines]);
        _writer = new StreamWriter(_stdin, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _run = Task.Run(() => Backend.RunAsync(backendIn));
        _read = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        using var reader = new StreamReader(_stdout, new UTF8Encoding(false));
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (_messages) _messages.Add((JsonObject)JsonNode.Parse(line)!);
            _arrived.Release();
        }
    }

    /// <summary>Every message so far, in the order the backend wrote them.</summary>
    public IReadOnlyList<JsonObject> Messages
    {
        get { lock (_messages) return [.. _messages]; }
    }

    public int Send(string method, JsonObject? @params = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params ?? [] };
        lock (_writer) _writer.WriteLine(request.ToJsonString());
        return id;
    }

    /// <summary>Writes one line as it is, for requests that are not well-formed.</summary>
    public void SendRaw(string line)
    {
        lock (_writer) _writer.WriteLine(line);
    }

    /// <summary>Sends a request and returns its response (result or error).</summary>
    public Task<JsonObject> CallAsync(string method, JsonObject? @params = null)
    {
        var id = Send(method, @params);
        return WaitForAsync(m => m["id"]?.GetValue<int>() == id && m["method"] is null, $"response to {method}");
    }

    /// <summary>Sends a request and returns its result; an error response fails the test.</summary>
    public async Task<JsonObject> ResultAsync(string method, JsonObject? @params = null)
    {
        var response = await CallAsync(method, @params);
        return response["result"] as JsonObject
            ?? throw new TestFailure($"{method} failed: {response["error"]?.ToJsonString()}");
    }

    public Task<JsonObject> NotificationAsync(string method, string queryId) =>
        WaitForAsync(m => m["method"]?.GetValue<string>() == method && m["params"]?["query_id"]?.GetValue<string>() == queryId,
            $"{method} for {queryId}");

    public async Task<JsonObject> WaitForAsync(Func<JsonObject, bool> match, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            if (Messages.FirstOrDefault(match) is { } found) return found;
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !await _arrived.WaitAsync(left))
            {
                throw new TestFailure($"timed out waiting for {what}");
            }
        }
    }

    /// <param name="env">The connection's environment tag; left out when null.</param>
    public async Task<string> ConnectAsync(string? env = null)
    {
        var p = new JsonObject { ["engine"] = "fake", ["connection_string"] = "fake" };
        if (env is not null) p["env"] = env;
        var result = await ResultAsync("connect", p);
        return result["connection_id"]!.GetValue<string>();
    }

    /// <summary>Closes stdin, as Neovim exiting does, and waits for the backend to finish.</summary>
    public async Task CloseAsync()
    {
        _stdin.Dispose();
        await _run.WaitAsync(Timeout);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CloseAsync();
            await _read.WaitAsync(Timeout);
        }
        catch (TimeoutException)
        {
        }
        _stdout.Dispose();
    }
}

/// <summary>
/// What the Lua client believes about each connection's transaction: the value from the last
/// query/done or transaction/* response, applied in arrival order (lua/dbbliss/init.lua does the same).
/// </summary>
public static class ClientView
{
    public static Dictionary<string, string> Transactions(IReadOnlyList<JsonObject> messages, IReadOnlyDictionary<int, string> requestConnections, IReadOnlyDictionary<string, string> queryConnections)
    {
        var view = new Dictionary<string, string>();
        foreach (var m in messages)
        {
            if (m["method"]?.GetValue<string>() == "query/done"
                && queryConnections.TryGetValue(m["params"]!["query_id"]!.GetValue<string>(), out var qc)
                && m["params"]!["transaction"]?.GetValue<string>() is { } qt)
            {
                view[qc] = qt;
            }
            else if (m["method"] is null && m["id"]?.GetValue<int>() is { } id
                && requestConnections.TryGetValue(id, out var rc)
                && m["result"]?["transaction"]?.GetValue<string>() is { } rt)
            {
                view[rc] = rt;
            }
        }
        return view;
    }
}

/// <summary>
/// Replaces stderr, where the backend logs. Lets a test hold the backend at a chosen log line,
/// which is how the stale-report race is made deterministic.
/// </summary>
public sealed class StderrGate : TextWriter
{
    private readonly TextWriter _original;
    private readonly bool _echo;
    private string? _armed;
    private TaskCompletionSource? _reached;
    private ManualResetEventSlim? _release;

    private StderrGate(TextWriter original, bool echo)
    {
        _original = original;
        _echo = echo;
    }

    public static StderrGate Current { get; private set; } = null!;

    public static StderrGate Install(bool echo)
    {
        Current = new StderrGate(Console.Error, echo);
        Console.SetError(Current);
        return Current;
    }

    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>The next log line containing <paramref name="text"/> blocks until <see cref="Release"/>.</summary>
    public Task Arm(string text)
    {
        _release = new ManualResetEventSlim(false);
        _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _armed, text);
        return _reached.Task;
    }

    public void Release()
    {
        Volatile.Write(ref _armed, null);
        _release?.Set();
    }

    public override void WriteLine(string? value)
    {
        if (_echo) _original.WriteLine(value);
        if (value is not null && Volatile.Read(ref _armed) is { } armed && value.Contains(armed, StringComparison.Ordinal))
        {
            Volatile.Write(ref _armed, null);
            _reached!.TrySetResult();
            _release!.Wait(TimeSpan.FromSeconds(10));
        }
    }

    public override void Write(char value)
    {
        if (_echo) _original.Write(value);
    }
}

/// <summary>
/// Passes everything through. When armed, the flush that completes a message containing the given
/// text reaches the reader and then does not return until <see cref="Release"/>: the backend has
/// written the message (the client can act on it) but is still inside that write.
/// </summary>
public sealed class HoldingStream(Stream inner) : Stream
{
    private readonly MemoryStream _pending = new();
    private string? _armed;
    private TaskCompletionSource? _reached;
    private TaskCompletionSource? _release;

    public Task Arm(string text)
    {
        _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _armed, text);
        return _reached.Task;
    }

    public void Release() => _release?.TrySetResult();

    private string? _failOn;
    private volatile bool _failed;

    /// <summary>
    /// Once a message containing <paramref name="text"/> has been written, this and every later write
    /// fails with an IOException, as a closed stdout does. The reader has seen that message.
    /// </summary>
    public void FailAfter(string text) => Volatile.Write(ref _failOn, text);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (_failed) throw new IOException("stdout is closed (simulated)");
        lock (_pending) _pending.Write(buffer.Span);
        await inner.WriteAsync(buffer, ct);
    }

    public override async Task FlushAsync(CancellationToken ct)
    {
        await inner.FlushAsync(ct);
        string written;
        lock (_pending)
        {
            written = Encoding.UTF8.GetString(_pending.GetBuffer(), 0, (int)_pending.Length);
            _pending.SetLength(0);
        }
        if (Volatile.Read(ref _failOn) is { } failOn && written.Contains(failOn, StringComparison.Ordinal)) _failed = true;
        if (Volatile.Read(ref _armed) is { } armed && written.Contains(armed, StringComparison.Ordinal))
        {
            Volatile.Write(ref _armed, null);
            _reached!.TrySetResult();
            await _release!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
