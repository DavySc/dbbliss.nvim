using System.Text;
using Dbbliss.Backend.Engines;
using static Dbbliss.Backend.Catalog.CatalogHelpers;

namespace Dbbliss.Backend.Catalog;

/// <summary>
/// SQL Server catalog queries over the sys.* views, plus the raw sp_help output. Any database of the
/// server can be browsed from one connection: a query for database X runs through
/// <c>EXEC [X].sys.sp_executesql</c>, which executes it in X's context. Objects are found with
/// OBJECT_ID, so the user's default schema applies to an unqualified name as it does in a query.
/// </summary>
public sealed class SqlServerCatalog : ICatalog
{
    private static readonly (string Name, string Kind)[] Folders =
        [("Tables", "table"), ("Views", "view"), ("Functions", "function"), ("Procedures", "procedure")];

    public Task PrepareAsync(IEngineSession session, CancellationToken ct) => Task.CompletedTask;

    // The type of a column or parameter as it is written in a declaration. {t} is the sys.types alias,
    // {c} the sys.columns / sys.parameters alias.
    private static string TypeSql(string t, string c) => $"""
        CASE WHEN {t}.is_user_defined = 1 THEN QUOTENAME(SCHEMA_NAME({t}.schema_id)) + '.' + QUOTENAME({t}.name) ELSE {t}.name END
        + CASE WHEN {t}.is_user_defined = 1 THEN ''
               WHEN {t}.name IN ('varchar', 'char', 'varbinary', 'binary') THEN '(' + CASE WHEN {c}.max_length = -1 THEN 'max' ELSE CAST({c}.max_length AS varchar(10)) END + ')'
               WHEN {t}.name IN ('nvarchar', 'nchar') THEN '(' + CASE WHEN {c}.max_length = -1 THEN 'max' ELSE CAST({c}.max_length / 2 AS varchar(10)) END + ')'
               WHEN {t}.name IN ('decimal', 'numeric') THEN '(' + CAST({c}.precision AS varchar(10)) + ',' + CAST({c}.scale AS varchar(10)) + ')'
               WHEN {t}.name IN ('datetime2', 'datetimeoffset', 'time') THEN '(' + CAST({c}.scale AS varchar(10)) + ')'
               ELSE '' END
        """;

    // Tree ----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CatalogNode>> ChildrenAsync(IEngineSession s, CatalogPath path, CancellationToken ct)
    {
        if (path.Database is null)
        {
            var t = await First(s, """
                SELECT d.name, CAST(CASE WHEN d.database_id <= 4 THEN 1 ELSE 0 END AS bit) AS system
                FROM sys.databases d
                WHERE d.state = 0 AND HAS_DBACCESS(d.name) = 1
                ORDER BY d.name
                """, null, ct);
            return t.Rows.Select(r => new CatalogNode(Str(r[0])!, "database", true, System: Bool(r[1]))).ToList();
        }
        if (path.Schema is null)
        {
            var t = await First(s, Wrap(path.Database, """
                SELECT sc.name, CAST(CASE WHEN sc.name IN ('sys', 'INFORMATION_SCHEMA', 'guest') OR sc.name LIKE 'db[_]%' THEN 1 ELSE 0 END AS bit) AS system
                FROM sys.schemas sc
                ORDER BY sc.name
                """, out var p0), p0, ct);
            return t.Rows.Select(r => new CatalogNode(Str(r[0])!, "schema", true, System: Bool(r[1]))).ToList();
        }
        if (path.Folder is null)
        {
            return Folders.Select(f => new CatalogNode(f.Name, "folder", true, Folder: f.Kind)).ToList();
        }
        var objects = path.Folder switch
        {
            "table" => "SELECT o.name FROM sys.tables o WHERE o.schema_id = SCHEMA_ID(@schema) AND o.is_ms_shipped = 0 ORDER BY o.name",
            "view" => "SELECT o.name FROM sys.views o WHERE o.schema_id = SCHEMA_ID(@schema) AND o.is_ms_shipped = 0 ORDER BY o.name",
            "procedure" => "SELECT o.name FROM sys.procedures o WHERE o.schema_id = SCHEMA_ID(@schema) AND o.is_ms_shipped = 0 ORDER BY o.name",
            "function" => "SELECT o.name FROM sys.objects o WHERE o.schema_id = SCHEMA_ID(@schema) AND o.type IN ('FN', 'IF', 'TF', 'FS', 'FT') AND o.is_ms_shipped = 0 ORDER BY o.name",
            _ => throw new CatalogException($"Unknown folder {path.Folder}."),
        };
        var p = new Dictionary<string, object?> { ["schema"] = path.Schema };
        var rows = await First(s, Wrap(path.Database, objects, out var wrapped, p), wrapped, ct);
        return rows.Rows.Select(r => new CatalogNode(Str(r[0])!, path.Folder, false)).ToList();
    }

