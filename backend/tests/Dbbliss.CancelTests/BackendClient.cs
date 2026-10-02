using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Dbbliss.CancelTests;

/// <summary>Spawns the real backend and speaks its stdio protocol, like the Lua client does.</summary>
public sealed class BackendClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly List<JsonObject> _notifications = [];
    private readonly SemaphoreSlim _notified = new(0);
    private readonly StringBuilder _stderr = new();
    private readonly ManualResetEventSlim _readGate = new(true);
    private readonly Task _reader;
    private int _nextId;

    private BackendClient(Process process)
    {
        _process = process;
        _reader = Task.Run(ReadLoopAsync);
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) lock (_stderr) _stderr.AppendLine(e.Data);
        };
        _process.BeginErrorReadLine();
    }

    public int Pid => _process.Id;
    public bool HasExited => _process.HasExited;

    public string Stderr
    {
        get { lock (_stderr) return _stderr.ToString(); }
    }

    public static BackendClient Start(string path, IDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo(path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var (k, v) in env) psi.Environment[k] = v;
        return new BackendClient(Process.Start(psi) ?? throw new InvalidOperationException("Failed to start backend."));
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await ReadLinesAsync();
        }
        catch (Exception ex)
        {
            lock (_stderr) _stderr.AppendLine("HARNESS READ LOOP FAILED: " + ex);
            Console.Error.WriteLine("harness read loop failed: " + ex);
            foreach (var tcs in _pending.Values) tcs.TrySetException(ex);
        }
    }

    private async Task ReadLinesAsync()
    {
        var stdout = _process.StandardOutput;
        while (true)
        {
            _readGate.Wait();
            var line = await stdout.ReadLineAsync();
            if (line is null) break;
            var msg = JsonNode.Parse(line)!.AsObject();
            if (msg["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var id) && _pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetResult(msg);
            }
            else
            {
                lock (_notifications) _notifications.Add(msg);
                _notified.Release();
            }
        }
        foreach (var tcs in _pending.Values) tcs.TrySetException(new IOException("backend stdout closed"));
    }

    /// <summary>Sends a request; returns the response task without waiting for it.</summary>
    public Task<JsonObject> Send(string method, JsonObject? @params = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params ?? [] };
        lock (_process)
        {
            _process.StandardInput.Write(msg.ToJsonString() + "\n");
            _process.StandardInput.Flush();
        }
        return tcs.Task;
    }

    /// <summary>Sends a request and returns its result; throws on an error response.</summary>
    public async Task<JsonObject> RequestAsync(string method, JsonObject? @params = null, int timeoutMs = 30000)
    {
        var response = await Send(method, @params).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        if (response["error"] is JsonObject error)
        {
            throw new BackendErrorException(error["message"]?.GetValue<string>() ?? "?", error);
        }
        return response["result"]?.AsObject() ?? [];
    }

    public async Task<JsonObject> WaitNotificationAsync(Func<JsonObject, bool> predicate, int timeoutMs)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            lock (_notifications)
            {
                var found = _notifications.FirstOrDefault(predicate);
                if (found is not null) return found;
            }
            var remaining = timeoutMs - (int)deadline.ElapsedMilliseconds;
            if (remaining <= 0)
            {
                string seen;
                lock (_notifications)
                {
                    seen = string.Join(", ", _notifications.GroupBy(n => n["method"]?.GetValue<string>()).Select(g => $"{g.Key}×{g.Count()}"));
                }
                var stderrTail = string.Join(" / ", Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3));
                throw new TimeoutException($"notification not received within {timeoutMs} ms; seen [{seen}]; backend stderr: {stderrTail}");
            }
            await _notified.WaitAsync(Math.Min(remaining, 200));
        }
    }

    public Task<JsonObject> WaitDoneAsync(string queryId, int timeoutMs) =>
        WaitNotificationAsync(n => n["method"]?.GetValue<string>() == "query/done"
            && n["params"]?["query_id"]?.GetValue<string>() == queryId, timeoutMs);

    public int RowsReceived(string queryId)
    {
        lock (_notifications)
        {
            return _notifications
                .Where(n => n["method"]?.GetValue<string>() == "query/rows" && n["params"]?["query_id"]?.GetValue<string>() == queryId)
                .Sum(n => n["params"]!["rows"]!.AsArray().Count);
        }
    }

    /// <summary>Stops reading stdout, so the backend's pipe fills and its writes block (a frozen Neovim).</summary>
    public void PauseReading() => _readGate.Reset();

    public void ResumeReading() => _readGate.Set();

    public void CloseStdin() => _process.StandardInput.Close();

    public void Kill() => _process.Kill(entireProcessTree: true);

    public async Task<bool> WaitExitAsync(int timeoutMs)
    {
        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _readGate.Set();
        if (!_process.HasExited)
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
            if (!await WaitExitAsync(8000)) _process.Kill(entireProcessTree: true);
        }
        try
        {
            await _reader.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }
        _process.Dispose();
    }
}

public sealed class BackendErrorException(string message, JsonObject error) : Exception(message)
{
    public JsonObject Error { get; } = error;
}
