# dbbliss.nvim

A Neovim-native database client for SQL Server and PostgreSQL, built for reliability:
protocol-level cancel, no lost results, no surprise commits.

The plugin (Lua, Neovim 0.10+) talks to one persistent .NET 10 backend process over
line-delimited JSON-RPC on stdio.

> **Status: Phase 1 (core query loop) done.** Statement scopes, paged results, a results buffer,
> prod/transaction safety and credential stores, for PostgreSQL and SQL Server on Linux and
> Windows. Decisions to review: [docs/phase1-decisions.md](docs/phase1-decisions.md); Phase 0
> (cancel) results: [docs/phase0-results.md](docs/phase0-results.md). DB2 for i is deferred.

## Try it

```sh
scripts/build.sh                         # or scripts\build.ps1 on Windows; needs the .NET 10 SDK
```
Put the repository on your `runtimepath` (lazy.nvim: `{ dir = '/path/to/dbbliss.nvim', config = function() ... end }`),
call `setup` as below, then in an SQL buffer:

```
:Dbbliss connect local_pg
:Dbbliss exec            " the statement under the cursor
:Dbbliss exec_all        " the whole buffer
```
Use `:Dbbliss status` to see what is open, and `:Dbbliss cancel` to stop a query (it works while
the server is busy, and on a paused result). Add `require('dbbliss').statusline()` to your
`'statusline'` to see the connection, `PROD`, an open transaction and a running query.
Needs Neovim 0.10 or newer.

## Build the backend

Requires the .NET 10 SDK. The result is a self-contained single file, so the machine running
Neovim needs no .NET runtime.

```sh
scripts/build.sh            # → bin/linux-x64/dbbliss-backend
```
```powershell
scripts\build.ps1           # → bin\win-x64\dbbliss-backend.exe
```

## Configure

```lua
require('dbbliss').setup({
  connections = {
    local_pg = {
      engine = 'postgres',              -- 'postgres' | 'sqlserver' ('db2i' is deferred)
      env = 'dev',                      -- 'dev' | 'test' | 'prod'
      connection_string = 'Host=localhost;Port=5432;Username=me;Database=app',
      password = { env = 'APP_PG_PASSWORD' },   -- a reference; literal passwords are rejected
    },
    local_mssql = {
      engine = 'sqlserver',
      connection_string = 'Server=localhost,1433;User Id=sa;TrustServerCertificate=true',
      password = { env = 'APP_MSSQL_PASSWORD' },
    },
  },
})
```

### Passwords

`password` names where to find the password; the plugin never holds it. Exactly one of:

