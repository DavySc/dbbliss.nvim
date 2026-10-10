using System.Globalization;
using System.Text.RegularExpressions;
using Dbbliss.Backend.Catalog;
using Dbbliss.Backend.Engines;

namespace Dbbliss.Backend.Management;

/// <summary>
/// SQL Server backup with BACKUP DATABASE ... WITH COPY_ONLY, CHECKSUM and RESTORE VERIFYONLY, on a
/// session of its own; the file is on the server. Drops run on a session of their own, with names the
/// server quotes (QUOTENAME).
/// </summary>
public sealed partial class SqlServerManagement : IManagement
{
    public bool RemovesPartialFile => false;

    [GeneratedRegex(@"^(\d{1,3}) percent processed\.")]
    private static partial Regex Percent();

    public async Task<string?> DefaultBackupDirAsync(ManagementContext context, CancellationToken ct)
    {
        await using var session = await context.Engine.OpenAsync(context.Spec, new MessageLogSink(), ct);
        // InstanceDefaultBackupPath exists from SQL Server 2019; before that the property is NULL.
        var table = await CatalogHelpers.First(session, "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(512))", null, ct);
        return table.Rows.Count > 0 ? CatalogHelpers.Str(table.Rows[0][0]) : null;
    }

    public async Task<Func<IOperationReporter, CancellationToken, Task<BackupOutcome>>> PrepareBackupAsync(
        ManagementContext context, BackupRequest request, CancellationToken ct)
    {
        var sink = new ProgressSink();
        var session = await context.Engine.OpenAsync(context.Spec, sink, ct);
        try
        {
            var exists = await CatalogHelpers.First(session, "SELECT DB_ID(@name)", new Dictionary<string, object?> { ["name"] = request.Database }, ct);
            if (exists.Rows.Count == 0 || exists.Rows[0][0] is null) throw new ManagementException($"There is no database named {request.Database}.");
            // xp_fileexist is undocumented but long stable; it is the way to ask the server about a path without writing to it.
            // Its "parent directory exists" column is not reliable (always 0 on Linux), so the folder is asked about itself.
            var folder = FolderOf(request.Path) ?? throw new ManagementException($"{request.Path} is not a full path on the server; give the folder and the file name.");
            var file = await FileFactsAsync(session, request.Path, ct);
            if (file.IsDirectory) throw new ManagementException($"{request.Path} is a folder on the server; give a file name.");
            if (!(await FileFactsAsync(session, folder, ct)).IsDirectory) throw new ManagementException($"The folder {folder} does not exist on the server.");
            if (file.Exists && !request.Overwrite) throw new ManagementException($"{request.Path} exists on the server. Choose another file, or overwrite it deliberately.");
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
        return (reporter, token) => RunAsync(session, sink, request, reporter, token);
    }

    private static string? FolderOf(string path)
    {
        var at = path.LastIndexOfAny(['/', '\\']);
        if (at < 0) return null;
        var folder = at == 0 ? path[..1] : path[..at];
        return folder.EndsWith(':') ? folder + "\\" : folder;
    }

    private static async Task<(bool Exists, bool IsDirectory)> FileFactsAsync(IEngineSession session, string path, CancellationToken ct)
    {
        var table = await CatalogHelpers.First(session, "EXEC master.sys.xp_fileexist @path", new Dictionary<string, object?> { ["path"] = path }, ct);
        return table.Rows.Count > 0 ? (CatalogHelpers.Long(table.Rows[0][0]) != 0, CatalogHelpers.Long(table.Rows[0][1]) != 0) : (false, false);
    }

    private static async Task<BackupOutcome> RunAsync(IEngineSession session, ProgressSink sink, BackupRequest request, IOperationReporter reporter, CancellationToken ct)
    {
        await using var _ = session;
        var path = request.Path.Replace("'", "''", StringComparison.Ordinal);
        sink.Listen(reporter, "backup");
        await ExecuteAsync(session,
            $"BACKUP DATABASE {ObjectNames.QuoteBracket(request.Database)} TO DISK = N'{path}' WITH COPY_ONLY, CHECKSUM, {(request.Overwrite ? "INIT" : "NOINIT")}, STATS = 5", ct);
        sink.Listen(reporter, "verify");
        await ExecuteAsync(session, $"RESTORE VERIFYONLY FROM DISK = N'{path}' WITH CHECKSUM", ct);
        return new BackupOutcome(request.Path, null, "BACKUP ... WITH COPY_ONLY, CHECKSUM; RESTORE VERIFYONLY ... WITH CHECKSUM");
    }

    private static readonly TimeSpan[] RefireDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1)];

    /// <summary>
    /// Runs a statement with the protocol-level cancel (the attention of TDS) as the way to stop it, re-sent
    /// until the statement ends, as for the user's queries (a cancel before the statement is on the wire does nothing).
    /// </summary>
    private static async Task ExecuteAsync(IEngineSession session, string sql, CancellationToken ct)
    {
        using var control = new QueryControl("backup");
        using var _ = ct.Register(() => control.Cancel());
        using var stopWatching = new CancellationTokenSource();
        var watching = Task.Run(async () =>
        {
            try
            {
                using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, stopWatching.Token);
                await Task.Delay(Timeout.Infinite, either.Token);
            }
            catch (OperationCanceledException)
            {
            }
            for (var attempt = 0; !stopWatching.IsCancellationRequested; attempt++)
            {
                try
                {
                    await Task.Delay(RefireDelays[Math.Min(attempt, RefireDelays.Length - 1)], stopWatching.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                control.RefireProtocolCancel();
            }
        });
        try
        {
            await session.ExecuteAsync(sql, new DiscardRows(), control);
        }
        catch (Exception) when (control.CancelRequested)
        {
            throw new OperationCanceledException(ct);
        }
        finally
        {
            control.MarkFinished();
            await stopWatching.CancelAsync();
            await watching;
        }
    }

    private sealed class DiscardRows : IResultSink
    {
        public ValueTask ResultSetAsync(int index, IReadOnlyList<ColumnInfo> columns) => ValueTask.CompletedTask;
        public ValueTask RowAsync(int index, System.Text.Json.Nodes.JsonArray row) => ValueTask.CompletedTask;
        public ValueTask ResultSetDoneAsync(int index, long rows) => ValueTask.CompletedTask;
    }

    /// <summary>The server's messages while a backup runs: "10 percent processed." and the like become progress.</summary>
    private sealed class ProgressSink : IMessageSink
    {
        private IOperationReporter? _reporter;
        private string _phase = "backup";

        public void Listen(IOperationReporter reporter, string phase)
        {
            _phase = phase;
            _reporter = reporter;
        }

        public void Message(string severity, string text, int? number = null, int? line = null)
        {
            var match = Percent().Match(text);
            _reporter?.Progress(_phase, text, match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null);
        }
    }

    public async Task DropAsync(ManagementContext context, DropRequest request, CancellationToken ct)
    {
        await using var session = await context.Engine.OpenAsync(context.Spec, new MessageLogSink(), ct);
        var statement = await DropStatementAsync(session, request, ct);
        await session.QueryAsync(statement, null, ct);
    }

    private static async Task<string> DropStatementAsync(IEngineSession session, DropRequest request, CancellationToken ct)
    {
        if (request.Kind == "database")
        {
            var named = await CatalogHelpers.First(session, "SELECT QUOTENAME(d.name) FROM sys.databases d WHERE d.name = @name", new Dictionary<string, object?> { ["name"] = request.Name }, ct);
            return named.Rows.Count > 0 ? "DROP DATABASE " + CatalogHelpers.Str(named.Rows[0][0]) : throw new ManagementException($"There is no database named {request.Name}.");
        }
        var database = request.Database;
        var schema = request.Schema ?? throw new ManagementException("Dropping an object needs its schema.");
        var db = ObjectNames.QuoteBracket(database);
        var found = await CatalogHelpers.First(session, $"""
            EXEC {db}.sys.sp_executesql
                N'SELECT QUOTENAME(s.name) + N''.'' + QUOTENAME(o.name) AS q, o.type AS t FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = @s AND o.name = @n',
                N'@s sysname, @n sysname', @s = @schema, @n = @name
            """, new Dictionary<string, object?> { ["schema"] = schema, ["name"] = request.Name }, ct);
        if (found.Rows.Count == 0) throw new ManagementException($"There is no {request.Kind} {schema}.{request.Name} in {database}.");
        var qualified = CatalogHelpers.Str(found.Rows[0][0])!;
        var type = CatalogHelpers.Str(found.Rows[0][1])?.Trim();
        var (actual, keyword) = type switch
        {
            "U" => ("table", "TABLE"),
            "V" => ("view", "VIEW"),
            "P" => ("procedure", "PROCEDURE"),
            "FN" or "IF" or "TF" => ("function", "FUNCTION"),
            _ => ("", ""),
        };
        if (keyword.Length == 0) throw new ManagementException($"{schema}.{request.Name} is not a table, view, function or procedure (object type {type}); nothing was dropped.");
        if (actual != request.Kind) throw new ManagementException($"{schema}.{request.Name} is a {actual}, not a {request.Kind}; nothing was dropped.");
        // The server's quoted name sits inside a string literal: its quotes are doubled.
        return $"EXEC {db}.sys.sp_executesql N'DROP {keyword} {qualified.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}
