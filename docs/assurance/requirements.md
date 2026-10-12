# Requirements

Every requirement has an ID and is verified by at least one test; the test carries a
`Verifies: <ID>` comment and `scripts/assurance/trace.py` checks both directions
(`docs/assurance/traceability.md` is generated from it). IDs are never reused. A requirement is
changed by editing its text here in the same commit as its tests; one that is dropped is moved to
"Withdrawn" at the end, never deleted.

- **HLR** (high-level): what the system shall do for the user, from the priorities in `CLAUDE.md`
  (reliability, performance, Windows first) and the plan. **LLR** (low-level): design decisions
  detailed enough to test directly; each names the decision in `docs/phase*-decisions.md` it comes
  from.
- Verification methods: **T** test (a suite named in the matrix), **M** formal model
  (`spec/`), **I** inspection (a review record in `docs/assurance/reviews/`), **A** analysis (a
  build rule or script). A requirement checked only by M, I or A says so in its text.
- "Server" means the database server; "backend" the .NET process; "client" the Lua plugin.

## Cancel

### HLR-CANCEL-1
A user cancel of a running query shall reach the server as the engine's protocol-level cancel
(PostgreSQL CancelRequest, SQL Server attention) and shall never be carried out by closing the
connection. Source: priorities; phase 0 decisions 1, 2.

### HLR-CANCEL-2
After a cancel the server shall stop executing the statement, and the backend shall report the
outcome as `cancelled` only when the driver has reported the end of the query. Source: priorities.

### HLR-CANCEL-3
A cancel shall not discard rows the server had already produced: rows read before the driver reports
the cancel are delivered, and rows beyond the overflow cap are reported as truncated, not dropped
silently. Source: phase 0 decision 8; `ResultStreamer`.

### HLR-CANCEL-4
A cancel shall stop the server while the client is not reading (stalled Neovim) and while a paged
result is paused for lack of credit. Source: phase 0 decision 9; phase 1 decision 25; models
`QueryLifecycle.tla` (`CancelStopsServer`).

### HLR-CANCEL-5
A cancel shall be re-sent until the query ends, and the user shall be warned (not escalated) when
the server has not acknowledged it within 10 seconds. Source: phase 0 decision 2.

### HLR-CANCEL-6
A cancel requested immediately after an execute (before the statement has started on the server)
shall still end the query as cancelled. Source: phase 0 results.

### HLR-CANCEL-7
A cancel inside a transaction shall leave the transaction as the server has it (aborted or active)
and report that state; the backend shall neither commit nor roll back on its own. Source: phase 0
decisions 6, 7.

## Transactions

### HLR-TX-1
The backend shall never commit on its own. `disconnect` shall be refused while the server reports a
transaction (active, aborted, or unknown when the engine cannot ask) unless the caller passes
`rollback = true`. Source: priorities (no surprise commits); decision 6.

### HLR-TX-2
The transaction state reported after every statement and before every transaction call shall be the
state the server reports, not a state the backend tracks; a transaction opened or ended by typed
SQL is treated the same as one opened through the API. Source: decision 7; model `client.qnt`.

### HLR-TX-3
`commit` on an aborted PostgreSQL transaction shall be refused (the server would silently roll
back). Source: decision 6.

### HLR-TX-4
On shutdown the backend shall cancel running queries and close every connection, so open
transactions are rolled back by the server, and shall do so within a bounded time. Source:
decisions 6, 13, 20.

### HLR-TX-5
Quitting Neovim while a transaction is open or an operation is running shall ask the user first;
cancelling the question shall keep Neovim open. Source: phase 1 decision 31; model `client_quit.qnt`
(variants `QuitNoPrompt`, `QuitViewOnly` fail `NoSilentRollback`).

### HLR-TX-6
Closing a buffer that ran statements on a connection with an open transaction shall ask, and shall
say that closing does not end the transaction. Source: phase 1 decision 32.

## Concurrency

### HLR-CONC-1
A connection shall run one operation at a time; a second `execute`, a transaction call or a
`disconnect` while one is running shall fail as `connection_busy`, not queue or wait. Source:
decisions 5, 25.

### LLR-CONC-2
A connection's lease shall be released under the output lock immediately before the operation's
report (`query/done` or the response) is written, so that a request sent the moment the client reads
the report finds the connection free and no stale report follows a rollback. Source: decision 17;
`CLAUDE.md` rules.

### HLR-CONC-3
The client shall refuse `connect` for a name that is connecting, and for a name that is connected
it shall open no second session: the connection already open becomes the current one. Source:
decision 18; PR-027.

