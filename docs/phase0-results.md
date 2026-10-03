# Phase 0 — cancel spike results

Every scenario checks the server from a second, independent "observer" connection
(`pg_stat_activity`, `sys.dm_exec_requests` + `sys.dm_exec_sessions`, `QSYS2.ACTIVE_JOB_INFO`).
"Server stopped" is the time from the cancel or kill until the observer no longer sees the
statement executing. Raw output of the recorded run: [phase0-results-linux.md](phase0-results-linux.md).

Recorded on Linux x64 (EndeavourOS, kernel 7.2), .NET 10.0.401 SDK, Neovim 0.13-dev,
PostgreSQL 17.11 and SQL Server 2022 (16.0.4295.3) in Docker. 24 scenarios, 0 failures, stable
across 3 consecutive full runs. Re-run on 2026-10-03 after the transaction fixes (below): 30
scenarios, 0 failures.

## Results by engine and platform

| Scenario | PG · Linux | PG · Windows | MSSQL · Linux | MSSQL · Windows |
|---|---|---|---|---|
| Long query cancelled (`sleep_cancel`) | ✅ 30 ms | not run | ✅ 33 ms | not run |
| Cancel right after execute, 20× (`cancel_immediately`) | ✅ ≤ 151 ms | not run | ✅ ≤ 129 ms | not run |
| Earlier batch results survive the cancel (`batch_cancel_keeps_results`) | ✅ | not run | ✅ | not run |
| Cancel during row streaming (`streaming_cancel`) | ✅ 110 ms | not run | ✅ 367 ms | not run |
| Cancel during streaming, Neovim not reading (`streaming_cancel_stalled_client`) | ✅ 57 ms | not run | ✅ 53 ms | not run |
| Cancel inside an open transaction (`tx_cancel`) | ✅ tx → aborted | not run | ✅ tx stays active | not run |
| Same, `XACT_ABORT ON` (`tx_cancel_xact_abort`) | n/a | n/a | ✅ tx rolled back by server | not run |
| Neovim `:qa!` mid-query (`nvim_quit`) | ✅ 51 ms | not run | ✅ 53 ms | not run |
| Neovim killed mid-query (`nvim_killed`) | ✅ 52 ms | not run | ✅ 51 ms | not run |
| Backend stdin closed (`backend_stdin_closed`) | ✅ 54 ms | not run | ✅ 53 ms | not run |
| Backend SIGTERM (`backend_sigterm`) | ✅ 62 ms | n/a | ✅ 54 ms | n/a |
| Backend killed hard (`backend_killed`) | ✅ 1.9 s ¹ | not run | ✅ 92 ms | not run |
| Control: killed, no connection check (`backend_killed_no_conncheck`) | ℹ️ keeps running ² | not run | n/a | n/a |
| Typed `BEGIN` seen, blocks a plain disconnect, API rollback ends it (`tx_typed_begin`) | ✅ | not run | ✅ | not run |
| API `begin`, typed `COMMIT`, then `begin` again (`tx_typed_commit_after_api_begin`) | ✅ | not run | ✅ | not run |
| Commit refused after an error, rollback works (`tx_aborted_commit_refused`) | ✅ tx → aborted | not run | ✅ tx rolled back by server | not run |

¹ Through `client_connection_check_interval = 2000`, which the backend sets on every PostgreSQL
session (PG 14+). The server notices the dead client within one interval.
² Without that setting, `pg_sleep(60)` keeps running for its full minute after the client died.
This is PostgreSQL behaviour. It is why the setting is on by default. A Windows-hosted PostgreSQL
server does not support the setting; the backend then warns at connect.

**Windows: not run.** This machine is Linux only. The suite, the build script (`scripts/build.ps1`)
and the CI job (`.github/workflows/ci.yml`) are written for Windows, but none of them has been run
there yet. Windows is the primary platform, so this is the first thing to do before Phase 1.

**DB2 for i: deferred** (decision 16). It is no longer part of the Phase 0 gate. The engine, its
scenarios and [db2i-checklist.md](db2i-checklist.md) stay in the repository, unrun.

## Transaction state after a cancel

| Engine | Server state after cancel | What the backend reports | Notes |
|---|---|---|---|
| PostgreSQL | `idle in transaction (aborted)` | `aborted` (probed, and confirmed by the observer) | Only `ROLLBACK` works. The backend refuses `commit`: PostgreSQL would silently turn it into a rollback. |
| SQL Server, `XACT_ABORT OFF` (default) | transaction still open, `XACT_STATE() = 1` | `active` | The statement is cancelled; earlier work in the transaction is kept. |
| SQL Server, `XACT_ABORT ON` | transaction rolled back by the server | `none` | The Lua side raises an error: "the server rolled back the transaction". |

In every case the uncommitted insert was gone after rollback: nothing was committed by surprise.

## Bugs the spike found (all fixed, all now regression scenarios)

1. **SQL Server: a cancel is ignored while Neovim isn't reading.** The query thread blocked writing
   to a full stdout pipe, so it stopped reading the TDS socket. The server sat in `ASYNC_NETWORK_IO`
   and never processed the attention. Fix: rows go through a bounded page queue and a separate
   writer, and the wait for a free page slot is cancellable (`streaming_cancel_stalled_client`).
2. **Npgsql: a token cancel never reaches the server while rows are buffered.** Npgsql turns a
   `CancellationToken` into a CancelRequest only while it is blocked on network I/O. With rows
   already buffered, nothing was sent, and disposing the reader drained all of the remaining 100M
   rows. Fix: explicit `NpgsqlCommand.Cancel()`, which sends the same CancelRequest
   (`streaming_cancel`).
3. **Results lost after a cancel.** `SemaphoreSlim.WaitAsync(token)` throws on a cancelled token
   even when the semaphore is free, so result sets the server had already produced (held in its
   send buffer until the batch ends) were dropped (`batch_cancel_keeps_results`).
4. **SqlClient cancel race.** `SqlCommand.Cancel()` does nothing when it lands before the statement
   is on the wire. The backend re-sends the protocol cancel at 100, 250, 500 and 1000 ms, then every
   second, until the query ends (`cancel_immediately`; the worst case was 1009 ms before the faster
   schedule).

## Bugs found after the spike (all fixed, all now tests)

Found by the formal models in [spec/](../spec/README.md) and by the real drivers. Details are in
spec/README.md.

5. **A `BEGIN` typed as SQL was invisible on PostgreSQL**, and on both engines a plain `disconnect`
   then closed the session, silently rolling the transaction back (`tx_typed_begin`).
6. **API `begin` then typed `COMMIT` broke PostgreSQL transactions on that connection.** Npgsql
   kept its `NpgsqlTransaction` object. The probe then failed (`unknown`), and every later `begin`
   was refused (`tx_typed_commit_after_api_begin`).
7. **A stale transaction report could arrive last.** The connection was released before
   `query/done` was written, so a rollback could report first and Lua then showed `active`
   (Dbbliss.ProtocolTests).
8. **Connecting a name twice orphaned the first session** with its transaction and locks
   (tests/nvim/client_test.lua).
