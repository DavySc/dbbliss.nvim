using System.Globalization;
using System.Text;
using Dbbliss.Backend.Engines;
using static Dbbliss.Backend.Catalog.CatalogHelpers;

namespace Dbbliss.Backend.Catalog;

/// <summary>
/// DB2 for i catalog queries over the QSYS2 views. UNVERIFIED: written from IBM's documentation and
/// DBeaver's DB2 for i plugin, never run on an IBM i (decision 77, docs/db2i-checklist.md). Reads
/// <see cref="CatalogHelpers"/>-style, by column position, so a select list and its reader change together.
/// DB2 for i has no database level below the system: the system (CURRENT_SERVER) is shown as the one database.
/// A table script is built from catalog rows rather than QSYS2.GENERATE_SQL, whose shape is not verified.
/// </summary>
public sealed class Db2iCatalog : ICatalog
{
    private static readonly (string Name, string Kind)[] Folders =
        [("Tables", "table"), ("Views", "view"), ("Functions", "function"), ("Procedures", "procedure")];

    // IBM-supplied libraries: hidden in the tree and left out of completion unless asked for.
    private static readonly string[] SystemSchemas =
        ["QSYS", "QSYS2", "SYSIBM", "SYSIBMADM", "SYSPROC", "SYSTOOLS", "SYSCAT", "SYSSTAT", "QTEMP", "QGPL", "QUSRSYS", "SQLJ"];

    private static string SystemList => string.Join(", ", SystemSchemas.Select(s => "'" + s + "'"));

    public Task PrepareAsync(IEngineSession session, CancellationToken ct) => Task.CompletedTask;

    private static async Task<string> ServerAsync(IEngineSession s, CancellationToken ct) =>
        Str((await First(s, "SELECT CURRENT_SERVER FROM SYSIBM.SYSDUMMY1", null, ct)).Rows[0][0])!;

    private static async Task RequireServerAsync(IEngineSession s, string? database, CancellationToken ct)
    {
        if (database is null) return;
        var server = await ServerAsync(s, ct);
        if (!string.Equals(database, server, StringComparison.OrdinalIgnoreCase))
        {
            throw new CatalogException($"\"{database}\" is not the system this connection is to ({server}). DB2 for i has one database per connection.");
        }
    }

    // Tree ----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CatalogNode>> ChildrenAsync(IEngineSession s, CatalogPath path, CancellationToken ct)
    {
        if (path.Database is null) return [new CatalogNode(await ServerAsync(s, ct), "database", true)];
        await RequireServerAsync(s, path.Database, ct);
        if (path.Schema is null)
        {
            var t = await First(s, "SELECT SCHEMA_NAME FROM QSYS2.SYSSCHEMAS ORDER BY SCHEMA_NAME", null, ct);
            return t.Rows.Select(r => new CatalogNode(Str(r[0])!, "schema", true, System: SystemSchemas.Contains(Str(r[0])!))).ToList();
        }
        if (path.Folder is null) return Folders.Select(f => new CatalogNode(f.Name, "folder", true, Folder: f.Kind)).ToList();
        var p = new Dictionary<string, object?> { ["schema"] = path.Schema };
        switch (path.Folder)
        {
            case "table":
            case "view":
            {
                var types = path.Folder == "table" ? "('T', 'P', 'M')" : "('V', 'L')";
                var t = await First(s, $"""
                    SELECT TABLE_NAME, TABLE_TYPE FROM QSYS2.SYSTABLES
                    WHERE TABLE_SCHEMA = @schema AND TABLE_TYPE IN {types}
                    ORDER BY TABLE_NAME
                    """, p, ct);
                return t.Rows.Select(r => new CatalogNode(Str(r[0])!, path.Folder, false, Detail: Str(r[1]) switch
                {
                    "P" => "physical file", "L" => "logical file", "M" => "materialized query table", _ => null,
                })).ToList();
            }
            case "function":
            case "procedure":
            {
                var type = path.Folder == "function" ? "FUNCTION" : "PROCEDURE";
                var t = await First(s, $"""
                    SELECT ROUTINE_NAME, SPECIFIC_NAME FROM QSYS2.SYSROUTINES
                    WHERE ROUTINE_SCHEMA = @schema AND ROUTINE_TYPE = '{type}'
                    ORDER BY ROUTINE_NAME, SPECIFIC_NAME
                    """, p, ct);
                return t.Rows.Select(r => new CatalogNode(Str(r[0])!, path.Folder, false, Identity: Str(r[1]), Detail: Str(r[1]))).ToList();
            }
            default:
                throw new CatalogException($"Unknown folder {path.Folder}.");
        }
    }

