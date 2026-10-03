# Phase 0 — decisions to review

1. **Npgsql cancel uses `NpgsqlCommand.Cancel()`, not a `CancellationToken`.** This deviates from
   the plan. Both send the same CancelRequest on the wire, but the token path does nothing while
   rows are buffered (see bug 2 in phase0-results.md). All engines now cancel the same way:
   an explicit driver call, re-sent until the query ends.
2. **The protocol cancel is re-sent** 100, 250, 500 and 1000 ms after the first attempt, then every
   second. After 10 s without an acknowledgement the user gets a warning. The backend never
   escalates on its own, for example by killing the connection. The user decides.
3. **PostgreSQL sessions get `client_connection_check_interval = 2000`** (PG 14+), configurable via
   `options.pg_client_connection_check_interval_ms` (0 = off). Without it, a query keeps running
   after the backend is killed hard. When the server cannot enable it, the backend warns at connect.
4. **Connection pooling is off** (SqlClient and Npgsql). A pooled connection outlives "disconnect"
   and keeps its server session and transaction until it is reset.
5. **One running operation per connection.** A second `execute` while one runs is an error
   (`connection_busy`), not a queue.
6. **The backend never commits on its own.** `disconnect` is refused while the server reports a
   transaction (`active`, `aborted`, or `unknown` when the engine cannot ask), unless the caller
   passes `rollback = true`. The Lua side then asks the user. PostgreSQL `commit` on an aborted transaction is
   refused (PG would silently roll back). On shutdown (Neovim exiting), open transactions are
   rolled back by closing the connection. Phase 1 will prompt before quitting with an open
   transaction.
7. **Transactions are plain SQL, and their state always comes from the server.** The engines send
   `BEGIN`/`COMMIT`/`ROLLBACK` themselves and use no driver transaction objects, so a transaction
   the user opens or ends by typing SQL is the same as one from `:Dbbliss begin`. The state is asked
   after every statement and before every transaction call. SQL Server: `@@TRANCOUNT`/`XACT_STATE()`,
   one extra round trip. PostgreSQL: set a transaction-local setting, then read it back. It survives
   only inside a transaction block, and setting it fails with 25P02 in an aborted one. That is two
   extra round trips per statement, with no errors or warnings in the server log.
8. **A cancel does not cut off results.** The backend keeps reading until the driver reports the
   server's cancel, so everything the server produced is delivered. The cost is that a SQL Server
   streaming cancel takes ~200–350 ms instead of ~40 ms (buffered rows are drained).
9. **Push streaming with backpressure, for now.** Rows go out in pages of 500, flushed at least
   every 100 ms, with at most 8 pages in flight. Phase 1's "fetch more on demand" will add a pull
   mode on top. The Phase 0 Lua side just shows the first 1000 rows.
10. **Wire format.** JSON-RPC 2.0, one object per line, `\n` on every platform, UTF-8. Decimals,
    64-bit integers beyond 2^53, and floats are sent as strings so that Lua doubles lose nothing.
    Binary values are sent as `0x…` hex.
11. **Credentials, Phase 0 only:** `password = { env = "NAME" }`, resolved in the backend. A literal
    string password is rejected by both the Lua config and the backend. Credential Manager, `pass`
    and libsecret come in Phase 1 behind the same `{ ... }` reference shape.
12. **The test runner is a plain console app (no xUnit).** This keeps the dependency list at the
    three drivers. The drivers are also used directly by the observer connection.
13. **Phase 0 Lua API:** one `:Dbbliss` command with subcommands (connect, exec, cancel, begin,
    commit, rollback, disconnect, status). On `VimLeavePre` the plugin sends `shutdown` and waits
    up to 7 s for the backend to cancel queries and close connections.
14. **CI uses third-party actions** (`rhysd/action-setup-vim` for Neovim) and, on Windows, the
    runner's preinstalled PostgreSQL plus SQL Server Express from Chocolatey. None of it has run
    yet. The Windows SQL Server setup in particular needs checking on the first push.
15. **Layout:** the backend is published to `bin/<rid>/dbbliss-backend[.exe]` inside the plugin
    directory, where the Lua side looks for it by default. `backend.cmd` overrides that.
16. **DB2 for i is deferred.** The focus is SQL Server and PostgreSQL. The DB2 engine, its scenarios
    and the manual checklist stay in the repository, unrun and outside the Phase 0 gate. It still
    tracks transactions locally and reports `unknown` inside one.
17. **Queries and transaction calls hold the connection until their report is written** (`query/done` or the
    `transaction/*` response), so reports reach Lua in the order the server changed state.
18. **Lua refuses `connect` for a name that is connected or connecting.** Disconnect first. Before,
    the second session silently replaced the first, which stayed open.