    // Running a query in another database's context ------------------------------------------------

    /// <summary>
    /// The text to send for <paramref name="statement"/> in the context of <paramref name="database"/>
    /// (null: the session's own), and the parameters to send with it. The database name is bracket-quoted.
    /// </summary>
    private static string Wrap(string? database, string statement, out Dictionary<string, object?>? parameters, Dictionary<string, object?>? original = null)
    {
        if (database is null)
        {
            parameters = original;
            return statement;
        }
        var all = new Dictionary<string, object?>();
        var sql = new StringBuilder($"EXEC {ObjectNames.QuoteBracket(database)}.sys.sp_executesql @stmt = @__stmt");
        all["__stmt"] = statement;
        if (original is { Count: > 0 })
        {
            var declarations = string.Join(", ", original.Select(kv => $"@{kv.Key} {SqlType(kv.Value)}"));
            all["__decl"] = declarations;
            sql.Append(", @params = @__decl");
            foreach (var (name, value) in original)
            {
                all[name] = value;
                sql.Append($", @{name} = @{name}");
            }
        }
        parameters = all;
        return sql.ToString();
    }

    private static string SqlType(object? value) => value switch
    {
        int => "int",
        long => "bigint",
        _ => "nvarchar(4000)",
    };

    private static async Task<IReadOnlyList<QueryTable>> Run(IEngineSession s, string? database, string statement, Dictionary<string, object?>? p, CancellationToken ct)
    {
        var sql = Wrap(database, statement, out var wrapped, p);
        return await s.QueryAsync(sql, wrapped, ct);
    }

    private static async Task<QueryTable> RunFirst(IEngineSession s, string? database, string statement, Dictionary<string, object?>? p, CancellationToken ct)
    {
        var tables = await Run(s, database, statement, p, ct);
        return tables.Count > 0 ? tables[0] : new QueryTable([], []);
    }

    // Resolving a name ----------------------------------------------------------------------------

    private sealed record Target(string? Database, long Id, string Schema, string Name, string Type, string Kind)
    {
        public string Qualified => $"{ObjectNames.QuoteBracket(Schema)}.{ObjectNames.QuoteBracket(Name)}";
    }

    private static async Task<Target> ResolveAsync(IEngineSession s, ObjectRequest req, CancellationToken ct)
    {
        string? database = req.Database, schema = req.Schema, name = req.Name;
        if (req.Typed is not null)
        {
            IReadOnlyList<string> parts;
            try
            {
                parts = ObjectNames.Parse(req.Typed, brackets: true);
            }
            catch (FormatException ex)
            {
                throw new CatalogException(ex.Message);
            }
            if (parts.Count > 3) throw new CatalogException($"\"{req.Typed}\" has too many parts (server names are not supported).");
            name = parts[^1];
            schema = parts.Count >= 2 ? parts[^2] : null;
            database = parts.Count == 3 ? parts[0] : null;
        }
        if (name is null) throw new CatalogException("No object name.");
        var qualified = schema is null ? ObjectNames.QuoteBracket(name) : $"{ObjectNames.QuoteBracket(schema)}.{ObjectNames.QuoteBracket(name)}";
        var t = await RunFirst(s, database, """
            SELECT o.object_id, SCHEMA_NAME(o.schema_id) AS schema_name, o.name, o.type
            FROM sys.objects o
            WHERE o.object_id = OBJECT_ID(@name)
            """, new Dictionary<string, object?> { ["name"] = qualified }, ct);
        if (t.Rows.Count == 0)
        {
            var current = database ?? Str((await First(s, "SELECT DB_NAME()", null, ct)).Rows[0][0]);
            throw new CatalogException(
                $"Nothing named {(schema is null ? name : schema + "." + name)} in database {current}. Qualify it with its schema, or refresh if it was just created.");
        }
        var r = t.Rows[0];
        var type = Str(r[3])!.Trim();
        return new Target(database, Long(r[0]), Str(r[1])!, Str(r[2])!, type, KindOf(type));
    }

