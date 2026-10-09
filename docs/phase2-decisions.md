# Phase 2 decisions

For the user's review, continuing the numbering of `phase1-decisions.md` (22–34).

**35. The catalog uses a second session per connection.** Opened on first use, closed with the
connection, 60 s timeout; a driver error drops it so it reopens. Chosen so catalog calls work while
the user's session is busy, paused, or (PostgreSQL) in an aborted transaction. Cost: one more
server session per connection that uses the tree or info. On PostgreSQL it is set READ ONLY.

**36. Object info is engine-agnostic sections** (title, columns, rows, optional `text`). Lua knows no
engine. Cost: engine-specific presentation (e.g. SQL Server's `sp_help` output) is shown as plain
tables.

**37. Names are resolved by the server** (`to_regclass`, `pg_function_is_visible`, `OBJECT_ID`), not
parsed in Lua beyond finding the name under the cursor. Aliases and CTE names are not resolved to
tables. Overloaded PostgreSQL functions: the first visible match is used unless the tree supplies
the exact identity.

**38. No DISTINCT or GROUP BY for deduplication in catalog SQL**, as asked. Joins, EXISTS and
window functions are used; SQL Server index column lists are folded in C# from ordered rows.

**39. SQL Server reaches other databases through `EXEC [db].sys.sp_executesql`**, so one session
serves every database. Three-part names pick the database. Scripts begin with `USE [db]` and `GO`
because the object lives in a database the connection is not in (found by CI).

**40. PostgreSQL browses only the current database** (a connection is to one database); others are
listed, not expandable.

**41. Scripting limits.** PostgreSQL: tables, views, materialized views, indexes, sequences, functions, procedures (foreign tables are refused). SQL Server: tables, views, functions, procedures, triggers. Table
scripts are rebuilt from catalog data (columns, defaults, identity, constraints, indexes, foreign
keys as ALTER TABLE) and are not guaranteed byte-identical to what created them; the scenarios drop
the objects, run the script and script again, and require equal results. Not scripted: grants, ownership,
extended properties, partition bounds beyond what `pg_get_*def` gives.

**42. System objects are hidden by default** (`S` toggles): SQL Server's four system databases,
`sys`, `INFORMATION_SCHEMA`; PostgreSQL's `pg_*` and `information_schema`; extension members.

**Not checked:** the SQL Server catalog was verified only by CI on SQL Server 2022 (Linux) and
Express (Windows) with the fixture in the scenarios; older versions, case-sensitive collations,
partitioned or temporal tables, and columnstore indexes are untested. No interactive use on a real
Windows desktop.