## Result data

### HLR-DATA-1
Values shall reach the client without loss or reinterpretation: 64-bit integers beyond 2^53,
decimals and floats that a Lua double cannot hold travel as strings, binary as `0x` hex, dates and
times in a fixed text format, independent of the machine's locale; arrays, key-value maps and bit
strings travel as their JSON or bit text, and any other type as its own text, never as the name of
a .NET type. Source: decision 10; problem report PR-020.

### HLR-DATA-2
When the client stops reading, the backend shall stop reading from the server after a bounded
number of unsent pages (8 pages of 500 rows) instead of buffering without limit. Source: decision 9.

### HLR-PAGE-1
With a row window, the backend shall send that many rows, then announce `query/paused` and stop
reading until `fetch` grants more rows; the paused query remains running and holds its connection.
Source: phase 1 decision 25; model `QueryLifecycle.tla` (`ClientFetch`, `credit`).

### HLR-PAGE-2
Cancelling a paused query shall end it without losing rows: rows that arrived after the cancel are
counted and reported, not shown. Source: decision 25.

### HLR-PAGE-3
Without a window the backend shall stream every row. Source: decision 9.

### HLR-EXPORT-1
`execute` with an export path shall write the first result set as RFC 4180 CSV (UTF-8 without BOM,
CRLF, NULL as an empty field) from the backend, refuse to overwrite an existing file unless asked,
and mark a cancelled or failed export incomplete. Source: decision 27.

## Scripts and safety

### HLR-SCRIPT-1
The backend shall split a script into statements per dialect (PostgreSQL at semicolons, SQL Server
at `GO` lines with an optional repeat count) without splitting inside strings, comments, quoted
identifiers or dollar quoting. Source: decision 22.

### HLR-SCRIPT-2
A script shall stop at the first statement that does not complete and say which one stopped it.
Source: decision 23.

### HLR-SCRIPT-3
An error shall be mapped to the buffer line (statement start line plus the engine's line, or the
converted character offset on PostgreSQL). Source: decision 24.

### HLR-PROD-1
On a connection marked `prod`, a script containing a write shall ask for confirmation once, listing
up to five writes; reads and session settings shall not ask; a statement the backend did not
classify shall count as a write. Source: decision 30.

### LLR-CLASS-1
A statement shall be classified a read only if it starts with SELECT, WITH, VALUES, TABLE, SHOW,
EXPLAIN or DESCRIBE and contains none of INSERT, UPDATE, DELETE, MERGE, INTO, CALL, EXEC, TRUNCATE,
DROP, ALTER, CREATE; COMMIT is a write. Source: decision 30.

## Credentials

### HLR-CRED-1
A literal password shall be rejected by the client and by the backend; a connection shall carry at
most one password reference of `env`, `credman`, `pass` or `libsecret`. Source: decisions 11, 34.

### LLR-CRED-2
Credential tools shall run without a shell, with a 60 second timeout, and their standard output
shall never appear in an error message or the log. Source: decision 34.

### LLR-CRED-3
A Windows Credential Manager blob shall be decoded as UTF-16 if it contains NUL bytes, else UTF-8.
Source: decision 34.

## Process lifetime and platforms

### HLR-LIFE-1
When the backend's stdin closes, or it receives SIGTERM, or Neovim exits or is killed, it shall
cancel running queries and close its connections so the server ends the sessions. Source: decisions
13, 20.

### HLR-LIFE-2
A PostgreSQL session left by a hard-killed backend shall be ended on the next connect, and only a
session provably belonging to a dead backend instance on this machine shall ever be ended. Source:
decisions 19, 21; `CLAUDE.md` rules.

### LLR-LIFE-3
An instance shall be considered dead only if its process is gone or has a different start time; a
state file or process that cannot be read shall never be considered dead. Source: decision 21.

### HLR-PLAT-1
The plugin shall refuse to start on a Neovim older than 0.10. Source: README.

### LLR-PG-1
New PostgreSQL sessions shall get `client_connection_check_interval = 2000` (configurable, 0 = off)
a negative value shall be refused at connect, and a warning shall be given when the server cannot
enable it. Source: decision 3; problem report PR-022.

### LLR-SESS-1
A new session shall name itself `dbbliss.nvim` (PostgreSQL: `dbbliss.nvim <instance id>`, which the
orphan sweep relies on) unless the user's connection string already sets an application name, which
shall be kept. Source: decision 21.

