using System.Collections.Concurrent;
using Dbbliss.Backend.Management;

namespace Dbbliss.ProtocolTests;

/// <summary>
/// Backup and drop without a database or a tool. The path steers a backup: "notool" and "exists" are
/// refused before it starts, "fail" fails, "hang" runs until cancelled; anything else completes. The
/// name steers a drop: "inuse" is a driver-style failure, "refuse" a request the user can fix.
/// </summary>
public sealed class FakeManagement : IManagement
{
    public ConcurrentQueue<string> Drops { get; } = new();
    public ConcurrentQueue<string> Envs { get; } = new();
    public bool RemovesPartialFile { get; set; } = true;
    public int Cancelled;
    public int Prepared;

    /// <summary>Set when a "hang" backup has seen its cancellation and finished.</summary>
    public TaskCompletionSource HangEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A "stubborn" backup ignores cancellation until this is set, like a tool that does not stop.</summary>
    public TaskCompletionSource StubbornRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string?> DefaultBackupDirAsync(ManagementContext context, CancellationToken ct) => Task.FromResult<string?>("/var/backups");

    public Task<Func<IOperationReporter, CancellationToken, Task<BackupOutcome>>> PrepareBackupAsync(ManagementContext context, BackupRequest request, CancellationToken ct)
    {
        Envs.Enqueue(context.Env);
        if (request.Path == "notool") throw new ManagementException("pg_dump was not found in PATH.");
        if (request.Path == "exists" && !request.Overwrite) throw new ManagementException("The file exists.");
        Interlocked.Increment(ref Prepared);
        return Task.FromResult<Func<IOperationReporter, CancellationToken, Task<BackupOutcome>>>(async (reporter, token) =>
        {
            reporter.Progress("dump", "dumping", 30);
            if (request.Path == "fail") throw new OperationFailedException("pg_dump exited with code 1: boom");
            if (request.Path == "crash") throw new InvalidOperationException("driver exploded");
            if (request.Path == "stubborn") await StubbornRelease.Task;
            if (request.Path == "hang")
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref Cancelled);
                    HangEnded.TrySetResult();
                    throw;
                }
            }
            reporter.Progress("verify", "listing", 100);
            return new BackupOutcome(request.Path, 1234, "fake 1.0");
        });
    }

    public async Task DropAsync(ManagementContext context, DropRequest request, CancellationToken ct)
    {
        Envs.Enqueue(context.Env);
        if (request.Name == "hang") await Task.Delay(Timeout.Infinite, ct);
        if (request.Name == "inuse") throw new InvalidOperationException("database is being accessed by other users");
        if (request.Name == "refuse") throw new ManagementException("Not that one.");
        Drops.Enqueue($"{request.Kind} {request.Database}.{request.Schema}.{request.Name}");
    }
}
