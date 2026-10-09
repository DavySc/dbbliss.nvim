using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Dbbliss.Backend;
using Dbbliss.Backend.Rpc;

namespace Dbbliss.ProtocolTests;

/// <summary>Password references: validation, the command-line stores with a fake runner, and the real stores where they exist.</summary>
public static class CredentialTests
{
    private static void Fails(JsonNode? reference, int code, string mustSay, Credentials.ToolRunner? run = null)
    {
        try
        {
            Credentials.Resolve(reference, run);
        }
        catch (RpcException ex)
        {
            if (ex.Code != code) throw new TestFailure($"{reference?.ToJsonString()}: error {ex.Code}, expected {code} ({ex.Message})");
            if (!ex.Message.Contains(mustSay, StringComparison.OrdinalIgnoreCase)) throw new TestFailure($"{reference?.ToJsonString()}: message \"{ex.Message}\" lacks \"{mustSay}\"");
            return;
        }
        throw new TestFailure($"{reference?.ToJsonString()}: no error");
    }

    public static void Validation()
    {
        Fails(JsonValue.Create("literal-password"), RpcErrors.InvalidParams, "never a literal");
        Fails(new JsonObject(), RpcErrors.InvalidParams, "exactly one");
        Fails(new JsonObject { ["env"] = "A", ["pass"] = "b" }, RpcErrors.InvalidParams, "exactly one");
        Fails(new JsonObject { ["vault"] = "x" }, RpcErrors.InvalidParams, "unsupported");
        Fails(new JsonObject { ["env"] = "" }, RpcErrors.InvalidParams, "non-empty");
        Fails(new JsonObject { ["env"] = "DBBLISS_SURELY_NOT_SET_9F2" }, RpcErrors.CredentialNotFound, "not set");
        Fails(new JsonObject { ["libsecret"] = new JsonObject() }, RpcErrors.InvalidParams, "attributes");
        Fails(new JsonObject { ["libsecret"] = new JsonObject { ["service"] = 5 } }, RpcErrors.InvalidParams, "string");
        if (Credentials.Resolve(null) is not null) throw new TestFailure("no reference must give no password");
        Environment.SetEnvironmentVariable("DBBLISS_TEST_CRED_ENV", "from-env");
        if (Credentials.Resolve(new JsonObject { ["env"] = "DBBLISS_TEST_CRED_ENV" }) != "from-env") throw new TestFailure("env reference");
    }

    public static void Tools()
    {
        string? file = null;
        List<string>? args = null;
        Credentials.ToolRunner Fake(int exit, string output, string error = "") => (f, a) =>
        {
            file = f;
            args = [.. a];
            return (exit, output, error);
        };

        var pass = new JsonObject { ["pass"] = "db/prod" };
        if (Credentials.Resolve(pass, Fake(0, "s3cret\nuser: me\n")) != "s3cret") throw new TestFailure("pass: first line");
        if (file != "pass" || !args!.SequenceEqual(["show", "db/prod"])) throw new TestFailure($"pass called as {file} {string.Join(' ', args!)}");
        if (Credentials.Resolve(pass, Fake(0, "  pa ss  \r\nmore\r\n")) != "  pa ss  ") throw new TestFailure("pass: spaces are part of the password, the CR is not");
        if (Credentials.Resolve(pass, Fake(0, "no-newline")) != "no-newline") throw new TestFailure("pass: no line end");

        var secret = new JsonObject { ["libsecret"] = new JsonObject { ["service"] = "dbbliss", ["account"] = "prod" } };
        if (Credentials.Resolve(secret, Fake(0, "tool-secret")) != "tool-secret") throw new TestFailure("libsecret value");
        if (file != "secret-tool" || !args!.SequenceEqual(["lookup", "service", "dbbliss", "account", "prod"])) throw new TestFailure($"secret-tool called as {file} {string.Join(' ', args!)}");

        // A failing tool: its stderr is passed on, its stdout (which could be the secret) is not.
        try
        {
            Credentials.Resolve(pass, Fake(1, "LEAKED-SECRET", "Error: db/prod is not in the password store."));
            throw new TestFailure("a failing pass gave a password");
        }
        catch (RpcException ex)
        {
            if (ex.Code != RpcErrors.CredentialNotFound || !ex.Message.Contains("not in the password store") || ex.Message.Contains("LEAKED-SECRET"))
                throw new TestFailure($"failing tool: {ex.Code} {ex.Message}");
        }
        Fails(pass, RpcErrors.CredentialNotFound, "empty password", Fake(0, "\nsecond"));
        Fails(pass, RpcErrors.CredentialNotFound, "not installed", (_, _) => throw new Win32Exception("no such file"));
    }

