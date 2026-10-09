using System.Globalization;
using Dbbliss.Backend.Engines;

namespace Dbbliss.Backend.Catalog;

/// <summary>Reading catalog query results: values come back as driver types, rows as arrays.</summary>
public static class CatalogHelpers
{
    /// <summary>The first result set of a query (an empty table if there is none).</summary>
    public static async Task<QueryTable> First(IEngineSession s, string sql, IReadOnlyDictionary<string, object?>? p, CancellationToken ct)
    {
        var tables = await s.QueryAsync(sql, p, ct);
        return tables.Count > 0 ? tables[0] : new QueryTable([], []);
    }

    /// <summary>A one-row result as property / value rows, skipping empty values.</summary>
    public static InfoSection Properties(string title, QueryTable t)
    {
        var rows = new List<object?[]>();
        if (t.Rows.Count > 0)
        {
            for (var i = 0; i < t.Columns.Count; i++)
            {
                var v = t.Rows[0][i];
                if (v is null or "") continue;
                rows.Add([t.Columns[i].Replace('_', ' '), v is bool b ? (b ? "yes" : "no") : v]);
            }
        }
        return new InfoSection(title, ["property", "value"], rows);
    }

    public static InfoSection Table(string title, QueryTable t) => new(title, t.Columns, t.Rows);

    public static string? Str(object? v) => v switch { null => null, string s => s, char c => c.ToString(), _ => Convert.ToString(v, CultureInfo.InvariantCulture) };

    public static bool Bool(object? v) => v switch { bool b => b, null => false, _ => Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0 };

    public static long Long(object? v) => Convert.ToInt64(v, CultureInfo.InvariantCulture);

    public static string Num(object? v) => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
}
