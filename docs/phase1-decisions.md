# Phase 1 decisions

For the user's review, in the style of `phase0-decisions.md`. Each says what was decided, why, and
what it costs. Numbers continue the Phase 0 list (1–21).

## M1: statements and scope

**22. Splitting is the backend's, per dialect (`script/split`).** PostgreSQL splits at semicolons;
SQL Server splits only at `GO` lines (`GO 5` repeats the batch), because T-SQL semicolons are
optional and a batch is the unit the server parses. Both skip strings, comments (which nest),
quoted identifiers, `[ ]` on SQL Server, and on PostgreSQL `E'..'`, dollar quoting and
`BEGIN ATOMIC` bodies. Cost: `;` does not split a SQL Server buffer, so "run the statement under
the cursor" runs the whole `GO` batch. Lua only chooses among the backend's answer
(`lua/dbbliss/script.lua`).

**23. A script runs one statement at a time and stops at the first that does not complete.** The
rest are not run and the results say which one stopped it and how many were left. Chosen over
"carry on", because a script's later statements usually depend on the earlier ones. A `GO 5`
batch counts five times.

**24. Error lines.** The backend adds the statement's first buffer line to the engine's line
(`error.buffer_line`, 1-based) and the plugin shows it as a diagnostic. PostgreSQL reports a
character offset, which is exact; it is converted to a line in the backend. **SQL Server reports
the line the failing *statement* starts on, not the line of the bad token** (measured: an
"Invalid object name" on line 2 of a statement reports line 1). Inside a long statement the SQL
Server line is therefore the statement's first line.

## M2: paging and export

**25. Pull paging by a row window, not by pages.** `execute` takes `window` rows (the plugin sends
`results.window_rows`, default 1000). When they are used up the backend flushes, sends
`query/paused` and stops reading; the driver stops draining the socket and TCP backpressure holds
the server. `fetch` grants more. This reuses the slot wait that was already modelled for a stalled
Neovim: a cancel ends the wait, and the rest drains through the overflow path.
Consequences to know:
- **A paused query is still running.** The connection stays busy, a transaction stays open and
  locks stay held until the result is fetched in full, cancelled, or Neovim quits. Disconnect,
  begin/commit and another `exec` on that connection are refused as busy.
- **A script waits on a paused query.** `exec_all` with two big SELECTs stops at the first window
  until it is fetched or cancelled (a cancel also stops the script).
- **Rows that had already arrived when a paused query is cancelled are counted, not shown**
  ("N more rows had already arrived ... not shown"). Up to the overflow cap (200 000) can arrive;
  appending them all would freeze Neovim.

**26. Moving the cursor to the end of a paused result fetches the next window**, as does `gm` and
`:Dbbliss fetch`. The plan said "moving past the end fetches more".

**27. Export is a query option, written by the backend.** `execute` with `export = { path }`
writes the first result set as RFC 4180 CSV (UTF-8, no BOM, CRLF, NULL as an empty field); the rows
never pass through Neovim. It refuses to overwrite a file unless asked (the plugin asks you). A
cancelled or failed export leaves the file as far as it got and says `complete = false`. Further
result sets are not exported, and the plugin says so. No BOM means Excel may mis-detect non-ASCII
text; import it as UTF-8.

## M3: results buffer

**28. Plain text and highlights, no UI dependencies.** Aligned tables with `│` separators. Widths
come from the first page and **grow** when a later page has wider values (a first version cut
`2500` to `24…`, found by the end-to-end test); text beyond `max_col_width` (40) is shown shortened
but yanked in full. NULL is dimmed and yanks as empty. Each run starts with empty results and
messages buffers; there is no history.

**29. Server messages (NOTICE, PRINT, RAISERROR) go to their own `dbbliss://messages` pane**, not
into the table buffer, so rows stay contiguous (cell navigation maps lines to rows).

## M4: safety

