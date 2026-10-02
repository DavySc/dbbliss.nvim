using System.Runtime.InteropServices;
using Dbbliss.Backend;
using Dbbliss.Backend.Rpc;

if (args.Contains("--version"))
{
    Console.WriteLine($"dbbliss-backend {typeof(Backend).Assembly.GetName().Version} protocol {Backend.ProtocolVersion}");
    return 0;
}

var stdout = Console.OpenStandardOutput();
var stdin = Console.OpenStandardInput();
// Nothing may write to stdout except the protocol writer; stray Console.Write goes to stderr.
Console.SetOut(Console.Error);

var backend = new Backend(new Output(stdout));

// Graceful paths besides stdin EOF: SIGTERM/SIGINT/SIGHUP on Linux; on Windows these map to
// console close/Ctrl+C events. A hard kill (SIGKILL, TerminateProcess) cannot be intercepted.
var registrations = new List<PosixSignalRegistration>();
foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
{
    try
    {
        registrations.Add(PosixSignalRegistration.Create(signal, ctx =>
        {
            ctx.Cancel = true;
            _ = backend.ShutdownAsync($"signal {ctx.Signal}");
        }));
    }
    catch (PlatformNotSupportedException)
    {
    }
}

Log.Info($"dbbliss-backend started, pid {Environment.ProcessId}");
_ = Task.Run(() => backend.RunAsync(stdin));
await backend.Completion;
Log.Info("dbbliss-backend exiting");
foreach (var r in registrations) r.Dispose();
return 0;
