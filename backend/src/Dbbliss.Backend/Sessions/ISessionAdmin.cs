using System.Text.Json.Nodes;
using Dbbliss.Backend.Engines;

namespace Dbbliss.Backend.Sessions;

/// <param name="Id">The server's session id as text (PostgreSQL pid, SQL Server SPID).</param>
/// <param name="Identity">Opaque token that tells this session from a later one that reuses the id
/// (PostgreSQL backend_start, SQL Server login_time). Sent back with every action.</param>
/// <param name="DurationMs">Time in the current state: running time for an active statement, idle time otherwise.</param>
/// <param name="BlockedBy">The id of the session this one waits for, if any.</param>
public sealed record ServerSession(
    string Id, string Identity, string? User, string? Database, string? Host, string? Application, string State,
    long? DurationMs, string? Wait, string? BlockedBy, bool InTransaction, string? Query)
{
    public JsonObject ToJson(bool own) => new()
    {
        ["id"] = Id,
        ["identity"] = Identity,
        ["user"] = User,
        ["database"] = Database,
        ["host"] = Host,
        ["application"] = Application,
        ["state"] = State,
        ["duration_ms"] = DurationMs,
        ["wait"] = Wait,
        ["blocked_by"] = BlockedBy,
        ["in_transaction"] = InTransaction,
        ["query"] = Query,
        ["own"] = own,
    };
}

/// <summary>The session an action is meant for: the id and the identity token the caller saw in the list.</summary>
public sealed record SessionTarget(string Id, string Identity);

/// <param name="Present">The session still exists with that identity.</param>
/// <param name="Detail">Engine text about it, such as the rollback progress of a killed SQL Server session.</param>
public sealed record SessionStatus(bool Present, string? Detail);

/// <summary>A session action the user can fix: the session changed or is gone, or the engine has no such action.</summary>
public sealed class SessionActionException(string message) : Exception(message);

/// <summary>
/// Other sessions on the server: list them, cancel a statement, end one. Runs on the connection's
/// catalog session. An action names its target by id and identity token and does nothing when no
/// session matches both.
/// </summary>
public interface ISessionAdmin
{
    /// <summary>False when the engine has no server-side way to stop another session's statement alone.</summary>
    bool CanCancel { get; }

    Task<IReadOnlyList<ServerSession>> ListAsync(IEngineSession session, CancellationToken ct);

    /// <summary>True when the server signalled the session. Throws <see cref="SessionActionException"/> when no session matches.</summary>
    Task<bool> CancelAsync(IEngineSession session, SessionTarget target, CancellationToken ct);

    /// <summary>As <see cref="CancelAsync"/>, ending the session and rolling back what it had open.</summary>
    Task<bool> TerminateAsync(IEngineSession session, SessionTarget target, CancellationToken ct);

    /// <param name="messages">What the server said on this session while the call ran (SQL Server reports rollback progress as a message).</param>
    Task<SessionStatus> StatusAsync(IEngineSession session, SessionTarget target, MessageLog messages, CancellationToken ct);
}

/// <summary>Messages a session received, kept for the operation that wants them. Bounded.</summary>
public sealed class MessageLog : IMessageSink
{
    private const int Max = 100;
    private readonly Lock _gate = new();
    private readonly List<string> _lines = [];

    public void Message(string severity, string text, int? number = null, int? line = null)
    {
        lock (_gate)
        {
            if (_lines.Count < Max) _lines.Add(text);
        }
    }

    /// <summary>Returns what arrived since the last call and forgets it.</summary>
    public IReadOnlyList<string> Drain()
    {
        lock (_gate)
        {
            var copy = _lines.ToArray();
            _lines.Clear();
            return copy;
        }
    }
}
