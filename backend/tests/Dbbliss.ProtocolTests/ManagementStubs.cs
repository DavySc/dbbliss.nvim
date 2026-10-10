using Dbbliss.Backend.Engines;
using Dbbliss.Backend.Management;
using Dbbliss.Backend.Sessions;

namespace Dbbliss.ProtocolTests;

/// <summary>An engine that hands out a prepared session and keeps the message sink it was given.</summary>
public sealed class StubEngine(Func<IMessageSink, IEngineSession> open) : IEngine
{
    public string Name => "stub";
    public ISessionAdmin? Sessions => null;
    public IManagement? Management => null;
    public int Opened;
    public IMessageSink? Sink { get; private set; }

    public Task<IEngineSession> OpenAsync(ConnectionSpec spec, IMessageSink messages, CancellationToken ct)
    {
        Opened++;
        Sink = messages;
        return Task.FromResult(open(messages));
    }

    public ErrorInfo DescribeError(Exception ex) => new(ex.Message);
}

/// <summary>A server session that answers by what the SQL says and records everything sent to it.</summary>
public sealed class ScriptedSession(
    Func<string, IReadOnlyDictionary<string, object?>?, IReadOnlyList<QueryTable>>? answer = null,
    Func<string, QueryControl, Task>? execute = null) : IEngineSession
{
    public List<string> Sent { get; } = [];
    public List<IReadOnlyDictionary<string, object?>?> Parameters { get; } = [];
    public bool Disposed;
    public int ProtocolCancels;

    public string ServerSessionId => "1";

    public Task<IReadOnlyList<QueryTable>> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        Sent.Add(sql);
        Parameters.Add(parameters);
        return Task.FromResult(answer?.Invoke(sql, parameters) ?? []);
    }

    public async Task<ExecuteSummary> ExecuteAsync(string sql, IResultSink sink, QueryControl control)
    {
        Sent.Add(sql);
        Parameters.Add(null);
        control.SetProtocolCancel(() => Interlocked.Increment(ref ProtocolCancels));
        try
        {
            if (execute is not null) await execute(sql, control);
            return new ExecuteSummary(-1);
        }
        finally
        {
            control.ClearProtocolCancel();
        }
    }

    public Task BeginTransactionAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task CommitAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task RollbackAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<TransactionState> GetTransactionStateAsync(CancellationToken ct) => throw new NotSupportedException();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
