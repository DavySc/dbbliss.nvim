using System.Text;
using System.Text.Json.Nodes;

namespace Dbbliss.Backend.Rpc;

/// <summary>
/// Serialized writer for stdout. One JSON object per line, '\n' terminated on every platform.
/// Writes block when Neovim stops reading; that is the backpressure for streamed rows.
/// </summary>
public sealed class Output(Stream stdout)
{
    private static readonly byte[] Newline = "\n"u8.ToArray();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Raised once when stdout is gone (Neovim exited); the backend shuts down.</summary>
    public event Action? Broken;

    private int _broken;

    public Task WriteAsync(JsonObject message, CancellationToken ct = default) => WriteAsync(message, null, ct);

    /// <summary>
    /// Writes one message. <paramref name="ordered"/> runs once the message's place in the output is
    /// fixed (this writer's lock is held) and before its bytes go out: anything written by code that
    /// starts after the callback lands after this message, and a client reacting to this message
    /// already sees the callback's effect.
    /// </summary>
    public async Task WriteAsync(JsonObject message, Action? ordered, CancellationToken ct = default)
    {
        message["jsonrpc"] = "2.0";
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _lock.WaitAsync(ct);
        try
        {
            ordered?.Invoke();
            await stdout.WriteAsync(bytes, CancellationToken.None);
            await stdout.WriteAsync(Newline, CancellationToken.None);
            await stdout.FlushAsync(CancellationToken.None);
        }
        catch (IOException)
        {
            if (Interlocked.Exchange(ref _broken, 1) == 0) Broken?.Invoke();
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task NotifyAsync(string method, JsonObject @params, CancellationToken ct = default) =>
        NotifyAsync(method, @params, null, ct);

    public Task NotifyAsync(string method, JsonObject @params, Action? ordered, CancellationToken ct = default) =>
        WriteAsync(new JsonObject { ["method"] = method, ["params"] = @params }, ordered, ct);
}