**30. Prod confirmation is by the backend's classification** (`kind` in `script/split`). A
statement is a read only if it starts with SELECT, WITH, VALUES, TABLE, SHOW, EXPLAIN or DESCRIBE
and contains no INSERT, UPDATE, DELETE, MERGE, INTO, CALL, EXEC, TRUNCATE, DROP, ALTER or CREATE
word (so a data-modifying CTE, `SELECT .. INTO` and `SELECT .. FOR UPDATE` ask); SET, USE, BEGIN,
ROLLBACK, PRINT, DECLARE and similar do not ask; **COMMIT does**. Everything else, and anything
the backend did not classify, is a write. One prompt per script, listing up to five writes.
**Not caught:** a SELECT that calls a function with side effects (`nextval`). The prompt is by
connection `env = 'prod'` and applies to `exec`, `exec_all` and `export`, not to the raw Lua
function `require('dbbliss').execute`.

**31. Quitting asks, and an autocmd cannot cancel a quit.** `ExitPre` asks when quitting would
roll back a transaction **or cancel a running query or transaction call** (the second case is
needed: a typed `BEGIN` whose report has not arrived is invisible to the client; the Quint variant
`QuitViewOnly` finds it). Cancel works by leaving a hidden buffer modified for one tick so that
Neovim itself refuses with E37 ("No write since last change"), which is what the user sees next
to the plugin's own message. `:qa!` skips the check, as it skips every other. Modelled in
`spec/quint/client.qnt` (`UQuit`, `AskOnQuit`, `AskWhileRunning`): `Proposed` holds in simulation,
`QuitNoPrompt` and `QuitViewOnly` each fail `NoSilentRollback`.

**32. Closing a buffer asks only for buffers that ran statements on a connection with an open
transaction**, and says closing does not end it (the transaction belongs to the connection).
Cancelling raises an error from `BufDelete`/`BufWipeout`, which Neovim honours.

**33. The statusline is a function** (`require('dbbliss').statusline()`), not an installed
component: connection, `PROD` in red, `TX` / `TX aborted`, and `⏵ running` / `⏸ paused`. Add it to
`'statusline'` or your statusline plugin.

## M5: credentials

**34. Four password references, exactly one per connection:** `{ env }`, `{ credman }` (a *generic*
credential, read with `CredReadW`; the blob is decoded as UTF-16 if it contains NUL bytes, else
UTF-8, because `cmdkey` writes UTF-16), `{ pass }` (first line of `pass show`) and
`{ libsecret = { attribute = value } }` (`secret-tool lookup`). The tools get a 60 s timeout (a
passphrase prompt that nobody sees must not hang a connect), run without a shell, and their
stdout is never put in an error message or the log. Integrated authentication needs no reference:
leave `password` out and use `Integrated Security=true` (SQL Server on Windows: SSPI; on Linux:
a Kerberos ticket from `kinit`). No new dependencies.

## Not done, or not checked

- **The paging protocol is in the TLA+ model, after the code** (the rule in `CLAUDE.md` is to
  model first). `QueryLifecycle.tla` has `credit` and `ClientFetch`; the wait for credit is the
  page-slot wait with the same wake on cancel. `Proposed` holds all safety properties and
  `CancelStopsServer` with Neovim stalled forever; `ProposedLive` also proves a paused query that is
  allowed more rows goes on; `PausedNoWake` (a cancel does not end the credit wait) fails
  `CancelStopsServer`; `PausedReachable` shows the model reaches a paused query. The model counts
  rows 1:1 with pages, as before, and a fetch is atomic (no fetch in flight).
- **`Proposed` in `client.qnt` with the quit action** verifies exhaustively with TLC through Apalache
  (about 2.5 minutes, no violation); the two quit variants live in `client_quit.qnt` and are
  checked by simulation, because Apalache fails on them without a message (see that file).
  `spec/check.sh` now runs in CI as its own job.
- **`libsecret` is tested with a fake runner only** (a D-Bus secret service is not in CI). `pass`
  and Credential Manager are tested for real.
- Windows: SQL Server Express on the GitHub runner is sometimes unreachable (seen once in M1);
  that failed every SQL Server scenario including the Phase 0 ones.
- Interactive use on a real Windows desktop and Docker/Rancher Desktop remain untested.
