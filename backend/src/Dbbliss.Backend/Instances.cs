using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dbbliss.Backend;

/// <summary>
/// Every backend process on this machine registers here, so a later one can tell which server
/// sessions belong to a backend that died without closing them (killed hard: TerminateProcess,
/// SIGKILL). One file per instance, <c>&lt;state dir&gt;/instances/&lt;id&gt;.json</c> holding
/// <c>{ "pid": …, "start": "…" }</c>, removed on a clean exit. A file whose process is gone, or whose
/// pid now belongs to a process started at another time, is a dead instance.
/// Nothing here may block a connect: file errors are logged and ignored.
/// </summary>
public sealed class Instances
{
    /// <summary>Dead instances stay sweepable this long: their orphans may sit on several servers.</summary>
    public static readonly TimeSpan KeepDead = TimeSpan.FromDays(7);

    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    private readonly string _dir;

    public string Id { get; }

    public Instances(string stateDir, string? id = null)
    {
        _dir = Path.Combine(stateDir, "instances");
        Id = id ?? Convert.ToHexStringLower(Guid.NewGuid().ToByteArray().AsSpan(0, 4));
    }

    /// <summary>The state dir: DBBLISS_STATE_DIR, or %LOCALAPPDATA%\dbbliss / ~/.local/share/dbbliss.</summary>
    public static string DefaultStateDir() =>
        Environment.GetEnvironmentVariable("DBBLISS_STATE_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbbliss");

    public void Register()
    {
        using var self = Process.GetCurrentProcess();
        Write(_dir, Id, self.Id, self.StartTime.ToUniversalTime());
    }

    public void Unregister() => Try(() => File.Delete(PathOf(_dir, Id)), "unregister");

    /// <summary>Writes an instance record. Public for tests.</summary>
    public static void Write(string instancesDir, string id, int pid, DateTime startUtc)
    {
        Try(() =>
        {
            Directory.CreateDirectory(instancesDir);
            var json = new JsonObject { ["pid"] = pid, ["start"] = startUtc.ToString("O") }.ToJsonString();
            var tmp = PathOf(instancesDir, id) + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, PathOf(instancesDir, id), overwrite: true);
        }, "register");
    }

    /// <summary>Ids of registered instances whose process is gone. Never includes this instance.</summary>
    public HashSet<string> DeadIds()
    {
        var dead = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, _) in DeadFiles()) dead.Add(id);
        return dead;
    }

    /// <summary>Deletes the files of instances dead for longer than <see cref="KeepDead"/>.</summary>
    public void Prune()
    {
        foreach (var (_, file) in DeadFiles())
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > KeepDead) Try(() => File.Delete(file), "prune");
        }
    }

    private List<(string Id, string File)> DeadFiles()
    {
        var dead = new List<(string, string)>();
        string[] files;
        try
        {
            files = Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*.json") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"instances: cannot list {_dir}: {ex.Message}");
            return dead;
        }
        foreach (var file in files)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (id == Id) continue;
            try
            {
                var record = JsonNode.Parse(File.ReadAllText(file))!;
                var pid = record["pid"]!.GetValue<int>();
                var start = DateTime.Parse(record["start"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind);
                if (!IsAlive(pid, start)) dead.Add((id, file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NullReferenceException or FormatException or InvalidOperationException)
            {
                // Unreadable or half-written: not provably dead, so never swept.
                Log.Warn($"instances: skipping {file}: {ex.Message}");
            }
        }
        return dead;
    }

    private static bool IsAlive(int pid, DateTime startUtc)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return (p.StartTime.ToUniversalTime() - startUtc).Duration() <= StartTolerance;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false; // exited while we looked
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true; // cannot read its start time (another user's process): not provably dead
        }
    }

    private static string PathOf(string dir, string id) => Path.Combine(dir, id + ".json");

    private static void Try(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"instances: {what} failed: {ex.Message}");
        }
    }
}
