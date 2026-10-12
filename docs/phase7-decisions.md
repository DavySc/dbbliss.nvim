# Phase 7 decisions (DB2 for i)

For the user's review, continuing the numbering of `phase6-decisions.md` (73–76). **Everything here is unverified against an
IBM i**: the project has no system to run on. The references are IBM's QSYS2 catalog documentation (as remembered) and DBeaver's
DB2 for i plugin (`docs/db2i-research.md`). Built spec-first (`HLR-DB2I-1..2`, `LLR-DB2I-3..6`), tests before code, against a scripted
session: the tests prove what is asked and how answers are read, **not** that the SQL runs. `docs/db2i-checklist.md` stays the gate.

**77. Catalog scope and the unverified rule.** Step 1 of the plan is the catalog only: tree, `describe`, `script`, completion
`names`/`columns`, plus completion quoting and keywords in Lua. Sessions, plans and management are still `null` for this engine
(the UI says the engine does not support them). Decisions inside it:
- *One database.* DB2 for i has no database level under the system, so the tree shows the system (`CURRENT_SERVER`) as the single
  database; a different database name is refused with a message naming the system.
- *Table scripts are built, not generated.* `QSYS2.GENERATE_SQL`'s shape and availability are not verified, so the CREATE TABLE is
  assembled from `SYSCOLUMNS`, `SYSCST`, `SYSKEYCST`, `CHECK_CONSTRAINTS`, `SYSREFCST`. Not included yet: indexes, triggers, comments,
  identity start/increment, journaling, CCSID, `RCDFMT`. A view or routine script is the stored definition; `CREATE VIEW ... AS` is added
  only when the stored text is just the query (DBeaver adds it; whether the catalog keeps the head is not known, so both are handled).
- *Names.* Unquoted names fold to upper case, but the parser does not tell quoted from unquoted, so a name is tried as written then folded
  (both parts together). An unqualified name uses `CURRENT SCHEMA`; the system-naming library list is not searched.
- *No row estimate and no triggers in `describe`*: neither view's columns are known well enough, and a wrong column name fails the
  whole `describe`.
- *System schemas* are a fixed list (QSYS, QSYS2, SYSIBM, SYSIBMADM, SYSPROC, SYSTOOLS, SYSCAT, SYSSTAT, QTEMP, QGPL, QUSRSYS, SQLJ).
- *ODBC parameters* are positional, so `Db2iSql.Positional` rewrites `@name` to `?` in order (literals and quoted identifiers skipped).
  The `FETCH FIRST n ROWS ONLY` limit is a checked integer in the text, not a marker.

**What was not checked.** Every QSYS2 view and column name, the type names in `SYSCOLUMNS.DATA_TYPE`, whether `LENGTH` means bytes or
characters for graphic types, the `IS_NULLABLE`/`IS_IDENTITY` values, `ODBC` handling of `FETCH FIRST` and of `UNION ALL` inside a
derived table, and every performance property of the catalog queries on a large system (`QSYS2.SYSTABLES` on a busy IBM i can be slow:
completion `names` filters system schemas but not before reading). The mutation checks DB2I-1..11 show the tests can fail; they say
nothing about the server.

**78. Research pass and what it changed.** Facts from vscode-db2i (MIT) and JTOpen (IPL) are in `docs/db2i-research.md` (second pass);
nothing was copied, and ACS was not looked at (closed source). It corrected four things in decision 77's catalog: key columns are ordered by
`COLUMN_POSITION`; columns come from `SYSCOLUMNS2` (`HAS_DEFAULT` tells identity and generated columns apart); objects also match by system
name; `SYSPARMS` return columns are not parameters. A generated column other than an identity now makes `script` refuse, naming the column,
instead of writing it as a plain column (a wrong script is worse than none). Mutations DB2I-12..15. The same pass gives the recipe for
the next step: cancel from a second job with `CALL QSYS2.CANCEL_SQL('<job>')`, then judge the outcome from
`qsys2.get_job_info('<job>')`'s `V_SQL_STMT_STATUS` (`ACTIVE` = still running), never from the call returning.