    // Resolving a name ----------------------------------------------------------------------------

    private sealed record Target(string Kind, string Schema, string Name, string? Specific)
    {
        public string Qualified => Db2iSql.Ident(Schema) + "." + Db2iSql.Ident(Name);
    }

    /// <summary>The names to try: as written, then folded to upper case (unquoted names fold; whether it was quoted is not known here).</summary>
    private static IEnumerable<(string? Schema, string Name)> Candidates(string? schema, string name)
    {
        yield return (schema, name);
        var upper = (schema?.ToUpperInvariant(), name.ToUpperInvariant());
        if (upper.Item1 != schema || upper.Item2 != name) yield return upper;
    }

    private async Task<Target> ResolveAsync(IEngineSession s, ObjectRequest req, CancellationToken ct)
    {
        string? schema = req.Schema, name = req.Name, kind = req.Kind, identity = req.Identity;
        var exact = req.Typed is null;
        if (req.Typed is not null)
        {
            IReadOnlyList<string> parts;
            try
            {
                parts = ObjectNames.Parse(req.Typed, brackets: false);
            }
            catch (FormatException ex)
            {
                throw new CatalogException(ex.Message);
            }
            if (parts.Count > 3) throw new CatalogException($"\"{req.Typed}\" has too many parts.");
            if (parts.Count == 3) await RequireServerAsync(s, parts[0], ct);
            name = parts[^1];
            schema = parts.Count >= 2 ? parts[^2] : null;
            kind = null;
            identity = null;
        }
        else
        {
            await RequireServerAsync(s, req.Database, ct);
        }
        if (name is null) throw new CatalogException("No object name.");

        // From the tree the object is exact: no lookup, a missing one shows when it is read.
        if (exact && schema is not null && kind is "table" or "view" or "function" or "procedure")
        {
            return new Target(kind!, schema, name, identity);
        }

        foreach (var (sch, nm) in Candidates(schema, name))
        {
            var p = new Dictionary<string, object?> { ["name"] = nm };
            if (sch is not null) p["schema"] = sch;
            var where = sch is null ? "TABLE_SCHEMA = CURRENT SCHEMA" : "TABLE_SCHEMA = @schema";
            var t = await First(s, $"""
                SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM QSYS2.SYSTABLES
                WHERE {where} AND (TABLE_NAME = @name OR SYSTEM_TABLE_NAME = @name)
                """, p, ct);
            if (t.Rows.Count > 0)
            {
                var r = t.Rows[0];
                var type = Str(r[2]);
                var found = type switch
                {
                    "T" or "P" or "M" => "table",
                    "V" or "L" => "view",
                    _ => throw new CatalogException($"{Str(r[0])}.{Str(r[1])} is an alias or another kind of table object, which dbbliss does not show yet."),
                };
                return new Target(found, Str(r[0])!, Str(r[1])!, null);
            }
        }
        foreach (var (sch, nm) in Candidates(schema, name))
        {
            var p = new Dictionary<string, object?> { ["name"] = nm };
            if (sch is not null) p["schema"] = sch;
            var where = sch is null ? "ROUTINE_SCHEMA = CURRENT SCHEMA" : "ROUTINE_SCHEMA = @schema";
            var t = await First(s, $"""
                SELECT ROUTINE_SCHEMA, ROUTINE_NAME, SPECIFIC_NAME, ROUTINE_TYPE FROM QSYS2.SYSROUTINES
                WHERE {where} AND ROUTINE_NAME = @name
                ORDER BY SPECIFIC_NAME
                """, p, ct);
            if (t.Rows.Count > 1)
            {
                var overloads = string.Join("; ", t.Rows.Select(r => $"{Str(r[1])} (specific name {Str(r[2])})"));
                throw new CatalogException($"Several routines are named {nm}: {overloads}. Open the one you want from the schema tree.");
            }
            if (t.Rows.Count == 1)
            {
                var r = t.Rows[0];
                return new Target(Str(r[3]) == "PROCEDURE" ? "procedure" : "function", Str(r[0])!, Str(r[1])!, Str(r[2]));
            }
        }
        var server = await ServerAsync(s, ct);
        throw new CatalogException(
            $"Nothing named {(schema is null ? name : schema + "." + name)} on {server}. Qualify it with its schema, or refresh if it was just created.");
    }