    private static string KindOf(string type) => type switch
    {
        "U" => "table",
        "V" => "view",
        "P" or "PC" => "procedure",
        "FN" or "IF" or "TF" or "FS" or "FT" => "function",
        "TR" or "TA" => "trigger",
        "SN" => "synonym",
        "SO" => "sequence",
        _ => "object",
    };

    // Object info ---------------------------------------------------------------------------------

    public async Task<ObjectInfo> DescribeAsync(IEngineSession s, ObjectRequest req, CancellationToken ct)
    {
        var t = await ResolveAsync(s, req, ct);
        var id = new Dictionary<string, object?> { ["id"] = (int)t.Id };
        var sections = new List<InfoSection>();
        var title = $"{t.Kind} {t.Schema}.{t.Name}";

        sections.Add(Properties("Summary", await RunFirst(s, t.Database, """
            SELECT LOWER(o.type_desc) AS kind, SCHEMA_NAME(o.schema_id) AS [schema], USER_NAME(OBJECTPROPERTY(o.object_id, 'OwnerId')) AS owner,
                   o.create_date AS created, o.modify_date AS modified,
                   (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)) AS row_estimate
            FROM sys.objects o
            WHERE o.object_id = @id
            """, id, ct)));

        if (t.Kind is "table" or "view")
        {
            sections.Add(Table("Columns", await RunFirst(s, t.Database, $"""
                SELECT c.column_id AS [#], c.name, {TypeSql("ty", "c")} AS type, c.is_nullable AS nullable,
                       dc.definition AS [default],
                       CASE WHEN c.is_identity = 1 THEN 'identity(' + CAST(ic.seed_value AS varchar(30)) + ',' + CAST(ic.increment_value AS varchar(30)) + ')' END AS [identity],
                       CASE WHEN c.is_computed = 1 THEN cc.definition + CASE WHEN cc.is_persisted = 1 THEN ' (persisted)' ELSE '' END END AS computed
                FROM sys.columns c
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
                LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
                WHERE c.object_id = @id
                ORDER BY c.column_id
                """, id, ct)));
            sections.Add(new InfoSection("Indexes", ["name", "type", "unique", "primary key", "disabled", "key columns", "included", "filter"],
                (await IndexesAsync(s, t, id, ct)).Select(i => new object?[]
                    { i.Name, i.Type, i.Unique, i.PrimaryKey, i.Disabled, i.Keys, i.Included, i.Filter }).ToList()));
            sections.Add(new InfoSection("Constraints", ["name", "type", "definition"], await ConstraintsAsync(s, t, id, ct)));
            sections.Add(new InfoSection("Foreign keys", ["name", "columns", "references", "on delete", "on update", "disabled"],
                (await ForeignKeysAsync(s, t, id, outgoing: true, ct)).Select(f => new object?[]
                    { f.Name, f.Columns, f.Other, f.OnDelete, f.OnUpdate, f.Disabled }).ToList()));
            sections.Add(new InfoSection("Referenced by", ["name", "table", "columns", "on delete", "on update", "disabled"],
                (await ForeignKeysAsync(s, t, id, outgoing: false, ct)).Select(f => new object?[]
                    { f.Name, f.ChildTable, f.Columns, f.OnDelete, f.OnUpdate, f.Disabled }).ToList()));
            sections.Add(new InfoSection("Triggers", ["name", "type", "events", "disabled"], (await TriggersAsync(s, t, id, ct))
                .Select(g => new object?[] { g.Name, g.Type, g.Events, g.Disabled }).ToList()));
        }
        else if (t.Kind is "procedure" or "function")
        {
            sections.Add(Table("Parameters", await RunFirst(s, t.Database, $"""
                SELECT pa.parameter_id AS [#], pa.name, {TypeSql("ty", "pa")} AS type, pa.is_output AS [output], pa.has_default_value AS has_default
                FROM sys.parameters pa
                JOIN sys.types ty ON ty.user_type_id = pa.user_type_id
                WHERE pa.object_id = @id
                ORDER BY pa.parameter_id
                """, id, ct)));
        }
        if (t.Kind is "view" or "procedure" or "function" or "trigger")
        {
            sections.Add(InfoSection.OfText("Definition", await DefinitionAsync(s, t, id, ct)));
        }
        // The classic output, for anyone who knows it by heart: one section per result set.
        try
        {
            var help = await Run(s, t.Database, "EXEC sp_help @objname", new Dictionary<string, object?> { ["objname"] = t.Qualified }, ct);
            for (var i = 0; i < help.Count; i++) sections.Add(Table($"sp_help ({i + 1})", help[i]));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sections.Add(InfoSection.OfText("sp_help", "sp_help failed: " + ex.Message));
        }
        return new ObjectInfo(title, t.Kind, sections);
    }

