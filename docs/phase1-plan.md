# Next steps: Phase 0 review, then Phase 1

## Context

Phase 0 (cancel spike) is done for SQL Server and PostgreSQL on Linux and Windows.
- **Tests:** CI is green, with 31 real-database scenarios plus 9 in-process and 4 Lua tests.
- **Models:** the formal models in `spec/` pass.
- **Out of scope:** DB2 for i is deferred (decision 16).

The project plan says: deliver code, tests, a README section and decisions to review, then **stop
after each phase for review**. So the next step is the user's review, not Phase 1 code. This plan
covers that gate and then how Phase 1 is built, milestone by milestone, in the way that has worked
so far:
1. Model the risky part (TLA+ or Quint).
2. Write a failing test.
3. Fix or build.
4. Get CI green on both platforms.
5. Stop at the end of the phase.

## Step 0: Phase 0 review (the user's step, needed before any Phase 1 code)

1. **Read `docs/phase0-decisions.md` (1–21)** and accept or reject each one. Most worth a look:
   - #1: Npgsql cancel deviates from the plan.
   - #7: two extra round trips per PostgreSQL statement for the transaction probe.
   - #8: a SQL Server cancel drains buffered rows, which costs 200–350 ms.
   - #12: no xUnit.
   - #19 and #21: the orphan sweep ends sessions automatically.
   - #20: the backend is started detached on Windows.
2. **Optional, Windows checks CI doesn't cover:**
   - `docker compose up` under Docker Desktop or Rancher Desktop, then the README test commands.
   - One interactive session: `:Dbbliss connect`, `exec`, `cancel`, then quit Neovim mid-query.
3. **Rejected decisions** are fixed first, as Phase 0 follow-ups.

## Phase 1: core query loop (SQL Server and PostgreSQL)

Ordered so that each milestone is usable on its own and the riskiest protocol change comes early.
Every milestone ends with tests on both platforms and a commit; the phase ends with a review stop.

**M1. Statement splitting and execution scope** (backend, new RPC `script/split`)
- The backend splits buffer text into statements: `GO` lines for SQL Server, semicolons for
  PostgreSQL. Splitting must respect strings, comments, dollar quoting and `[ ]`/`" "` identifiers.
- It returns line and column ranges. Lua picks the statement under the cursor, a visual selection
  or the whole buffer. Splitting is engine logic, so it stays out of Lua (architecture rule).
- Error lines are mapped back to the source buffer: the statement's offset plus the engine's line.
  The PostgreSQL error position (a character offset) is converted to a line.
- Tests: a table of splitter cases (no database needed), and real scenarios checking that the
  reported error line matches the buffer line.

**M2. Paged results and fetch-more** (protocol change: model it first)
- The plan, and decision 9, call for pull paging on top of today's push with backpressure.
  Extend `spec/tla/QueryLifecycle.tla` with "client requests the next page" and keep `NoSilentLoss`
  and cancel liveness. Only then change `ResultStreamer.cs` (`PageSize`, `MaxPagesInFlight`).
- Rows the user hasn't asked for yet wait in the backend, bounded, and the server is paused
  through TCP backpressure.
- The backend writes the full result to CSV itself (`query/export`), so export never goes through Lua.
- Tests: the model's variants fail as documented; a scenario fetches page 3 after a stall; a
  100M-row export can be cancelled.

**M3. Results buffer** (Lua only, no UI plugin dependencies)
- Aligned table with column widths taken from the first page and capped. Cell-wise navigation.
  Yank a cell, row or column.
- Multiple result sets. A messages pane for NOTICE, PRINT and RAISERROR with severity. Row count
  and elapsed time.
- Shows a capped window of rows (configurable). Moving past the end fetches more (M2).
- Tests: headless-Neovim tests with the stub backend (the `tests/nvim/client_test.lua` pattern).

**M4. Safety: transactions, prod, statusline**
- Prompt before closing a buffer, disconnecting or quitting Neovim (`VimLeavePre`) while a
  transaction is open. The disconnect prompt already exists; buffer close and quit are new.
- Prod connections (`env = 'prod'`): confirm before any non-SELECT statement. The backend
  classifies statements per engine, so a CTE that writes or `SELECT … INTO` counts as a write.
- Statusline component: connection, env tag (prod in red), open-transaction indicator,
  running-query indicator.
- Model the quit/close prompt in the Quint model (`spec/quint/client.qnt`), since it touches
  `NoSilentRollback`.
- Tests: classifier table; Lua tests for prompts and statusline; Quint variants.

**M5. Credentials** (no new dependencies)
- Windows Credential Manager through P/Invoke (`CredRead` in advapi32).
- Linux: `pass` and `secret-tool` (libsecret) through their command-line tools.
- Integrated auth: SSPI on Windows and Kerberos on Linux. Both drivers support it already
  (`Integrated Security=true`); only config and docs are needed.
- `password = { credman = …, pass = …, libsecret = … }`, alongside the current `{ env = … }`.
- Tests: Windows CI writes a credential and connects with it; Linux CI does the same with
  `pass` (gpg in CI).

**Phase 1 end:** README section, a decisions list, the results doc, `CLAUDE.md` status. Then stop
for review.

## Open choices (my defaults; the user can change them at the review)

- **Milestone order** M1→M5, as above. Credentials last, because `{ env = … }` works meanwhile.
- **Batch execution stops at the first error,** in both engines, and reports which statement failed.
- **Results rendering:** plain buffer text plus highlights (extmarks), with no floating windows
  except for errors.

## Verification, per milestone

- `dotnet run --project backend/tests/Dbbliss.ProtocolTests`, the Lua tests, and `spec/check.sh`
  when a model changed.
- The real-database suite locally (`docker compose`, `scripts/build.sh`, then the cancel suite).
- Push, then `gh run watch`: Windows and Linux green before the next milestone starts.
