using System.Collections;
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
    /// <summary>The JSON text of an array or map cell is read by people: quotes as \", letters as they are, not \u0022 and \u00e9.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions Readable = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
        // Arrays (PostgreSQL), key-value maps (hstore) and bit strings have no JSON cell of their own;
        // their text form is the data, not the name of their .NET type.
        Array array => JsonValue.Create(ArrayToJson(array).ToJsonString(Readable)),
        IDictionary map => JsonValue.Create(MapToJson(map).ToJsonString(Readable)),
        BitArray bits => JsonValue.Create(string.Concat(bits.Cast<bool>().Select(b => b ? '1' : '0'))),
        IFormattable fmt => JsonValue.Create(fmt.ToString(null, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>One level of an array per dimension; an element is converted like a cell, a nested array stays an array.</summary>
    private static JsonArray ArrayToJson(Array array) => ArrayLevel(array, 0, new int[array.Rank]);

    private static JsonArray ArrayLevel(Array array, int dimension, int[] index)
    {
        var level = new JsonArray();
        for (var i = array.GetLowerBound(dimension); i <= array.GetUpperBound(dimension); i++)
        {
            index[dimension] = i;
            level.Add(dimension == array.Rank - 1 ? Element(array.GetValue(index)) : ArrayLevel(array, dimension + 1, index));
        }
        return level;
    }

    private static JsonObject MapToJson(IDictionary map)
    {
        var result = new JsonObject();
        foreach (DictionaryEntry entry in map) result[System.Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? ""] = Element(entry.Value);
        return result;
    }

    private static JsonNode? Element(object? value) => value switch
    {
        Array nested and not byte[] => ArrayToJson(nested),
        IDictionary map => MapToJson(map),
        _ => Convert(value),
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
