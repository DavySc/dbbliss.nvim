namespace Dbbliss.Backend.Catalog;

/// <summary>
/// Splits a name as typed in SQL (db.schema.table, "My Table", [dbo].[T]) into its parts, unquoted.
/// Quotes keep dots and spaces inside a part; a doubled closing quote is a literal quote.
/// </summary>
public static class ObjectNames
{
    public static IReadOnlyList<string> Parse(string text, bool brackets)
    {
        var parts = new List<string>();
        var i = 0;
        text = text.Trim();
        if (text.Length == 0) throw new FormatException("The name is empty.");
        while (true)
        {
            if (i >= text.Length) throw new FormatException($"The name \"{text}\" ends with a dot.");
            var c = text[i];
            string part;
            if (c == '"' || (brackets && c == '['))
            {
                var close = c == '[' ? ']' : '"';
                var sb = new System.Text.StringBuilder();
                i++;
                while (true)
                {
                    if (i >= text.Length) throw new FormatException($"The quoted name in \"{text}\" is not closed.");
                    if (text[i] == close)
                    {
                        if (i + 1 < text.Length && text[i + 1] == close)
                        {
                            sb.Append(close);
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    sb.Append(text[i++]);
                }
                part = sb.ToString();
            }
            else
            {
                var start = i;
                while (i < text.Length && text[i] != '.') i++;
                part = text[start..i].Trim();
                if (part.Length == 0) throw new FormatException($"The name \"{text}\" has an empty part.");
            }
            if (part.Length == 0) throw new FormatException($"The name \"{text}\" has an empty quoted part.");
            parts.Add(part);
            if (i >= text.Length) return parts;
            if (text[i] != '.') throw new FormatException($"Unexpected \"{text[i]}\" in the name \"{text}\".");
            i++;
        }
    }

    public static string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public static string QuoteBracket(string name) => "[" + name.Replace("]", "]]") + "]";
}
