# Formal models

Two models, each with design variants. Every variant except the proposed design must fail, and
`spec/check.sh` checks that it fails the documented way. A model that passes only for the design it
was written for proves little. The failing variants show that the model can see the bug.

```sh
spec/check.sh     # ~3 min; needs Java, $TLA_JAR (default ~/.local/share/tla/tla2tools.jar), quint
```

## `tla/QueryLifecycle.tla`: one query in the backend

The query thread, pull paging (`ClientFetch`: the client allows rows, the query thread waits when it has none), cancel/re-fire, backpressure, the stdout pump, a Neovim that may stall or die, and
the database server. Rows are not counted. Loss is tracked by the only actions that can drop a row.

| Config | Design | Result |
|---|---|---|
| `Proposed.cfg` | the current code | all safety properties, plus cancel/shutdown stop the server even with Neovim stalled forever |
| `ProposedLive.cfg` | same, Neovim keeps reading | a cancelled query always reports `query/done`, and a paused query that is allowed more rows goes on |
| `PausedNoWake.cfg` | a cancel does not end the wait for credit | liveness: a cancelled paused query leaves the server running (SQL Server never sees the attention) |
| `PausedReachable.cfg` | witness | `PausedUnreachable` is violated: the model does reach a paused query |
| `Current.cfg` | streamer before the post-cancel overflow | `NoSilentLoss`: a cancel while Neovim is stalled drops rows the server already sent |
| `BeforeQueue.cfg` | blocking write (Phase 0 bug) | liveness: a stalled Neovim keeps the server running |
| `NoRefire.cfg` | protocol cancel sent once | liveness: a cancel before the statement is on the wire is lost |
| `NoConnCheck.cfg` | no `client_connection_check_interval` | liveness: a killed backend leaves the query running |

## `tla/Operations.tla`: a backup next to disconnect, shutdown and a drop

Phase 4. One connection, one database, one long operation (a backup: `pg_dump` as a child process, or
`BACKUP DATABASE` on a session of its own), and the things that can end its world. Boolean constants select
design variants, as in `QueryLifecycle.tla`.

| Config | Design | Result |
|---|---|---|
| `Ops_Proposed.cfg` | the current code | `NoOrphanTool`, `NoOpOnClosedConn`, `ProdDropBacked`, `NoDropDuringOp`; a cancelled operation's tool does stop; a stopping backend does exit |
| `Ops_NoDisconnectGuard.cfg` | disconnect is allowed under a running operation | `NoOpOnClosedConn` |
| `Ops_NoDropGuard.cfg` | a drop is allowed under a running operation | `NoDropDuringOp` |
| `Ops_NoShutdownCancel.cfg` | shutdown does not cancel the tool | `NoOrphanTool` (a `pg_dump` outlives the backend) |
| `Ops_NoBackupRule.cfg` | a prod drop needs no backup | `ProdDropBacked` |
| `Ops_StaleBackupCounts.cfg` | a backup that has gone stale (time passed) still counts | `ProdDropBacked` |

Not modelled: a *killed* backend (nothing ties `pg_dump` to its parent; `docs/phase4-decisions.md`, 55).

## `quint/client.qnt`: Lua client, connection lease, transactions

The user (connect, exec, cancel, begin/commit/rollback, disconnect), the Lua client's state and
callbacks, the backend's per-connection lease and transaction bookkeeping, and the server's real
transaction state for PostgreSQL and SQL Server. Statements may be `BEGIN`/`COMMIT`/`ROLLBACK`
typed by the user. A query is a single server step here. Streaming belongs to the TLA+ model.

Properties are checked when nothing is in flight, so Lua has heard everything the backend did:

- `NoSilentRollback`: `disconnect` never rolls back a transaction the user was not asked about.
- `NoOrphanSession`: every open server session is one Lua knows about.
- `HonestTxView`: Lua never shows a transaction state the server does not have (`unknown` is allowed).
- `NoDeadConn`, `NoGhostQuery`: Lua does not hold closed connections or queries the backend has finished.

Bounds: 6 user actions, 2 connects. `Proposed` is checked exhaustively with TLC (6.2M states).
The other variants are checked by random simulation, which finds their counterexamples in under 2 s.

