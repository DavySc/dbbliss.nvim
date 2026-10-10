using System.Globalization;
using System.Text;
using Dbbliss.Backend.Engines;
using static Dbbliss.Backend.Catalog.CatalogHelpers;

namespace Dbbliss.Backend.Catalog;

/// <summary>
/// PostgreSQL catalog queries over pg_catalog (the detail of psql's \d+). Needs PostgreSQL 11 or
/// newer (pg_proc.prokind). Objects are found by names the server resolves itself (to_regclass,
/// pg_function_is_visible), so the search_path applies to an unqualified name as it does in a query.
/// </summary>
public sealed class PostgresCatalog : ICatalog
{
    private static readonly (string Name, string Kind)[] Folders =
        [("Tables", "table"), ("Views", "view"), ("Functions", "function"), ("Procedures", "procedure")];

    public async Task PrepareAsync(IEngineSession session, CancellationToken ct)
    {
        // The catalog session reads and never writes: whatever goes wrong, it cannot change data.
        await session.QueryAsync("SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY", null, ct);
    }

    // Tree ----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CatalogNode>> ChildrenAsync(IEngineSession s, CatalogPath path, CancellationToken ct)
    {
        if (path.Database is null)
        {
            var t = await First(s, """
                SELECT d.datname AS name, d.datname = current_database() AS current
                FROM pg_database d
                WHERE NOT d.datistemplate AND has_database_privilege(d.oid, 'CONNECT')
                ORDER BY d.datname
                """, null, ct);
            return t.Rows.Select(r => new CatalogNode(Str(r[0])!, "database", Bool(r[1]), Browsable: Bool(r[1]),
                Detail: Bool(r[1]) ? "current" : "connect to it to browse")).ToList();
        }
        await RequireCurrentDatabase(s, path.Database, ct);
        if (path.Schema is null)
        {
            var t = await First(s, """
                SELECT n.nspname AS name,
                       (n.nspname = 'information_schema' OR left(n.nspname, 3) = 'pg_') AS system
                FROM pg_namespace n
                WHERE n.nspname !~ '^pg_(toast|temp)' AND has_schema_privilege(n.oid, 'USAGE')
                ORDER BY n.nspname
                """, null, ct);
            return t.Rows.Select(r => new CatalogNode(Str(r[0])!, "schema", true, System: Bool(r[1]))).ToList();
        }
        if (path.Folder is null)
        {
            return Folders.Select(f => new CatalogNode(f.Name, "folder", true, Folder: f.Kind)).ToList();
        }
        var p = new Dictionary<string, object?> { ["schema"] = path.Schema };
        switch (path.Folder)
        {
            case "table":
            case "view":
            {
                var kinds = path.Folder == "table" ? "('r','p','f')" : "('v','m')";
                var t = await First(s, $"""
                    SELECT c.relname AS name, c.relkind::text AS relkind
                    FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = @schema AND c.relkind IN {kinds}
                    ORDER BY c.relname
                    """, p, ct);
                return t.Rows.Select(r => new CatalogNode(Str(r[0])!, path.Folder, false,
                    Detail: Str(r[1]) switch { "m" => "materialized", "p" => "partitioned", "f" => "foreign", _ => null })).ToList();
            }
            case "function":
            case "procedure":
            {
                var kind = path.Folder == "function" ? "f" : "p";
                var t = await First(s, $"""
                    SELECT p.proname AS name, pg_get_function_identity_arguments(p.oid) AS identity
                    FROM pg_proc p
                    JOIN pg_namespace n ON n.oid = p.pronamespace
                    WHERE n.nspname = @schema AND p.prokind = '{kind}'
                      AND NOT EXISTS (SELECT 1 FROM pg_depend d
                                      WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
                    ORDER BY p.proname, pg_get_function_identity_arguments(p.oid)
                    """, p, ct);
                return t.Rows.Select(r => new CatalogNode(Str(r[0])!, path.Folder, false, Identity: Str(r[1]), Detail: Str(r[1]))).ToList();
            }
            default:
                throw new CatalogException($"Unknown folder {path.Folder}.");
        }
    }

    // Resolving a name ----------------------------------------------------------------------------