### LLR-MSSQL-1
New SQL Server sessions shall apply `SET ARITHABORT ON` and the user's `mssql_set_options`; a name or
value that is not a plain SET option shall fail the connect. Source: commit "SQL Server: start
sessions with ARITHABORT ON".

## Protocol

### LLR-WIRE-1
The backend shall answer a request that is not valid JSON with a parse error, one that is not a
JSON object or has no method with an invalid-request error, and one with an unknown method with a
method-not-found error naming the method, and shall keep serving. Blank lines shall be ignored, and
a request without an id shall be performed without a response. Source: JSON-RPC 2.0; decision 10.

### LLR-WIRE-2
A request with a missing or invalid parameter (unknown engine, missing sql or text, a window below
1, an export together with a window, an export without a path or that is not an object, fetch of
fewer than 1 row, a query id already in use) shall get
an invalid-params error that names the parameter; an unknown connection shall get its own error
code; a request about a query that is not running shall get the answer `not_running`, not an error;
a failed connect shall return the server's message as a database error and leave no session open.
Source: phase 1 decisions 25, 27.

### LLR-WIRE-3
After shutdown has begun, every request except `shutdown` shall be refused with a shutting-down
error, and a repeated `shutdown` shall not fail. Source: decision 13.

### LLR-WIRE-4
A message the server sends (NOTICE, PRINT, RAISERROR) shall reach the client as `query/message`
before the query's `query/done` while a query runs, and as `connection/message` otherwise, with its
severity, text, number and line. Source: decision 29.

### LLR-WIRE-5
When the backend can no longer write to stdout, whichever message failed, it shall stop writing,
shut down cleanly and not throw. Source: decision 13 (Neovim gone).

## Catalog (Phase 2)

### HLR-CAT-1
`catalog/describe` shall return, for a table or view, columns (type, nullability, default,
identity), indexes, constraints, foreign keys out and in, triggers and a row estimate; for a routine,
its parameters and definition; as engine-neutral sections. Source: phase 2 plan; decision 36.

### HLR-CAT-2
The schema tree shall list database, schema and tables/views/functions/procedures, load children
only when a node is first expanded, cache them per connection, and refresh on request without
collapsing open nodes. Source: phase 2 plan.

### HLR-CAT-3
`catalog/script` shall produce a CREATE script that, run after the object was dropped, recreates an
object that scripts identically (round trip). SQL Server scripts shall start with `USE [database]`.
Source: phase 2 plan; decisions 39, 41.

### HLR-CAT-4
Catalog calls shall work while the user's session is busy, has a paused result, or is in an aborted
transaction, by using a second session that is closed with the connection. Source: decision 35.

### HLR-CAT-5
A name that does not exist or cannot be parsed shall produce a user-fixable catalog
error (code 1007) that names the problem, not an internal error. Source: decision 37.

### LLR-CAT-6
A written object name (`db.schema.table`, `"Quoted Name"`, `[Bracketed]`, with doubled closing
quotes) shall parse into its parts unquoted, and malformed names shall fail with a message.
Source: decision 37.

### LLR-CAT-7
The client shall take the name under the cursor, schema-qualified if written so, including quoted
and bracketed parts, and shall bind the info key only in SQL buffers. Source: phase 2 plan.

### LLR-CAT-8
System databases and schemas shall be hidden in the tree unless toggled. Source: decision 42.

### LLR-CAT-9
Catalog SQL shall not use DISTINCT or GROUP BY to remove duplicates. Source: phase 2 plan;
decision 38. Verified by analysis.

## Session administration (Phase 3)

### HLR-ADM-1
`sessions/list` shall return the server's user sessions as engine-neutral rows with id, user,
database, host, application, state, duration, wait, blocking session, open-transaction flag and
query text, plus an identity token for each, and whether the engine can cancel another session's
statement. PostgreSQL reads `pg_stat_activity` (client backends); SQL Server joins
`sys.dm_exec_sessions` to `sys.dm_exec_requests`. Source: phase 3 plan; decisions 43, 44.

### HLR-ADM-2
Session calls shall work while the user's session is busy, has a paused result, or is in an aborted
transaction, by using the connection's catalog session. Source: HLR-CAT-4; decision 43.

### HLR-ADM-3
`sessions/cancel` shall stop the running statement of another session without ending the session
(PostgreSQL `pg_cancel_backend`). An engine with no such server-side action (SQL Server) shall
refuse with a user-fixable error (code 1008) before sending anything. Source: phase 3 plan;
decision 45.

