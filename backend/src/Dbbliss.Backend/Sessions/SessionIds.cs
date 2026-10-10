using System.Globalization;

namespace Dbbliss.Backend.Sessions;

public static class SessionIds
{
    /// <summary>
    /// A session id the engines paste into a statement (SQL Server <c>KILL</c> takes no parameter) or
    /// bind as an integer. Only ASCII digits that fit an int are accepted (NumberStyles.None allows nothing else).
    /// </summary>
    public static int ParseInt(string id)
    {
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new SessionActionException($"\"{id}\" is not a session id.");
        }
        return value;
    }

    /// <summary>The identity token of a session start time: round-trippable, ticks included.</summary>
    public static string FormatIdentity(object? startTime) => startTime switch
    {
        DateTime t => t.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("The server returned no start time for a session."),
    };

    public static DateTime ParseIdentity(string identity)
    {
        if (!DateTime.TryParseExact(identity, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
        {
            throw new SessionActionException($"\"{identity}\" is not a session identity from the list; refresh the list.");
        }
        return time;
    }
}
