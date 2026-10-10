using Dbbliss.Backend.Engines;
using Dbbliss.Backend.Sessions;

namespace Dbbliss.ProtocolTests;

/// <summary>
/// Session admin over the fake server. Ids steer it: "bad" is a SessionActionException, "crash" a
/// driver-style failure. It records which session ran each call, so tests can see the backend used
/// the catalog session.
/// </summary>
public sealed class FakeSessionAdmin(FakeServer server) : ISessionAdmin
{
    public List<string> SessionsUsed { get; } = [];

    public bool CanCancel => server.CanCancelOthers;

    public Task<IReadOnlyList<ServerSession>> ListAsync(IEngineSession session, CancellationToken ct)
    {
        SessionsUsed.Add(session.ServerSessionId);
        var rows = new List<ServerSession>();
        foreach (var own in server.Open.Keys.Order())
        {
            rows.Add(new ServerSession(own, "own-" + own, "me", "db", "127.0.0.1", "dbbliss.nvim", "idle", 10, null, null, false, null));
        }
        foreach (var f in server.Foreign.Values.OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            rows.Add(new ServerSession(f.Id, f.Identity, "alice", "sales", "10.0.0.7", "psql", f.InTransaction ? "idle in transaction" : "active",
                1500, f.InTransaction ? null : "Lock:relation", f.Id == "f2" ? "f1" : null, f.InTransaction, "SELECT 1"));
        }
        return Task.FromResult<IReadOnlyList<ServerSession>>(rows);
    }

    public Task<bool> CancelAsync(IEngineSession session, SessionTarget target, CancellationToken ct)
    {
        SessionsUsed.Add(session.ServerSessionId);
        var f = Find(target);
        if (!f.Signals) return Task.FromResult(false);
        Interlocked.Increment(ref f.Cancelled);
        return Task.FromResult(true);
    }

    public Task<bool> TerminateAsync(IEngineSession session, SessionTarget target, CancellationToken ct)
    {
        SessionsUsed.Add(session.ServerSessionId);
        var f = Find(target);
        if (!f.Signals) return Task.FromResult(false);
        f.Terminated = true;
        // A session with an open transaction takes time to roll back and stays visible meanwhile.
        if (!f.InTransaction) server.Foreign.TryRemove(f.Id, out _);
        return Task.FromResult(true);
    }

    public Task<SessionStatus> StatusAsync(IEngineSession session, SessionTarget target, MessageLog messages, CancellationToken ct)
    {
        SessionsUsed.Add(session.ServerSessionId);
        if (!server.Foreign.TryGetValue(target.Id, out var f) || f.Identity != target.Identity) return Task.FromResult(new SessionStatus(false, null));
        if (f.Terminated) ((FakeSession)session).Messages.Message("info", $"SPID {f.Id}: rollback 40%");
        var said = messages.Drain();
        return Task.FromResult(new SessionStatus(true, said.Count == 0 ? null : string.Join("\n", said)));
    }

    private ForeignSession Find(SessionTarget target)
    {
        if (target.Id == "crash") throw new InvalidOperationException("the driver broke");
        if (target.Id == "bad") throw new SessionActionException("bad id");
        if (!server.Foreign.TryGetValue(target.Id, out var f) || f.Identity != target.Identity)
        {
            throw new SessionActionException($"Session {target.Id} no longer exists, or is not the one you saw.");
        }
        return f;
    }
}