### HLR-ADM-4
`sessions/terminate` shall end another session and roll back what it had open (PostgreSQL
`pg_terminate_backend`, SQL Server `KILL`). Source: phase 3 plan.

### HLR-ADM-5
An action shall only reach the session the caller saw: the request carries the identity token from
the list (PostgreSQL `backend_start`, SQL Server `login_time`), and when no session with that id and
token exists the backend shall refuse with code 1008 and send no signal. Source: priorities (no
surprise damage); decision 46.

### HLR-ADM-6
The backend shall refuse (code 1008) any action on the connection's own sessions, the user's and
the catalog session; cancelling one's own query goes through `cancel`. Source: decision 47.

### HLR-ADM-7
`sessions/status` shall report whether the session is still present and, on SQL Server, the
rollback progress of a killed session (`KILL ... WITH STATUSONLY`). Source: phase 3 plan;
decision 48.

### HLR-ADM-8
An action the server did not carry out (a signal sent to nothing) shall be reported as not done,
and a server error (permissions, for one) shall reach the user as a database error: never a silent
success. Source: priorities (no silent failures).

### LLR-ADM-9
A session id shall be a plain integer where the engine's actions take one, and `KILL` shall be built
only from that integer; anything else is refused with code 1008. Source: decision 46.

### LLR-ADM-10
Session SQL shall not use DISTINCT or GROUP BY to remove duplicates (as LLR-CAT-9). Source: LLR-CAT-9; phase 3
decisions. Method: A (`CatalogSqlRules`).

### LLR-ADM-11
The sessions buffer shall show the rows as an aligned table in the order given, mark the
connection's own sessions, and refresh on request. Cancel and terminate shall ask for confirmation
naming the session, its user and its query, default to No, warn when the session has an open
transaction (terminating rolls it back), and use stronger wording on a prod connection. Nothing
is sent when the user declines. Source: phase 3 plan; decision 49.

### LLR-ADM-12
`:Dbbliss sessions` shall open the sessions buffer for the current connection and complete.
Source: phase 3 plan.

## Management (Phase 4)

### HLR-MGT-1
`backup/start` on PostgreSQL shall run `pg_dump` in custom format into the given file and then
verify the file with `pg_restore --list`. The backup is reported `completed` only when both end with
exit code 0 and the file exists. The result names the file, its size and the tool versions used.
Source: phase 4 plan; decisions 51, 52.

### HLR-MGT-2
`backup/start` on SQL Server shall run `BACKUP DATABASE ... WITH COPY_ONLY, CHECKSUM` and then
`RESTORE VERIFYONLY ... WITH CHECKSUM`, on a session of its own; the path is on the server's file
system. `COPY_ONLY` leaves the backup chain of the database untouched. Source: phase 4 plan;
decision 53.

### HLR-MGT-3
A backup shall not overwrite an existing file unless `overwrite = true`; the refusal (code 1009)
comes before anything runs. Source: priorities (no surprise damage); decision 54.

### HLR-MGT-4
`pg_dump` shall be found at `options.pg_dump` (a file, or the directory holding it) or on `PATH`
(`.exe` on Windows), and `pg_restore` next to it or on `PATH`; a tool that cannot be found is a
user-fixable error (1009) that says where it looked. The password shall reach the tool only through
its environment, never its command line or the log. Source: phase 4 plan; decision 52.

### HLR-MGT-5
An operation shall report its progress (`backup/progress`: phase, text, percent when known) and end
with exactly one `backup/done` (`completed`, `failed` or `cancelled`) that carries the reason on
failure: the tool's exit code and the end of its error output. No failure is silent. Source:
priorities; phase 4 plan.

### HLR-MGT-6
`backup/cancel` shall stop the operation (the tool's whole process tree; or the protocol-level cancel
of the BACKUP statement) and report `cancelled`. A partial file on this machine (PostgreSQL) is
removed. SQL Server's file is on the server, where the backend cannot remove what a cancelled or
failed BACKUP began: `backup/done` then says the file may remain, naming it. A cancelled or failed
backup is never reported verified and never counts for HLR-MGT-9. Source: priorities; HLR-CANCEL-1;
decision 55.

### HLR-MGT-7
Disconnect shall be refused (`connection_busy`) while an operation of the connection runs. Shutdown
shall cancel running operations and wait, bounded, for the tool to end; no tool process outlives the
backend. Verified by model (`spec/tla/Operations.tla`: `NoOrphanTool`, `NoOpOnClosedConn`) and tests.
Source: priorities; decision 55.