| | |
|---|---|
| `{ env = 'NAME' }` | an environment variable of the backend (Neovim's environment) |
| `{ credman = 'target' }` | a **generic** credential in Windows Credential Manager: `cmdkey /generic:target /user:me /pass:...` |
| `{ pass = 'path/entry' }` | the first line of `pass show path/entry` |
| `{ libsecret = { service = 'dbbliss', account = 'prod' } }` | `secret-tool lookup service dbbliss account prod` |

Leave `password` out for integrated authentication (`Integrated Security=true` in the connection
string): SSPI on Windows, a Kerberos ticket (`kinit`) on Linux. `env = 'prod'` on a connection makes
every statement that can change data ask for confirmation first.

## Use

| Command | |
|---|---|
| `:Dbbliss connect <name>` | open a connection; it becomes current |
| `:Dbbliss exec` | run the statement under the cursor (SQL Server: the `GO` batch) |
| `:{range}Dbbliss exec` | run every statement in the lines of the range, e.g. a visual selection |
| `:Dbbliss exec_all` | run the whole buffer, one statement at a time |
| `:Dbbliss export [path]` | write the statement under the cursor (or the range) to a CSV file; the backend writes it, the rows never pass through Neovim |
| `:Dbbliss fetch` | fetch the next window of rows of a paused result (also: `gm`, or move to the end of the buffer) |
| `:Dbbliss cancel` | cancel the running query (protocol-level); the rest of a script is not run |
| `:Dbbliss begin` / `commit` / `rollback` | explicit transactions |
| `:Dbbliss status` | connections, transaction state, running queries |
| `:Dbbliss disconnect` | asks before rolling back an open transaction |

A script stops at the first statement that does not complete and says which one it was; the
statements after it are not run. The backend splits the text (`script/split`): PostgreSQL at
semicolons, SQL Server at `GO` lines (`GO 5` repeats the batch), both aware of strings, comments,
quoted identifiers and, on PostgreSQL, dollar quoting. An error is shown on its line in the buffer
(as a diagnostic) as well as in the results.

### Object info, schema tree, scripting

| Command | |
|---|---|
| `:Dbbliss info [name]` | info buffer for the name under the cursor (schema-qualified if written so), or the given name. Also `<M-F1>` in SQL buffers |
| `:Dbbliss tree` | schema browser: database, schema, Tables / Views / Functions / Procedures |
| `:Dbbliss script [name]` | the object's CREATE script in a new SQL buffer |

The info buffer shows a summary, columns (type, nullability, default, identity, computed), indexes,
constraints, foreign keys out and in, triggers, and for views and routines the definition. PostgreSQL
reads `pg_catalog` (the detail of `psql \d+`); SQL Server reads `sys.*` and adds `sp_help`'s own
sections. In it: `r` refreshes, `s` scripts the object, `q` closes.

In the tree: `<CR>` expands or collapses (children load on the first expansion and are cached per
connection), `o` the same, `i` info, `s` script, `r` refresh the node, `R` refresh everything, `S`
show or hide system databases and schemas, `q` close. PostgreSQL lists every database but can only
browse the one the connection is on.

The catalog is read on a second session of its own, so it works while a result is paused or a
PostgreSQL transaction is aborted. SQL Server scripts start with `USE [database]` and `GO`.

```lua
require('dbbliss').setup({
  info = { max_col_width = 200 }, tree = { show_system = false },
  mappings = { info = '<M-F1>' },   -- false: no key
})
```

### Results

Results go to the `dbbliss://results` buffer as aligned tables (column widths from the first rows,
capped at `results.max_col_width`; wider values grow the column up to the cap, long text is shown
shortened but yanked in full). A query shows `results.window_rows` rows (default 1000) and then
**pauses**: the server is held by TCP backpressure, not finished, and the connection stays busy.
`gm`, `:Dbbliss fetch` or moving the cursor to the end of the buffer fetches the next window;
`:Dbbliss cancel` ends it. Rows that had already arrived when a paused query was cancelled are
counted, not shown.

In the results buffer: `<Tab>` / `<S-Tab>` next/previous cell, `yc` / `yr` / `yC` yank the cell /
row / column (full values, NULL as empty), `]]` / `[[` next/previous result set, `gm` fetch more,
`q` close. Server messages (NOTICE, PRINT, RAISERROR) go to a `dbbliss://messages` pane with their
severity. Each run starts with empty buffers.

```lua
require('dbbliss').setup({ results = { window_rows = 1000, max_col_width = 40 } })
```

Backend diagnostics go to `stdpath('log')/dbbliss-backend.log`.

## Tests

```sh
docker compose up -d --wait
scripts/build.sh
export DBBLISS_TEST_PG='Host=localhost;Port=55432;Username=dbbliss;Database=dbbliss' DBBLISS_TEST_PG_PASSWORD=dbbliss-test-Pw1
export DBBLISS_TEST_MSSQL='Server=localhost,51433;User Id=sa;TrustServerCertificate=true' DBBLISS_TEST_MSSQL_PASSWORD=dbbliss-test-Pw1
dotnet run --project backend/tests/Dbbliss.CancelTests

# No database needed:
dotnet run --project backend/tests/Dbbliss.ProtocolTests
nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/client_test.lua
```

The cancel suite drives the real backend (and a real headless Neovim for the "Neovim closed"
scenarios) and checks the server from an independent connection. DB2 for i (deferred) runs only
when `DBBLISS_TEST_DB2I` is set; see [docs/db2i-checklist.md](docs/db2i-checklist.md).

Decisions to review: [docs/phase0-decisions.md](docs/phase0-decisions.md).
