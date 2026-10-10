using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Dbbliss.Backend.Plans;
using Dbbliss.Backend.Rpc;
using Dbbliss.Backend.Scripts;

namespace Dbbliss.Backend.Management;

/// <summary>Where an operation happens: the connection, how it is tagged, and its engine's management.</summary>
public sealed record OperationTarget(string ConnectionId, ManagementContext Context, IManagement Management);

/// <summary>
/// Backups and drops. Each runs on sessions or processes of its own, so the user's session stays free,
/// and at most one runs per connection (a drop under a running backup, or a disconnect under either,
/// would leave the operation on a world that is gone: spec/tla/Operations.tla). A backup reports
/// progress and ends with exactly one <c>backup/done</c>; shutdown cancels what runs and waits for the
/// tools to end.
/// </summary>
public sealed class OperationManager(Output output)
{
    /// <summary>A verified backup counts for a drop on prod this long.</summary>
    public TimeSpan DropBackupMaxAge { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long shutdown waits for cancelled operations to end.</summary>
    public TimeSpan ShutdownWait { get; set; } = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, Operation> _operations = new();
    private readonly Dictionary<string, string> _running = [];   // connection id -> operation id; guarded by _gate
    private readonly HashSet<string> _closing = [];              // connections being disconnected; guarded by _gate
    private bool _stopping;                                      // guarded by _gate
    private int _next;

    private sealed class Operation(string id, string kind, string connectionId, string database)
    {
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public string ConnectionId { get; } = connectionId;
        public string Database { get; } = database;
        public CancellationTokenSource Cts { get; } = new();
        public Task? Task { get; set; }
        public volatile string Status = "running";   // running | cancelling | completed | failed | cancelled
        public long FinishedAt;                      // Stopwatch timestamp; 0 while running
    }

    /// <summary>
    /// Called by disconnect once it has decided to go ahead: false while an operation runs. After a true,
    /// no operation starts on the connection.
    /// </summary>
    public bool TryBeginClose(string connectionId)
    {
        lock (_gate)
        {
            if (_running.ContainsKey(connectionId)) return false;
            _closing.Add(connectionId);
            return true;
        }
    }

    private Operation Register(string kind, string connectionId, string database)
    {
        var op = new Operation(kind[0] + Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture), kind, connectionId, database);
        lock (_gate)
        {
            if (_stopping) throw new RpcException(RpcErrors.ShuttingDown, "The backend is shutting down.");
            if (_closing.Contains(connectionId)) throw new RpcException(RpcErrors.UnknownConnection, $"Connection {connectionId} is being closed.");
            if (!_running.TryAdd(connectionId, op.Id))
            {
                throw new RpcException(RpcErrors.ConnectionBusy, $"Connection {connectionId} is running a {_operations[_running[connectionId]].Kind}; wait for it or cancel it.");
            }
            _operations[op.Id] = op;
        }
        return op;
    }

