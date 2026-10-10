using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Dbbliss.Backend.Management;

/// <param name="Environment">Variables added to the child's environment; a null value removes one.</param>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?>? Environment = null);

/// <param name="StderrTail">The end of the error output, for the message of a failure.</param>
public sealed record ProcessResult(int ExitCode, string StderrTail);

/// <summary>Runs a command-line tool. Tests replace it; the real one starts a process.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the tool to its end. <paramref name="onLine"/> gets each output line (text, true for error output)
    /// as it arrives. When <paramref name="ct"/> is cancelled the whole process tree is ended and
    /// <see cref="OperationCanceledException"/> follows once it is gone. A tool that cannot be started is a
    /// <see cref="ManagementException"/>.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string, bool>? onLine, CancellationToken ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    private const int TailLines = 20;

    public static ProcessRunner Default { get; } = new();

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string, bool>? onLine, CancellationToken ct)
    {
        var info = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in spec.Arguments) info.ArgumentList.Add(argument);
        foreach (var (name, value) in spec.Environment ?? new Dictionary<string, string?>())
        {
            if (value is null) info.Environment.Remove(name);
            else info.Environment[name] = value;
        }

        var tail = new Queue<string>();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) onLine?.Invoke(e.Data, false);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (tail)
            {
                tail.Enqueue(e.Data);
                if (tail.Count > TailLines) tail.Dequeue();
            }
            onLine?.Invoke(e.Data, true);
        };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new ManagementException($"Could not start {spec.FileName}: {ex.Message}");
        }
        // The tool never reads a password or an answer from here: nothing to wait for, and no prompt to hang on.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (ct.Register(() => Kill(process)))
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        ct.ThrowIfCancellationRequested();
        lock (tail) return new ProcessResult(process.ExitCode, string.Join('\n', tail));
    }

    /// <summary>Ends the process and everything it started (pg_dump has helpers; a shell wrapper has the tool).</summary>
    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }
}

/// <summary>Finds a command-line tool: where the user said, else on PATH.</summary>
public static class ToolLocator
{
    /// <param name="configured">A file, or the folder holding the tool (<c>options.pg_dump</c>). When given it is the only place looked at.</param>
    /// <param name="pathVariable">The PATH to search (the environment's, in production).</param>
    public static string Find(string tool, string? configured, string? pathVariable)
    {
        var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe" } : [tool];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured)) return Path.GetFullPath(configured);
            if (!Directory.Exists(configured)) throw new ManagementException($"{configured} is neither the {tool} file nor the folder that holds it (options).");
            return InFolder(configured, names) ?? throw new ManagementException($"{tool} is not in {configured} (options).");
        }
        var folders = (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var folder in folders)
        {
            if (Directory.Exists(folder) && InFolder(folder, names) is { } found) return found;
        }
        throw new ManagementException(
            $"{tool} was not found on PATH ({(string.IsNullOrWhiteSpace(pathVariable) ? "PATH is empty" : string.Join(Path.PathSeparator, folders))}). " +
            $"Install the PostgreSQL client tools, put them on PATH, or set options.{tool} to the file or its folder.");
    }

    private static string? InFolder(string folder, string[] names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        return null;
    }
}