    private sealed record IndexInfo(string Name, string Type, bool Unique, bool PrimaryKey, bool UniqueConstraint, bool Disabled, string Keys, string? Included, string? Filter,
        IReadOnlyList<string> KeyColumns, IReadOnlyList<string> IncludedColumns);

    private static async Task<List<IndexInfo>> IndexesAsync(IEngineSession s, Target t, Dictionary<string, object?> id, CancellationToken ct)
    {
        var rows = await RunFirst(s, t.Database, """
            SELECT i.index_id, i.name, i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.is_disabled, i.filter_definition,
                   ic.is_included_column, ic.is_descending_key, c.name AS column_name
            FROM sys.indexes i
            LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            LEFT JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = @id AND i.type > 0
            ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id
            """, id, ct);
        var result = new List<IndexInfo>();
        long? current = null;
        List<string> keys = [], included = [];
        object?[]? first = null;
        void Flush()
        {
            if (first is null) return;
            result.Add(new IndexInfo(Str(first[1])!, Str(first[2])!.Replace('_', ' ').ToLowerInvariant(), Bool(first[3]), Bool(first[4]), Bool(first[5]), Bool(first[6]),
                string.Join(", ", keys), included.Count > 0 ? string.Join(", ", included) : null, Str(first[7]), [.. keys], [.. included]));
        }
        foreach (var r in rows.Rows)
        {
            var indexId = Long(r[0]);
            if (current != indexId)
            {
                Flush();
                current = indexId;
                first = r;
                keys = [];
                included = [];
            }
            if (Str(r[10]) is not { } column) continue;
            if (Bool(r[8])) included.Add(column);
            else keys.Add(Bool(r[9]) ? column + " DESC" : column);
        }
        Flush();
        return result;
    }

    private static async Task<List<object?[]>> ConstraintsAsync(IEngineSession s, Target t, Dictionary<string, object?> id, CancellationToken ct)
    {
        var result = new List<object?[]>();
        var keys = await RunFirst(s, t.Database, """
            SELECT kc.name, kc.type_desc, c.name AS column_name, ic.is_descending_key
            FROM sys.key_constraints kc
            JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE kc.parent_object_id = @id AND ic.is_included_column = 0
            ORDER BY kc.name, ic.key_ordinal
            """, id, ct);
        foreach (var g in keys.Rows.GroupBy(r => Str(r[0])))
        {
            result.Add([g.Key, Str(g.First()[1])!.Replace('_', ' ').ToLowerInvariant(), "(" + string.Join(", ", g.Select(r => Bool(r[3]) ? Str(r[2]) + " DESC" : Str(r[2]))) + ")"]);
        }
        var checks = await RunFirst(s, t.Database, """
            SELECT cc.name, cc.definition, cc.is_disabled
            FROM sys.check_constraints cc
            WHERE cc.parent_object_id = @id
            ORDER BY cc.name
            """, id, ct);
        result.AddRange(checks.Rows.Select(r => new object?[] { Str(r[0]), "check", "CHECK " + Str(r[1]) + (Bool(r[2]) ? " (disabled)" : "") }));
        return result;
    }

    private sealed record ForeignKey(string Name, string Columns, string Other, string Referenced, string OnDelete, string OnUpdate, bool Disabled,
        string ChildTable, IReadOnlyList<string> ChildColumns, string ParentTable, IReadOnlyList<string> ParentColumns);

