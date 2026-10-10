using System.Globalization;
using Dbbliss.Backend.Catalog;
using Dbbliss.Backend.Engines;
using Npgsql;

namespace Dbbliss.Backend.Management;

/// <summary>
/// PostgreSQL backup with pg_dump (custom format) and pg_restore --list, run as child processes on this
/// machine; drops on a session of their own, with names the server resolves and quotes.
/// </summary>
public sealed class PostgresManagement(IProcessRunner runner) : IManagement
{
    public bool RemovesPartialFile => true;

    /// <summary>The file lives on the machine of the plugin, not on the server.</summary>
    public Task<string?> DefaultBackupDirAsync(ManagementContext context, CancellationToken ct) => Task.FromResult<string?>(null);

    public async Task<Func<IOperationReporter, CancellationToken, Task<BackupOutcome>>> PrepareBackupAsync(
        ManagementContext context, BackupRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Path)) throw new ManagementException("A backup needs a path for the file.");
        var path = Path.GetFullPath(request.Path);
        if (Directory.Exists(path)) throw new ManagementException($"{path} is a folder; give a file name.");
        var folder = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(folder)) throw new ManagementException($"The folder {folder} does not exist.");
        if (File.Exists(path) && !request.Overwrite) throw new ManagementException($"{path} exists. Choose another file, or overwrite it deliberately.");

        var options = context.Spec.Options;
        var pgDump = ToolLocator.Find("pg_dump", options?["pg_dump"]?.GetValue<string>(), Environment.GetEnvironmentVariable("PATH"));
        var pgRestore = FindRestore(pgDump, options?["pg_restore"]?.GetValue<string>());
        var environment = ConnectionEnvironment(context.Spec, out var builder);
        var version = await VersionOfAsync(pgDump, ct);

        var arguments = new List<string> { "--format=custom", "--file=" + path };
        if (!string.IsNullOrEmpty(builder.Host)) arguments.Add("--host=" + builder.Host.Split(',')[0]);
        arguments.Add("--port=" + builder.Port.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(builder.Username)) arguments.Add("--username=" + builder.Username);
        arguments.Add("--dbname=" + request.Database);
        // Verbose names each object as it is dumped (the progress), and the tool never asks for a password on a terminal nobody sees.
        arguments.Add("--verbose");
        arguments.Add("--no-password");

        return (reporter, token) => RunAsync(new ProcessSpec(pgDump, arguments, environment), pgRestore, path, version, reporter, token);
    }

    private static string FindRestore(string pgDump, string? configured)
    {
        var sibling = Path.GetDirectoryName(pgDump)!;
        try
        {
            return ToolLocator.Find("pg_restore", sibling, null);
        }
        catch (ManagementException)
        {
            return ToolLocator.Find("pg_restore", configured, Environment.GetEnvironmentVariable("PATH"));
        }
    }

    /// <summary>Npgsql's SSL Mode as libpq spells it; Prefer is libpq's default too.</summary>
    private static readonly Dictionary<SslMode, string> SslModes = new()
    {
        [SslMode.Disable] = "disable",
        [SslMode.Allow] = "allow",
        [SslMode.Require] = "require",
        [SslMode.VerifyCA] = "verify-ca",
        [SslMode.VerifyFull] = "verify-full",
    };

    /// <summary>The password and TLS settings reach the tool through its environment only.</summary>
    private static Dictionary<string, string?> ConnectionEnvironment(ConnectionSpec spec, out NpgsqlConnectionStringBuilder builder)
    {
        builder = new NpgsqlConnectionStringBuilder(spec.ConnectionString);
        var environment = new Dictionary<string, string?>
        {
            ["PGSSLMODE"] = SslModes.GetValueOrDefault(builder.SslMode, "prefer"),
        };
        var password = spec.Password ?? builder.Password;
        if (!string.IsNullOrEmpty(password)) environment["PGPASSWORD"] = password;
        if (!string.IsNullOrEmpty(builder.RootCertificate)) environment["PGSSLROOTCERT"] = builder.RootCertificate;
        return environment;
    }

    private async Task<string> VersionOfAsync(string tool, CancellationToken ct)
    {
        string? line = null;
        var result = await runner.RunAsync(new ProcessSpec(tool, ["--version"]), (l, isError) => line ??= isError ? null : l, ct);
        if (result.ExitCode != 0 || line is null) throw new ManagementException($"{tool} does not run (exit code {result.ExitCode}): {result.StderrTail}");
        return line;
    }

    private async Task<BackupOutcome> RunAsync(ProcessSpec dump, string pgRestore, string path, string version, IOperationReporter reporter, CancellationToken ct)
    {
        reporter.Progress("dump", $"Running {version}", null);
        try
        {
            var result = await runner.RunAsync(dump, (line, _) => reporter.Progress("dump", line), ct);
            if (result.ExitCode != 0) throw new OperationFailedException($"pg_dump exited with code {result.ExitCode}: {result.StderrTail}");
        }
        catch
        {
            // Failed or cancelled: what is on disk is not a backup, and the path was free (or overwriting was asked for).
            DeleteQuietly(path);
            throw;
        }

        reporter.Progress("verify", "Listing the archive with pg_restore --list", null);
        var entries = 0;
        var listed = await runner.RunAsync(new ProcessSpec(pgRestore, ["--list", path]), (line, isError) =>
        {
            if (!isError && line.Length > 0 && line[0] != ';') entries++;
        }, ct);
        if (listed.ExitCode != 0) throw new OperationFailedException($"The archive was not verified: pg_restore --list exited with code {listed.ExitCode}: {listed.StderrTail}. The file {path} was kept.");
        if (entries == 0) throw new OperationFailedException($"The archive was not verified: pg_restore --list shows no entries. The file {path} was kept.");
        reporter.Progress("verify", $"{entries} entries listed", 100);
        return new BackupOutcome(path, new FileInfo(path).Length, $"{version}; pg_restore --list shows {entries} entries");
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"could not remove the partial backup {path}: {ex.Message}");
        }
    }

    public async Task DropAsync(ManagementContext context, DropRequest request, CancellationToken ct)
    {
        await using var session = await context.Engine.OpenAsync(context.Spec, new MessageLogSink(), ct);
        var statement = await DropStatementAsync(session, request, ct);
        await session.QueryAsync(statement, null, ct);
    }

    /// <summary>(relkind, the kind asked for) -> the DROP that fits it. Anything else is a kind mismatch.</summary>
    private static readonly Dictionary<(string RelKind, string Kind), string> RelationDrops = new()
    {
        [("r", "table")] = "DROP TABLE",
        [("p", "table")] = "DROP TABLE",
        [("f", "table")] = "DROP FOREIGN TABLE",
        [("v", "view")] = "DROP VIEW",
        [("m", "view")] = "DROP MATERIALIZED VIEW",
    };

    /// <summary>The DROP statement, built from what the server says the object is called (regclass, regprocedure, quote_ident).</summary>
    private static async Task<string> DropStatementAsync(IEngineSession session, DropRequest request, CancellationToken ct)
    {
        if (request.Kind == "database")
        {
            var named = await CatalogHelpers.First(session, "SELECT quote_ident(d.datname) FROM pg_database d WHERE d.datname = @name", new Dictionary<string, object?> { ["name"] = request.Name }, ct);
            return named.Rows.Count > 0 ? "DROP DATABASE " + CatalogHelpers.Str(named.Rows[0][0]) : throw new ManagementException($"There is no database named {request.Name}.");
        }
        var schema = request.Schema ?? throw new ManagementException("Dropping an object needs its schema.");
        var parameters = new Dictionary<string, object?> { ["schema"] = schema, ["name"] = request.Name };
        if (request.Kind is "table" or "view")
        {
            var found = await CatalogHelpers.First(session, """
                SELECT c.oid::regclass::text, c.relkind::text
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relname = @name AND c.relkind IN ('r', 'p', 'f', 'v', 'm')
                """, parameters, ct);
            if (found.Rows.Count == 0) throw new ManagementException($"There is no {request.Kind} {schema}.{request.Name}.");
            var relkind = CatalogHelpers.Str(found.Rows[0][1])!;
            return RelationDrops.TryGetValue((relkind, request.Kind), out var drop)
                ? drop + " " + CatalogHelpers.Str(found.Rows[0][0])
                : throw new ManagementException($"{schema}.{request.Name} is a {(relkind is "v" or "m" ? "view" : "table")}, not a {request.Kind}.");
        }
        parameters["identity"] = request.Identity;
        var routines = await CatalogHelpers.First(session, """
            SELECT p.oid::regprocedure::text, p.prokind::text
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = @schema AND p.proname = @name AND p.prokind IN ('f', 'p')
              AND (@identity::text IS NULL OR pg_get_function_identity_arguments(p.oid) = @identity::text)
            """, parameters, ct);
        if (routines.Rows.Count == 0) throw new ManagementException($"There is no {request.Kind} {schema}.{request.Name}.");
        if (routines.Rows.Count > 1) throw new ManagementException($"{schema}.{request.Name} is overloaded; drop it from the tree to pick one.");
        var isProcedure = CatalogHelpers.Str(routines.Rows[0][1]) == "p";
        if (isProcedure != (request.Kind == "procedure"))
        {
            throw new ManagementException($"{schema}.{request.Name} is a {(isProcedure ? "procedure" : "function")}, not a {request.Kind}.");
        }
        return (isProcedure ? "DROP PROCEDURE " : "DROP FUNCTION ") + CatalogHelpers.Str(routines.Rows[0][0]);
    }
}

/// <summary>What a management session says is not shown anywhere.</summary>
internal sealed class MessageLogSink : IMessageSink
{
    public void Message(string severity, string text, int? number = null, int? line = null)
    {
    }
}
