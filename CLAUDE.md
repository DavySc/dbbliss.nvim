# dbbliss.nvim: working notes

A Neovim database client (Lua frontend, .NET 10 backend over line-delimited JSON-RPC on stdio)
for **SQL Server and PostgreSQL**. Priorities, in order: reliability (no silent failures,
protocol-level cancel, no lost results, no surprise commits), performance, Windows first.

## Status (2026-10-09)

**Phase 0 (cancel spike) and Phase 1 (core query loop, M1–M5) are complete.** Stop for the user's
review at the end of each phase. Decisions to review: `docs/phase1-decisions.md` (22–34), including
what was not checked. Phase 1 plan: `docs/phase1-plan.md`.

Phase 1 in short: `script/split` (backend splits and classifies statements; `Scripts/`), error lines
mapped to the buffer, pull paging (`window`, `fetch`, `query/paused`) and `export` in
`Rpc/ResultStreamer.cs` / `Rpc/CsvExport.cs`, the results buffer (`lua/dbbliss/results.lua`,
`render.lua`), prod confirmation, quit/buffer-close guards and the statusline in `init.lua`, password
references in `Credentials.cs`.

- All 30 real-database scenarios pass on Linux (PostgreSQL 17 and SQL Server 2022 in Docker) and on
  Windows (CI: native PostgreSQL and SQL Server Express). Results: `docs/phase0-results.md`.
  Decisions for the user to review: `docs/phase0-decisions.md` (1–21).
- A PostgreSQL session left by a hard-killed backend is ended on the next connect (decisions 19, 21).
- Windows gaps: Docker Desktop or Rancher Desktop on Windows is untested, and so is interactive use
  on a real Windows desktop.
- **On GitHub** as `git@github.com:DavySc/dbbliss.nvim.git` (`origin`), first pushed 2026-10-06.
  `gh` is installed and logged in. CI (`.github/workflows/ci.yml`) runs every test suite on
  `windows-latest` and `ubuntu-latest` on every push.
- **DB2 for i is deferred** (decision 16). Its engine, scenarios and `docs/db2i-checklist.md` stay
  in the repo, unrun. Don't work on it unless the user brings it back, and don't delete it without asking.
- Formal models in `spec/` (TLA+ query lifecycle, Quint client/transactions) found four bugs after
  the spike. All are fixed and covered by tests (`docs/phase0-results.md`, bugs 5–8; `spec/README.md`).

`spec/check.sh` runs in CI (job `spec-check`). Pull paging is in `spec/tla/QueryLifecycle.tla`
(`ClientFetch`, `credit`); the quit variants are in `spec/quint/client_quit.qnt`. To run the TLA+
part without downloading `tla2tools.jar`, point `TLA_JAR` at Apalache's jar
(`~/.quint/apalache-dist-*/apalache/lib/apalache.jar`), which contains TLC.

## Rules that are easy to miss

- Allowed dependencies: the database drivers (Microsoft.Data.SqlClient, Npgsql, System.Data.Odbc)
  only. Ask before adding anything. That includes test frameworks: test runners are plain console
  apps, no xUnit (decision 12).
- Transactions are plain `BEGIN`/`COMMIT`/`ROLLBACK` SQL sent by the engines, with no driver
  transaction objects. Every transaction decision asks the server first (decision 7).
- PostgreSQL sessions are tagged `application_name = "dbbliss.nvim <instance id>"`, and backends
  register in `<LocalAppData>/dbbliss/instances` (`Instances.cs`). The orphan sweep relies on
  both. Never sweep a session that isn't provably from a dead instance on this machine.
- A connection's lease is released under the output lock, right before the operation's report
  (`query/done` or the response) is written: not earlier (stale reports), not later (spurious
  busy). See `Output.WriteAsync(message, ordered)` and decision 17.
- A new design that involves concurrency or transactions should be checked against the models in
  `spec/`. Every variant except `Proposed` must fail; `spec/check.sh` checks that (~3 min).

## Commands

`dotnet` lives in `~/.dotnet` (not on PATH by default): `export PATH=$HOME/.dotnet:$PATH`.

```sh
# No database needed
dotnet run --project backend/tests/Dbbliss.ProtocolTests            # backend in-process, fake engine
nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/client_test.lua   # Lua client, stub backend
spec/check.sh                                                       # formal models (Java, quint, TLA jar)

# Real databases (images are already pulled locally)
docker compose up -d --wait
scripts/build.sh                     # publishes bin/linux-x64/dbbliss-backend; the cancel suite uses it
export DBBLISS_TEST_PG='Host=localhost;Port=55432;Username=dbbliss;Database=dbbliss' DBBLISS_TEST_PG_PASSWORD=dbbliss-test-Pw1
export DBBLISS_TEST_MSSQL='Server=localhost,51433;User Id=sa;TrustServerCertificate=true' DBBLISS_TEST_MSSQL_PASSWORD=dbbliss-test-Pw1
dotnet run --project backend/tests/Dbbliss.CancelTests [-- --scenario name,... --report file.md]
# Through the real plugin and the published backend (also needs Neovim 0.10+):
DBBLISS_E2E_ENGINE=postgres DBBLISS_E2E_CS="$DBBLISS_TEST_PG" DBBLISS_E2E_PW_ENV=DBBLISS_TEST_PG_PASSWORD \
  nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/e2e_test.lua
nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/results_test.lua
```

Rebuild with `scripts/build.sh` after any backend change before you run the cancel suite: it
drives the published binary, not the project.
