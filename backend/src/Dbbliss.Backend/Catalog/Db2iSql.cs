using System.Globalization;
using System.Text;

namespace Dbbliss.Backend.Catalog;

/// <summary>Text helpers for DB2 for i catalog SQL: ODBC markers, identifiers, and column types as declared.</summary>
public static class Db2iSql
{
    /// <summary>
    /// ODBC has positional markers only. Replaces each <c>@name</c> outside a string literal or quoted
    /// identifier with <c>?</c> and returns the values in the order the markers occur (a name used twice is sent twice).
    /// </summary>
    public static string Positional(string sql, IReadOnlyDictionary<string, object?>? parameters, out List<object?> values)
    {
        values = [];
        var sb = new StringBuilder(sql.Length);
        char? quote = null;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (quote is not null)
            {
                sb.Append(c);
                if (c == quote) quote = null;
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                sb.Append(c);
            }
            else if (c == '@' && i + 1 < sql.Length && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_'))
            {
                var end = i + 1;
                while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
                var name = sql[(i + 1)..end];
                if (parameters is null || !parameters.TryGetValue(name, out var value))
                {
                    throw new InvalidOperationException($"The catalog query uses @{name} but no value was given.");
                }
                values.Add(value);
                sb.Append('?');
                i = end - 1;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>An identifier as it is written in DDL: plain when it is an ordinary upper-case name, quoted otherwise.</summary>
    public static string Ident(string name)
    {
        var plain = name.Length > 0 && char.IsAsciiLetterUpper(name[0]) && name.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '_' or '$' or '#' or '@');
        return plain ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static readonly HashSet<string> WithLength = new(StringComparer.OrdinalIgnoreCase)
    {
        "CHAR", "CHARACTER", "VARCHAR", "CHARACTER VARYING", "GRAPHIC", "VARGRAPHIC", "BINARY", "VARBINARY", "NCHAR", "NVARCHAR", "CLOB", "DBCLOB", "BLOB",
    };

    /// <summary>
    /// A column type as it is written in a declaration, from the catalog's type name and its
    /// CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION and NUMERIC_SCALE (SYSCOLUMNS2, SYSPARMS). Unverified: whether a
    /// TIMESTAMP's fractional digits are in NUMERIC_SCALE.
    /// </summary>
    public static string TypeName(string dataType, long? length, long? precision, long? scale)
    {
        static string N(long v) => v.ToString(CultureInfo.InvariantCulture);
        var type = dataType.ToUpperInvariant();
        if (type is "DECIMAL" or "NUMERIC") return precision is null ? dataType : $"{dataType}({N(precision.Value)},{N(scale ?? 0)})";
        if (type == "DECFLOAT") return precision is null ? dataType : $"{dataType}({N(precision.Value)})";
        if (type == "TIMESTAMP") return scale is null ? dataType : $"{dataType}({N(scale.Value)})";
        if (WithLength.Contains(dataType) && length is not null) return $"{dataType}({N(length.Value)})";
        return dataType;
    }
}
