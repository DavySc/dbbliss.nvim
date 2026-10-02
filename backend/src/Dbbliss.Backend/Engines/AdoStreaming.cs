using System.Data.Common;

namespace Dbbliss.Backend.Engines;

/// <summary>
/// Shared result-set streaming loop for the ADO.NET drivers.
///
/// The loop does not stop on its own when a cancel is requested: the protocol cancel stops the
/// server, and the driver then ends the stream with the server's cancel error. Everything the
/// server produced before that point (for example, earlier statements of a batch, which the server
/// may still hold in its send buffer) is delivered. The token only interrupts the sink when it
/// would block on a client that is not reading.
/// </summary>
internal static class AdoStreaming
{
    /// <summary>
    /// Streams every result set of <paramref name="reader"/> into <paramref name="sink"/>.
    /// <paramref name="driverToken"/> is passed to the driver (Npgsql turns it into a CancelRequest);
    /// drivers cancelled through <see cref="QueryControl.SetProtocolCancel"/> get CancellationToken.None
    /// so cancel is not sent twice. The sink always gets the query token, so a blocked write wakes up.
    /// </summary>
    public static async Task StreamAsync(DbDataReader reader, IResultSink sink, QueryControl control, CancellationToken driverToken)
    {
        var index = 0;
        do
        {
            if (reader.FieldCount > 0)
            {
                await sink.ResultSetAsync(index, ValueConverter.Columns(reader), control.Token);
                long rows = 0;
                while (await reader.ReadAsync(driverToken))
                {
                    await sink.RowAsync(index, ValueConverter.ReadRow(reader), control.Token);
                    rows++;
                }
                await sink.ResultSetDoneAsync(index, rows, control.Token);
                index++;
            }
        }
        while (await reader.NextResultAsync(driverToken));
    }

    /// <summary>Synchronous variant for drivers whose async API is not truly async (System.Data.Odbc).</summary>
    public static void Stream(DbDataReader reader, IResultSink sink, QueryControl control)
    {
        var index = 0;
        do
        {
            if (reader.FieldCount > 0)
            {
                sink.ResultSetAsync(index, ValueConverter.Columns(reader), control.Token).AsTask().GetAwaiter().GetResult();
                long rows = 0;
                while (reader.Read())
                {
                    sink.RowAsync(index, ValueConverter.ReadRow(reader), control.Token).AsTask().GetAwaiter().GetResult();
                    rows++;
                }
                sink.ResultSetDoneAsync(index, rows, control.Token).AsTask().GetAwaiter().GetResult();
                index++;
            }
        }
        while (reader.NextResult());
    }
}
