using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Dbbliss.CancelTests;

// Phase 0 cancel regression suite.
//
//   dotnet run --project backend/tests/Dbbliss.CancelTests -- [--engine postgres,sqlserver,db2i]
//       [--scenario name,...] [--backend path] [--nvim path] [--report file.md]
//
// Engines are configured through the environment; an engine without configuration is skipped:
//   DBBLISS_TEST_PG,    DBBLISS_TEST_PG_PASSWORD
//   DBBLISS_TEST_MSSQL, DBBLISS_TEST_MSSQL_PASSWORD
//   DBBLISS_TEST_DB2I,  DBBLISS_TEST_DB2I_PASSWORD, DBBLISS_TEST_DB2I_SCHEMA   (real IBM i only)
//
// CI mode, so that a green run cannot hide a gap:
//   DBBLISS_REQUIRE_ENGINES=postgres,sqlserver   an engine in the list that is not configured fails the run
//   DBBLISS_ALLOWED_SKIPS=name,...               with the line above, a scenario that skips and is not
//                                                named here fails the run (skips are always counted)

var settings = Settings.FromArgs(args);
var profiles = new List<EngineProfile>();
void AddIfConfigured(string csEnv, string pwEnv, Func<string, string, EngineProfile> make)
{
    var cs = Environment.GetEnvironmentVariable(csEnv);
    if (!string.IsNullOrEmpty(cs)) profiles.Add(make(cs, pwEnv));
    else Console.WriteLine($"skip: {csEnv} not set");
}
AddIfConfigured("DBBLISS_TEST_PG", "DBBLISS_TEST_PG_PASSWORD", (cs, pw) => new PostgresProfile { ConnectionString = cs, PasswordEnv = pw });
AddIfConfigured("DBBLISS_TEST_MSSQL", "DBBLISS_TEST_MSSQL_PASSWORD", (cs, pw) => new SqlServerProfile { ConnectionString = cs, PasswordEnv = pw });
AddIfConfigured("DBBLISS_TEST_DB2I", "DBBLISS_TEST_DB2I_PASSWORD", (cs, pw) => new Db2iProfile { ConnectionString = cs, PasswordEnv = pw });
if (settings.Engines is { } only) profiles.RemoveAll(p => !only.Contains(p.Engine));

var required = (Environment.GetEnvironmentVariable("DBBLISS_REQUIRE_ENGINES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var allowedSkips = (Environment.GetEnvironmentVariable("DBBLISS_ALLOWED_SKIPS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
foreach (var engine in required.Where(e => profiles.All(p => p.Engine != e)))
{
    Console.Error.WriteLine($"engine {engine} is required (DBBLISS_REQUIRE_ENGINES) but not configured or filtered out");
    return 2;
}

if (!File.Exists(settings.BackendPath))
{
    Console.Error.WriteLine($"backend not found at {settings.BackendPath}; run scripts/build.sh or pass --backend");
    return 2;
}

var results = new List<ScenarioResult>();
foreach (var profile in profiles)
{
    foreach (var (name, run) in new Scenarios(profile, settings).All())
    {
        if (settings.ScenarioFilter is { } f && !f.Contains(name)) continue;
        Console.Write($"{profile.Engine,-10} {name,-34} ");
        ScenarioResult r;
        try
        {
            r = await run();
        }
        catch (Exception ex)
        {
            r = new ScenarioResult(profile.Engine, name, Outcome.Fail, $"{ex.GetType().Name}: {ex.Message}");
        }
        results.Add(r);
        Console.WriteLine($"{r.Outcome.ToString().ToUpperInvariant(),-5} {(r.ServerStopMs is { } ms ? ms + " ms" : "-"),8}  {r.Detail}");
    }
}

var platform = $"{(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "other")} {RuntimeInformation.OSArchitecture}";
var md = new StringBuilder();
md.AppendLine(CultureInfo.InvariantCulture, $"### {platform} — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
md.AppendLine();
md.AppendLine("| Engine | Scenario | Result | Server stopped after | Detail |");
md.AppendLine("|---|---|---|---|---|");
foreach (var r in results)
{
    md.AppendLine(CultureInfo.InvariantCulture,
        $"| {r.Engine} | {r.Scenario} | {r.Outcome.ToString().ToUpperInvariant()} | {(r.ServerStopMs is { } ms ? ms + " ms" : "—")} | {r.Detail.Replace("|", "\\|", StringComparison.Ordinal)} |");
}
if (settings.ReportPath is { } report)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
    await File.AppendAllTextAsync(report, md + Environment.NewLine);
}
Console.WriteLine();
Console.WriteLine(md);

var failed = results.Count(r => r.Outcome == Outcome.Fail);
var skipped = results.Where(r => r.Outcome == Outcome.Skip).ToList();
if (required.Length > 0)
{
    foreach (var r in skipped.Where(r => !allowedSkips.Contains(r.Scenario)))
    {
        Console.Error.WriteLine($"{r.Engine} {r.Scenario} skipped ({r.Detail}) and is not in DBBLISS_ALLOWED_SKIPS");
        failed++;
    }
}
Console.WriteLine($"{results.Count} scenarios, {failed} failed, {skipped.Count} skipped");
return failed == 0 && results.Count > 0 ? 0 : 1;

public sealed class Settings
{
    public required string BackendPath { get; init; }
    public required string PluginRoot { get; init; }
    public string? NvimPath { get; init; }
    public HashSet<string>? Engines { get; init; }
    public HashSet<string>? ScenarioFilter { get; init; }
    public string? ReportPath { get; init; }
    public string? Db2iSchema { get; init; } = Environment.GetEnvironmentVariable("DBBLISS_TEST_DB2I_SCHEMA");

    public string QualifyProbeTable(string name) => Db2iSchema is { Length: > 0 } s ? $"{s}.{name}" : name;

    public static Settings FromArgs(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        HashSet<string>? Set(string? v) => v is null ? null : [.. v.Split(',', StringSplitOptions.RemoveEmptyEntries)];

        var root = FindPluginRoot();
        var rid = (OperatingSystem.IsWindows() ? "win-" : "linux-") + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64");
        var exe = OperatingSystem.IsWindows() ? "dbbliss-backend.exe" : "dbbliss-backend";
        return new Settings
        {
            PluginRoot = root,
            BackendPath = Path.GetFullPath(Arg("--backend") ?? Path.Combine(root, "bin", rid, exe)),
            NvimPath = Arg("--nvim") ?? FindOnPath(OperatingSystem.IsWindows() ? "nvim.exe" : "nvim"),
            Engines = Set(Arg("--engine")),
            ScenarioFilter = Set(Arg("--scenario")),
            ReportPath = Arg("--report"),
        };
    }

    private static string FindPluginRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "plugin", "dbbliss.lua"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find the plugin root (plugin/dbbliss.lua) above " + AppContext.BaseDirectory);
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(d => Path.Combine(d, exe))
        .FirstOrDefault(File.Exists);
}