| Module | Fixes on | Fails |
|---|---|---|
| `Proposed` | all three (the code now) | nothing |
| `Current` | none (the code before the fixes) | `NoSilentRollback`, `NoOrphanSession`, `HonestTxView` |
| `NoServerTruth` | lease, Lua | `NoSilentRollback`, `HonestTxView` |
| `LeaseBeforeWrite` | server truth, Lua | `HonestTxView` |
| `LuaByName` | server truth, lease | `NoOrphanSession`, `HonestTxView` |
| `QuitNoPrompt` (`client_quit.qnt`) | all three, but Neovim quits without asking | `NoSilentRollback` |
| `QuitViewOnly` (`client_quit.qnt`) | all three, asks only when Lua's view shows a transaction | `NoSilentRollback` (a typed `BEGIN` whose report is still on its way) |

What the counterexamples were in the code before the fixes. All three are fixed now, and each has
tests that failed before the fix (see [Tests](#tests)):

1. **Typed `BEGIN` on PostgreSQL is invisible** (`ServerTruth`). `GetTransactionStateAsync` returns
   `None` when the backend did not open the transaction itself (`_tx is null`), so Lua shows
   `none`. `disconnect` then goes out without `rollback`, the guard (`InTransaction`, also `_tx`)
   lets it through, and closing the session silently rolls the transaction back. On SQL Server the
   probe asks the server, so Lua shows `active` and prompts. However, the API `rollback` does
   nothing to a transaction opened by typed SQL. The fix is to take the state from the server for
   the probe, the disconnect guard and the begin/commit/rollback calls.
2. **The lease is released before the report is written** (`LeaseAfterWrite`). `RunQueryAsync`
   disposes the lease before `query/done`, and `TransactionAsync` disposes it before its
   response. A later operation on the same connection can then run and report first, and the
   stale report arrives last: Lua shows `active` after a `rollback` that worked. The window is
   narrow, because the overtaking operation needs a server round trip. The fix is to release the lease
   once the report holds the output lock, just before it is written. Releasing it after the write,
   as first done, has the opposite race: a client that sends its next request on reading the report
   finds the connection busy. The model treats the write as one step, so it does not see that race.
3. **Connecting a name twice orphans a session** (`LuaById`). `M.connect` overwrites
   `state.connections[name]`. The old session stays open in the backend, with its transaction and
   locks, until the backend exits. Callbacks also find their connection by name (`query/done`,
   `disconnect`), so a stale report can land on the new connection. The fix is to refuse `connect`
   for a name that is connected or connecting, and to key callbacks by connection id.

Assumptions about driver behaviour are marked `ASSUMPTION` in the model. They all concern
`NpgsqlTransaction` objects, which the engines no longer use: transactions are plain
`BEGIN`/`COMMIT`/`ROLLBACK` statements and every decision asks the server. One was settled against
the real driver before the fix: `SELECT 1` on an `NpgsqlTransaction` the server already ended
throws, so after an API `begin` and a typed `COMMIT` the probe said `unknown` and every later
`begin` was refused (`tx_typed_commit_after_api_begin`).

## Tests

The model's properties, checked against the code:

| Test | Runs | Checks |
|---|---|---|
| `backend/tests/Dbbliss.ProtocolTests` | the real backend in-process, fake engine, no database | bug 1 (disconnect guard), bug 2 (stale `query/done`), shutdown closes every session |
| `tests/nvim/client_test.lua` | the real Lua client, stub backend, headless Neovim | bug 3 (no orphaned session), a refused disconnect prompts |
| `tx_*` scenarios in `backend/tests/Dbbliss.CancelTests` | the real backend and drivers against PostgreSQL and SQL Server | bug 1 in the engines: typed `BEGIN`/`COMMIT` seen, API rollback ends a typed `BEGIN`, aborted commit refused |

The stale-report test holds the backend at its `query … completed` log line, which sits between
the probe and the `query/done` write. If that line moves, the test fails with "hook moved" rather
than passing. The same race on the `transaction/*` responses has no such hook and is not tested.
