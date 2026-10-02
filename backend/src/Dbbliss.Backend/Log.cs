using System.Globalization;

namespace Dbbliss.Backend;

/// <summary>Diagnostics go to stderr; stdout is reserved for the protocol.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:HH:mm:ss.fff} {level} {message}");
        lock (Gate)
        {
            try
            {
                Console.Error.WriteLine(line);
            }
            catch (IOException)
            {
                // stderr gone too; nothing left to tell.
            }
        }
    }
}