    // Columns and definitions ----------------------------------------------------------------------

    private sealed record Column(long Ordinal, string Name, string Type, bool Nullable, string? Default, string? Identity);

    private static long? OptLong(object? v) => v is null ? null : Long(v);

    // HAS_DEFAULT in SYSCOLUMNS2 (as vscode-db2i reads it): Y = has a default, N = none, anything else = a generated column.
    private static readonly Dictionary<string, string> GeneratedKinds = new()
    {
        ["A"] = "ROWID generated always", ["D"] = "ROWID generated by default",
        ["E"] = "row change timestamp generated always", ["F"] = "row change timestamp generated by default",
        ["Q"] = "row begin", ["R"] = "row end", ["X"] = "transaction start ID",
        ["a"] = "generated expression using a special register", ["c"] = "generated expression using a global variable",
        ["d"] = "generated expression using DATA CHANGE OPERATION",
    };

    private static async Task<List<Column>> ReadColumnsAsync(IEngineSession s, Target t, CancellationToken ct)
    {
        var r = await First(s, """
            SELECT ORDINAL_POSITION, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE,
                   IS_NULLABLE, COLUMN_DEFAULT, HAS_DEFAULT, IS_IDENTITY
            FROM QSYS2.SYSCOLUMNS2
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @name
            ORDER BY ORDINAL_POSITION
            """, new Dictionary<string, object?> { ["schema"] = t.Schema, ["name"] = t.Name }, ct);
        if (r.Rows.Count == 0)
        {
            throw new CatalogException($"{t.Schema}.{t.Name} has no columns here: it does not exist, or you may not see it. Refresh if it was just created.");
        }
        return r.Rows.Select(x =>
        {
            var has = (Str(x[8]) ?? "").Trim();
            var identity = has == "I" ? "ALWAYS" : has == "J" ? "BY DEFAULT" : Str(x[9]) == "YES" ? "BY DEFAULT" : null;
            if (identity is null && has is not ("" or "Y" or "N"))
            {
                // Not written as a plain default: scripting it would drop what makes it generated.
                throw new CatalogException(
                    $"Column {Str(x[1])} of {t.Schema}.{t.Name} is a {(GeneratedKinds.TryGetValue(has, out var kind) ? kind : "generated value")} column, which the script cannot write yet.");
            }
            return new Column(Long(x[0]), Str(x[1])!, Db2iSql.TypeName(Str(x[2])!, OptLong(x[3]), OptLong(x[4]), OptLong(x[5])),
                Str(x[6]) != "N", has == "Y" ? Str(x[7]) : null, identity);
        }).ToList();
    }

    private sealed record Constraint(string Name, string Type, List<string> Columns, string? Check);

    private static readonly string[] ConstraintOrder = ["PRIMARY KEY", "UNIQUE", "CHECK", "FOREIGN KEY"];