### HLR-MGT-8
`management/drop` shall drop a table, view, function, procedure or database only when `confirm_name`
equals the object's name exactly; otherwise it is refused (1009) and nothing is sent. It never
uses CASCADE, and runs on a session of its own, not inside the user's transaction. Source: phase 4
plan; decisions 56, 57.

### HLR-MGT-9
When the connection's `env` is `prod`, or not given, a drop shall require the `backup_id` of a
verified backup of the same database made by this backend process within the last 30 minutes;
without it the drop is refused (1009). Other environments may drop without a backup. Verified by
model (`ProdDropBacked`) and tests. Source: phase 4 plan; decision 58.

### HLR-MGT-10
A drop the server refuses (database in use, objects depending on it, no permission) shall reach the
user as the server's error, and nothing shall be forced: no CASCADE, no kicking other sessions out.
Source: priorities; decision 57.

### HLR-MGT-11
A backup or drop shall be refused (`connection_busy`) while another operation of the same connection
runs. Verified by model (`NoDropDuringOp`) and tests. Source: decision 55.

### LLR-MGT-12
The name of an object in a `DROP` statement shall be the one the server resolves and quotes itself
(`regclass`, `regprocedure`, `quote_ident`, `QUOTENAME`), found by schema and name passed as parameters;
the only names written into statements by the backend are a bracketed database name (`]` doubled) and
string literals (`'` doubled). So a name cannot end the statement it is in. A kind that does not
match what the server found (a view asked for as a table) is refused. Source: decision 56.

### LLR-MGT-13
`backup/defaults` shall return the server's default backup directory for SQL Server and nothing for
PostgreSQL, whose file is on the machine of the plugin. Source: decision 53.

### LLR-MGT-14
`connect` shall take the connection's `env` (dev, test, prod); a missing or unknown value shall be
treated as prod by management operations. The plugin sends the connection's tag. Source: decision 58.

### LLR-MGT-15
The progress window shall be a floating window that shows each phase and its percent as lines, the
final status, and the error text on failure (also as an error notification); it stays until closed
with `q`, and `c` cancels the running operation. Source: phase 4 plan; decision 59.

### LLR-MGT-16
`:Dbbliss drop` shall ask the user to type the object's name and send nothing for any other input;
shall offer a backup first (default yes), and on a prod connection shall run one first and not drop
when it did not complete. Source: phase 4 plan; decisions 58, 59.

### LLR-MGT-17
`:Dbbliss backup [path]` shall dispatch and complete, naming the connection's database from its
connection string; the schema tree shall drop the database or object under the cursor with `D`
(`:Dbbliss drop` is the same from inside the tree and says how from anywhere else). Source: phase 4
plan.

### LLR-MGT-18
The process runner shall deliver output lines as they arrive, keep the end of the error output, end
the whole process tree on cancel, and report a missing executable as an error, not a crash. Source:
HLR-MGT-5, HLR-MGT-6.

## Plans (Phase 5)

### HLR-PLAN-1
`plan/start` shall run on a session of its own, never the user's session or transaction, and end with
exactly one `plan/done` that carries the plan or the reason there is none. Source: phase 5 plan;
decision 61.

### HLR-PLAN-2
PostgreSQL: an estimated plan is `EXPLAIN (FORMAT JSON)`, which does not run the statement; an actual
plan is `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`. SQL Server: estimated is `SET SHOWPLAN_XML ON`
(not run), actual is `SET STATISTICS XML ON` (run, its result sets discarded). Source: phase 5 plan.

### HLR-PLAN-3
An actual plan runs the statement. It shall run in a transaction of the plan session that is rolled
back at the end (completed, failed or cancelled), so that nothing it changed is ever committed by the
backend. Verified by model (`PlanNeverCommits`, `PlanEndsUndone`) and by a scenario that runs an
INSERT, an UPDATE and a DELETE under an actual plan and counts the rows. Source: priorities (no
surprise commits); decision 62.

### HLR-PLAN-4
An actual plan of a statement that is not plainly a read (`StatementClassifier`) shall be refused
(1009) unless the caller says `confirm_execute = true`; the plugin sets it only after asking the
user, who is told the statement will be run. Estimated plans never need it. Verified by model
(`NoUnconfirmedWritePlan`). Source: priorities; decision 62.

### HLR-PLAN-5
`plan/cancel` shall stop a running plan with the protocol-level cancel and report `cancelled`, the
transaction rolled back. Source: HLR-CANCEL-1.

