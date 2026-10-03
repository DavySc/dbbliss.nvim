# Formal models

Two models, each with design variants. Every variant except the proposed design must fail, and
`spec/check.sh` checks that it fails the documented way. A model that passes only for the design it
was written for proves little. The failing variants show that the model can see the bug.

```sh
spec/check.sh     # ~3 min; needs Java, $TLA_JAR (default ~/.local/share/tla/tla2tools.jar), quint
```

## `tla/QueryLifecycle.tla`: one query in the backend

The query thread, cancel/re-fire, backpressure, the stdout pump, a Neovim that may stall or die, and
the database server. Rows are not counted. Loss is tracked by the only actions that can drop a row.

| Config | Design | Result |
|---|---|---|
| `Proposed.cfg` | the current code | all safety properties, plus cancel/shutdown stop the server even with Neovim stalled forever |
| `ProposedLive.cfg` | same, Neovim keeps reading | a cancelled query always reports `query/done` |
| `Current.cfg` | streamer before the post-cancel overflow | `NoSilentLoss`: a cancel while Neovim is stalled drops rows the server already sent |
| `BeforeQueue.cfg` | blocking write (Phase 0 bug) | liveness: a stalled Neovim keeps the server running |
| `NoRefire.cfg` | protocol cancel sent once | liveness: a cancel before the statement is on the wire is lost |
| `NoConnCheck.cfg` | no `client_connection_check_interval` | liveness: a killed backend leaves the query running |

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
| `Proposed` | all three | nothing |
| `Current` | none (the code today) | `NoSilentRollback`, `NoOrphanSession`, `HonestTxView` |
| `NoServerTruth` | lease, Lua | `NoSilentRollback`, `HonestTxView` |
| `LeaseBeforeWrite` | server truth, Lua | `HonestTxView` |
| `LuaByName` | server truth, lease | `NoOrphanSession`, `HonestTxView` |

What the counterexamples are, in the current code:

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
   after the write.
3. **Connecting a name twice orphans a session** (`LuaById`). `M.connect` overwrites
   `state.connections[name]`. The old session stays open in the backend, with its transaction and
   locks, until the backend exits. Callbacks also find their connection by name (`query/done`,
   `disconnect`), so a stale report can land on the new connection. The fix is to refuse `connect`
   for a name that is connected or connecting, and to key callbacks by connection id.

Assumptions about driver behaviour are marked `ASSUMPTION` in the model. Each is a test to write
against the real driver: Npgsql refusing `BeginTransaction`/`Commit` when the server state
disagrees with the transaction object, and what `SELECT 1` and `Rollback` do on a completed
`NpgsqlTransaction`. The failures above do not depend on them.
