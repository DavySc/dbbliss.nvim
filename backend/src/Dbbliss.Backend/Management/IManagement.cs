using System.Text.Json.Nodes;
using Dbbliss.Backend.Engines;

namespace Dbbliss.Backend.Management;

/// <summary>A refusal the user can fix: a tool that is missing, a file that exists, a name typed wrong, a backup that is missing.</summary>
public sealed class ManagementException(string message) : Exception(message);

/// <summary>An operation that ran and did not succeed; <see cref="Exception.Message"/> says why.</summary>
public sealed class OperationFailedException(string message) : Exception(message);

/// <param name="Env">dev, test or prod; anything the caller did not say is prod.</param>
public sealed record ManagementContext(IEngine Engine, ConnectionSpec Spec, string Env);

/// <param name="Database">The database to back up.</param>
/// <param name="Path">PostgreSQL: a file on this machine. SQL Server: a file on the server.</param>
public sealed record BackupRequest(string Database, string Path, bool Overwrite);

/// <param name="Bytes">Null where the engine does not report the size (SQL Server).</param>
/// <param name="Detail">What was used and checked, for the user: tool versions, entries listed.</param>
public sealed record BackupOutcome(string Path, long? Bytes, string Detail);

/// <param name="Kind">database | table | view | function | procedure</param>
/// <param name="Database">The database the object is in; for a database, its own name.</param>
public sealed record DropRequest(string Kind, string Database, string? Schema, string Name, string? Identity);

public interface IOperationReporter
{
    void Progress(string phase, string text, int? percent = null);
}

/// <summary>Backup and drop for one engine.</summary>
public interface IManagement
{
    /// <summary>
    /// True when a backup that fails or is cancelled removes the file it began (the file is on this machine).
    /// False when it cannot (SQL Server's file is on the server): <c>backup/done</c> then says it may remain.
    /// </summary>
    bool RemovesPartialFile { get; }

    /// <summary>The server's default backup directory, if the backup file lives on the server; null if it lives on this machine.</summary>
    Task<string?> DefaultBackupDirAsync(ManagementContext context, CancellationToken ct);

    /// <summary>
    /// Everything that can be refused before anything runs: the tool is missing, the folder or database
    /// does not exist, the file exists. Throws <see cref="ManagementException"/>. Returns the operation
    /// itself; it ends the operation's resources, whatever happens to it. Cancellation: the token.
    /// </summary>
    Task<Func<IOperationReporter, CancellationToken, Task<BackupOutcome>>> PrepareBackupAsync(ManagementContext context, BackupRequest request, CancellationToken ct);

    /// <summary>
    /// Drops the object on a session of its own. The server decides whether it may (no CASCADE, nobody
    /// is thrown out). Throws <see cref="ManagementException"/> for a request the user can fix; a
    /// server error is the driver's exception.
    /// </summary>
    Task DropAsync(ManagementContext context, DropRequest request, CancellationToken ct);
}
