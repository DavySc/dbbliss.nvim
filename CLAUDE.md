# dbbliss.nvim: working notes

A Neovim database client (Lua frontend, .NET 10 backend over line-delimited JSON-RPC on stdio)
for **SQL Server and PostgreSQL**. Priorities, in order: reliability (no silent failures,
protocol-level cancel, no lost results, no surprise commits), performance, Windows first.

## Status (2026-10-03)

**Phase 0 (cancel spike): complete on Linux and Windows, awaiting the user's review.** Phases
1–6 have not started. Stop for the user's review at the end of each phase.

- All 30 real-database scenarios pass on Linux (PostgreSQL 17 and SQL Server 2022 in Docker) and on
  Windows (CI: native PostgreSQL and SQL Server Express). Results: `docs/phase0-results.md`.
  Decisions for the user to review: `docs/phase0-decisions.md` (1–20).
- Windows gaps: Docker Desktop or Rancher Desktop on Windows is untested, and so is interactive use
  on a real Windows desktop.
- **On GitHub** as `git@github.com:DavySc/dbbliss.nvim.git` (`origin`), first pushed 2026-10-06.
  `gh` is installed and logged in. CI (`.github/workflows/ci.yml`) runs every test suite on
  `windows-latest` and `ubuntu-latest` on every push.
- **DB2 for i is deferred** (decision 16). Its engine, scenarios and `docs/db2i-checklist.md` stay
  in the repo, unrun. Don't work on it unless the user brings it back, and don't delete it without asking.
- Formal models in `spec/` (TLA+ query lifecycle, Quint client/transactions) found four bugs after
  the spike. All are fixed and covered by tests (`docs/phase0-results.md`, bugs 5–8; `spec/README.md`).

Already in place for Phase 1:
- Server-aborted transactions are detected, and a disconnect with an open transaction prompts.

Not yet built for Phase 1:
- Prompting on buffer close or Neovim exit.
- Paged results and fetch-more (streaming is push with backpressure).
- The results UI (today a plain text dump).
- Credential stores other than `{ env = "NAME" }`, and integrated auth.

## Rules that are easy to miss

- Allowed dependencies: the database drivers (Microsoft.Data.SqlClient, Npgsql, System.Data.Odbc)
  only. Ask before adding anything. That includes test frameworks: test runners are plain console
  apps, no xUnit (decision 12).
- Transactions are plain `BEGIN`/`COMMIT`/`ROLLBACK` SQL sent by the engines, with no driver
  transaction objects. Every transaction decision asks the server first (decision 7).
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
```

Rebuild with `scripts/build.sh` after any backend change before you run the cancel suite: it
drives the published binary, not the project.