    private static async Task<List<Constraint>> ReadConstraintsAsync(IEngineSession s, Target t, CancellationToken ct)
    {
        var p = new Dictionary<string, object?> { ["schema"] = t.Schema, ["name"] = t.Name };
        var list = (await First(s, """
            SELECT CONSTRAINT_NAME, CONSTRAINT_TYPE FROM QSYS2.SYSCST
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @name
            """, p, ct)).Rows.Select(r => new Constraint(Str(r[0])!, Str(r[1])!, [], null)).ToList();
        var keys = await First(s, """
            SELECT CONSTRAINT_NAME, COLUMN_NAME, COLUMN_POSITION FROM QSYS2.SYSKEYCST
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @name
            ORDER BY CONSTRAINT_NAME, COLUMN_POSITION
            """, p, ct);
        var checks = await First(s, """
            SELECT CONSTRAINT_NAME, CHECK_CLAUSE FROM QSYS2.CHECK_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = @schema AND CONSTRAINT_NAME IN
                (SELECT CONSTRAINT_NAME FROM QSYS2.SYSCST WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @name AND CONSTRAINT_TYPE = 'CHECK')
            """, p, ct);
        return list
            .Select(c => c with
            {
                Columns = keys.Rows.Where(k => Str(k[0]) == c.Name).Select(k => Str(k[1])!).ToList(),
                Check = checks.Rows.Where(k => Str(k[0]) == c.Name).Select(k => Str(k[1])).FirstOrDefault(),
            })
            .OrderBy(c => Array.IndexOf(ConstraintOrder, c.Type) is var i and >= 0 ? i : ConstraintOrder.Length)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record ForeignKey(string Name, List<string> Columns, string RefSchema, string RefTable, List<string> RefColumns, string UpdateRule, string DeleteRule);

    private static async Task<List<ForeignKey>> ReadForeignKeysAsync(IEngineSession s, Target t, IReadOnlyList<Constraint> constraints, CancellationToken ct)
    {
        var r = await First(s, """
            SELECT c.CONSTRAINT_NAME, r.UPDATE_RULE, r.DELETE_RULE, u.TABLE_SCHEMA, u.TABLE_NAME, r.UNIQUE_CONSTRAINT_SCHEMA, r.UNIQUE_CONSTRAINT_NAME
            FROM QSYS2.SYSCST c
            JOIN QSYS2.SYSREFCST r ON r.CONSTRAINT_SCHEMA = c.CONSTRAINT_SCHEMA AND r.CONSTRAINT_NAME = c.CONSTRAINT_NAME
            JOIN QSYS2.SYSCST u ON u.CONSTRAINT_SCHEMA = r.UNIQUE_CONSTRAINT_SCHEMA AND u.CONSTRAINT_NAME = r.UNIQUE_CONSTRAINT_NAME
            WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @name AND c.CONSTRAINT_TYPE = 'FOREIGN KEY'
            ORDER BY c.CONSTRAINT_NAME
            """, new Dictionary<string, object?> { ["schema"] = t.Schema, ["name"] = t.Name }, ct);
        var result = new List<ForeignKey>();
        foreach (var row in r.Rows)
        {
            var name = Str(row[0])!;
            var referenced = await First(s, """
                SELECT COLUMN_NAME FROM QSYS2.SYSKEYCST
                WHERE CONSTRAINT_SCHEMA = @cschema AND CONSTRAINT_NAME = @cname
                ORDER BY COLUMN_POSITION
                """, new Dictionary<string, object?> { ["cschema"] = Str(row[5]), ["cname"] = Str(row[6]) }, ct);
            result.Add(new ForeignKey(name, constraints.FirstOrDefault(c => c.Name == name)?.Columns ?? [], Str(row[3])!, Str(row[4])!,
                referenced.Rows.Select(x => Str(x[0])!).ToList(), Str(row[1]) ?? "NO ACTION", Str(row[2]) ?? "NO ACTION"));
        }
        return result;
    }

    private static string ColumnList(IEnumerable<string> names) => string.Join(", ", names.Select(Db2iSql.Ident));

    // Object info ---------------------------------------------------------------------------------

    public async Task<ObjectInfo> DescribeAsync(IEngineSession s, ObjectRequest req, CancellationToken ct)
    {
        var t = await ResolveAsync(s, req, ct);
        var title = $"{t.Kind} {t.Schema}.{t.Name}{(t.Kind is "function" or "procedure" && t.Specific is not null ? " (" + t.Specific + ")" : "")}";
        var sections = new List<InfoSection>();
        if (t.Kind is "function" or "procedure")
        {
            var p = new Dictionary<string, object?> { ["schema"] = t.Schema, ["specific"] = await SpecificAsync(s, t, ct) };
            var parms = await First(s, """
                SELECT ORDINAL_POSITION, PARAMETER_MODE, PARAMETER_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, ROW_TYPE
                FROM QSYS2.SYSPARMS
                WHERE SPECIFIC_SCHEMA = @schema AND SPECIFIC_NAME = @specific AND ROW_TYPE IN ('P', 'R')
                ORDER BY ORDINAL_POSITION
                """, p, ct);
            sections.Add(new InfoSection("Parameters", ["#", "mode", "name", "type"],
                parms.Rows.Select(r => new object?[]
                {
                    r[0], Str(r[7]) == "R" ? "returns" : r[1], r[2], Db2iSql.TypeName(Str(r[3])!, OptLong(r[4]), OptLong(r[5]), OptLong(r[6])),
                }).ToList()));
            sections.Add(InfoSection.OfText("Definition", await DefinitionAsync(s, t, ct)));
            return new ObjectInfo(title, t.Kind, sections);
        }

        var columns = await ReadColumnsAsync(s, t, ct);
        sections.Add(new InfoSection("Columns", ["#", "name", "type", "nullable", "default", "identity"],
            columns.Select(c => new object?[] { c.Ordinal, c.Name, c.Type, c.Nullable ? "yes" : "no", c.Default, c.Identity }).ToList()));
        if (t.Kind == "view")
        {
            sections.Add(InfoSection.OfText("Definition", await DefinitionAsync(s, t, ct)));
            return new ObjectInfo(title, t.Kind, sections);
        }

        var indexes = await First(s, """
            SELECT i.INDEX_NAME, i.IS_UNIQUE, k.COLUMN_NAME, k.ORDERING, k.ORDINAL_POSITION
            FROM QSYS2.SYSINDEXES i
            JOIN QSYS2.SYSKEYS k ON k.INDEX_SCHEMA = i.INDEX_SCHEMA AND k.INDEX_NAME = i.INDEX_NAME
            WHERE i.TABLE_SCHEMA = @schema AND i.TABLE_NAME = @name
            ORDER BY i.INDEX_NAME, k.ORDINAL_POSITION
            """, new Dictionary<string, object?> { ["schema"] = t.Schema, ["name"] = t.Name }, ct);
        var indexRows = new List<object?[]>();
        foreach (var index in indexes.Rows.GroupBy(r => Str(r[0])!))
        {
            var first = index.First();
            indexRows.Add([index.Key, Str(first[1]) switch { "U" => "yes", "V" => "yes (nulls allowed)", _ => "no" },
                string.Join(", ", index.Select(r => Str(r[2]) + (Str(r[3]) == "D" ? " DESC" : "")))]);
        }
        sections.Add(new InfoSection("Indexes", ["name", "unique", "columns"], indexRows));

        var constraints = await ReadConstraintsAsync(s, t, ct);
        sections.Add(new InfoSection("Constraints", ["name", "type", "columns", "check"],
            constraints.Select(c => new object?[] { c.Name, c.Type, string.Join(", ", c.Columns), c.Check }).ToList()));
        var fks = await ReadForeignKeysAsync(s, t, constraints, ct);
        sections.Add(new InfoSection("Foreign keys", ["name", "columns", "references", "referenced columns", "on update", "on delete"],
            fks.Select(f => new object?[] { f.Name, string.Join(", ", f.Columns), $"{f.RefSchema}.{f.RefTable}", string.Join(", ", f.RefColumns), f.UpdateRule, f.DeleteRule }).ToList()));
        return new ObjectInfo(title, t.Kind, sections);
    }

    private static Task<string> SpecificAsync(IEngineSession s, Target t, CancellationToken ct)
    {
        if (t.Specific is not null) return Task.FromResult(t.Specific);
        // From the tree without an identity: the routine must have one specific name.
        return SingleSpecificAsync(s, t, ct);
    }

    private static async Task<string> SingleSpecificAsync(IEngineSession s, Target t, CancellationToken ct)
    {
        var r = await First(s, """
            SELECT SPECIFIC_NAME FROM QSYS2.SYSROUTINES
            WHERE ROUTINE_SCHEMA = @schema AND ROUTINE_NAME = @name
            ORDER BY SPECIFIC_NAME
            """, new Dictionary<string, object?> { ["schema"] = t.Schema, ["name"] = t.Name }, ct);
        if (r.Rows.Count == 0) throw new CatalogException($"{t.Schema}.{t.Name} does not exist. Refresh if it was just created.");
        if (r.Rows.Count > 1)
        {
            throw new CatalogException($"Several routines are named {t.Name}: specific names {string.Join(", ", r.Rows.Select(x => Str(x[0])))}. Open the one you want from the schema tree.");
        }
        return Str(r.Rows[0][0])!;
    }

    /// <summary>The stored definition text, as the catalog keeps it, trimmed.</summary>
    private static async Task<string> DefinitionAsync(IEngineSession s, Target t, CancellationToken ct)
    {
        QueryTable r;
        if (t.Kind == "view")
        {
            r = await First(s, "SELECT VIEW_DEFINITION FROM QSYS2.SYSVIEWS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @name",
                new Dictionary<string, object?> { ["schema"] = t.Schema, ["name"] = t.Name }, ct);
        }
        else
        {
            r = await First(s, "SELECT ROUTINE_DEFINITION FROM QSYS2.SYSROUTINES WHERE ROUTINE_SCHEMA = @schema AND SPECIFIC_NAME = @specific",
                new Dictionary<string, object?> { ["schema"] = t.Schema, ["specific"] = await SpecificAsync(s, t, ct) }, ct);
        }
        var text = r.Rows.Count > 0 ? Str(r.Rows[0][0])?.Trim() : null;
        if (string.IsNullOrEmpty(text))
        {
            throw new CatalogException($"{t.Schema}.{t.Name} has no stored definition (an external routine, a system object, or it does not exist).");
        }
        return text;
    }

    // Scripts -------------------------------------------------------------------------------------

    public async Task<string> ScriptAsync(IEngineSession s, ObjectRequest req, CancellationToken ct)
    {
        var t = await ResolveAsync(s, req, ct);
        switch (t.Kind)
        {
            case "view":
            {
                var def = await DefinitionAsync(s, t, ct);
                // The catalog may keep the whole statement or only the query: add the head only when it is missing.
                return (def.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) ? def : $"CREATE VIEW {t.Qualified} AS\n{def}") + "\n";
            }
            case "function":
            case "procedure":
                return await DefinitionAsync(s, t, ct) + "\n";
            default:
                return await TableScriptAsync(s, t, ct);
        }
    }