    private void Release(Operation op)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(op.ConnectionId, out var id) && id == op.Id) _running.Remove(op.ConnectionId);
        }
    }

    public async Task<string?> DefaultBackupDirAsync(OperationTarget target, CancellationToken ct) =>
        await target.Management.DefaultBackupDirAsync(target.Context, ct);

    /// <summary>
    /// Checks everything that can be refused (the tool, the file, the database), registers the backup and
    /// returns its id and the action that starts it; the caller starts it once the response is written, so
    /// the client knows the id before the first notification.
    /// </summary>
    public async Task<(string Id, Action Start)> StartBackupAsync(OperationTarget target, BackupRequest request)
    {
        var op = Register("backup", target.ConnectionId, request.Database);
        Func<IOperationReporter, CancellationToken, Task<BackupOutcome>> run;
        try
        {
            run = await target.Management.PrepareBackupAsync(target.Context, request, op.Cts.Token);
        }
        catch
        {
            op.Status = "failed";
            Release(op);
            throw;
        }
        return (op.Id, () => op.Task = RunBackupAsync(op, target, request, run));
    }

    private async Task RunBackupAsync(Operation op, OperationTarget target, BackupRequest request, Func<IOperationReporter, CancellationToken, Task<BackupOutcome>> run)
    {
        await Task.Yield();
        var notifier = new SequentialNotifier(output);
        var reporter = new Reporter(op, notifier);
        string status;
        string? error = null;
        BackupOutcome? outcome = null;
        try
        {
            outcome = await run(reporter, op.Cts.Token);
            status = "completed";
        }
        catch (Exception ex) when (op.Cts.IsCancellationRequested)
        {
            // A cancel we asked for is "cancelled" whatever the tool or driver threw.
            _ = ex;
            status = "cancelled";
        }
        catch (OperationFailedException ex)
        {
            status = "failed";
            error = ex.Message;
        }
        catch (Exception ex)
        {
            status = "failed";
            error = target.Context.Engine.DescribeError(ex).Message;
            Log.Error($"backup {op.Id}: {ex}");
        }
        op.FinishedAt = Stopwatch.GetTimestamp();
        op.Status = status;
        Log.Info($"backup {op.Id} {status}{(error is null ? "" : ": " + error)}");
        var done = new JsonObject
        {
            ["backup_id"] = op.Id,
            ["connection_id"] = op.ConnectionId,
            ["status"] = status,
            ["verified"] = status == "completed",
            ["path"] = outcome?.Path,
            ["bytes"] = outcome?.Bytes,
            ["detail"] = outcome?.Detail,
            ["error"] = error,
            // SQL Server's file is on the server, where the backend cannot remove what an ended backup began.
            ["note"] = status != "completed" && !target.Management.RemovesPartialFile
                ? $"The server may have left a partial file at {request.Path}; it can only be removed on the server."
                : null,
        };
        // The connection is free as the client learns the backup ended: not earlier (it would see a stale
        // "running"), not later (its next request would be refused as busy).
        await notifier.SendAsync("backup/done", done, () => Release(op));
    }

    /// <param name="kind">backup or plan: an id of the other kind is not found.</param>
    /// <returns>cancelling, or finished if it had ended already.</returns>
    public string Cancel(string id, string kind)
    {
        if (!_operations.TryGetValue(id, out var op) || op.Kind != kind)
        {
            throw new RpcException(RpcErrors.InvalidParams, $"Unknown {kind} {id}.");
        }
        lock (_gate)
        {
            if (op.Status != "running" && op.Status != "cancelling") return "finished";
            op.Status = "cancelling";
        }
        op.Cts.Cancel();
        return "cancelling";
    }

    /// <summary>
    /// Registers a plan and returns its id and the action that starts it (after the response is written). An actual
    /// plan runs the statement, so one that is not plainly a read needs the caller's word that the user agreed.
    /// </summary>
    public (string Id, Action Start) StartPlan(string connectionId, ManagementContext context, IPlanner planner, PlanRequest request)
    {
        if (request.Mode == PlanMode.Actual && !request.ConfirmExecute
            && StatementClassifier.Classify(request.Sql, context.Engine.Dialect) == StatementKind.Write)
        {
            throw new ManagementException("An actual plan runs the statement (inside a transaction that is rolled back), and this one may change data or has effects a rollback does not undo. Ask the user, then pass confirm_execute.");
        }
        var op = Register("plan", connectionId, "");
        return (op.Id, () => op.Task = RunPlanAsync(op, context, planner, request));
    }

    private async Task RunPlanAsync(Operation op, ManagementContext context, IPlanner planner, PlanRequest request)
    {
        await Task.Yield();
        var notifier = new SequentialNotifier(output);
        string status;
        string? error = null;
        IReadOnlyList<PlanDocument> plans = [];
        try
        {
            plans = await planner.PlanAsync(context, request, op.Cts.Token);
            if (plans.Count == 0) throw new OperationFailedException("The server returned no plan.");
            status = "completed";
        }
        catch (Exception ex) when (op.Cts.IsCancellationRequested)
        {
            _ = ex;
            status = "cancelled";
            plans = [];
        }
        catch (Exception ex) when (ex is OperationFailedException or ManagementException)
        {
            status = "failed";
            error = ex.Message;
        }
        catch (Exception ex)
        {
            status = "failed";
            error = context.Engine.DescribeError(ex).Message;
            Log.Error($"plan {op.Id}: {ex}");
        }
        op.FinishedAt = Stopwatch.GetTimestamp();
        op.Status = status;
        Log.Info($"plan {op.Id} {status}{(error is null ? "" : ": " + error)}");
        var done = new JsonObject
        {
            ["plan_id"] = op.Id,
            ["connection_id"] = op.ConnectionId,
            ["status"] = status,
            ["plans"] = new JsonArray(plans.Select(p => (JsonNode)p.ToJson()).ToArray()),
            ["error"] = error,
        };
        await notifier.SendAsync("plan/done", done, () => Release(op));
    }

    /// <summary>
    /// Drops after the checks: the typed name equals the object's name; on prod (or a connection whose
    /// environment is not known) a verified backup of the same database, made on this connection, is
    /// recent enough. Server errors are the driver's exceptions.
    /// </summary>
    public async Task DropAsync(OperationTarget target, DropRequest request, string confirmName, string? backupId)
    {
        if (confirmName != request.Name)
        {
            throw new ManagementException($"The name typed does not match \"{request.Name}\"; nothing was dropped.");
        }
        if (target.Context.Env is not ("dev" or "test")) RequireBackup(target.ConnectionId, request.Database, backupId);
        var op = Register("drop", target.ConnectionId, request.Database);
        try
        {
            await target.Management.DropAsync(target.Context, request, op.Cts.Token);
            op.Status = "completed";
        }
        catch
        {
            op.Status = "failed";
            throw;
        }
        finally
        {
            op.FinishedAt = Stopwatch.GetTimestamp();
            Release(op);
        }
    }

    private void RequireBackup(string connectionId, string database, string? backupId)
    {
        const string needs = "This connection is prod (or not tagged): back up the database first and pass the backup's id; nothing was dropped.";
        if (backupId is null) throw new ManagementException(needs);
        if (!_operations.TryGetValue(backupId, out var backup) || backup.Kind != "backup" || backup.ConnectionId != connectionId || backup.Database != database)
        {
            throw new ManagementException($"Backup {backupId} is not a backup of {database} made on this connection. {needs}");
        }
        if (backup.Status != "completed") throw new ManagementException($"Backup {backupId} did not complete ({backup.Status}). {needs}");
        if (Stopwatch.GetElapsedTime(backup.FinishedAt) > DropBackupMaxAge)
        {
            throw new ManagementException($"Backup {backupId} is older than {DropBackupMaxAge.TotalMinutes:0} minutes. Back up again; nothing was dropped.");
        }
    }

    /// <summary>Cancels every running operation and waits, bounded, for it to end. No new one starts.</summary>
    public async Task ShutdownAsync()
    {
        Operation[] running;
        lock (_gate)
        {
            _stopping = true;
            running = [.. _operations.Values.Where(o => o.Status is "running" or "cancelling")];
        }
        foreach (var op in running)
        {
            op.Status = "cancelling";
            op.Cts.Cancel();
        }
        var tasks = running.Select(o => o.Task).OfType<Task>().ToArray();
        var all = Task.WhenAll(tasks);
        if (tasks.Length > 0 && await Task.WhenAny(all, Task.Delay(ShutdownWait)) != all)
        {
            Log.Warn($"{tasks.Count(t => !t.IsCompleted)} operation(s) did not end within {ShutdownWait.TotalSeconds:0} s of shutdown.");
        }
    }

    private sealed class Reporter(Operation op, SequentialNotifier notifier) : IOperationReporter
    {
        public void Progress(string phase, string text, int? percent = null) =>
            notifier.Send("backup/progress", new JsonObject
            {
                ["backup_id"] = op.Id,
                ["connection_id"] = op.ConnectionId,
                ["phase"] = phase,
                ["text"] = text,
                ["percent"] = percent,
            });
    }

    /// <summary>Notifications of one operation, written in the order they were sent.</summary>
    private sealed class SequentialNotifier(Output output)
    {
        private readonly Lock _gate = new();
        private Task _tail = Task.CompletedTask;

        public void Send(string method, JsonObject @params) => _ = SendAsync(method, @params, null);

        public Task SendAsync(string method, JsonObject @params, Action? ordered)
        {
            lock (_gate)
            {
                _tail = Chain(_tail, method, @params, ordered);
                return _tail;
            }
        }

        private async Task Chain(Task before, string method, JsonObject @params, Action? ordered)
        {
            await before;
            try
            {
                await output.NotifyAsync(method, @params, ordered);
            }
            catch (IOException)
            {
                // stdout is gone; shutdown is already under way. The ordered action still must run.
                ordered?.Invoke();
            }
        }
    }
}
