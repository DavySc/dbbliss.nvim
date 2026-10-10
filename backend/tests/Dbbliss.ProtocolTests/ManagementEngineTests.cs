using System.Diagnostics;
using System.Text.Json.Nodes;
using Dbbliss.Backend.Engines;
using Dbbliss.Backend.Management;

namespace Dbbliss.ProtocolTests;

/// <summary>
/// The tool and SQL side of management without a database or a PostgreSQL install: a scripted runner
/// stands in for pg_dump, a scripted session for the server. Real tools and servers run in the cancel suite.
/// </summary>
public static class ManagementEngineTests
{
    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dbbliss-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly string Exe = OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump";
    private static readonly string RestoreExe = OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore";

    private static void Eq<T>(T actual, T expected, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new TestFailure($"{what}: got {actual}, expected {expected}");
    }

    private static void Same(IEnumerable<string> actual, IEnumerable<string> expected, string what)
    {
        if (!actual.SequenceEqual(expected)) throw new TestFailure($"{what}:\n  got      {string.Join(" | ", actual)}\n  expected {string.Join(" | ", expected)}");
    }

    private static async Task<T> Throws<T>(Func<Task> action, string what, string? contains = null) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T ex)
        {
            if (contains is not null && !ex.Message.Contains(contains, StringComparison.Ordinal)) throw new TestFailure($"{what}: message \"{ex.Message}\" lacks \"{contains}\"");
            return ex;
        }
        throw new TestFailure($"{what}: expected {typeof(T).Name}");
    }

    private sealed class Recorder : IOperationReporter
    {
        public List<(string Phase, string Text, int? Percent)> Events { get; } = [];

        public void Progress(string phase, string text, int? percent = null) => Events.Add((phase, text, percent));
    }

    // ---- tool location ---------------------------------------------------------------------------

    // Verifies: HLR-MGT-4
    public static async Task ToolsAreFoundWhereTheyAre()
    {
        var dir = Temp();
        var elsewhere = Temp();
        var third = Temp();
        File.WriteAllText(Path.Combine(dir, Exe), "");
        File.WriteAllText(Path.Combine(third, Exe), "");
        var expected = Path.Combine(dir, Exe);
        Eq(ToolLocator.Find("pg_dump", null, elsewhere + Path.PathSeparator + Path.PathSeparator + Path.Combine(elsewhere, "nope") + Path.PathSeparator + dir), expected, "on PATH, empty and missing entries skipped");
        Eq(ToolLocator.Find("pg_dump", dir, ""), expected, "configured folder");
        Eq(ToolLocator.Find("pg_dump", expected, null), expected, "configured file");
        Eq(ToolLocator.Find("pg_dump", third, dir), Path.Combine(third, Exe), "configured wins over PATH");
        await Throws<ManagementException>(() => Task.FromResult(ToolLocator.Find("pg_dump", null, elsewhere)), "not on PATH", elsewhere);
        await Throws<ManagementException>(() => Task.FromResult(ToolLocator.Find("pg_dump", null, null)), "no PATH at all", "PATH");
        await Throws<ManagementException>(() => Task.FromResult(ToolLocator.Find("pg_dump", Path.Combine(elsewhere, "nope"), dir)), "configured path that is nothing", "nope");
        await Throws<ManagementException>(() => Task.FromResult(ToolLocator.Find("pg_dump", elsewhere, dir)), "configured folder without the tool does not fall back to PATH", elsewhere);
    }

    // ---- process runner --------------------------------------------------------------------------

    private static ProcessSpec Child(params string[] args)
    {
        var (file, arguments) = ChildProcess.Command(args);
        return new ProcessSpec(file, arguments);
    }

    // Verifies: LLR-MGT-18
    public static async Task RunnerDeliversLinesAndTheExitCode()
    {
        var lines = new List<(string, bool)>();
        var result = await ProcessRunner.Default.RunAsync(Child("lines"), (l, e) => { lock (lines) lines.Add((l, e)); }, CancellationToken.None);
        Eq(result.ExitCode, 3, "exit code");
        Eq(result.StderrTail, "err1\nerr2", "stderr tail");
        Same(lines.Where(l => !l.Item2).Select(l => l.Item1), ["out1", "out2"], "stdout lines");
        Same(lines.Where(l => l.Item2).Select(l => l.Item1), ["err1", "err2"], "stderr lines");

        var silent = await ProcessRunner.Default.RunAsync(Child("lines"), null, CancellationToken.None);
        Eq(silent.ExitCode, 3, "nobody listening");
        var noisy = await ProcessRunner.Default.RunAsync(Child("noisy"), null, CancellationToken.None);
        var tail = noisy.StderrTail.Split('\n');
        if (tail.Length is < 5 or > 40 || tail[^1] != "err999") throw new TestFailure($"the tail is bounded and ends at the end: {tail.Length} lines, last {tail[^1]}");
    }

    // Verifies: LLR-MGT-18, HLR-MGT-4
    public static async Task RunnerPassesArgumentsAndEnvironmentUntouched()
    {
        var seen = new List<string>();
        var spec = Child("args", "a b", "c\"d", "--x=y z", "'q'", "$HOME", "é");
        await ProcessRunner.Default.RunAsync(spec, (l, _) => seen.Add(l), CancellationToken.None);
        Same(seen, ["a b", "c\"d", "--x=y z", "'q'", "$HOME", "é"], "arguments");
        seen.Clear();
        await ProcessRunner.Default.RunAsync(Child("env", "DBBLISS_TEST_VAR") with { Environment = new Dictionary<string, string?> { ["DBBLISS_TEST_VAR"] = "s3 cret" } }, (l, _) => seen.Add(l), CancellationToken.None);
        Same(seen, ["s3 cret"], "environment variable");
        seen.Clear();
        await ProcessRunner.Default.RunAsync(Child("env", "PATH") with { Environment = new Dictionary<string, string?> { ["PATH"] = null } }, (l, _) => seen.Add(l), CancellationToken.None);
        Same(seen, ["(unset)"], "a null value removes the variable");
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // Verifies: LLR-MGT-18, HLR-MGT-6
    public static async Task RunnerCancelEndsTheWholeTree()
    {
        var pids = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var run = ProcessRunner.Default.RunAsync(Child("spawn"), (l, _) => pids.TrySetResult(l), cts.Token);
        var both = (await pids.Task.WaitAsync(TimeSpan.FromSeconds(20))).Split(' ').Select(int.Parse).ToArray();
        await cts.CancelAsync();
        await Throws<OperationCanceledException>(() => run, "cancel");
        for (var waited = 0; waited < 10000 && both.Any(Alive); waited += 50) await Task.Delay(50);
        if (both.Any(Alive)) throw new TestFailure($"processes left after the cancel: {string.Join(",", both.Where(Alive))}");
    }

    // Verifies: LLR-MGT-18
    public static async Task RunnerReportsAToolThatCannotStart()
    {
        await Throws<ManagementException>(() => ProcessRunner.Default.RunAsync(new ProcessSpec(Path.Combine(Temp(), "no-such-tool"), []), null, CancellationToken.None),
            "missing executable", "no-such-tool");
    }
}