### HLR-PLAN-6
A plan counts as an operation of its connection: it is refused while another operation runs, it
refuses disconnect while it runs, and shutdown cancels it and waits. Verified by model and tests as
HLR-MGT-7 and HLR-MGT-11. Source: decision 61.

### HLR-PLAN-7
A plan shall be an engine-neutral tree whose nodes carry the operator, what it works on, estimated
rows, estimated cost, and for an actual plan the rows it produced, its loops and its time; each node's
own cost (and time), which is the node's minus its children's and never negative; the hottest node
(greatest own time for an actual plan, own cost for an estimated one); and for the whole plan its
totals. Source: phase 5 plan; decision 63.

### HLR-PLAN-8
Output that holds no plan, or that cannot be read, shall end the plan as `failed` with a message, never
as an empty plan. A SQL Server batch of several statements shall give one plan per statement.
Source: priorities (no silent failures).

### LLR-PLAN-9
The PostgreSQL plan reader shall take operator, relation, alias, index, join type and the conditions
(filter, index, hash, merge, join) from the JSON, estimated rows from "Plan Rows", actual rows as
"Actual Rows" times "Actual Loops", time as "Actual Total Time" times loops, and buffers read and hit.
Source: decision 63.

### LLR-PLAN-10
The SQL Server plan reader shall take each RelOp's physical and logical operator, object and
predicate, rows from `RunTimeCountersPerThread` summed over threads, time as the greatest
`ActualElapsedms` of any thread, and warnings. Own time is the node's minus its children's (row mode;
batch mode times are not comparable and are shown as given). Source: decision 63.

### LLR-PLAN-11
The plan viewer shall show the tree collapsible (`<CR>` / `o` one node, `zR` / `zM` all), with
cost, estimated and actual rows (and how far apart, as a factor), loops and time per node; the
hottest node highlighted; the raw plan one key away; the final state of a failed or cancelled plan
named. Source: phase 5 plan; decision 64.

### LLR-PLAN-12
`:Dbbliss plan` shall show the estimated plan of the statement under the cursor (or the range), and
`:Dbbliss plan actual` the actual plan; before an actual plan of a statement that is not plainly a
read the user is asked, naming the statement and saying it will be run and rolled back, and nothing is
sent when the answer is No. Source: phase 5 plan; decision 62.

## Completion (Phase 5)

### HLR-COMP-1
`catalog/names` shall list the tables, views, functions and procedures of a database (schema, name,
kind), system schemas left out unless asked, at most 20 000 and saying when it stopped there; and
`catalog/columns` the columns (name, type) of a table or view the server resolves. Both use the
catalog session. Source: phase 5 plan; decision 65.

### LLR-COMP-2
Catalog SQL for completion shall not use DISTINCT or GROUP BY to remove duplicates (as LLR-CAT-9).
Source: LLR-CAT-9; phase 5 plan. Method: A (`CatalogSqlRules`).

### LLR-COMP-3
The completion core shall tell from the text before the cursor what is wanted: object names after
FROM, JOIN, UPDATE, INTO, TABLE, TRUNCATE and the like; the objects of a schema after `schema.`; the
columns of a table after `table.` or `alias.`, the alias read from the statement the cursor is in; in
other places the columns of the tables that statement names. An identifier that needs quotes is
inserted quoted for the engine. Source: phase 5 plan; decision 65.

### LLR-COMP-4
Completion shall never wait for the server: it answers from the per-connection cache, starts a load
when the cache has no answer yet, and offers the items when the load ends. The cache is dropped on
disconnect; a refresh of the schema tree reloads it (LLR-COMP-11). Source: priorities (nonblocking
UI); decision 65.

### LLR-COMP-5
The blink.cmp and nvim-cmp adapters shall be thin: they call the core, need neither plugin to be
installed, and a request outside an SQL buffer with a connection answers nothing. Source: phase 5
plan; decision 66.

### HLR-COMP-6
Completion shall also offer SQL keywords of the connection's dialect (PostgreSQL, SQL Server): the
opening keywords of a statement where a statement can begin, and clause and expression keywords
elsewhere in a statement, but none after `qualifier.`, in a position where only an object name can
stand (after FROM, JOIN, UPDATE, INTO and the like), or inside a string or comment. Keywords rank
after names and columns. Source: user request, 2026-10-11; decision 68.

### LLR-COMP-7
A keyword shall be inserted in the case the statement is written in: upper case when the statement's
first word is upper case or the typed prefix is (two letters or more), lower case otherwise; the
`completion.keyword_case` option can force `upper` or `lower`. Source: user request; decision 68.

