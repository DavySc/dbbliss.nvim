using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Dbbliss.Backend.Engines;

/// <summary>
/// Converts driver values into JSON without losing information: decimals and 64-bit integers
/// travel as strings (Lua numbers are doubles), binary as 0x-prefixed hex.
/// </summary>
public static class ValueConverter
{
    public static JsonArray ReadRow(DbDataReader reader)
    {
        var row = new JsonArray();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row.Add(reader.IsDBNull(i) ? null : Convert(reader.GetValue(i)));
        }
        return row;
    }

    public static JsonNode? Convert(object? value) => value switch
    {
        null or DBNull => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        byte or sbyte or short or ushort or int => JsonValue.Create(System.Convert.ToInt32(value, CultureInfo.InvariantCulture)),
        long l when l is >= -(1L << 53) and <= 1L << 53 => JsonValue.Create(l),
        long l => JsonValue.Create(l.ToString(CultureInfo.InvariantCulture)),
        uint u => JsonValue.Create((long)u),
        ulong ul => JsonValue.Create(ul.ToString(CultureInfo.InvariantCulture)),
        float f => JsonValue.Create(f.ToString("R", CultureInfo.InvariantCulture)),
        double d when double.IsFinite(d) => JsonValue.Create(d),
        double d => JsonValue.Create(d.ToString(CultureInfo.InvariantCulture)),
        decimal m => JsonValue.Create(m.ToString(CultureInfo.InvariantCulture)),
        DateTime dt => JsonValue.Create(dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture)),
        DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        TimeOnly t => JsonValue.Create(t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
        TimeSpan ts => JsonValue.Create(ts.ToString("c", CultureInfo.InvariantCulture)),
        Guid g => JsonValue.Create(g.ToString()),
        byte[] bytes => JsonValue.Create("0x" + System.Convert.ToHexString(bytes)),
        IFormattable fmt => JsonValue.Create(fmt.ToString(null, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value.ToString()),
    };

    public static IReadOnlyList<ColumnInfo> Columns(DbDataReader reader)
    {
        var cols = new ColumnInfo[reader.FieldCount];
        for (var i = 0; i < cols.Length; i++)
        {
            cols[i] = new ColumnInfo(reader.GetName(i), reader.GetDataTypeName(i));
        }
        return cols;
    }
}
