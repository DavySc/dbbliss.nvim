# DB2 for i: what DBeaver tells us, and the plan

Source read: `dbeaver/dbeaver`, `plugins/org.jkiss.dbeaver.ext.db2.i` (574 lines of Java, shallow clone,
2026-10-11). No IBM i is available to the project, so nothing below was run against a real system.

## What DBeaver does and does not give us

DBeaver reaches DB2 for i through the **JT400 JDBC driver** and its generic `DatabaseMetaData` model.
Only the parts the driver does not cover are written by hand, so the plugin is small. It is a source
for QSYS2 catalog SQL, not for the harder parts of this project.

Usable as a reference (all unverified by us):

| Need | DBeaver's SQL |
|---|---|
| System schemas | `QSYS`, `QSYS2`, `SYSIBM`, `SYSPROC`, `SYSTOOLS` |
| View source | `SELECT VIEW_DEFINITION FROM QSYS2.SYSVIEWS WHERE TABLE_SCHEMA=? AND TABLE_NAME=?` (can be empty for system views) |
| Routine source | `SELECT ROUTINE_DEFINITION FROM QSYS2.SYSROUTINES WHERE ROUTINE_SCHEMA=? AND ROUTINE_NAME=?` |
| Keys / unique | `QSYS2.SYSKEYCST` joined to `SYSIBM.TABLE_CONSTRAINTS` (type `<> 'FOREIGN KEY'`) |
| Check constraints | `QSYS2.CHECK_CONSTRAINTS` joined to `SYSIBM.TABLE_CONSTRAINTS` |
| Sequences | `SYSIBM.SEQUENCES` (`MINIMUM_VALUE`, `MAXIMUM_VALUE`, `INCREMENT`) |
| Rename | `RENAME TABLE s.old TO new` |
| Current schema | `SELECT CURRENT_SCHEMA FROM SYSIBM.SYSDUMMY1`, `SET SCHEMA ?` |
| Dialect | `BEGIN ... END` and `CASE ... END` blocks; no ORDER BY index; no alias in conditions |
| Connection | `omit-catalog`: DB2 for i has no catalog level, so the tree is schema, then objects |

Not in DBeaver (no help): cancel, session list/cancel/terminate, plans, backup, transaction state,
the job name. These come from IBM documentation and need a real system to verify.

## Plan (each step spec-first, with a requirement and a tagged test)

1. **Catalog** (`Db2iCatalog`): schema tree, object info, CREATE scripts, completion `names`/`columns`.
   Prefer `QSYS2.SYSTABLES`, `SYSCOLUMNS`, `SYSINDEXES`, `SYSKEYCST`, `SYSREFCST`, `SYSVIEWS`,
   `SYSROUTINES`, and `QSYS2.GENERATE_SQL` for scripts. Tested through a fake engine (what the Lua
   and protocol layers see); the SQL itself stays unverified.
2. **Sessions**: `QSYS2.ACTIVE_JOB_INFO` for list; `QSYS2.CANCEL_SQL` for cancel; `ENDJOB` through
   `QCMDEXC` for terminate. Listed in the checklist as assumptions.
3. **Plans**: estimated plans only, no actual plans (decision to take: DB2 for i's visual explain
   data lives in `QSYS2.EXPLAIN_*` tables and the plan cache, a different shape).
4. **Management**: drop through SQL; backup is out of scope (`SAVLIB` is not a SQL-level backup).
5. **Dialect pieces** for statement splitting and the classifier (`ChangesSchema`, block bounds).

Everything built this way is marked **unverified** in the requirements and the README. The checklist
in `docs/db2i-checklist.md` stays the gate: someone with an IBM i runs it before the engine is called
supported. The cancel scenario is the main reason to wait: CPU-bound cancel must not run on production.

## Second pass: vscode-db2i (MIT) and JTOpen (IPL 1.0), 2026-10-11

Read: `codefori/vscode-db2i` (`src/database/{schemas,table,callable,crud}.ts`, `src/connection/extendedSQLJob.ts`) and `IBM/jtopen`
(`AS400JDBCDatabaseMetaData.java`). ACS itself is closed source and was not looked at. Only facts were taken; no code was copied
(vscode-db2i is MIT, JTOpen is the IBM Public License). Both projects have run these statements on real systems, which makes them better
evidence than my memory, but they are still not a run of *our* queries.