public static class PostgresManagementTests
{
    private sealed class Runner(Func<ProcessSpec, Action<string, bool>?, CancellationToken, Task<ProcessResult>> script) : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string, bool>? onLine, CancellationToken ct)
        {
            lock (Calls) Calls.Add(spec);
            return script(spec, onLine, ct);
        }
    }

    private sealed class Recorder : IOperationReporter
    {
        public List<(string Phase, string Text, int? Percent)> Events { get; } = [];

        public void Progress(string phase, string text, int? percent = null) => Events.Add((phase, text, percent));
    }

    private static string Tools(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "dbbliss-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump"), "");
        File.WriteAllText(Path.Combine(dir, OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore"), "");
        return dir;
    }

    private static ManagementContext Context(string toolDir, string connection = "Host=db.example;Port=6543;Username=alice;Database=app;SSL Mode=Require", string? password = "s3cret")
    {
        var options = new JsonObject { ["pg_dump"] = toolDir };
        return new ManagementContext(null!, new ConnectionSpec(connection, password, options), "dev");
    }

    private static string Name(ProcessSpec s) => Path.GetFileNameWithoutExtension(s.FileName);

    private static IEnumerable<string> LinesOf(params string[] lines) => lines;

    /// <summary>pg_dump that writes a 10-byte file, then pg_restore that lists two entries.</summary>
    private static Runner Working(Func<ProcessSpec, int>? exit = null) => new((spec, onLine, ct) =>
    {
        if (spec.Arguments is ["--version"])
        {
            onLine?.Invoke("pg_dump: a warning on the error stream", true);
            onLine?.Invoke("pg_dump (PostgreSQL) 17.11", false);
            return Task.FromResult(new ProcessResult(0, ""));
        }
        if (Name(spec) == "pg_dump")
        {
            File.WriteAllText(spec.Arguments.Single(a => a.StartsWith("--file=", StringComparison.Ordinal))[7..], "0123456789");
            onLine?.Invoke("pg_dump: dumping contents of table \"public.t\"", true);
            return Task.FromResult(new ProcessResult(exit?.Invoke(spec) ?? 0, ""));
        }
        onLine?.Invoke("", false);
        onLine?.Invoke("pg_restore: a note on the error stream", true);
        onLine?.Invoke("; Archive created at 2026-10-10", false);
        onLine?.Invoke("1; 2615 2200 SCHEMA - public alice", false);
        onLine?.Invoke("2; 1259 16385 TABLE public t alice", false);
        return Task.FromResult(new ProcessResult(0, ""));
    });

    // Verifies: HLR-MGT-1, HLR-MGT-4
    public static async Task BackupRunsPgDumpThenListsTheArchive()
    {
        var dir = Tools(out var tools);
        var path = Path.Combine(dir, "app.dump");
        var runner = Working();
        var management = new PostgresManagement(runner);
        var run = await management.PrepareBackupAsync(Context(tools), new BackupRequest("app", path, false), CancellationToken.None);
        var rec = new Recorder();
        var outcome = await run(rec, CancellationToken.None);

        var dump = runner.Calls.Single(c => c.Arguments.Contains("--verbose"));
        string[] expected = ["--format=custom", "--file=" + Path.GetFullPath(path), "--host=db.example", "--port=6543", "--username=alice", "--dbname=app", "--verbose", "--no-password"];
        if (!dump.Arguments.SequenceEqual(expected)) throw new TestFailure("pg_dump arguments:\n  " + string.Join(" ", dump.Arguments) + "\n  " + string.Join(" ", expected));
        if (dump.Environment?["PGPASSWORD"] != "s3cret" || dump.Environment["PGSSLMODE"] != "require") throw new TestFailure("environment: " + string.Join(",", dump.Environment ?? new Dictionary<string, string?>()));
        if (runner.Calls.Any(c => c.Arguments.Any(a => a.Contains("s3cret", StringComparison.Ordinal)))) throw new TestFailure("the password is on a command line");
        var restore = runner.Calls.Single(c => Name(c) == "pg_restore");
        if (!restore.Arguments.SequenceEqual(["--list", Path.GetFullPath(path)])) throw new TestFailure("pg_restore arguments: " + string.Join(" ", restore.Arguments));
        if (outcome.Path != Path.GetFullPath(path) || outcome.Bytes != 10 || !outcome.Detail.Contains("17.11", StringComparison.Ordinal) || !outcome.Detail.Contains("2 entries", StringComparison.Ordinal))
        {
            throw new TestFailure("outcome: " + outcome);
        }
        if (!rec.Events.Any(e => e.Phase == "dump" && e.Text.Contains("dumping contents", StringComparison.Ordinal)) || !rec.Events.Any(e => e.Phase == "verify")) throw new TestFailure("progress: " + string.Join(" | ", rec.Events));

        // Another database than the connection's, and no password in the spec at all.
        var other = Working();
        var run2 = await new PostgresManagement(other).PrepareBackupAsync(Context(tools, "Host=h;Database=app", password: null), new BackupRequest("other", Path.Combine(dir, "o.dump"), false), CancellationToken.None);
        await run2(new Recorder(), CancellationToken.None);
        var dump2 = other.Calls.Single(c => c.Arguments.Contains("--verbose"));
        if (!dump2.Arguments.Contains("--dbname=other") || !dump2.Arguments.Contains("--port=5432") || dump2.Arguments.Any(a => a.StartsWith("--username", StringComparison.Ordinal))) throw new TestFailure("defaults: " + string.Join(" ", dump2.Arguments));
        if (dump2.Environment?.ContainsKey("PGPASSWORD") == true) throw new TestFailure("a password was invented");
    }

    // Verifies: HLR-MGT-4
    public static async Task TheToolGetsItsSettingsThroughItsEnvironment()
    {
        var dir = Tools(out var tools);
        foreach (var (mode, expected) in new[] { ("Disable", "disable"), ("Allow", "allow"), ("Prefer", "prefer"), ("Require", "require"), ("VerifyCA", "verify-ca"), ("VerifyFull", "verify-full") })
        {
            var runner = Working();
            var run = await new PostgresManagement(runner).PrepareBackupAsync(Context(tools, $"Host=h;SSL Mode={mode};Root Certificate=/ca.crt"), new BackupRequest("app", Path.Combine(dir, mode + ".dump"), false), CancellationToken.None);
            await run(new Recorder(), CancellationToken.None);
            var env = runner.Calls.Single(c => c.Arguments.Contains("--verbose")).Environment!;
            if (env["PGSSLMODE"] != expected || env["PGSSLROOTCERT"] != "/ca.crt") throw new TestFailure($"{mode}: {string.Join(",", env)}");
        }
        // No options at all, or options without a tool entry: the tools are on PATH; a password in the connection string itself still reaches the tool by environment.
        var saved = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", tools);
        try
        {
            foreach (var options in new JsonObject?[] { null, new JsonObject() })
            {
                var runner = Working();
                var context = new ManagementContext(null!, new ConnectionSpec("Host=h;Password=inline", null, options), "dev");
                var run = await new PostgresManagement(runner).PrepareBackupAsync(context, new BackupRequest("app", Path.Combine(dir, $"path{(options is null ? 0 : 1)}.dump"), false), CancellationToken.None);
                await run(new Recorder(), CancellationToken.None);
                var dump = runner.Calls.Single(c => c.Arguments.Contains("--verbose"));
                if (dump.Environment!["PGPASSWORD"] != "inline" || dump.Environment.ContainsKey("PGSSLROOTCERT") || dump.Arguments.Any(a => a.Contains("inline", StringComparison.Ordinal))) throw new TestFailure("environment: " + string.Join(",", dump.Environment));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", saved);
        }
    }

    // Verifies: HLR-MGT-3, HLR-MGT-4
    public static async Task BackupIsRefusedBeforeAnythingRuns()
    {
        var dir = Tools(out var tools);
        var empty = Path.Combine(dir, "empty");
        Directory.CreateDirectory(empty);
        var management = new PostgresManagement(Working());
        var existing = Path.Combine(dir, "there.dump");
        File.WriteAllText(existing, "precious");

        async Task Refused(ManagementContext ctx, string path, bool overwrite, string contains) =>
            await Throws(() => management.PrepareBackupAsync(ctx, new BackupRequest("app", path, overwrite), CancellationToken.None), contains);

        await Refused(Context(empty), Path.Combine(dir, "x.dump"), false, "pg_dump");
        await Refused(Context(tools), Path.Combine(dir, "no-such-folder", "x.dump"), false, "no-such-folder");
        await Refused(Context(tools), existing, false, "exists");
        await Refused(Context(tools), dir, true, "folder");
        await Refused(Context(tools), "", false, "path");
        if (File.ReadAllText(existing) != "precious") throw new TestFailure("a refusal touched the file");
        await management.PrepareBackupAsync(Context(tools), new BackupRequest("app", existing, true), CancellationToken.None);
        if (File.ReadAllText(existing) != "precious") throw new TestFailure("preparing touched the file");

        var broken = new PostgresManagement(new Runner((_, _, _) => Task.FromResult(new ProcessResult(127, "cannot execute"))));
        await Throws(() => broken.PrepareBackupAsync(Context(tools), new BackupRequest("app", Path.Combine(dir, "y.dump"), false), CancellationToken.None), "cannot execute");
        // pg_restore is looked for next to pg_dump, then where options.pg_restore says, then on PATH.
        var lonely = Path.Combine(dir, "lonely");
        Directory.CreateDirectory(lonely);
        File.WriteAllText(Path.Combine(lonely, OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump"), "");
        var saved = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", empty);
        try
        {
            await Refused(Context(lonely), Path.Combine(dir, "z.dump"), false, "pg_restore");
            var withRestore = new ManagementContext(null!, new ConnectionSpec("Host=h", null, new JsonObject { ["pg_dump"] = lonely, ["pg_restore"] = tools }), "dev");
            await management.PrepareBackupAsync(withRestore, new BackupRequest("app", Path.Combine(dir, "z.dump"), false), CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", saved);
        }
    }

    private static async Task Throws(Func<Task> action, string contains)
    {
        try
        {
            await action();
        }
        catch (ManagementException ex) when (ex.Message.Contains(contains, StringComparison.Ordinal))
        {
            return;
        }
        catch (ManagementException ex)
        {
            throw new TestFailure($"refused, but the message lacks \"{contains}\": {ex.Message}");
        }
        throw new TestFailure($"expected a refusal mentioning \"{contains}\"");
    }

    // Verifies: HLR-MGT-5, HLR-MGT-6
    public static async Task FailuresAndCancelsLeaveNoPartialFile()
    {
        var dir = Tools(out var tools);

        // pg_dump fails: the reason carries the exit code and the tool's words; the partial file is gone.
        var failing = new Runner((spec, onLine, ct) =>
        {
            if (spec.Arguments is ["--version"]) { onLine?.Invoke("pg_dump (PostgreSQL) 17.0", false); return Task.FromResult(new ProcessResult(0, "")); }
            File.WriteAllText(spec.Arguments.Single(a => a.StartsWith("--file=", StringComparison.Ordinal))[7..], "partial");
            return Task.FromResult(new ProcessResult(1, "pg_dump: error: connection refused"));
        });
        var path = Path.Combine(dir, "f.dump");
        var run = await new PostgresManagement(failing).PrepareBackupAsync(Context(tools), new BackupRequest("app", path, false), CancellationToken.None);
        try
        {
            await run(new Recorder(), CancellationToken.None);
            throw new TestFailure("a failing pg_dump was reported as a success");
        }
        catch (OperationFailedException ex) when (ex.Message.Contains("code 1", StringComparison.Ordinal) && ex.Message.Contains("connection refused", StringComparison.Ordinal))
        {
        }
        if (File.Exists(path)) throw new TestFailure("the partial file was kept after a failure");

        // Cancelled while pg_dump runs.
        var running = new TaskCompletionSource();
        var hanging = new Runner(async (spec, onLine, ct) =>
        {
            if (spec.Arguments is ["--version"]) { onLine?.Invoke("pg_dump (PostgreSQL) 17.0", false); return new ProcessResult(0, ""); }
            File.WriteAllText(spec.Arguments.Single(a => a.StartsWith("--file=", StringComparison.Ordinal))[7..], "partial");
            running.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new ProcessResult(0, "");
        });
        var path2 = Path.Combine(dir, "c.dump");
        var run2 = await new PostgresManagement(hanging).PrepareBackupAsync(Context(tools), new BackupRequest("app", path2, false), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var pending = run2(new Recorder(), cts.Token);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        try
        {
            await pending;
            throw new TestFailure("a cancelled backup completed");
        }
        catch (OperationCanceledException)
        {
        }
        if (File.Exists(path2)) throw new TestFailure("the partial file was kept after a cancel");

        // The archive does not verify: not reported as a backup, and the file stays for the user to look at.
        foreach (var (restoreExit, restoreLines, expect) in new[] { (1, Array.Empty<string>(), "code 1"), (0, new[] { "; only a comment" }, "no entries") })
        {
            var unverified = new Runner((spec, onLine, ct) =>
            {
                if (spec.Arguments is ["--version"]) { onLine?.Invoke("pg_dump (PostgreSQL) 17.0", false); return Task.FromResult(new ProcessResult(0, "")); }
                if (Name(spec) == "pg_dump")
                {
                    File.WriteAllText(spec.Arguments.Single(a => a.StartsWith("--file=", StringComparison.Ordinal))[7..], "dump");
                    return Task.FromResult(new ProcessResult(0, ""));
                }
                foreach (var l in restoreLines) onLine?.Invoke(l, false);
                return Task.FromResult(new ProcessResult(restoreExit, "pg_restore: error: bad archive"));
            });
            var path3 = Path.Combine(dir, "u" + restoreExit + ".dump");
            var run3 = await new PostgresManagement(unverified).PrepareBackupAsync(Context(tools), new BackupRequest("app", path3, false), CancellationToken.None);
            try
            {
                await run3(new Recorder(), CancellationToken.None);
                throw new TestFailure("an archive that does not verify was reported as a backup");
            }
            catch (OperationFailedException ex) when (ex.Message.Contains("not verified", StringComparison.Ordinal) && ex.Message.Contains(expect, StringComparison.Ordinal))
            {
            }
            if (!File.Exists(path3)) throw new TestFailure("the unverified file was removed");
        }
    }

    // ---- drop ------------------------------------------------------------------------------------

    private static ManagementContext Server(ScriptedSession session) => new(new StubEngine(_ => session), new ConnectionSpec("Host=h;Database=app", null, null), "dev");

    private static QueryTable Rows(params object?[][] rows) => new(["a", "b"], rows);

    // Verifies: HLR-MGT-8, HLR-MGT-10, LLR-MGT-12
    public static async Task PostgresDropsWhatTheServerNamedAndNothingMore()
    {
        var management = new PostgresManagement(Working());
        async Task<ScriptedSession> Run(DropRequest request, params object?[][] resolved)
        {
            var session = new ScriptedSession((_, _) => [Rows(resolved)]);
            await management.DropAsync(Server(session), request, CancellationToken.None);
            if (!session.Disposed) throw new TestFailure("the session was kept");
            return session;
        }

        var table = await Run(new DropRequest("table", "app", "public", "Order Line", null), ["\"public\".\"Order Line\"", "r"]);
        Eq(table.Sent[1], "DROP TABLE \"public\".\"Order Line\"", "table");
        Eq((string)table.Parameters[0]!["schema"]!, "public", "schema is a parameter");
        Eq((string)table.Parameters[0]!["name"]!, "Order Line", "name is a parameter");
        Eq((await Run(new DropRequest("table", "app", "public", "p", null), ["public.p", "p"])).Sent[1], "DROP TABLE public.p", "partitioned table");
        Eq((await Run(new DropRequest("table", "app", "public", "f", null), ["public.f", "f"])).Sent[1], "DROP FOREIGN TABLE public.f", "foreign table");
        Eq((await Run(new DropRequest("view", "app", "public", "v", null), ["public.v", "v"])).Sent[1], "DROP VIEW public.v", "view");
        Eq((await Run(new DropRequest("view", "app", "public", "m", null), ["public.m", "m"])).Sent[1], "DROP MATERIALIZED VIEW public.m", "materialized view");
        Eq((await Run(new DropRequest("function", "app", "public", "add_one", "a integer"), ["public.add_one(integer)", "f"])).Sent[1], "DROP FUNCTION public.add_one(integer)", "function");
        Eq((await Run(new DropRequest("procedure", "app", "public", "noop", null), ["public.noop()", "p"])).Sent[1], "DROP PROCEDURE public.noop()", "procedure");
        Eq((await Run(new DropRequest("database", "x\"; DROP DATABASE y; --", null, "x\"; DROP DATABASE y; --", null), ["\"x\"\"; DROP DATABASE y; --\"", null])).Sent[1], "DROP DATABASE \"x\"\"; DROP DATABASE y; --\"", "database, quoted by the server");

        // A request the user can fix: nothing is sent past the lookup.
        async Task Refused(DropRequest request, string contains, params object?[][] resolved)
        {
            var session = new ScriptedSession((_, _) => [Rows(resolved)]);
            await Throws(() => management.DropAsync(Server(session), request, CancellationToken.None), contains);
            if (session.Sent.Count > 1 || !session.Disposed) throw new TestFailure($"after a refusal: {session.Sent.Count} statement(s), disposed {session.Disposed}");
        }
        await Refused(new DropRequest("table", "app", "public", "gone", null), "no table");
        await Refused(new DropRequest("table", "app", "public", "v", null), "view", ["public.v", "v"]);
        await Refused(new DropRequest("view", "app", "public", "t", null), "table", ["public.t", "r"]);
        await Refused(new DropRequest("function", "app", "public", "f", null), "overloaded", ["public.f(integer)", "f"], ["public.f(text)", "f"]);
        await Refused(new DropRequest("view", "app", "public", "f", null), "table", ["public.f", "f"]);
        await Refused(new DropRequest("table", "app", "public", "m", null), "view", ["public.m", "m"]);
        await Refused(new DropRequest("function", "app", "public", "gone", null), "no function");
        await Refused(new DropRequest("function", "app", "public", "p", null), "procedure", ["public.p()", "p"]);
        await Refused(new DropRequest("procedure", "app", "public", "f", null), "function", ["public.f()", "f"]);
        await Refused(new DropRequest("database", "gone", null, "gone", null), "no database");
        await Refused(new DropRequest("table", "app", null, "t", null), "schema");

        // A server error is the driver's, and the session is still closed.
        var failing = new ScriptedSession((sql, _) => sql.StartsWith("DROP", StringComparison.Ordinal) ? throw new InvalidOperationException("other sessions are using the database") : [Rows(["public.t", "r"])]);
        try
        {
            await management.DropAsync(Server(failing), new DropRequest("table", "app", "public", "t", null), CancellationToken.None);
            throw new TestFailure("a refused drop passed");
        }
        catch (InvalidOperationException)
        {
        }
        if (!failing.Disposed) throw new TestFailure("the session was kept after a server error");
        if (new[] { table }.Concat([failing]).SelectMany(s => s.Sent).Any(s => s.Contains("CASCADE", StringComparison.OrdinalIgnoreCase))) throw new TestFailure("CASCADE");
    }

    private static void Eq<T>(T actual, T expected, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new TestFailure($"{what}: got {actual}, expected {expected}");
    }

    // Verifies: LLR-MGT-13
    public static async Task PostgresBackupFileIsOnThisMachine()
    {
        if (await new PostgresManagement(Working()).DefaultBackupDirAsync(Context("x"), CancellationToken.None) is not null) throw new TestFailure("the file of a PostgreSQL backup is on this machine");
    }
}

public static class SqlServerManagementTests
{
    private sealed class Recorder : IOperationReporter
    {
        public List<(string Phase, string Text, int? Percent)> Events { get; } = [];

        public void Progress(string phase, string text, int? percent = null) => Events.Add((phase, text, percent));
    }

    private static QueryTable T(params object?[][] rows) => new(["a", "b", "c"], rows);

    /// <summary>A server where the database exists and the file's parent folder exists, and the file does not.</summary>
    private static ScriptedSession Healthy(Func<string, QueryControl, Task>? execute = null, int fileExists = 0, int isDirectory = 0, int parentExists = 1, bool database = true) =>
        new((sql, p) =>
        {
            if (sql.Contains("DB_ID", StringComparison.Ordinal)) return [T([database ? 5 : null, null, null])];
            if (sql.Contains("xp_fileexist", StringComparison.Ordinal))
            {
                // The folder is asked about itself; the quirk of the real column (parent always 0 on Linux) is in the third value.
                var asked = (string)p!["path"]!;
                return asked.EndsWith(".bak", StringComparison.Ordinal) ? [T([fileExists, isDirectory, 0])] : [T([0, parentExists, 0])];
            }
            return [];
        }, execute);

    private static ManagementContext Context(StubEngine engine) => new(engine, new ConnectionSpec("Server=s", null, null), "dev");

    private static async Task Refused(Func<Task> action, string contains)
    {
        try
        {
            await action();
        }
        catch (ManagementException ex) when (ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        catch (ManagementException ex)
        {
            throw new TestFailure($"refused, but the message lacks \"{contains}\": {ex.Message}");
        }
        throw new TestFailure($"expected a refusal mentioning \"{contains}\"");
    }

    // Verifies: HLR-MGT-2, LLR-MGT-12
    public static async Task BackupIsCopyOnlyChecksummedAndVerified()
    {
        var session = Healthy();
        var engine = new StubEngine(_ => session);
        var run = await new SqlServerManagement().PrepareBackupAsync(Context(engine), new BackupRequest("app", @"C:\backups\app.bak", false), CancellationToken.None);
        if (session.Disposed) throw new TestFailure("the session closed before the backup ran");
        var rec = new Recorder();
        engine.Sink!.Message("info", "welcome");   // before the run: nobody listens
        var running = run(rec, CancellationToken.None);
        await running;
        string[] sent = [.. session.Sent.Where(s => s.StartsWith("BACKUP", StringComparison.Ordinal) || s.StartsWith("RESTORE", StringComparison.Ordinal))];
        if (!sent.SequenceEqual(["BACKUP DATABASE [app] TO DISK = N'C:\\backups\\app.bak' WITH COPY_ONLY, CHECKSUM, NOINIT, STATS = 5", "RESTORE VERIFYONLY FROM DISK = N'C:\\backups\\app.bak' WITH CHECKSUM"]))
        {
            throw new TestFailure("statements: " + string.Join(" | ", sent));
        }
        if (!session.Disposed) throw new TestFailure("the session was kept after the backup");
        var outcome = await running;
        if (outcome.Path != @"C:\backups\app.bak" || outcome.Bytes is not null) throw new TestFailure("outcome: " + outcome);

        // Quoting: a bracket in the database name, a quote in the path, INIT when overwriting.
        var tricky = Healthy(fileExists: 1);
        var run2 = await new SqlServerManagement().PrepareBackupAsync(Context(new StubEngine(_ => tricky)), new BackupRequest("we]ird", "/var/it's.bak", true), CancellationToken.None);
        await run2(new Recorder(), CancellationToken.None);
        var backup = tricky.Sent.Single(s => s.StartsWith("BACKUP", StringComparison.Ordinal));
        if (backup != "BACKUP DATABASE [we]]ird] TO DISK = N'/var/it''s.bak' WITH COPY_ONLY, CHECKSUM, INIT, STATS = 5") throw new TestFailure("quoting: " + backup);
        if (tricky.Parameters.Any(p => p is not null && p.Values.Any(v => v is string s && s.Contains(";", StringComparison.Ordinal)))) throw new TestFailure("unexpected");
    }

    // Verifies: HLR-MGT-5
    public static async Task ProgressFollowsTheServersMessages()
    {
        IMessageSink? sink = null;
        var session = Healthy((sql, _) =>
        {
            if (sql.StartsWith("BACKUP", StringComparison.Ordinal))
            {
                sink!.Message("info", "10 percent processed.");
                sink.Message("info", "Processed 1000 pages for database 'app', file 'app' on file 1.");
                sink.Message("info", "100 percent processed.");
            }
            else
            {
                sink!.Message("info", "The backup set on file 1 is valid.");
            }
            return Task.CompletedTask;
        });
        var engine = new StubEngine(m => { sink = m; return session; });
        var run = await new SqlServerManagement().PrepareBackupAsync(Context(engine), new BackupRequest("app", "/b/app.bak", false), CancellationToken.None);
        var rec = new Recorder();
        await run(rec, CancellationToken.None);
        var backup = rec.Events.Where(e => e.Phase == "backup").ToList();
        if (backup.Count != 3 || backup[0].Percent != 10 || backup[1].Percent is not null || backup[2].Percent != 100) throw new TestFailure("backup progress: " + string.Join(" | ", rec.Events));
        if (!rec.Events.Any(e => e.Phase == "verify" && e.Text.Contains("valid", StringComparison.Ordinal))) throw new TestFailure("verify progress: " + string.Join(" | ", rec.Events));
    }

    // Verifies: HLR-MGT-3
    public static async Task BackupIsRefusedBeforeAnythingRuns()
    {
        var management = new SqlServerManagement();
        async Task Prepare(ScriptedSession session, bool overwrite, string contains)
        {
            await Refused(() => management.PrepareBackupAsync(Context(new StubEngine(_ => session)), new BackupRequest("app", "/b/app.bak", overwrite), CancellationToken.None), contains);
            if (!session.Disposed) throw new TestFailure("a refused backup kept its session");
            if (session.Sent.Any(s => s.StartsWith("BACKUP", StringComparison.Ordinal))) throw new TestFailure("a refused backup ran");
        }
        await Prepare(Healthy(database: false), false, "no database");
        await Prepare(Healthy(fileExists: 1), false, "exists");
        await Prepare(Healthy(isDirectory: 1), true, "folder");
        await Prepare(Healthy(parentExists: 0), true, "does not exist");
        await Refused(() => management.PrepareBackupAsync(Context(new StubEngine(_ => Healthy())), new BackupRequest("app", "app.bak", false), CancellationToken.None), "full path");
        // The server answers nothing at all about the database, or about the paths.
        await Refused(() => management.PrepareBackupAsync(Context(new StubEngine(_ => new ScriptedSession((sql, _) => sql.Contains("DB_ID", StringComparison.Ordinal) ? [] : []))), new BackupRequest("app", "/b/app.bak", false), CancellationToken.None), "no database");
        await Refused(() => management.PrepareBackupAsync(Context(new StubEngine(_ => new ScriptedSession((sql, _) => sql.Contains("DB_ID", StringComparison.Ordinal) ? [T([5, null, null])] : []))), new BackupRequest("app", "/b/app.bak", false), CancellationToken.None), "does not exist");
        // The folder of a file at the root, and of a file at the top of a drive.
        foreach (var (file, folder) in new[] { ("/root.bak", "/"), (@"C:\x.bak", @"C:\"), (@"\\server\share\x.bak", @"\\server\share") })
        {
            var s = Healthy();
            await management.PrepareBackupAsync(Context(new StubEngine(_ => s)), new BackupRequest("app", file, false), CancellationToken.None);
            if (!s.Parameters.Any(p => p is not null && p.TryGetValue("path", out var v) && (string?)v == folder)) throw new TestFailure($"the folder of {file} should be asked about as {folder}");
        }
        // A file that exists may be overwritten when the caller says so.
        var ok = Healthy(fileExists: 1);
        await management.PrepareBackupAsync(Context(new StubEngine(_ => ok)), new BackupRequest("app", "/b/app.bak", true), CancellationToken.None);
        // A server failure while checking is the driver's, and the session is closed.
        var broken = new ScriptedSession((_, _) => throw new InvalidOperationException("access denied"));
        try
        {
            await management.PrepareBackupAsync(Context(new StubEngine(_ => broken)), new BackupRequest("app", "/b/app.bak", false), CancellationToken.None);
            throw new TestFailure("a server error vanished");
        }
        catch (InvalidOperationException)
        {
        }
        if (!broken.Disposed) throw new TestFailure("the session was kept after a server error");
    }

    // Verifies: HLR-MGT-6
    public static async Task CancelUsesTheProtocolCancel()
    {
        var started = new TaskCompletionSource();
        var session = Healthy(async (sql, control) =>
        {
            if (!sql.StartsWith("BACKUP", StringComparison.Ordinal)) return;
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, control.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException("Cancelling statement due to attention");
            }
        });
        var run = await new SqlServerManagement().PrepareBackupAsync(Context(new StubEngine(_ => session)), new BackupRequest("app", "/b/app.bak", false), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var pending = run(new Recorder(), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        try
        {
            await pending;
            throw new TestFailure("a cancelled backup completed");
        }
        catch (OperationCanceledException)
        {
        }
        if (session.ProtocolCancels < 1 || !session.Disposed) throw new TestFailure($"protocol cancels {session.ProtocolCancels}, disposed {session.Disposed}");
        if (session.Sent.Any(s => s.StartsWith("RESTORE", StringComparison.Ordinal))) throw new TestFailure("a cancelled backup was verified");

        // A failure that is not a cancel is the driver's.
        var failing = Healthy((sql, _) => sql.StartsWith("BACKUP", StringComparison.Ordinal) ? throw new InvalidOperationException("Operating system error 5(Access is denied.)") : Task.CompletedTask);
        var run2 = await new SqlServerManagement().PrepareBackupAsync(Context(new StubEngine(_ => failing)), new BackupRequest("app", "/b/app.bak", false), CancellationToken.None);
        try
        {
            await run2(new Recorder(), CancellationToken.None);
            throw new TestFailure("a failed backup passed");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Access is denied", StringComparison.Ordinal))
        {
        }
        if (!failing.Disposed || failing.Sent.Any(s => s.StartsWith("RESTORE", StringComparison.Ordinal))) throw new TestFailure("after a failure");
    }

    // Verifies: LLR-MGT-13
    public static async Task DefaultsToTheServersBackupFolder()
    {
        var session = new ScriptedSession((sql, _) => [new QueryTable(["dir"], [["/var/opt/mssql/data"]])]);
        var dir = await new SqlServerManagement().DefaultBackupDirAsync(Context(new StubEngine(_ => session)), CancellationToken.None);
        if (dir != "/var/opt/mssql/data" || !session.Sent.Single().Contains("InstanceDefaultBackupPath", StringComparison.Ordinal) || !session.Disposed) throw new TestFailure($"default: {dir}");
        var nothing = new ScriptedSession((_, _) => []);
        if (await new SqlServerManagement().DefaultBackupDirAsync(Context(new StubEngine(_ => nothing)), CancellationToken.None) is not null) throw new TestFailure("no answer, no default");
        var none = new ScriptedSession((_, _) => [new QueryTable(["dir"], [[null]])]);
        if (await new SqlServerManagement().DefaultBackupDirAsync(Context(new StubEngine(_ => none)), CancellationToken.None) is not null) throw new TestFailure("an old server has no default");
    }

    // Verifies: HLR-MGT-8, HLR-MGT-10, LLR-MGT-12
    public static async Task DropsWhatTheServerNamedAndNothingMore()
    {
        var management = new SqlServerManagement();
        async Task<ScriptedSession> Run(DropRequest request, params object?[][] resolved)
        {
            var session = new ScriptedSession((_, _) => [T(resolved)]);
            await management.DropAsync(Context(new StubEngine(_ => session)), request, CancellationToken.None);
            if (!session.Disposed) throw new TestFailure("the session was kept");
            return session;
        }

        var table = await Run(new DropRequest("table", "app", "dbo", "Order Line", null), ["[dbo].[Order Line]", "U", null]);
        if (table.Sent[1] != "EXEC [app].sys.sp_executesql N'DROP TABLE [dbo].[Order Line]'") throw new TestFailure("table: " + table.Sent[1]);
        if (!table.Sent[0].StartsWith("EXEC [app].sys.sp_executesql", StringComparison.Ordinal) || table.Parameters[0]!["schema"] as string != "dbo" || table.Parameters[0]!["name"] as string != "Order Line") throw new TestFailure("the lookup passes names as parameters: " + table.Sent[0]);
        string Dropped(ScriptedSession s) => s.Sent[1];
        if (Dropped(await Run(new DropRequest("view", "app", "dbo", "v", null), ["[dbo].[v]", "V", null])) != "EXEC [app].sys.sp_executesql N'DROP VIEW [dbo].[v]'") throw new TestFailure("view");
        if (Dropped(await Run(new DropRequest("procedure", "app", "dbo", "p", null), ["[dbo].[p]", "P", null])) != "EXEC [app].sys.sp_executesql N'DROP PROCEDURE [dbo].[p]'") throw new TestFailure("procedure");
        foreach (var type in new[] { "FN", "IF", "TF" })
        {
            if (Dropped(await Run(new DropRequest("function", "app", "dbo", "f", null), ["[dbo].[f]", type, null])) != "EXEC [app].sys.sp_executesql N'DROP FUNCTION [dbo].[f]'") throw new TestFailure("function " + type);
        }
        // Quotes in the server's quoted name are doubled for the string literal; the database is bracketed.
        if (Dropped(await Run(new DropRequest("table", "we]ird", "d'bo", "t", null), ["[d'bo].[t]", "U", null])) != "EXEC [we]]ird].sys.sp_executesql N'DROP TABLE [d''bo].[t]'") throw new TestFailure("quoting");
        if (Dropped(await Run(new DropRequest("database", "x]; DROP DATABASE y; --", null, "x]; DROP DATABASE y; --", null), ["[x]]; DROP DATABASE y; --]", null, null])) != "DROP DATABASE [x]]; DROP DATABASE y; --]") throw new TestFailure("database");

        async Task NotDropped(DropRequest request, string contains, params object?[][] resolved)
        {
            var session = new ScriptedSession((_, _) => [T(resolved)]);
            await Refused(() => management.DropAsync(Context(new StubEngine(_ => session)), request, CancellationToken.None), contains);
            if (session.Sent.Count > 1 || !session.Disposed) throw new TestFailure($"after a refusal: {session.Sent.Count} statement(s)");
        }
        await NotDropped(new DropRequest("table", "app", "dbo", "gone", null), "no table");
        await NotDropped(new DropRequest("table", "app", "dbo", "v", null), "view", ["[dbo].[v]", "V", null]);
        await NotDropped(new DropRequest("view", "app", "dbo", "t", null), "table", ["[dbo].[t]", "U", null]);
        await NotDropped(new DropRequest("function", "app", "dbo", "p", null), "procedure", ["[dbo].[p]", "P", null]);
        await NotDropped(new DropRequest("procedure", "app", "dbo", "f", null), "function", ["[dbo].[f]", "FN", null]);
        await NotDropped(new DropRequest("table", "app", "dbo", "q", null), "object type SQ", ["[dbo].[q]", "SQ", null]);
        await NotDropped(new DropRequest("table", "app", "dbo", "q", null), "object type", ["[dbo].[q]", null, null]);
        await NotDropped(new DropRequest("database", "gone", null, "gone", null), "no database");
        await NotDropped(new DropRequest("table", "app", null, "t", null), "schema");

        var failing = new ScriptedSession((sql, _) => sql.Contains("DROP TABLE", StringComparison.Ordinal) ? throw new InvalidOperationException("referenced by a foreign key") : [T(["[dbo].[t]", "U", null])]);
        try
        {
            await management.DropAsync(Context(new StubEngine(_ => failing)), new DropRequest("table", "app", "dbo", "t", null), CancellationToken.None);
            throw new TestFailure("a refused drop passed");
        }
        catch (InvalidOperationException)
        {
        }
        if (!failing.Disposed) throw new TestFailure("the session was kept after a server error");
    }
}
