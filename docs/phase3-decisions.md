# Phase 3 decisions

For the user's review, continuing the numbering of `phase2-decisions.md` (35–42). Phase 3 was
built specification-first: requirements `HLR-ADM-1..8` and `LLR-ADM-9..12` in
`docs/assurance/requirements.md` were written before the tests, the protocol tests were run and seen to
fail (`Unknown method sessions/list`), then the code was written. Mutation checks are listed at the end.

**43. Session calls use the connection's catalog session** (decision 35). They work while the user's
query is running, paused or in an aborted transaction, and share the catalog lock and its 60 s timeout,
so a tree load and a kill never run at once. PostgreSQL's catalog session is READ ONLY; that does not
stop `pg_cancel_backend` / `pg_terminate_backend` (function calls), which the real-server scenarios
confirm.

**44. One row shape for both engines**: id, user, database, host, application, state, duration, wait,
blocked-by, open-transaction flag, query, plus an opaque identity token. Choices a reader may disagree
with:
- Only user sessions: PostgreSQL `backend_type = 'client backend'`, SQL Server `is_user_process = 1`.
- Duration is the running time of an active statement, otherwise the time since the last state
  change (PostgreSQL) or the end of the last request (SQL Server). The server's clock, not Neovim's.
- Wait: PostgreSQL only while `active` (an idle session's `ClientRead` is noise); SQL Server `wait_type`.
- Query: PostgreSQL's `query` column (truncated by the server's `track_activity_query_size`); SQL Server
  the batch text of the current or most recent request, cut at 4000 characters, not the single statement.
- State words are each engine's own (`active`, `idle in transaction`; `suspended` for a SQL Server
  `WAITFOR`), not normalised.
- Visibility is the server's: a PostgreSQL role without `pg_read_all_stats` sees only its own
  sessions (the server blanks other rows, including `backend_type`, so they drop out of the filter); a
  SQL Server login without `VIEW SERVER STATE` sees only its own session.

**45. SQL Server cannot "cancel query".** It has no server-side way to stop another session's
statement and keep the session; only `KILL`. `sessions/cancel` is refused (code 1008) before anything
is sent, `can_cancel` is false in the list, and the UI says to terminate instead. Rejected: mapping
cancel to `KILL` — it would silently do something more destructive than asked.

**46. An action names its target by id and start time.** The list gives each row an identity token
(PostgreSQL `backend_start`, SQL Server `login_time`, round-trip format). The action matches both in
the statement that acts: PostgreSQL in one statement, so a reused pid is never signalled; SQL Server
checks, then runs `KILL`, which leaves a window of one round trip. `KILL` takes no parameter, so it is
built from an integer that passed `int.TryParse` with `NumberStyles.None`.

**47. The connection's own sessions are refused** (the user's and the catalog session), by the backend,
whatever the client does. Cancelling one's own query goes through `cancel`, which has the lifecycle
bookkeeping (`spec/tla/QueryLifecycle.tla`); killing it from the side would leave that bookkeeping
believing a query is running.

**48. Terminate does not wait, and status is manual.** `sessions/terminate` returns when the server
accepted the signal. `s` (`sessions/status`) reports presence on both engines and, on SQL Server, the
rollback progress from `KILL n WITH STATUSONLY` (captured from the server's message on the catalog
session; the real scenario saw "transaction rollback in progress. Estimated rollback completion: 0%.
Estimated time remaining: 11 seconds."). No automatic polling, no auto-refresh of the list.

**49. Confirmation.** Cancel and terminate always ask; default No; the question names id, user,
database, host, state, duration and the query's first 120 characters; terminating a session with an
open transaction says it is rolled back; on a `prod` connection the question starts with PROD and the
button reads "... on PROD". Not done: typing the session id or database name (Phase 4 does that for
drops).

**50. The formal models were not extended**, on purpose. The design adds no new concurrency or
transaction state: it reuses the catalog lock (decision 35) and acts on other sessions only. The one
interaction, a session killed under a user query of the same server by *another* tool, ends the
user's query as an ordinary database error, which `spec/` already covers as a failed query. If a
later phase lets dbbliss kill its own other connections, check that against `QueryLifecycle.tla`.

**What the real servers corrected** (the scenarios, not my reading of the documentation):
- SQL Server's refusal of `KILL` for a login without `ALTER ANY CONNECTION` is error 6102, not 300.
- A PostgreSQL role must be in `pg_read_all_stats` to see other users' sessions at all.
- A `WAITFOR` shows as `suspended`, not `running`.

**Mutation checks** (break the code once, watch a test fail): 19 mutations in the backend and the
Lua UI, each caught by the test named in the commit message; and, on both real servers, removing the
identity condition from the SQL made `sessions_list_cancel` fail (the wrong-identity request killed the
victim).

**Not checked:** SQL Server before 2016 (`DATEDIFF_BIG`), Azure SQL, PostgreSQL before 13, MARS
sessions beyond the `TOP (1)` guard, sessions of other databases' catalogs, a login that holds
`VIEW SERVER STATE` but hits `KILL` on a system process, Windows Credential Manager logins for the
limited-user scenario (the SQL Server variant is skipped when the runner refuses SQL logins),
interactive use on a real Windows desktop, a very long session list (no paging), and sorting or
filtering the list.