    public static void BlobDecoding()
    {
        foreach (var text in new[] { "plain", "pässwörd-π", "a" })
        {
            if (Credentials.DecodeBlob(Encoding.Unicode.GetBytes(text)) != text) throw new TestFailure($"UTF-16 blob \"{text}\"");
        }
        if (Credentials.DecodeBlob(Encoding.UTF8.GetBytes("pässwörd")) != "pässwörd") throw new TestFailure("UTF-8 blob");
        if (Credentials.DecodeBlob([]) != "") throw new TestFailure("empty blob");
    }

    /// <summary>cmdkey writes a generic credential; the backend's CredReadW must read it back.</summary>
    public static void WindowsCredentialManager()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string target = "dbbliss-test-credential";
        const string password = "s3cret-Pw!1";
        Run("cmdkey", $"/generic:{target} /user:dbbliss /pass:{password}");
        try
        {
            var read = Credentials.Resolve(new JsonObject { ["credman"] = target });
            if (read != password) throw new TestFailure($"read \"{read}\" back, expected \"{password}\"");
            Fails(new JsonObject { ["credman"] = target + "-missing" }, RpcErrors.CredentialNotFound, "No generic credential");
        }
        finally
        {
            Run("cmdkey", $"/delete:{target}");
        }
    }

    /// <summary>
    /// A real `pass` store with a throwaway GPG key. Runs where `pass` and `gpg` exist; with
    /// DBBLISS_TEST_PASS=1 (CI) their absence is a failure, not a skip.
    /// </summary>
    public static void PassStore()
    {
        var required = Environment.GetEnvironmentVariable("DBBLISS_TEST_PASS") == "1";
        if (OperatingSystem.IsWindows()) return;
        if (!OnPath("pass") || !OnPath("gpg"))
        {
            if (required) throw new TestFailure("DBBLISS_TEST_PASS=1 but pass or gpg is not installed");
            return;
        }
        var home = Directory.CreateTempSubdirectory("dbbliss-pass-");
        var gnupg = Path.Combine(home.FullName, "gnupg");
        Directory.CreateDirectory(gnupg);
        File.SetUnixFileMode(gnupg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var saved = new[] { "GNUPGHOME", "PASSWORD_STORE_DIR" }.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("GNUPGHOME", gnupg);
            Environment.SetEnvironmentVariable("PASSWORD_STORE_DIR", Path.Combine(home.FullName, "store"));
            Run("gpg", "--batch --passphrase \"\" --quick-gen-key dbbliss-test@example.invalid default default never");
            Run("pass", "init dbbliss-test@example.invalid");
            Run("pass", "insert -m -f dbbliss/test", stdin: "pass-store-secret\nsecond line\n");
            var read = Credentials.Resolve(new JsonObject { ["pass"] = "dbbliss/test" });
            if (read != "pass-store-secret") throw new TestFailure($"pass gave \"{read}\"");
            Fails(new JsonObject { ["pass"] = "dbbliss/missing" }, RpcErrors.CredentialNotFound, "not in the password store");
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
            Run("gpgconf", $"--homedir {gnupg} --kill all", check: false);
            home.Delete(recursive: true);
        }
    }

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, tool)));

    private static void Run(string file, string arguments, string? stdin = null, bool check = true)
    {
        var info = new ProcessStartInfo(file, arguments) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(info) ?? throw new TestFailure($"cannot start {file}");
        if (stdin is not null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (check && p.ExitCode != 0) throw new TestFailure($"{file} {arguments} exited {p.ExitCode}: {err.Result.Trim()}");
    }
}
