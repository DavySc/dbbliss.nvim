# DB2 for i — manual test checklist (Phase 0 gate)

DB2 for i cannot be containerized. Run this against a real IBM i before Phase 1.
**If cancel is unreliable, stop and report; do not work around it.**

## Prerequisites

- IBM i Access ODBC driver installed (Linux: the `ibm-iaccess` package plus unixODBC; Windows:
  IBM i Access Client Solutions, Windows Application Package).
- A **test** system or partition. The CPU scenario runs a 2-billion-step recursive CTE until it is
  cancelled; don't run it on production.
- A schema created with `CREATE SCHEMA` (so tables are journaled; inserts under commitment control
  fail with SQL7008 on a non-journaled library).
- A user with `*JOBCTL` or another way to read `QSYS2.ACTIVE_JOB_INFO` details for other jobs.

```sh
export DBBLISS_TEST_DB2I='Driver={IBM i Access ODBC Driver};System=myibmi;UID=MYUSER;'
export DBBLISS_TEST_DB2I_PASSWORD='...'     # or set it from your secret store
export DBBLISS_TEST_DB2I_SCHEMA='DBBLISSTST'
scripts/build.sh
dotnet run --project backend/tests/Dbbliss.CancelTests -- --engine db2i --report docs/phase0-results-db2i.md
```

## Assumptions to verify first (the suite depends on them)

- [ ] `SELECT QSYS2.JOB_NAME FROM SYSIBM.SYSDUMMY1` returns the server job (`nnnnnn/QUSER/QZDASOINIT`),
      and it matches the job shown in `WRKACTJOB` for the ODBC connection.
- [ ] `QSYS2.ACTIVE_JOB_INFO(JOB_NAME_FILTER => 'QZDASOINIT', DETAILED_INFO => 'ALL')` has the columns
      `JOB_NAME`, `SQL_STATEMENT_STATUS`, `SQL_STATEMENT_TEXT`, `JOB_STATUS`, `FUNCTION`
      (check your IBM i release and PTF level).
- [ ] `SQL_STATEMENT_STATUS` is `ACTIVE` while a statement runs and `COMPLETE` afterwards.
- [ ] `CALL QSYS2.QCMDEXC('DLYJOB DLY(60)')` is allowed for the test user.

## Scenarios (the suite runs these; record the results)

| Scenario | What to look for |
|---|---|
| `sleep_cancel` (DLYJOB via QCMDEXC) | Does SQLCancel interrupt a CL command running inside a CALL? |
| `cpu_cancel` (recursive CTE) | Does SQLCancel interrupt work inside the SQL engine? |
| `cancel_immediately` | Re-sent SQLCancel covers the race. |
| `streaming_cancel`, `streaming_cancel_stalled_client` | Block fetch: how quickly does the server stop? |
| `tx_cancel` | Not checked automatically. After the cancel, check by hand that the job still has its pending change (`WRKCMTDFN`, or `QSYS2.DB_TRANSACTION_INFO`), that rollback removes it, and that nothing was committed. |
| `backend_killed`, `backend_stdin_closed`, `nvim_*` | Does the QZDASOINIT job end the statement when the client disappears, and how fast? |

Also check by hand:

- [ ] SQLCancel while the server is blocked on a record lock (`SELECT ... FOR UPDATE` against a row
      locked by a second session).
- [ ] After each cancel, the connection is still usable: run `SELECT 1 FROM SYSIBM.SYSDUMMY1`.
- [ ] `QSYS2.CANCEL_SQL` exists on the release, for Phase 3.
- [ ] Repeat on Windows with the Windows ODBC driver.

## Catalog queries (Phase 7, step 1)

Run these from `Db2iCatalog` by hand (or through `:Dbbliss tree` / `info` / `script`) and record the result; the open questions are in
`docs/db2i-research.md` (second pass). Replace `S` and `T` with a schema and table of yours.

- [ ] `SELECT CURRENT_SERVER FROM SYSIBM.SYSDUMMY1` returns the system name.
- [ ] `SELECT SCHEMA_NAME FROM QSYS2.SYSSCHEMAS ORDER BY SCHEMA_NAME` lists your schemas (compare with `OBJECT_STATISTICS('*ALLSIMPLE','LIB')`).
- [ ] `QSYS2.SYSCOLUMNS2` accepts the select list in `ReadColumnsAsync`; a `TIMESTAMP(0)` column shows its precision.
- [ ] `QSYS2.SYSINDEXES` has `IS_UNIQUE` (values D/U/V) and `QSYS2.SYSKEYS` has `ORDINAL_POSITION` and `ORDERING`.
- [ ] `QSYS2.SYSREFCST` has `UPDATE_RULE`, `DELETE_RULE`, `UNIQUE_CONSTRAINT_SCHEMA`, `UNIQUE_CONSTRAINT_NAME`; `QSYS2.CHECK_CONSTRAINTS` has `CHECK_CLAUSE`.
- [ ] `SYSVIEWS.VIEW_DEFINITION` holds the whole `CREATE VIEW` or only the query (the script copes with both).
- [ ] The table script, run after dropping a test table, recreates it.
- [ ] `FETCH FIRST n ROWS ONLY` after `UNION ALL` in a derived table works through the ODBC driver; `catalog/names` time on a large system.
