using Dbbliss.Backend.Catalog;
using Dbbliss.Backend.Engines;

namespace Dbbliss.ProtocolTests;

/// <summary>
/// The DB2 for i catalog against a scripted session: what is asked, with which values, and how the answers are
/// read. The QSYS2 SQL itself has never run on an IBM i (decision 77, docs/db2i-checklist.md).
/// </summary>
public static class Db2iCatalogTests
{
    private sealed class Scripted(Func<string, IReadOnlyDictionary<string, object?>?, QueryTable?> answer) : IEngineSession
    {
        public List<string> Sent { get; } = [];
        public List<IReadOnlyDictionary<string, object?>?> Parameters { get; } = [];

        public string ServerSessionId => "123456/QUSER/QZDASOINIT";

        public Task<IReadOnlyList<QueryTable>> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
        {
            Sent.Add(sql);
            Parameters.Add(parameters);
            var t = answer(sql, parameters);
            return Task.FromResult<IReadOnlyList<QueryTable>>(t is null ? [] : [t]);
        }

        public Task<ExecuteSummary> ExecuteAsync(string sql, IResultSink sink, QueryControl control) => throw new NotSupportedException();
        public Task BeginTransactionAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task CommitAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task RollbackAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<TransactionState> GetTransactionStateAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static QueryTable T(string[] columns, params object?[][] rows) => new(columns, rows);

    private static readonly Db2iCatalog Catalog = new();

    private static async Task<CatalogException> Refused(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (CatalogException ex)
        {
            return ex;
        }
        throw new TestFailure(what + " should have been refused");
    }

    private static string P(IReadOnlyDictionary<string, object?>? p, string key) =>
        p is not null && p.TryGetValue(key, out var v) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" : "<missing>";