    private static async Task<string> TableScriptAsync(IEngineSession s, Target t, CancellationToken ct)
    {
        var columns = await ReadColumnsAsync(s, t, ct);
        var constraints = await ReadConstraintsAsync(s, t, ct);
        var fks = await ReadForeignKeysAsync(s, t, constraints, ct);
        var items = new List<string>();
        foreach (var c in columns)
        {
            var sb = new StringBuilder(Db2iSql.Ident(c.Name)).Append(' ').Append(c.Type);
            if (c.Identity is not null) sb.Append(" GENERATED ").Append(c.Identity).Append(" AS IDENTITY");
            if (!c.Nullable) sb.Append(" NOT NULL");
            if (c.Identity is null && c.Default is { Length: > 0 }) sb.Append(" DEFAULT ").Append(c.Default);
            items.Add(sb.ToString());
        }
        foreach (var c in constraints)
        {
            var name = "CONSTRAINT " + Db2iSql.Ident(c.Name) + " ";
            switch (c.Type)
            {
                case "PRIMARY KEY":
                case "UNIQUE":
                    items.Add($"{name}{c.Type} ({ColumnList(c.Columns)})");
                    break;
                case "CHECK" when c.Check is { Length: > 0 }:
                    items.Add($"{name}CHECK ({c.Check})");
                    break;
                case "FOREIGN KEY":
                {
                    var fk = fks.FirstOrDefault(f => f.Name == c.Name)
                        ?? throw new CatalogException($"The foreign key {c.Name} on {t.Qualified} has no referenced table in the catalog.");
                    var text = $"{name}FOREIGN KEY ({ColumnList(fk.Columns)}) REFERENCES {Db2iSql.Ident(fk.RefSchema)}.{Db2iSql.Ident(fk.RefTable)} ({ColumnList(fk.RefColumns)})";
                    if (fk.DeleteRule != "NO ACTION") text += " ON DELETE " + fk.DeleteRule;
                    if (fk.UpdateRule != "NO ACTION") text += " ON UPDATE " + fk.UpdateRule;
                    items.Add(text);
                    break;
                }
                default:
                    throw new CatalogException($"The constraint {c.Name} on {t.Qualified} is of type {c.Type}, which the script cannot write.");
            }
        }
        return $"CREATE TABLE {t.Qualified} (\n    {string.Join(",\n    ", items)}\n);\n";
    }