### HLR-COMP-8
When a statement that changes the schema completes (CREATE, ALTER, DROP, SELECT ... INTO, sp_rename)
the connection's completion cache shall be reloaded without the user asking: at once under
autocommit, and when the transaction ends (commit or rollback) otherwise. A reload shall wait for the
transaction to end because the catalog session cannot see uncommitted schema changes (PostgreSQL) and
waits for their locks (SQL Server: a catalog read blocks on another session's uncommitted CREATE TABLE,
holding the connection's catalog lock up to the 30 s catalog timeout). Verified by the model
`CompletionCache.tla` (`NoBlockedRequest`, `NeverFalselyFresh`, `StaleReloads`) and by a scenario that
shows both engines' behaviour with uncommitted DDL. Source: user request, 2026-10-11 ("waiting for a
transaction to end is correct behaviour"); decision 69.

### LLR-COMP-9
`script/split` shall mark each statement with `ddl`: true when the statement's words say it creates,
alters or drops an object, creates a table with SELECT ... INTO, or renames one with sp_rename. It
shall err toward true. Source: user request; decision 69. Method: T (protocol suite).

### LLR-COMP-12
No catalog request for completion shall be sent while a transaction in which this client saw a schema
change complete is open on the connection: not a reload, and not the first load (a request for completion
is then answered without names, and the names load once the transaction has ended). Reloads are not sent
while any transaction is open. Source: user request, 2026-10-11; decision 69. Verified by the model
(`FirstLoadSkipsDdlTx`, `ReloadWaitsForTx`) and tests.

### LLR-COMP-10
A reload shall be stale-while-revalidate: the old names and columns keep being offered while the reload
runs; a successful reload replaces them; a failed reload keeps them, leaves them stale and is not
retried for 10 s; a schema change that completes while a reload runs makes that reload's result stale
(it is used, and another reload follows). Source: priorities (nonblocking UI); decision 69.

### LLR-COMP-11
A refresh of the schema tree shall reload the connection's completion names (not drop them). Names
older than `completion.max_age_s` (default 300; 0 turns it off) shall be reloaded in the background at
the next request, and never while a transaction is open on the connection (the catalog session could
wait for its locks). Columns are reloaded lazily when asked for after the connection's schema changed.
Source: user request; decision 69.

## AI hook (Phase 6)

### HLR-AI-1
`:Dbbliss ask` shall do nothing unless a command is configured (`ai.command`) and the connection has
opted in (`ai = true` on the connection); it shall say which of the two is missing. Source: phase 6 plan;
decision 73.

### HLR-AI-2
On a connection tagged `prod`, or not tagged at all, `ask` shall be refused unless the connection says
`ai = { prod = true }`: schema, SQL and error texts leave the machine. Source: priorities (no surprise
exposure); the prod rule of decision 58; decision 73.

### HLR-AI-3
Before anything is sent the user shall be shown what will be sent (which parts, how large, and the full
text on request) and asked; nothing is sent on No. The question cannot be turned off on a prod or untagged
connection. Source: phase 6 plan; decision 73.

### HLR-AI-4
What is sent shall be limited to: the question, the statement, the definitions of the tables that
statement names, and the last plan and last error of that same statement. Result rows are never sent. The
total is bounded (`ai.max_bytes`) and anything cut is said, in the payload and in the confirmation. Source:
phase 6 plan; decision 73.

### LLR-AI-5
The command shall be run without a shell (an argument list), with the payload on its standard input, bounded
by `ai.timeout_ms`, and be cancellable; a non-zero exit, a timeout and an empty answer shall each be reported
with the command's own stderr, never shown as an answer. Source: priorities (no silent failures); decision 74.

### LLR-AI-6
The answer shall be shown in a scratch buffer of its own. No SQL is executed by `ask`, no result buffer, tree
or connection state is changed, and the command is ended when Neovim quits. Source: phase 6 plan; decision 74.

### LLR-AI-7
The tables of the statement shall be found from its text (as completion does) and their definitions read with
`catalog/script` on the catalog session; a table that cannot be read is named as missing, not skipped silently.
At most `ai.max_tables` are included, and the rest named. Source: phase 6 plan; decision 75.

### LLR-AI-8
`:Dbbliss ask [question]` shall take the statement under the cursor or the range (exactly one), ask for a
question when none is given, and be completed. Source: phase 6 plan.

## Installation with Neovim packages (Phase 7)

"Packages" are Neovim's built-in mechanism (`:help packages`: `pack/*/start`, `pack/*/opt` with `:packadd`) and
`vim.pack` (Neovim 0.12+), which installs into the same layout. The plugin shall need nothing else.

### HLR-PACK-1
The plugin shall load and work from a `pack/*/start/` directory and from a `pack/*/opt/` directory after `:packadd`, with no
change to `'runtimepath'` by the user, and wherever the package sits (a path with spaces, a symlink): the `:Dbbliss` command
exists, `require('dbbliss')` works and the backend is looked for inside the package. Source: user request 2026-10-12.

### LLR-PACK-2
After `vim.pack.add()` installs or updates the plugin, one documented `PackChanged` handler shall be enough to build the backend
(`require('dbbliss.build').run()`), and the plugin shall work in the same session once it is built. Source: HLR-PACK-1.

### LLR-PACK-3
A missing backend shall be reported with the path that was looked for and the fix (`:Dbbliss build`, or the script for the
platform), never as a bare spawn failure. Source: HLR-PACK-1; priority "no silent failures".

### LLR-PACK-4
`build` shall run the platform's build script (`scripts/build.sh <rid>`, `scripts\build.ps1 -Rid <rid>`) in the package
directory without blocking Neovim, refuse with a clear message when `dotnet` is not installed, report the exit code and the
tail of the output when it fails, and report success only if the backend executable exists afterwards. Source: HLR-PACK-1.

### LLR-PACK-5
The package shall carry its help (`doc/dbbliss.txt` with an up-to-date `doc/tags`), so `:help dbbliss` works after a
`:packadd` or `vim.pack.add` without a separate `:helptags`. Source: HLR-PACK-1.

### LLR-PACK-6
`:checkhealth dbbliss` shall report the Neovim version against the minimum, whether the backend executable exists (and where),
whether `dotnet` is available for building, and the platform identifier. Source: HLR-PACK-1.

## Client interface

### HLR-UI-1
Results shall be shown as aligned tables whose columns widen when a later page has wider values,
with long text shortened for display but yanked in full and NULL yanked as empty. Source: decision 28.

### HLR-UI-2
Server messages (NOTICE, PRINT, RAISERROR) shall go to their own pane with their severity, and shall
not interrupt the table. Source: decision 29.

### LLR-UI-3
The statusline function shall show the connection, `PROD`, an open or aborted transaction, and a
running or paused query. Source: decision 33.

### LLR-UI-4
`:Dbbliss` shall dispatch `info`, `tree` and `script` and complete their names. Source: phase 2 plan.

### LLR-UI-5
`:Dbbliss use <name>` shall bind the current buffer to an open connection and make it the default
connection (LLR-UI-6); `:Dbbliss use -` shall do the same for the connection that was the default
before the present one. A name that is not open, or `-` with no previous connection, shall be
refused with a message and change nothing. The names it completes are the open connections.
Source: PR-027; user suggestion.

### LLR-UI-6
The client shall remember the order in which connections were made the default (by connecting, by
connecting again to an open name, and by `:Dbbliss use`), most recent on top, as a stack; the top is
the default connection. `:Dbbliss disconnect [name]` shall close the named connection, or the one
the current buffer is bound to (the default when it is bound to none), and take it off the stack;
when it was the default, the connection now on top becomes the default, and with none left there is
no default. Closing a connection that is not the default shall not change the default. Source:
PR-027; user suggestion.

### LLR-UI-7
A buffer shall run on the connection it is bound to. Connecting from a buffer, `:Dbbliss use` and
the first statement command in an unbound buffer (LLR-UI-8) bind it. `exec`, `plan`, `export`,
`begin`, `commit`, `rollback`, completion and the statusline shall find a buffer's connection by one
rule (its binding, else the default), so that the connection the statusline shows for a buffer is
the one its statements run on, whatever the default is. The plugin's own buffers (`dbbliss://...`)
are not bound. Source: PR-028; decision of the user (per-buffer binding).

### LLR-UI-8
In an unbound buffer a command that runs statements or changes a transaction shall bind the buffer
to the default connection and say so. When that connection is `prod` it shall ask first; if the
answer is no it shall bind nothing and run nothing. With no default connection it shall fail as not
connected. Source: PR-028; the "no surprise" priority of `CLAUDE.md`.

### LLR-UI-9
Closing a connection, and the backend ending, shall unbind every buffer bound to it. A bound buffer
shall never be moved to another connection except by connecting from it, `:Dbbliss use`, or the
explicit bind of LLR-UI-8. Source: PR-028.

## Withdrawn

None yet.