    private static async Task<List<ForeignKey>> ForeignKeysAsync(IEngineSession s, Target t, Dictionary<string, object?> id, bool outgoing, CancellationToken ct)
    {
        var side = outgoing ? "fk.parent_object_id" : "fk.referenced_object_id";
        var rows = await RunFirst(s, t.Database, $"""
            SELECT fk.object_id, fk.name,
                   QUOTENAME(OBJECT_SCHEMA_NAME(fk.parent_object_id)) + '.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id)) AS child_table,
                   QUOTENAME(OBJECT_SCHEMA_NAME(fk.referenced_object_id)) + '.' + QUOTENAME(OBJECT_NAME(fk.referenced_object_id)) AS parent_table,
                   pc.name AS child_column, rc.name AS parent_column,
                   LOWER(REPLACE(fk.delete_referential_action_desc, '_', ' ')) AS on_delete,
                   LOWER(REPLACE(fk.update_referential_action_desc, '_', ' ')) AS on_update, fk.is_disabled
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE {side} = @id
            ORDER BY fk.name, fkc.constraint_column_id
            """, id, ct);
        var result = new List<ForeignKey>();
        foreach (var g in rows.Rows.GroupBy(r => Long(r[0])))
        {
            var first = g.First();
            var child = g.Select(r => Str(r[4])!).ToList();
            var parent = g.Select(r => Str(r[5])!).ToList();
            var childTable = Str(first[2])!;
            var parentTable = Str(first[3])!;
            result.Add(new ForeignKey(Str(first[1])!, string.Join(", ", child), parentTable + " (" + string.Join(", ", parent) + ")",
                childTable + " (" + string.Join(", ", child) + ")", Str(first[6])!, Str(first[7])!, Bool(first[8]), childTable, child, parentTable, parent));
        }
        return result;
    }

    private sealed record TriggerInfo(string Name, string Type, string Events, bool Disabled);

    private static async Task<List<TriggerInfo>> TriggersAsync(IEngineSession s, Target t, Dictionary<string, object?> id, CancellationToken ct)
    {
        var rows = await RunFirst(s, t.Database, """
            SELECT tr.object_id, tr.name, tr.is_disabled, tr.is_instead_of_trigger, te.type_desc AS event
            FROM sys.triggers tr
            LEFT JOIN sys.trigger_events te ON te.object_id = tr.object_id
            WHERE tr.parent_id = @id
            ORDER BY tr.name, te.type
            """, id, ct);
        return rows.Rows.GroupBy(r => Long(r[0])).Select(g =>
        {
            var f = g.First();
            return new TriggerInfo(Str(f[1])!, Bool(f[3]) ? "instead of" : "after", string.Join(", ", g.Select(r => Str(r[4])).Where(e => e is not null)), Bool(f[2]));
        }).ToList();
    }

    private static async Task<string> DefinitionAsync(IEngineSession s, Target t, Dictionary<string, object?> id, CancellationToken ct)
    {
        var d = await RunFirst(s, t.Database, "SELECT m.definition FROM sys.sql_modules m WHERE m.object_id = @id", id, ct);
        return d.Rows.Count > 0 && Str(d.Rows[0][0]) is { } text
            ? text
            : "-- The definition is not available: the object is encrypted, or you lack VIEW DEFINITION permission.";
    }

    // Scripting -----------------------------------------------------------------------------------

    /// <summary>
    /// The script starts with USE [database], as SSMS's does: the objects are in the database named
    /// in the request, which is rarely the one the script is run in.
    /// </summary>
    public async Task<string> ScriptAsync(IEngineSession s, ObjectRequest req, CancellationToken ct)
    {
        var t = await ResolveAsync(s, req, ct);
        var body = await ScriptBodyAsync(s, t, ct);
        return t.Database is null ? body : $"USE {ObjectNames.QuoteBracket(t.Database)}\nGO\n{body}";
    }

