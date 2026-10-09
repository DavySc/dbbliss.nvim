namespace Dbbliss.Backend.Catalog;

/// <summary>
/// Splits a name as typed in SQL (db.schema.table, "My Table", [dbo].[T]) into its parts, unquoted.
/// Quotes keep dots and spaces inside a part; a doubled closing quote is a literal quote.
/// </summary>
public static class ObjectNames
{
    public static IReadOnlyList<string> Parse(string text, bool brackets)
    {
        text = text.Trim();
        if (text.Length == 0) throw new FormatException("The name is empty.");
        var parts = new List<string>();
        var i = 0;
        while (true)
        {
            if (i >= text.Length) throw new FormatException($"The name \"{text}\" ends with a dot.");
            var c = text[i];
            var quoted = c == '"' || (brackets && c == '[');
            var part = quoted ? ReadQuoted(text, ref i, c == '[' ? ']' : '"') : ReadPlain(text, ref i);
            if (part.Length == 0) throw new FormatException($"The name \"{text}\" has an empty {(quoted ? "quoted " : "")}part.");
            parts.Add(part);
            if (i >= text.Length) return parts;
            if (text[i] != '.') throw new FormatException($"Unexpected \"{text[i]}\" in the name \"{text}\".");
            i++;
        }
    }

    /// <summary>From the opening quote at <paramref name="i"/>; leaves <paramref name="i"/> after the closing one.</summary>
    private static string ReadQuoted(string text, ref int i, char close)
    {
        var sb = new System.Text.StringBuilder();
        i++;
        for (; i < text.Length; i++)
        {
            if (text[i] != close)
            {
                sb.Append(text[i]);
            }
            else if (i + 1 < text.Length && text[i + 1] == close)
            {
                sb.Append(close);
                i++;
            }
            else
            {
                i++;
                return sb.ToString();
            }
        }
        throw new FormatException($"The quoted name in \"{text}\" is not closed.");
    }

    private static string ReadPlain(string text, ref int i)
    {
        var start = i;
        var dot = text.IndexOf('.', i);
        i = dot < 0 ? text.Length : dot;
        return text[start..i].Trim();
    }

    public static string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public static string QuoteBracket(string name) => "[" + name.Replace("]", "]]") + "]";
}