    // Completion ----------------------------------------------------------------------------------

    public async Task<CatalogNames> NamesAsync(IEngineSession s, string? database, bool includeSystem, int limit, CancellationToken ct)
    {
        await RequireServerAsync(s, database, ct);
        // The limit is a checked integer written into the text: FETCH FIRST takes no marker on every release.
        var fetch = (limit + 1).ToString(CultureInfo.InvariantCulture);
        var tableFilter = includeSystem ? "" : $" AND TABLE_SCHEMA NOT IN ({SystemList})";
        var routineFilter = includeSystem ? "" : $" AND ROUTINE_SCHEMA NOT IN ({SystemList})";
        var t = await First(s, $"""
            SELECT x.S, x.N, x.K FROM (
                SELECT TABLE_SCHEMA AS S, TABLE_NAME AS N, CASE WHEN TABLE_TYPE IN ('V', 'L') THEN 'view' ELSE 'table' END AS K
                FROM QSYS2.SYSTABLES
                WHERE TABLE_TYPE IN ('T', 'P', 'M', 'V', 'L'){tableFilter}
                UNION ALL
                SELECT ROUTINE_SCHEMA, ROUTINE_NAME, CASE ROUTINE_TYPE WHEN 'PROCEDURE' THEN 'procedure' ELSE 'function' END
                FROM QSYS2.SYSROUTINES
                WHERE 1 = 1{routineFilter}
            ) x
            ORDER BY x.S, x.N, x.K
            FETCH FIRST {fetch} ROWS ONLY
            """, null, ct);
        var items = t.Rows.Take(limit).Select(r => new NameEntry(Str(r[0])!, Str(r[1])!, Str(r[2])!)).ToList();
        return new CatalogNames(items, t.Rows.Count > limit);
    }

    public async Task<TableColumns> ColumnsAsync(IEngineSession s, ObjectRequest req, CancellationToken ct)
    {
        var t = await ResolveAsync(s, req, ct);
        if (t.Kind is not ("table" or "view"))
        {
            throw new CatalogException($"{t.Schema}.{t.Name} is a {t.Kind}, which has no columns to complete.");
        }
        var columns = await ReadColumnsAsync(s, t, ct);
        return new TableColumns(t.Schema, t.Name, columns.Select(c => new ColumnEntry(c.Name, c.Type)).ToList());
    }
}
