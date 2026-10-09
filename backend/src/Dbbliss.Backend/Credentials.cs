using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Dbbliss.Backend.Rpc;

namespace Dbbliss.Backend;

/// <summary>
/// Resolves a password reference. Passwords never appear in the Lua config, on the wire from Lua or
/// in the log; the config holds only where to find one:
/// <list type="bullet">
/// <item><c>{ env = "NAME" }</c>: an environment variable of the backend.</item>
/// <item><c>{ credman = "target" }</c>: a generic credential in Windows Credential Manager
/// (<c>cmdkey /generic:target /user:u /pass:...</c>), read with CredReadW.</item>
/// <item><c>{ pass = "path/in/store" }</c>: the first line of <c>pass show path/in/store</c>.</item>
/// <item><c>{ libsecret = { attribute = "value", ... } }</c>: <c>secret-tool lookup attribute value ...</c>.</item>
/// </list>
/// Integrated authentication needs no reference: leave the password out and put
/// <c>Integrated Security=true</c> in the connection string.
/// </summary>
public static class Credentials
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Result of running a command-line tool. Tests substitute a fake.</summary>
    public delegate (int ExitCode, string Output, string Error) ToolRunner(string file, IReadOnlyList<string> args);

    public static string? Resolve(JsonNode? reference, ToolRunner? run = null)
    {
        if (reference is null) return null;
        if (reference is not JsonObject obj)
        {
            throw new RpcException(RpcErrors.InvalidParams, "password must be a reference such as { env = \"NAME\" }, never a literal.");
        }
        var keys = obj.Select(p => p.Key).ToArray();
        if (keys.Length != 1)
        {
            throw new RpcException(RpcErrors.InvalidParams,
                $"password must name exactly one of env, credman, pass, libsecret (got {(keys.Length == 0 ? "none" : string.Join(", ", keys))}).");
        }
        run ??= RunTool;
        switch (keys[0])
        {
            case "env":
                var name = StringValue(obj, "env");
                return Environment.GetEnvironmentVariable(name)
                    ?? throw new RpcException(RpcErrors.CredentialNotFound, $"Environment variable {name} is not set.");
            case "credman":
                return FromCredentialManager(StringValue(obj, "credman"));
            case "pass":
                return FirstLine(Tool(run, "pass", ["show", StringValue(obj, "pass")], "pass entry " + StringValue(obj, "pass")), "pass");
            case "libsecret":
                if (obj["libsecret"] is not JsonObject attributes || attributes.Count == 0)
                {
                    throw new RpcException(RpcErrors.InvalidParams, "libsecret must be a table of attributes, e.g. { service = \"dbbliss\", account = \"prod\" }.");
                }
                var args = new List<string> { "lookup" };
                foreach (var (attribute, value) in attributes)
                {
                    args.Add(attribute);
                    args.Add(value is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new RpcException(RpcErrors.InvalidParams, $"libsecret attribute {attribute} must be a string."));
                }
                return FirstLine(Tool(run, "secret-tool", args, "libsecret item"), "secret-tool");
            default:
                throw new RpcException(RpcErrors.InvalidParams, $"Unsupported password reference {keys[0]}; use env, credman, pass or libsecret.");
        }
    }

    private static string StringValue(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
            ? s
            : throw new RpcException(RpcErrors.InvalidParams, $"password.{key} must be a non-empty string.");

    private static string Tool(ToolRunner run, string file, IReadOnlyList<string> args, string what)
    {
        (int ExitCode, string Output, string Error) result;
        try
        {
            result = run(file, args);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new RpcException(RpcErrors.CredentialNotFound, $"Cannot look up the {what}: {file} is not installed or not on PATH.");
        }
        if (result.ExitCode != 0)
        {
            // The tool's own message (e.g. "Error: x is not in the password store"); never its output.
            var reason = result.Error.Trim();
            throw new RpcException(RpcErrors.CredentialNotFound,
                $"Cannot read the {what}: {file} exited with {result.ExitCode}{(reason.Length > 0 ? ": " + reason : ". The item may not exist.")}");
        }
        return result.Output;
    }

    /// <summary>The first line, without its line end. Spaces are part of a password.</summary>
    private static string FirstLine(string output, string tool)
    {
        var end = output.IndexOf('\n');
        var line = end < 0 ? output : output[..end];
        line = line.TrimEnd('\r');
        if (line.Length == 0)
        {
            throw new RpcException(RpcErrors.CredentialNotFound, $"{tool} returned an empty password.");
        }
        return line;
    }

    private static (int, string, string) RunTool(string file, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new FileNotFoundException(file);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ToolTimeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new RpcException(RpcErrors.CredentialNotFound, $"{file} did not answer within {ToolTimeout.TotalSeconds:0} s (a passphrase prompt?).");
        }
        return (process.ExitCode, output.Result, error.Result);
    }

    private static string FromCredentialManager(string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new RpcException(RpcErrors.InvalidParams, "credman is Windows Credential Manager; use pass or libsecret on this system.");
        }
        return ReadGenericCredential(target);
    }

    [SupportedOSPlatform("windows")]
    private static string ReadGenericCredential(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            throw new RpcException(RpcErrors.CredentialNotFound,
                error == ErrorNotFound
                    ? $"No generic credential named \"{target}\" in Windows Credential Manager (cmdkey /generic:{target} /user:<user> /pass:<password>)."
                    : $"Cannot read credential \"{target}\": Windows error {error}.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var bytes = new byte[credential.CredentialBlobSize];
            if (bytes.Length > 0) Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return DecodeBlob(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    /// <summary>
    /// Credential Manager stores bytes. cmdkey and the control panel write UTF-16; other tools write
    /// UTF-8. UTF-16 text of ordinary characters has NUL bytes, UTF-8 text has none.
    /// </summary>
    public static string DecodeBlob(byte[] bytes) =>
        bytes.Length % 2 == 0 && Array.IndexOf(bytes, (byte)0) >= 0 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);

    private const uint CredTypeGeneric = 1;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
