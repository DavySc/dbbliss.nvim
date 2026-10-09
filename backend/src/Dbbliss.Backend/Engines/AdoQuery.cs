using System.Data.Common;

namespace Dbbliss.Backend.Engines;

/// <summary>Reads every result set of a command completely (catalog queries; the user's statements stream instead).</summary>
internal static class AdoQuery
{
    public static async Task<IReadOnlyList<QueryTable>> ReadAllAsync(DbCommand cmd, CancellationToken ct)
    {
        var tables = new List<QueryTable>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        do
        {
            var columns = new string[reader.FieldCount];
            for (var i = 0; i < columns.Length; i++) columns[i] = reader.GetName(i);
            var rows = new List<object?[]>();
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[columns.Length];
                for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            // A statement without a result set (a SET, a DDL) has no columns; it is not a table.
            if (columns.Length > 0) tables.Add(new QueryTable(columns, rows));
        }
        while (await reader.NextResultAsync(ct));
        return tables;
    }
}
