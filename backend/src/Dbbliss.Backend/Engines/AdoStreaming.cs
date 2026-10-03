using System.Data.Common;

namespace Dbbliss.Backend.Engines;

/// <summary>
/// Shared result-set streaming loop for the ADO.NET drivers.
///
/// The loop does not stop on its own when a cancel is requested: the protocol cancel stops the
/// server, and the driver then ends the stream with the server's cancel error. Everything the
/// server produced before that point (for example, earlier statements of a batch, which the server
/// may still hold in its send buffer) is delivered.
/// </summary>
internal static class AdoStreaming
{
    /// <summary>
    /// Streams every result set of <paramref name="reader"/> into <paramref name="sink"/>.
    /// </summary>
    public static async Task StreamAsync(DbDataReader reader, IResultSink sink)
    {
        var index = 0;
        do
        {
            if (reader.FieldCount > 0)
            {
                await sink.ResultSetAsync(index, ValueConverter.Columns(reader));
                long rows = 0;
                while (await reader.ReadAsync())
                {
                    await sink.RowAsync(index, ValueConverter.ReadRow(reader));
                    rows++;
                }
                await sink.ResultSetDoneAsync(index, rows);
                index++;
            }
        }
        while (await reader.NextResultAsync());
    }

    /// <summary>Synchronous variant for drivers whose async API is not truly async (System.Data.Odbc).</summary>
    public static void Stream(DbDataReader reader, IResultSink sink)
    {
        var index = 0;
        do
        {
            if (reader.FieldCount > 0)
            {
                sink.ResultSetAsync(index, ValueConverter.Columns(reader)).AsTask().GetAwaiter().GetResult();
                long rows = 0;
                while (reader.Read())
                {
                    sink.RowAsync(index, ValueConverter.ReadRow(reader)).AsTask().GetAwaiter().GetResult();
                    rows++;
                }
                sink.ResultSetDoneAsync(index, rows).AsTask().GetAwaiter().GetResult();
                index++;
            }
        }
        while (reader.NextResult());
    }
}