    // Verifies: LLR-DB2I-4
    public static void ParametersBecomePositionalMarkers()
    {
        var sql = Db2iSql.Positional("SELECT * FROM T WHERE A = @a AND B = '@b' AND C = @c AND \"@d\" = @a",
            new Dictionary<string, object?> { ["a"] = 1, ["c"] = "x" }, out var values);
        if (sql != "SELECT * FROM T WHERE A = ? AND B = '@b' AND C = ? AND \"@d\" = ?") throw new TestFailure("sql: " + sql);
        if (values.Count != 3 || !Equals(values[0], 1) || !Equals(values[1], "x") || !Equals(values[2], 1)) throw new TestFailure("values: " + string.Join(",", values));

        var quote = Db2iSql.Positional("SELECT 'it''s @a' FROM T WHERE X = @a", new Dictionary<string, object?> { ["a"] = 2 }, out var v2);
        if (quote != "SELECT 'it''s @a' FROM T WHERE X = ?" || v2.Count != 1) throw new TestFailure("doubled quote: " + quote);

        var none = Db2iSql.Positional("SELECT 1 FROM SYSIBM.SYSDUMMY1", null, out var v3);
        if (none != "SELECT 1 FROM SYSIBM.SYSDUMMY1" || v3.Count != 0) throw new TestFailure("no parameters changes the text");

        try
        {
            Db2iSql.Positional("SELECT @missing FROM T", new Dictionary<string, object?>(), out _);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new TestFailure("a name with no value should be an error");
    }

    // Verifies: HLR-DB2I-1
    public static async Task TreeShowsTheSystemSchemasAndFolders()
    {
        var s = new Scripted((sql, p) =>
        {
            if (sql.Contains("CURRENT_SERVER", StringComparison.Ordinal)) return T(["name"], ["MYIBMI"]);
            if (sql.Contains("QSYS2.SYSSCHEMAS", StringComparison.Ordinal)) return T(["name"], ["APPLIB"], ["QSYS2"], ["SYSIBM"]);
            if (sql.Contains("QSYS2.SYSROUTINES", StringComparison.Ordinal)) return T(["name", "identity"], ["ADD_TAX", "ADD_TAX_1"], ["ADD_TAX", "ADD_TAX_2"]);
            if (sql.Contains("QSYS2.SYSTABLES", StringComparison.Ordinal)) return T(["name", "type"], ["CUSTOMER", "T"], ["ORDERS", "P"]);
            return null;
        });
        var root = await Catalog.ChildrenAsync(s, new CatalogPath(), CancellationToken.None);
        if (root.Count != 1 || root[0].Name != "MYIBMI" || root[0].Kind != "database" || !root[0].Browsable) throw new TestFailure("root: " + string.Join(",", root.Select(n => n.Name)));

        var schemas = await Catalog.ChildrenAsync(s, new CatalogPath("MYIBMI"), CancellationToken.None);
        if (schemas.Select(n => n.Name + ":" + n.System).SequenceEqual(["APPLIB:False", "QSYS2:True", "SYSIBM:True"]) is false)
            throw new TestFailure("schemas: " + string.Join(",", schemas.Select(n => n.Name + ":" + n.System)));

        var folders = await Catalog.ChildrenAsync(s, new CatalogPath("MYIBMI", "APPLIB"), CancellationToken.None);
        if (!folders.Select(f => f.Folder).SequenceEqual(["table", "view", "function", "procedure"])) throw new TestFailure("folders: " + string.Join(",", folders.Select(f => f.Folder)));

        var tables = await Catalog.ChildrenAsync(s, new CatalogPath("MYIBMI", "APPLIB", "table"), CancellationToken.None);
        if (!tables.Select(n => n.Name).SequenceEqual(["CUSTOMER", "ORDERS"]) || tables.Any(n => n.Kind != "table" || n.Expandable)) throw new TestFailure("tables");
        if (P(s.Parameters[^1], "schema") != "APPLIB") throw new TestFailure("the schema was not bound");

        var functions = await Catalog.ChildrenAsync(s, new CatalogPath("MYIBMI", "APPLIB", "function"), CancellationToken.None);
        if (functions.Count != 2 || functions[0].Identity != "ADD_TAX_1" || functions[1].Identity != "ADD_TAX_2") throw new TestFailure("routines are told apart by specific name");
        if (!s.Sent[^1].Contains("'FUNCTION'", StringComparison.Ordinal)) throw new TestFailure("functions were not asked for");

        await Refused(() => Catalog.ChildrenAsync(s, new CatalogPath("OTHERSYSTEM"), CancellationToken.None), "a database that is not the connected system");
        await Refused(() => Catalog.ChildrenAsync(s, new CatalogPath("MYIBMI", "APPLIB", "trigger"), CancellationToken.None), "an unknown folder");
    }

    private static Scripted Customer(Action<string>? onSql = null) => new((sql, p) =>
    {
        onSql?.Invoke(sql);
        if (sql.Contains("CURRENT_SERVER", StringComparison.Ordinal)) return T(["name"], ["MYIBMI"]);
        if (sql.Contains("QSYS2.SYSTABLES", StringComparison.Ordinal) && sql.Contains("TABLE_NAME = @name", StringComparison.Ordinal))
        {
            // Only the folded name exists.
            return P(p, "name") == "CUSTOMER" ? T(["schema", "name", "type"], ["APPLIB", "CUSTOMER", "T"]) : T(["schema", "name", "type"]);
        }
        if (sql.Contains("QSYS2.SYSROUTINES", StringComparison.Ordinal) && sql.Contains("ROUTINE_NAME = @name", StringComparison.Ordinal))
            return T(["schema", "name", "specific", "type"]);
        if (sql.Contains("QSYS2.SYSCOLUMNS2", StringComparison.Ordinal))
            return T(["ordinal", "name", "data_type", "length", "precision", "scale", "nullable", "default", "has_default", "identity"],
                [1, "ID", "INTEGER", null, 9, 0, "N", null, "I", "YES"],
                [2, "NAME", "VARCHAR", 40, null, null, "N", "''", "Y", "NO"],
                [3, "BALANCE", "DECIMAL", null, 11, 2, "Y", "0", "Y", "NO"]);
        if (sql.Contains("QSYS2.SYSINDEXES", StringComparison.Ordinal))
            return T(["index", "unique", "column", "ordering", "ordinal"], ["CUST_NAME_IX", "D", "NAME", "A", 1], ["CUST_NAME_IX", "D", "ID", "D", 2]);
        if (sql.Contains("QSYS2.SYSREFCST", StringComparison.Ordinal))
            return T(["name", "update_rule", "delete_rule", "ref_schema", "ref_table", "ref_constraint_schema", "ref_constraint"],
                ["FK_CUST_REGION", "NO ACTION", "CASCADE", "APPLIB", "REGION", "APPLIB", "PK_REGION"]);
        if (sql.Contains("QSYS2.CHECK_CONSTRAINTS", StringComparison.Ordinal))
            return T(["name", "clause"], ["CK_BAL", "BALANCE >= 0"]);
        if (sql.Contains("QSYS2.SYSKEYCST", StringComparison.Ordinal))
        {
            if (P(p, "cname") == "PK_REGION") return T(["column"], ["REGION_ID"]);
            return T(["constraint", "column", "ordinal"], ["PK_CUST", "ID", 1], ["UQ_CUST_NAME", "NAME", 1], ["FK_CUST_REGION", "NAME", 1]);
        }
        if (sql.Contains("QSYS2.SYSCST", StringComparison.Ordinal))
            return T(["name", "type"], ["PK_CUST", "PRIMARY KEY"], ["UQ_CUST_NAME", "UNIQUE"], ["CK_BAL", "CHECK"], ["FK_CUST_REGION", "FOREIGN KEY"]);
        return null;
    });

    // Verifies: LLR-DB2I-3
    public static async Task NamesResolveTheWayTheServerReadsThem()
    {
        var s = Customer();
        var cols = await Catalog.ColumnsAsync(s, new ObjectRequest("customer", null, null, null, null, null), CancellationToken.None);
        if (cols.Schema != "APPLIB" || cols.Name != "CUSTOMER") throw new TestFailure($"resolved to {cols.Schema}.{cols.Name}");
        var asked = s.Parameters.Where(p => p is not null && p.ContainsKey("name")).Select(p => P(p, "name")).ToList();
        if (!asked.Take(2).SequenceEqual(["customer", "CUSTOMER"])) throw new TestFailure("tried: " + string.Join(",", asked));
        var firstResolve = s.Sent.First(q => q.Contains("QSYS2.SYSTABLES", StringComparison.Ordinal));
        if (!firstResolve.Contains("CURRENT SCHEMA", StringComparison.Ordinal) || firstResolve.Contains("@schema", StringComparison.Ordinal))
            throw new TestFailure("an unqualified name should use the current schema: " + firstResolve);

        if (!firstResolve.Contains("SYSTEM_TABLE_NAME = @name", StringComparison.Ordinal)) throw new TestFailure("an object can also be named by its system name: " + firstResolve);

        var q = Customer();
        await Catalog.ColumnsAsync(q, new ObjectRequest("applib.customer", null, null, null, null, null), CancellationToken.None);
        if (!q.Parameters.Any(p => P(p, "schema") == "APPLIB")) throw new TestFailure("a qualified name did not try the folded schema");

        var three = Customer();
        await Catalog.ColumnsAsync(three, new ObjectRequest("MYIBMI.APPLIB.CUSTOMER", null, null, null, null, null), CancellationToken.None);
        var wrong = await Refused(() => Catalog.ColumnsAsync(Customer(), new ObjectRequest("OTHER.APPLIB.CUSTOMER", null, null, null, null, null), CancellationToken.None), "another system");
        if (!wrong.Message.Contains("OTHER", StringComparison.Ordinal) || !wrong.Message.Contains("MYIBMI", StringComparison.Ordinal)) throw new TestFailure("message: " + wrong.Message);

        var many = new Scripted((sql, p) =>
        {
            if (sql.Contains("QSYS2.SYSROUTINES", StringComparison.Ordinal) && sql.Contains("ROUTINE_NAME = @name", StringComparison.Ordinal))
                return P(p, "name") == "ADD_TAX" ? T(["schema", "name", "specific", "type"], ["APPLIB", "ADD_TAX", "ADD_TAX_1", "FUNCTION"], ["APPLIB", "ADD_TAX", "ADD_TAX_2", "FUNCTION"]) : T(["schema", "name", "specific", "type"]);
            return T(["schema", "name", "type"]);
        });
        var ambiguous = await Refused(() => Catalog.DescribeAsync(many, new ObjectRequest("add_tax", null, null, null, null, null), CancellationToken.None), "an ambiguous routine");
        if (!ambiguous.Message.Contains("ADD_TAX_1", StringComparison.Ordinal) || !ambiguous.Message.Contains("ADD_TAX_2", StringComparison.Ordinal)) throw new TestFailure("overloads not named: " + ambiguous.Message);

        var none = await Refused(() => Catalog.ColumnsAsync(new Scripted((sql, _) => sql.Contains("CURRENT_SERVER", StringComparison.Ordinal) ? T(["name"], ["MYIBMI"]) : T(["schema", "name", "type"])), new ObjectRequest("nothing", null, null, null, null, null), CancellationToken.None), "a missing name");
        if (!none.Message.Contains("nothing", StringComparison.Ordinal)) throw new TestFailure("message: " + none.Message);
    }

    // Verifies: LLR-DB2I-5
    public static async Task NamesAndColumnsForCompletion()
    {
        var sqls = new List<string>();
        var s = new Scripted((sql, p) =>
        {
            sqls.Add(sql);
            if (sql.Contains("CURRENT_SERVER", StringComparison.Ordinal)) return T(["name"], ["MYIBMI"]);
            return T(["schema", "name", "kind"], ["APPLIB", "CUSTOMER", "table"], ["APPLIB", "CUSTV", "view"], ["APPLIB", "ADD_TAX", "function"]);
        });
        var all = await Catalog.NamesAsync(s, null, false, 10, CancellationToken.None);
        if (all.Truncated || all.Items.Count != 3 || all.Items[1].Kind != "view") throw new TestFailure("names");
        if (!sqls[^1].Contains("FETCH FIRST 11 ROWS ONLY", StringComparison.Ordinal)) throw new TestFailure("the limit is written as a checked integer, one more than asked: " + sqls[^1]);
        if (!sqls[^1].Contains("NOT IN (", StringComparison.Ordinal)) throw new TestFailure("system schemas should be left out unless asked");

        var cut = await Catalog.NamesAsync(s, "MYIBMI", true, 2, CancellationToken.None);
        if (!cut.Truncated || cut.Items.Count != 2) throw new TestFailure("truncation: " + cut.Items.Count);
        if (sqls[^1].Contains("NOT IN (", StringComparison.Ordinal)) throw new TestFailure("system schemas were asked for");

        await Refused(() => Catalog.NamesAsync(s, "OTHER", false, 5, CancellationToken.None), "another system");

        var cols = await Catalog.ColumnsAsync(Customer(), new ObjectRequest(null, null, "APPLIB", "CUSTOMER", "table", null), CancellationToken.None);
        if (!cols.Columns.Select(c => c.Name + " " + c.Type).SequenceEqual(["ID INTEGER", "NAME VARCHAR(40)", "BALANCE DECIMAL(11,2)"]))
            throw new TestFailure("columns: " + string.Join(", ", cols.Columns.Select(c => c.Name + " " + c.Type)));
    }

    // Verifies: HLR-DB2I-2
    public static async Task TableScriptIsBuiltFromCatalogRows()
    {
        var asked = new List<string>();
        var script = await Catalog.ScriptAsync(Customer(asked.Add), new ObjectRequest(null, null, "APPLIB", "CUSTOMER", "table", null), CancellationToken.None);
        var expected = string.Join("\n",
            "CREATE TABLE APPLIB.CUSTOMER (",
            "    ID INTEGER GENERATED ALWAYS AS IDENTITY NOT NULL,",
            "    NAME VARCHAR(40) NOT NULL DEFAULT '',",
            "    BALANCE DECIMAL(11,2) DEFAULT 0,",
            "    CONSTRAINT PK_CUST PRIMARY KEY (ID),",
            "    CONSTRAINT UQ_CUST_NAME UNIQUE (NAME),",
            "    CONSTRAINT CK_BAL CHECK (BALANCE >= 0),",
            "    CONSTRAINT FK_CUST_REGION FOREIGN KEY (NAME) REFERENCES APPLIB.REGION (REGION_ID) ON DELETE CASCADE",
            ");",
            "");
        if (script != expected) throw new TestFailure("script:\n" + script + "\nexpected:\n" + expected);
        // vscode-db2i and DBeaver both order SYSKEYCST by COLUMN_POSITION; it has no ORDINAL_POSITION.
        var keyQueries = asked.Where(q => q.Contains("QSYS2.SYSKEYCST", StringComparison.Ordinal)).ToList();
        if (keyQueries.Count == 0 || keyQueries.Any(q => !q.Contains("COLUMN_POSITION", StringComparison.Ordinal) || q.Contains("ORDINAL_POSITION", StringComparison.Ordinal)))
            throw new TestFailure("key columns must be ordered by COLUMN_POSITION: " + string.Join(" | ", keyQueries));
    }

    // Verifies: HLR-DB2I-2
    public static async Task GeneratedColumnsThatCannotBeScriptedAreRefused()
    {
        var s = new Scripted((sql, p) =>
        {
            if (sql.Contains("QSYS2.SYSCOLUMNS2", StringComparison.Ordinal))
                return T(["ordinal", "name", "data_type", "length", "precision", "scale", "nullable", "default", "has_default", "identity"],
                    [1, "ID", "INTEGER", null, 9, 0, "N", null, "J", "YES"],
                    [2, "CHANGED", "TIMESTAMP", null, null, 6, "N", null, "E", "NO"]);
            return T(["x"]);
        });
        var refused = await Refused(() => Catalog.ScriptAsync(s, new ObjectRequest(null, null, "APPLIB", "T", "table", null), CancellationToken.None), "a row change timestamp column");
        if (!refused.Message.Contains("CHANGED", StringComparison.Ordinal) || !refused.Message.Contains("row change timestamp", StringComparison.Ordinal)) throw new TestFailure("message: " + refused.Message);

        var identity = new Scripted((sql, p) => sql.Contains("QSYS2.SYSCOLUMNS2", StringComparison.Ordinal)
            ? T(["ordinal", "name", "data_type", "length", "precision", "scale", "nullable", "default", "has_default", "identity"], [1, "ID", "INTEGER", null, 9, 0, "N", null, "J", "YES"])
            : T(["x"]));
        var script = await Catalog.ScriptAsync(identity, new ObjectRequest(null, null, "APPLIB", "T", "table", null), CancellationToken.None);
        if (!script.Contains("ID INTEGER GENERATED BY DEFAULT AS IDENTITY NOT NULL", StringComparison.Ordinal)) throw new TestFailure("identity by default: " + script);
    }

    // Verifies: HLR-DB2I-2
    public static async Task RoutineDescriptionSeparatesParametersFromTheReturnValue()
    {
        var asked = new List<string>();
        var s = new Scripted((sql, p) =>
        {
            asked.Add(sql);
            if (sql.Contains("QSYS2.SYSPARMS", StringComparison.Ordinal))
                return T(["ordinal", "mode", "name", "data_type", "length", "precision", "scale", "row_type"],
                    [1, "IN", "AMOUNT", "DECIMAL", null, 11, 2, "P"], [2, "IN", "CODE", "CHAR", 3, null, null, "P"], [3, null, "", "INTEGER", null, 9, 0, "R"]);
            if (sql.Contains("ROUTINE_DEFINITION", StringComparison.Ordinal)) return T(["definition"], ["CREATE FUNCTION APPLIB.ADD_TAX () RETURNS INTEGER BEGIN RETURN 1; END"]);
            return null;
        });
        var info = await Catalog.DescribeAsync(s, new ObjectRequest(null, null, "APPLIB", "ADD_TAX", "function", "ADD_TAX_1"), CancellationToken.None);
        var parms = info.Sections[0];
        if (parms.Title != "Parameters" || parms.Rows.Count != 3) throw new TestFailure("parameters: " + parms.Rows.Count);
        if (!Equals(parms.Rows[0][3], "DECIMAL(11,2)") || !Equals(parms.Rows[1][3], "CHAR(3)")) throw new TestFailure("types: " + parms.Rows[0][3] + " / " + parms.Rows[1][3]);
        if (!Equals(parms.Rows[2][1], "returns")) throw new TestFailure("the return value is not a parameter: " + parms.Rows[2][1]);
        if (!asked.Any(q => q.Contains("ROW_TYPE IN ('P', 'R')", StringComparison.Ordinal))) throw new TestFailure("SYSPARMS holds parameters (P) and return columns (R)");
    }

    // Verifies: HLR-DB2I-2
    public static async Task ViewAndRoutineScriptsAreTheStoredDefinitions()
    {
        QueryTable? Answer(string sql, string definition)
        {
            if (sql.Contains("CURRENT_SERVER", StringComparison.Ordinal)) return T(["name"], ["MYIBMI"]);
            if (sql.Contains("QSYS2.SYSVIEWS", StringComparison.Ordinal) || sql.Contains("ROUTINE_DEFINITION", StringComparison.Ordinal)) return T(["definition"], [definition]);
            return null;
        }
        var bare = await Catalog.ScriptAsync(new Scripted((sql, _) => Answer(sql, "SELECT ID FROM APPLIB.CUSTOMER")),
            new ObjectRequest(null, null, "APPLIB", "CUSTV", "view", null), CancellationToken.None);
        if (bare != "CREATE VIEW APPLIB.CUSTV AS\nSELECT ID FROM APPLIB.CUSTOMER\n") throw new TestFailure("bare definition: " + bare);

        var full = await Catalog.ScriptAsync(new Scripted((sql, _) => Answer(sql, "  create view APPLIB.CUSTV as select ID from APPLIB.CUSTOMER")),
            new ObjectRequest(null, null, "APPLIB", "CUSTV", "view", null), CancellationToken.None);
        if (full != "create view APPLIB.CUSTV as select ID from APPLIB.CUSTOMER\n") throw new TestFailure("full definition: " + full);

        var proc = await Catalog.ScriptAsync(new Scripted((sql, _) => Answer(sql, "CREATE PROCEDURE APPLIB.P () BEGIN END")),
            new ObjectRequest(null, null, "APPLIB", "P", "procedure", "P_1"), CancellationToken.None);
        if (proc != "CREATE PROCEDURE APPLIB.P () BEGIN END\n") throw new TestFailure("routine: " + proc);

        var external = await Refused(() => Catalog.ScriptAsync(new Scripted((sql, _) => sql.Contains("CURRENT_SERVER", StringComparison.Ordinal) ? T(["name"], ["MYIBMI"]) : T(["definition"], new object?[] { null })),
            new ObjectRequest(null, null, "APPLIB", "P", "procedure", "P_1"), CancellationToken.None), "a routine with no stored definition");
        if (!external.Message.Contains("APPLIB.P", StringComparison.Ordinal)) throw new TestFailure("message: " + external.Message);
    }

    // Verifies: HLR-DB2I-2
    public static async Task TableDescriptionHasTheSections()
    {
        var info = await Catalog.DescribeAsync(Customer(), new ObjectRequest(null, null, "APPLIB", "CUSTOMER", "table", null), CancellationToken.None);
        if (info.Kind != "table" || info.Title != "table APPLIB.CUSTOMER") throw new TestFailure($"title: {info.Title} / {info.Kind}");
        var titles = info.Sections.Select(x => x.Title).ToList();
        if (!titles.SequenceEqual(["Columns", "Indexes", "Constraints", "Foreign keys"])) throw new TestFailure("sections: " + string.Join(",", titles));
        var columns = info.Sections[0];
        if (columns.Rows.Count != 3 || !Equals(columns.Rows[0][1], "ID")) throw new TestFailure("columns section");
        var indexes = info.Sections[1];
        if (indexes.Rows.Count != 1 || !Equals(indexes.Rows[0][2], "NAME, ID DESC") || !Equals(indexes.Rows[0][1], "no")) throw new TestFailure("indexes section");
        var constraints = info.Sections[2];
        if (constraints.Rows.Count != 4 || !constraints.Rows.Any(r => Equals(r[0], "PK_CUST") && Equals(r[2], "ID"))) throw new TestFailure("constraints section");
        var fks = info.Sections[3];
        if (fks.Rows.Count != 1 || !Equals(fks.Rows[0][2], "APPLIB.REGION")) throw new TestFailure("foreign keys section");
    }

    // Verifies: HLR-DB2I-2
    public static void IdentifiersAndTypesAreWrittenAsDeclared()
    {
        void Is(string got, string want)
        {
            if (got != want) throw new TestFailure($"got {got}, expected {want}");
        }
        Is(Db2iSql.Ident("ORDERS"), "ORDERS");
        Is(Db2iSql.Ident("A#1"), "A#1");
        Is(Db2iSql.Ident("Order Line"), "\"Order Line\"");
        Is(Db2iSql.Ident("Orders"), "\"Orders\"");
        Is(Db2iSql.Ident("1ST"), "\"1ST\"");
        Is(Db2iSql.Ident("SAY\"HI"), "\"SAY\"\"HI\"");
        Is(Db2iSql.TypeName("CHAR", 10, null, null), "CHAR(10)");
        Is(Db2iSql.TypeName("DECIMAL", null, 11, 2), "DECIMAL(11,2)");
        Is(Db2iSql.TypeName("NUMERIC", null, 7, null), "NUMERIC(7,0)");
        Is(Db2iSql.TypeName("INTEGER", null, 9, 0), "INTEGER");
        Is(Db2iSql.TypeName("TIMESTAMP", null, null, 6), "TIMESTAMP(6)");
        Is(Db2iSql.TypeName("TIMESTAMP", null, null, null), "TIMESTAMP");
        Is(Db2iSql.TypeName("DECFLOAT", null, 34, null), "DECFLOAT(34)");
        Is(Db2iSql.TypeName("DATE", null, null, null), "DATE");
        Is(Db2iSql.TypeName("VARCHAR", null, null, null), "VARCHAR");
    }
}
