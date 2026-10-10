using System.Diagnostics;

namespace Dbbliss.ProtocolTests;

/// <summary>The test runner as a child process for the process-runner tests: <c>--child mode [args]</c>; args[0] is --child.</summary>
public static class ChildProcess
{
    /// <summary>The command that starts this program again as a child: through the dotnet host when that is what runs it, else the apphost itself.</summary>
    public static (string File, string[] Arguments) Command(params string[] args)
    {
        var host = Environment.ProcessPath!;
        var viaDotnet = string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase);
        return (host, [.. viaDotnet ? new[] { System.Reflection.Assembly.GetEntryAssembly()!.Location } : [], "--child", .. args]);
    }

    public static int Run(string[] args)
    {
        // The runner reads UTF-8; a Windows console would write its own code page.
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        switch (args[1])
        {
            case "lines":
                Console.Out.WriteLine("out1");
                Console.Out.WriteLine("out2");
                Console.Error.WriteLine("err1");
                Console.Error.WriteLine("err2");
                return 3;
            case "noisy":
                for (var i = 0; i < 1000; i++) Console.Error.WriteLine("err" + i);
                return 1;
            case "env":
                Console.Out.WriteLine(Environment.GetEnvironmentVariable(args[2]) ?? "(unset)");
                return 0;
            case "args":
                foreach (var a in args.Skip(2)) Console.Out.WriteLine(a);
                return 0;
            case "sleep":
                Console.Out.WriteLine("up");
                Console.Out.Flush();
                Thread.Sleep(Timeout.Infinite);
                return 0;
            case "spawn":
                using (var grandchild = Process.Start(new ProcessStartInfo(Command("sleep").File) { RedirectStandardOutput = true }.WithArguments(Command("sleep").Arguments))!)
                {
                    Console.Out.WriteLine($"{Environment.ProcessId} {grandchild.Id}");
                    Console.Out.Flush();
                    Thread.Sleep(Timeout.Infinite);
                }
                return 0;
            default:
                return 99;
        }
    }
}

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo info, IEnumerable<string> arguments)
    {
        foreach (var a in arguments) info.ArgumentList.Add(a);
        return info;
    }
}
