using System.Text.RegularExpressions;

namespace Dbbliss.ProtocolTests;

/// <summary>Rules on the catalog queries that no behavioural test can see.</summary>
public static partial class CatalogSqlRules
{
    [GeneratedRegex(@"\bDISTINCT\b|\bGROUP\s+BY\b", RegexOptions.IgnoreCase)]
    private static partial Regex Dedup();

    /// <summary>
    /// Catalog and session SQL removes duplicates with correct joins, EXISTS or window functions, never with
    /// DISTINCT or GROUP BY (a duplicate is a wrong join, and DISTINCT hides it). Comments may
    /// mention the words; code and SQL text may not.
    /// </summary>
    public static void NoDedupKeywords()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend", "src", "Dbbliss.Backend", "Catalog"))) dir = dir.Parent;
        if (dir is null) throw new TestFailure("cannot find backend/src/Dbbliss.Backend/Catalog above " + AppContext.BaseDirectory);
        var src = Path.Combine(dir.FullName, "backend", "src", "Dbbliss.Backend");
        var files = Directory.GetFiles(Path.Combine(src, "Catalog"), "*.cs").Concat(Directory.GetFiles(Path.Combine(src, "Sessions"), "*.cs")).ToArray();
        if (files.Length < 6) throw new TestFailure($"expected the catalog and session sources, found {files.Length} file(s)");
        var found = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (Dedup().IsMatch(lines[i])) found.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        if (found.Count > 0) throw new TestFailure("DISTINCT or GROUP BY in catalog SQL:\n  " + string.Join("\n  ", found));
    }
}
