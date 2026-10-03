# dbbliss.nvim

A Neovim-native database client for SQL Server and PostgreSQL, built for reliability:
protocol-level cancel, no lost results, no surprise commits.

The plugin (Lua, Neovim 0.10+) talks to one persistent .NET 10 backend process over
line-delimited JSON-RPC on stdio.

> **Status: Phase 0 (cancel spike).** Cancel works and is verified for PostgreSQL and SQL Server
> on Linux. Windows has not been run yet. DB2 for i is deferred. See [docs/phase0-results.md](docs/phase0-results.md).

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

## Use (Phase 0)

| Command | |
|---|---|
| `:Dbbliss connect <name>` | open a connection; it becomes current |
| `:[range]Dbbliss exec` | run the range (default: whole buffer) |
| `:Dbbliss cancel` | cancel the running query (protocol-level) |
| `:Dbbliss begin` / `commit` / `rollback` | explicit transactions |
| `:Dbbliss status` | connections, transaction state, running queries |
| `:Dbbliss disconnect` | asks before rolling back an open transaction |

Results go to a minimal `dbbliss://results` buffer (Phase 1 replaces it). Backend diagnostics go to
`stdpath('log')/dbbliss-backend.log`.

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