    private sealed record Target(string Kind, long Oid, string Schema, string Name, string? Identity, string? RelKind, string Qualified);

    private async Task<Target> ResolveAsync(IEngineSession s, ObjectRequest req, CancellationToken ct)
    {
        string? schema = req.Schema, name = req.Name, kind = req.Kind, identity = req.Identity;
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
            if (parts.Count == 3) await RequireCurrentDatabase(s, parts[0], ct);
            name = parts[^1];
            schema = parts.Count >= 2 ? parts[^2] : null;
            kind = null;
        }
        if (name is null) throw new CatalogException("No object name.");

        var p = new Dictionary<string, object?> { ["name"] = name };
        if (schema is not null) p["schema"] = schema;
        // A relation: the server resolves the (qualified) name, search_path included.
        if (kind is null or "table" or "view")
        {
            var t = await First(s, $"""
                SELECT c.oid::bigint AS oid, n.nspname AS schema, c.relname AS name, c.relkind::text AS relkind,
                       quote_ident(n.nspname) || '.' || quote_ident(c.relname) AS qualified
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.oid = to_regclass({(schema is null ? "quote_ident(@name)" : "quote_ident(@schema) || '.' || quote_ident(@name)")})
                """, p, ct);
            if (t.Rows.Count > 0)
            {
                var r = t.Rows[0];
                var relkind = Str(r[3])!;
                return new Target(RelationKind(relkind), Long(r[0]), Str(r[1])!, Str(r[2])!, null, relkind, Str(r[4])!);
            }
        }
        if (kind is null or "function" or "procedure")
        {
            var where = schema is null ? "pg_function_is_visible(p.oid)" : "n.nspname = @schema";
            if (identity is not null) p["identity"] = identity;
            var t = await First(s, $"""
                SELECT p.oid::bigint AS oid, n.nspname AS schema, p.proname AS name, p.prokind::text AS prokind,
                       pg_get_function_identity_arguments(p.oid) AS identity
                FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE p.proname = @name AND {where} AND p.prokind IN ('f', 'p')
                  {(identity is null ? "" : "AND pg_get_function_identity_arguments(p.oid) = @identity")}
                ORDER BY n.nspname, pg_get_function_identity_arguments(p.oid)
                """, p, ct);
            if (t.Rows.Count > 1)
            {
                var overloads = string.Join("; ", t.Rows.Select(r => $"{Str(r[2])}({Str(r[4])})"));
                throw new CatalogException($"Several functions are named {name}: {overloads}. Open the one you want from the schema tree.");
            }
            if (t.Rows.Count == 1)
            {
                var r = t.Rows[0];
                return new Target(Str(r[3]) == "p" ? "procedure" : "function", Long(r[0]), Str(r[1])!, Str(r[2])!, Str(r[4]), null, "");
            }
        }
        var db = Str((await First(s, "SELECT current_database()", null, ct)).Rows[0][0]);
        throw new CatalogException(
            $"Nothing named {(schema is null ? name : schema + "." + name)} in database {db}. Qualify it with its schema, or refresh if it was just created.");
    }

    private static string RelationKind(string relkind) => relkind switch
    {
        "r" or "p" => "table",
        "f" => "foreign table",
        "v" => "view",
        "m" => "materialized view",
        "S" => "sequence",
        "i" or "I" => "index",
        "c" => "type",
        _ => "relation",
    };

    // Object info ---------------------------------------------------------------------------------

    public async Task<ObjectInfo> DescribeAsync(IEngineSession s, ObjectRequest req, CancellationToken ct)
    {
        var t = await ResolveAsync(s, req, ct);
        var title = $"{t.Kind} {t.Schema}.{t.Name}{(t.Identity is { Length: > 0 } ? "(" + t.Identity + ")" : t.Kind is "function" or "procedure" ? "()" : "")}";
        var sections = new List<InfoSection>();
        var oid = new Dictionary<string, object?> { ["oid"] = t.Oid };

        if (t.Kind is "function" or "procedure")
        {
            var f = await First(s, """
                SELECT CASE p.prokind WHEN 'p' THEN 'procedure' ELSE 'function' END AS kind, l.lanname AS language, pg_get_function_result(p.oid) AS returns,
                       pg_get_function_arguments(p.oid) AS arguments, pg_get_userbyid(p.proowner) AS owner,
                       CASE p.provolatile WHEN 'i' THEN 'immutable' WHEN 's' THEN 'stable' ELSE 'volatile' END AS volatility,
                       p.prosecdef AS security_definer, obj_description(p.oid, 'pg_proc') AS comment
                FROM pg_proc p
                JOIN pg_language l ON l.oid = p.prolang
                WHERE p.oid = @oid::oid
                """, oid, ct);
            sections.Add(Properties("Summary", f));
            sections.Add(InfoSection.OfText("Definition", Str((await First(s, "SELECT pg_get_functiondef(@oid::oid)", oid, ct)).Rows[0][0]) ?? ""));
            return new ObjectInfo(title, t.Kind, sections);
        }

        var summary = await First(s, """
            SELECT CASE c.relkind WHEN 'r' THEN 'table' WHEN 'p' THEN 'partitioned table' WHEN 'f' THEN 'foreign table'
                                  WHEN 'v' THEN 'view' WHEN 'm' THEN 'materialized view' WHEN 'S' THEN 'sequence'
                                  WHEN 'i' THEN 'index' WHEN 'I' THEN 'partitioned index' ELSE c.relkind::text END AS kind,
                   pg_get_userbyid(c.relowner) AS owner,
                   CASE WHEN c.relkind IN ('r', 'p', 'm', 'f') THEN
                        CASE WHEN c.reltuples < 0 THEN 'unknown (never analyzed)' ELSE c.reltuples::bigint::text END END AS row_estimate,
                   CASE WHEN c.relkind IN ('r', 'p', 'm', 'S', 'i') THEN pg_size_pretty(pg_total_relation_size(c.oid)) END AS total_size,
                   CASE c.relpersistence WHEN 'p' THEN 'permanent' WHEN 'u' THEN 'unlogged' ELSE 'temporary' END AS persistence,
                   CASE WHEN c.relkind = 'p' THEN pg_get_partkeydef(c.oid) END AS partition_key,
                   array_to_string(c.reloptions, ', ') AS options,
                   obj_description(c.oid, 'pg_class') AS comment
            FROM pg_class c WHERE c.oid = @oid::oid
            """, oid, ct);
        sections.Add(Properties("Summary", summary));

        if (t.RelKind == "S")
        {
            sections.Add(Properties("Sequence", await First(s, """
                SELECT format_type(q.seqtypid, NULL) AS type, q.seqstart AS start, q.seqincrement AS increment,
                       q.seqmin AS minimum, q.seqmax AS maximum, q.seqcache AS cache, q.seqcycle AS cycle
                FROM pg_sequence q WHERE q.seqrelid = @oid::oid
                """, oid, ct)));
            return new ObjectInfo(title, t.Kind, sections);
        }
        if (t.RelKind is "i" or "I")
        {
            sections.Add(InfoSection.OfText("Definition", Str((await First(s, "SELECT pg_get_indexdef(@oid::oid)", oid, ct)).Rows[0][0]) ?? ""));
            return new ObjectInfo(title, t.Kind, sections);
        }

        sections.Add(Table("Columns", await First(s, ColumnsSql, oid, ct)));
        sections.Add(Table("Indexes", await First(s, """
            SELECT ic.relname AS name, am.amname AS method, i.indisunique AS "unique", i.indisprimary AS "primary",
                   i.indisvalid AS valid, pg_get_indexdef(i.indexrelid) AS definition
            FROM pg_index i
            JOIN pg_class ic ON ic.oid = i.indexrelid
            JOIN pg_am am ON am.oid = ic.relam
            WHERE i.indrelid = @oid::oid
            ORDER BY ic.relname
            """, oid, ct)));
        sections.Add(Table("Constraints", await First(s, """
            SELECT con.conname AS name,
                   CASE con.contype WHEN 'p' THEN 'primary key' WHEN 'u' THEN 'unique' WHEN 'c' THEN 'check'
                                    WHEN 'x' THEN 'exclusion' WHEN 't' THEN 'trigger' ELSE con.contype::text END AS type,
                   pg_get_constraintdef(con.oid) AS definition, con.convalidated AS validated
            FROM pg_constraint con
            WHERE con.conrelid = @oid::oid AND con.contype <> 'f'
            ORDER BY con.contype, con.conname
            """, oid, ct)));
        sections.Add(Table("Foreign keys", await First(s, """
            SELECT con.conname AS name, pg_get_constraintdef(con.oid) AS definition, con.convalidated AS validated
            FROM pg_constraint con
            WHERE con.conrelid = @oid::oid AND con.contype = 'f'
            ORDER BY con.conname
            """, oid, ct)));
        sections.Add(Table("Referenced by", await First(s, """
            SELECT con.conrelid::regclass::text AS "table", con.conname AS name, pg_get_constraintdef(con.oid) AS definition
            FROM pg_constraint con
            WHERE con.confrelid = @oid::oid AND con.contype = 'f'
            ORDER BY con.conrelid::regclass::text, con.conname
            """, oid, ct)));
        sections.Add(Table("Triggers", await First(s, """
            SELECT tg.tgname AS name,
                   CASE tg.tgenabled WHEN 'O' THEN 'enabled' WHEN 'D' THEN 'disabled' WHEN 'R' THEN 'replica' ELSE 'always' END AS state,
                   pg_get_triggerdef(tg.oid) AS definition
            FROM pg_trigger tg
            WHERE tg.tgrelid = @oid::oid AND NOT tg.tgisinternal
            ORDER BY tg.tgname
            """, oid, ct)));
        if (t.RelKind is "v" or "m")
        {
            sections.Add(InfoSection.OfText("Definition", Str((await First(s, "SELECT pg_get_viewdef(@oid::oid, true)", oid, ct)).Rows[0][0]) ?? ""));
        }
        return new ObjectInfo(title, t.Kind, sections);
    }

    private const string ColumnsSql = """
        SELECT a.attnum AS "#", a.attname AS name, format_type(a.atttypid, a.atttypmod) AS type,
               NOT a.attnotnull AS nullable, pg_get_expr(d.adbin, d.adrelid) AS "default",
               CASE a.attidentity WHEN 'a' THEN 'always' WHEN 'd' THEN 'by default' END AS identity,
               CASE WHEN a.attgenerated = 's' THEN 'stored' END AS generated,
               col_description(a.attrelid, a.attnum) AS comment
        FROM pg_attribute a
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        WHERE a.attrelid = @oid::oid AND a.attnum > 0 AND NOT a.attisdropped
        ORDER BY a.attnum
        """;

    // Scripting -----------------------------------------------------------------------------------

    public async Task<string> ScriptAsync(IEngineSession s, ObjectRequest req, CancellationToken ct)
    {
        var t = await ResolveAsync(s, req, ct);
        var oid = new Dictionary<string, object?> { ["oid"] = t.Oid };
        var qualified = t.Qualified;

        if (t.Kind is "function" or "procedure")
        {
            return (Str((await First(s, "SELECT pg_get_functiondef(@oid::oid)", oid, ct)).Rows[0][0]) ?? "").TrimEnd() + ";\n";
        }
        switch (t.RelKind)
        {
            case "v":
                return $"CREATE OR REPLACE VIEW {qualified} AS\n{(Str((await First(s, "SELECT pg_get_viewdef(@oid::oid, true)", oid, ct)).Rows[0][0]) ?? "").TrimEnd().TrimEnd(';')};\n";
            case "i" or "I":
                return (Str((await First(s, "SELECT pg_get_indexdef(@oid::oid)", oid, ct)).Rows[0][0]) ?? "") + ";\n";
            case "S":
            {
                var q = (await First(s, """
                    SELECT format_type(q.seqtypid, NULL), q.seqstart, q.seqincrement, q.seqmin, q.seqmax, q.seqcache, q.seqcycle
                    FROM pg_sequence q WHERE q.seqrelid = @oid::oid
                    """, oid, ct)).Rows[0];
                return $"CREATE SEQUENCE {qualified}\n    AS {Str(q[0])}\n    START WITH {Num(q[1])}\n    INCREMENT BY {Num(q[2])}\n    MINVALUE {Num(q[3])}\n    MAXVALUE {Num(q[4])}\n    CACHE {Num(q[5])}{(Bool(q[6]) ? "\n    CYCLE" : "")};\n";
            }
            case "f":
                throw new CatalogException("Scripting a foreign table is not supported.");
            case "r" or "p" or "m":
                break;
            default:
                throw new CatalogException($"Scripting a {t.Kind} is not supported.");
        }

        var script = new StringBuilder();
        if (t.RelKind == "m")
        {
            var def = Str((await First(s, "SELECT pg_get_viewdef(@oid::oid, true)", oid, ct)).Rows[0][0]) ?? "";
            script.Append($"CREATE MATERIALIZED VIEW {qualified} AS\n{def.TrimEnd().TrimEnd(';')}\nWITH DATA;\n");
        }
        else
        {
            var info = (await First(s, """
                SELECT c.relispartition, pg_get_partkeydef(c.oid) AS partition_key,
                       CASE WHEN c.relispartition THEN pg_get_expr(c.relpartbound, c.oid) END AS bound,
                       CASE WHEN c.relispartition THEN c.relnamespace::regnamespace::text || '.' || quote_ident((SELECT pc.relname FROM pg_class pc WHERE pc.oid = i.inhparent)) END AS parent,
                       CASE c.relpersistence WHEN 'u' THEN 'UNLOGGED ' ELSE '' END AS unlogged
                FROM pg_class c
                LEFT JOIN pg_inherits i ON i.inhrelid = c.oid AND c.relispartition
                WHERE c.oid = @oid::oid
                """, oid, ct)).Rows[0];
            var cols = await First(s, """
                SELECT quote_ident(a.attname) AS name, format_type(a.atttypid, a.atttypmod) AS type, a.attnotnull,
                       a.attidentity::text AS identity, a.attgenerated::text AS generated, pg_get_expr(d.adbin, d.adrelid) AS default_expr
                FROM pg_attribute a
                LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
                WHERE a.attrelid = @oid::oid AND a.attnum > 0 AND NOT a.attisdropped
                ORDER BY a.attnum
                """, oid, ct);
            var lines = new List<string>();
            foreach (var c in cols.Rows)
            {
                var sb = new StringBuilder($"    {Str(c[0])} {Str(c[1])}");
                var generated = Str(c[4]);
                var identity = Str(c[3]);
                if (generated == "s") sb.Append($" GENERATED ALWAYS AS ({Str(c[5])}) STORED");
                else if (identity == "a") sb.Append(" GENERATED ALWAYS AS IDENTITY");
                else if (identity == "d") sb.Append(" GENERATED BY DEFAULT AS IDENTITY");
                else if (Str(c[5]) is { } def) sb.Append($" DEFAULT {def}");
                if (Bool(c[2])) sb.Append(" NOT NULL");
                lines.Add(sb.ToString());
            }
            var cons = await First(s, """
                SELECT quote_ident(con.conname), pg_get_constraintdef(con.oid)
                FROM pg_constraint con
                WHERE con.conrelid = @oid::oid AND con.contype <> 'f' AND con.conparentid = 0
                ORDER BY con.contype, con.conname
                """, oid, ct);
            lines.AddRange(cons.Rows.Select(c => $"    CONSTRAINT {Str(c[0])} {Str(c[1])}"));
            if (Bool(info[0]))
            {
                script.Append($"CREATE {Str(info[4])}TABLE {qualified} PARTITION OF {Str(info[3])}\n    {Str(info[2])};\n");
            }
            else
            {
                script.Append($"CREATE {Str(info[4])}TABLE {qualified} (\n{string.Join(",\n", lines)}\n)");
                if (Str(info[1]) is { } key) script.Append($"\nPARTITION BY {key}");
                script.Append(";\n");
            }
            var fks = await First(s, """
                SELECT quote_ident(con.conname), pg_get_constraintdef(con.oid)
                FROM pg_constraint con
                WHERE con.conrelid = @oid::oid AND con.contype = 'f' AND con.conparentid = 0
                ORDER BY con.conname
                """, oid, ct);
            foreach (var fk in fks.Rows) script.Append($"\nALTER TABLE {qualified}\n    ADD CONSTRAINT {Str(fk[0])} {Str(fk[1])};\n");
        }
        // Indexes the constraints above do not already create.
        var indexes = await First(s, """
            SELECT pg_get_indexdef(i.indexrelid)
            FROM pg_index i
            JOIN pg_class ic ON ic.oid = i.indexrelid
            WHERE i.indrelid = @oid::oid
              AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = i.indexrelid AND con.conrelid = i.indrelid)
            ORDER BY ic.relname
            """, oid, ct);
        foreach (var ix in indexes.Rows) script.Append($"\n{Str(ix[0])};\n");
        var triggers = await First(s, """
            SELECT pg_get_triggerdef(tg.oid)
            FROM pg_trigger tg
            WHERE tg.tgrelid = @oid::oid AND NOT tg.tgisinternal
            ORDER BY tg.tgname
            """, oid, ct);
        foreach (var tg in triggers.Rows) script.Append($"\n{Str(tg[0])};\n");
        return script.ToString();
    }

    // Helpers -------------------------------------------------------------------------------------

    private async Task RequireCurrentDatabase(IEngineSession s, string database, CancellationToken ct)
    {
        var current = Str((await First(s, "SELECT current_database()", null, ct)).Rows[0][0]);
        if (!string.Equals(current, database, StringComparison.Ordinal))
        {
            throw new CatalogException($"This connection is to database {current}; {database} can only be browsed by connecting to it.");
        }
    }

    // Completion ----------------------------------------------------------------------------------

    public async Task<CatalogNames> NamesAsync(IEngineSession s, string? database, bool includeSystem, int limit, CancellationToken ct)
    {
        if (database is not null) await RequireCurrentDatabase(s, database, ct);
        // One more than the limit, so "exactly the limit" is told from "there was more".
        var t = await First(s, """
            SELECT x.schema, x.name, x.kind FROM (
                SELECT n.nspname AS schema, c.relname AS name, CASE WHEN c.relkind IN ('v', 'm') THEN 'view' ELSE 'table' END AS kind
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p', 'f', 'v', 'm') AND n.nspname !~ '^pg_(toast|temp)' AND has_schema_privilege(n.oid, 'USAGE')
                  AND (@system OR NOT (n.nspname = 'information_schema' OR left(n.nspname, 3) = 'pg_'))
                UNION ALL
                SELECT n.nspname, p.proname, CASE p.prokind WHEN 'p' THEN 'procedure' ELSE 'function' END
                FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE p.prokind IN ('f', 'p') AND n.nspname !~ '^pg_(toast|temp)' AND has_schema_privilege(n.oid, 'USAGE')
                  AND (@system OR NOT (n.nspname = 'information_schema' OR left(n.nspname, 3) = 'pg_'))
                  AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
            ) x
            ORDER BY x.schema, x.name, x.kind
            LIMIT @limit
            """, new Dictionary<string, object?> { ["system"] = includeSystem, ["limit"] = limit + 1 }, ct);
        var items = t.Rows.Take(limit).Select(r => new NameEntry(Str(r[0])!, Str(r[1])!, Str(r[2])!)).ToList();
        return new CatalogNames(items, t.Rows.Count > limit);
    }

    public async Task<TableColumns> ColumnsAsync(IEngineSession s, ObjectRequest req, CancellationToken ct)
    {
        var t = await ResolveAsync(s, req, ct);
        if (t.RelKind is not ("r" or "p" or "f" or "v" or "m"))
        {
            throw new CatalogException($"{t.Schema}.{t.Name} is a {t.Kind}, which has no columns to complete.");
        }
        var columns = await First(s, """
            SELECT a.attname AS name, format_type(a.atttypid, a.atttypmod) AS type
            FROM pg_attribute a
            WHERE a.attrelid = @oid::oid AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """, new Dictionary<string, object?> { ["oid"] = t.Oid }, ct);
        return new TableColumns(t.Schema, t.Name, columns.Rows.Select(r => new ColumnEntry(Str(r[0])!, Str(r[1])!)).ToList());
    }
}