**Confirmed by vscode-db2i** (used as the same view and column names in `Db2iCatalog`):
`QSYS2.SYSTABLES` (`TABLE_SCHEMA`, `TABLE_NAME`, `TABLE_TYPE` T/P/M tables, V views, A aliases, L logical files, `TABLE_TEXT`,
`SYSTEM_TABLE_NAME`); `QSYS2.SYSROUTINES` (`ROUTINE_SCHEMA`, `ROUTINE_NAME`, `SPECIFIC_NAME`, `ROUTINE_TYPE` FUNCTION/PROCEDURE);
`QSYS2.SYSPARMS` (`ROW_TYPE` P = parameter, R = return column, `PARAMETER_MODE`, `DATA_TYPE`, `CHARACTER_MAXIMUM_LENGTH`,
`NUMERIC_PRECISION`, `NUMERIC_SCALE`, `ORDINAL_POSITION`); `QSYS2.SYSCST` (with `SYSTEM_TABLE_SCHEMA/NAME`); `QSYS2.SYSKEYCST`.

**Corrections this made** (each has a test and a recorded mutation, DB2I-12..15):
- `SYSKEYCST` is ordered by **`COLUMN_POSITION`**, not `ORDINAL_POSITION` (both vscode-db2i and DBeaver). My first version was wrong.
- Columns come from **`QSYS2.SYSCOLUMNS2`** with `CHARACTER_MAXIMUM_LENGTH`, `NUMERIC_PRECISION`, `NUMERIC_SCALE`, `HAS_DEFAULT`, not
  `SYSCOLUMNS.LENGTH`. `HAS_DEFAULT` is `Y`/`N` for an ordinary default and a letter for a generated column (I/J identity always/by
  default; A/D ROWID; E/F row change timestamp; Q/R row begin/end; X transaction start ID; a/c/d generated expressions). The script now
  writes identity from `I`/`J` and **refuses** the other generated kinds instead of writing them as plain columns.
- An object may be named by its **system name** (10 characters) as well as its long name: resolution matches `SYSTEM_TABLE_NAME` too.
- `SYSPARMS` also holds the return column (`ROW_TYPE = 'R'`): `describe` now shows it as `returns`, not as a parameter.

**Facts for the next steps**
- *Cancel:* from a second job, `CALL QSYS2.CANCEL_SQL('<number/user/name>')`; then ask whether the statement is still running with
  `qsys2.get_job_info('<job>')` column `V_SQL_STMT_STATUS` (`ACTIVE` = still running). The cancel is a request, not an outcome, which is
  the same rule as the other engines (report cancelled only when the work has ended).
- *Job identity:* the server job name is `number/user/name`; `QSYS2.JOB_NAME` (already used by `Db2iEngine`) matches it.
- *Current schema:* `values (current schema)`; in system naming the library list applies instead (`QSYS2.LIBRARY_LIST_INFO`).
- *`GENERATE_SQL`* is a **CALL** that writes to a source file or an IFS stream file (`SOURCE_STREAM_FILE`, `DATABASE_SOURCE_FILE_NAME => '*STMF'`),
  not a result set. That confirms decision 77 (build the table script ourselves): reading it back would need IFS access
  (`QSYS2.IFS_READ`) and write permission for a temp file. Worth revisiting as an optional richer script, not as the base.
- *Plans:* vscode-db2i's Visual Explain is driven through the Mapepire server job, not plain SQL: it does not give us a ready SQL recipe
  for estimated plans (`docs/phase7-decisions.md` keeps plans last for that reason).
- *Schema list:* vscode-db2i lists libraries with `QSYS2.OBJECT_STATISTICS('*ALLSIMPLE', 'LIB')` (`OBJLONGNAME`); we use
  `QSYS2.SYSSCHEMAS`. Whether the two differ (libraries with no SQL objects) is an open question for the checklist.

**Still unconfirmed** (add to `docs/db2i-checklist.md`): `SYSSCHEMAS.SCHEMA_NAME`; `SYSINDEXES.IS_UNIQUE` values (D/U/V); `SYSKEYS`
column names (`ORDINAL_POSITION`, `ORDERING`); `SYSVIEWS.VIEW_DEFINITION` and `SYSROUTINES.ROUTINE_DEFINITION` contents; `SYSREFCST`
column names; `CHECK_CONSTRAINTS.CHECK_CLAUSE`; whether a TIMESTAMP's fractional digits are in `NUMERIC_SCALE`.