    private async Task<string> ScriptBodyAsync(IEngineSession s, Target t, CancellationToken ct)
    {
        var id = new Dictionary<string, object?> { ["id"] = (int)t.Id };
        if (t.Kind is "view" or "procedure" or "function" or "trigger")
        {
            var d = await RunFirst(s, t.Database, "SELECT m.definition FROM sys.sql_modules m WHERE m.object_id = @id", id, ct);
            if (d.Rows.Count == 0 || Str(d.Rows[0][0]) is not { } text)
            {
                throw new CatalogException("The definition is not available: the object is encrypted, or you lack VIEW DEFINITION permission.");
            }
            return text.TrimEnd() + "\nGO\n";
        }
        if (t.Kind != "table") throw new CatalogException($"Scripting a {t.Kind} is not supported.");

        var columns = await RunFirst(s, t.Database, $"""
            SELECT c.name, {TypeSql("ty", "c")} AS type, c.is_nullable, c.is_identity, ic.seed_value, ic.increment_value,
                   dc.name AS default_name, dc.definition AS default_definition, cc.definition AS computed_definition, cc.is_persisted
            FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE c.object_id = @id
            ORDER BY c.column_id
            """, id, ct);
        var lines = new List<string>();
        foreach (var c in columns.Rows)
        {
            var name = ObjectNames.QuoteBracket(Str(c[0])!);
            if (Str(c[8]) is { } computed)
            {
                lines.Add($"    {name} AS {computed}{(Bool(c[9]) ? " PERSISTED" : "")}");
                continue;
            }
            var sb = new StringBuilder($"    {name} {Str(c[1])}");
            if (Bool(c[3])) sb.Append($" IDENTITY({Num(c[4])},{Num(c[5])})");
            sb.Append(Bool(c[2]) ? " NULL" : " NOT NULL");
            if (Str(c[7]) is { } def) sb.Append($" CONSTRAINT {ObjectNames.QuoteBracket(Str(c[6])!)} DEFAULT {def}");
            lines.Add(sb.ToString());
        }
        var indexes = await IndexesAsync(s, t, id, ct);
        foreach (var i in indexes.Where(i => i.PrimaryKey || i.UniqueConstraint))
        {
            var clustered = i.Type.StartsWith("clustered", StringComparison.Ordinal) ? "CLUSTERED" : "NONCLUSTERED";
            lines.Add($"    CONSTRAINT {ObjectNames.QuoteBracket(i.Name)} {(i.PrimaryKey ? "PRIMARY KEY" : "UNIQUE")} {clustered} ({i.Keys})");
        }
        var checks = await RunFirst(s, t.Database, "SELECT cc.name, cc.definition FROM sys.check_constraints cc WHERE cc.parent_object_id = @id ORDER BY cc.name", id, ct);
        lines.AddRange(checks.Rows.Select(c => $"    CONSTRAINT {ObjectNames.QuoteBracket(Str(c[0])!)} CHECK {Str(c[1])}"));

        var script = new StringBuilder($"CREATE TABLE {t.Qualified} (\n{string.Join(",\n", lines)}\n);\nGO\n");
        foreach (var i in indexes.Where(i => !i.PrimaryKey && !i.UniqueConstraint))
        {
            if (i.Type.Contains("columnstore") || i.Type is "xml" or "spatial")
            {
                script.Append($"\n-- {i.Type} index {ObjectNames.QuoteBracket(i.Name)} is not scripted\n");
                continue;
            }
            script.Append($"\nCREATE {(i.Unique ? "UNIQUE " : "")}{(i.Type.StartsWith("clustered", StringComparison.Ordinal) ? "CLUSTERED" : "NONCLUSTERED")} INDEX {ObjectNames.QuoteBracket(i.Name)}\n    ON {t.Qualified} ({i.Keys})");
            if (i.Included is not null) script.Append($"\n    INCLUDE ({i.Included})");
            if (i.Filter is not null) script.Append($"\n    WHERE {i.Filter}");
            script.Append(";\nGO\n");
        }
        foreach (var fk in await ForeignKeysAsync(s, t, id, outgoing: true, ct))
        {
            script.Append($"\nALTER TABLE {t.Qualified}\n    ADD CONSTRAINT {ObjectNames.QuoteBracket(fk.Name)} FOREIGN KEY ({string.Join(", ", fk.ChildColumns.Select(ObjectNames.QuoteBracket))})\n" +
                          $"    REFERENCES {fk.ParentTable} ({string.Join(", ", fk.ParentColumns.Select(ObjectNames.QuoteBracket))})");
            if (fk.OnDelete != "no action") script.Append($" ON DELETE {fk.OnDelete.ToUpperInvariant()}");
            if (fk.OnUpdate != "no action") script.Append($" ON UPDATE {fk.OnUpdate.ToUpperInvariant()}");
            script.Append(";\nGO\n");
        }
        var triggers = await RunFirst(s, t.Database, "SELECT m.definition FROM sys.triggers tr JOIN sys.sql_modules m ON m.object_id = tr.object_id WHERE tr.parent_id = @id ORDER BY tr.name", id, ct);
        foreach (var tr in triggers.Rows)
        {
            if (Str(tr[0]) is { } def) script.Append($"\n{def.TrimEnd()}\nGO\n");
        }
        return script.ToString();
    }
}
