# Phase 4 decisions

For the user's review, continuing the numbering of `phase3-decisions.md` (43–50). Built
specification-first: requirements `HLR-MGT-1..11` and `LLR-MGT-12..18` before the tests; the protocol
tests were run and failed (`Unknown method backup/start`) before the manager existed. This phase has
real concurrency (an operation running next to disconnect, shutdown and a drop), so it was modelled
first: `spec/tla/Operations.tla`, six variants, five of which must fail (`spec/check.sh`).

**51. A PostgreSQL backup is `pg_dump --format=custom` run as a child process, on this machine; the
file is on this machine.** "Verified" means `pg_restore --list` exits 0 and shows at least one entry:
the archive's table of contents reads. It does not mean a restore was tried (that needs a scratch
database and time); the scenarios do restore into a second database and compare row counts. Whole
databases only: no `--table`, `--schema`, or plain-SQL format.

**52. Tools and credentials.** `pg_dump` comes from `options.pg_dump` (a file or its folder; when it
is set nothing else is searched) or `PATH`; `pg_restore` from the same folder, else `options.pg_restore`,
else `PATH`. `pg_dump` refuses a server newer than itself; that message is passed on, and the version
used is in the result. The password goes in `PGPASSWORD` of the child only (never an argument, never the
log), `--no-password` stops a prompt nobody would see, `PGSSLMODE` follows the connection string's SSL
Mode. Not carried over: client certificates, GSS/SSPI, service files, a host list (the first host is
used). A connection that relies on them needs the tool's own configuration (`.pgpass`, `PG*` variables).

**53. A SQL Server backup is `BACKUP DATABASE ... WITH COPY_ONLY, CHECKSUM, STATS = 5` followed by
`RESTORE VERIFYONLY ... WITH CHECKSUM`, on a session of its own; the file is on the server.** Copy-only
leaves the backup chain alone (a DBA's differential base is not reset). No compression, striping,
encryption or `NAME`. `backup/defaults` gives the server's default folder (`InstanceDefaultBackupPath`,
SQL Server 2019+; older servers answer nothing and the user types a full path). Whether the file exists
is asked with `xp_fileexist` (undocumented, long stable); its "parent directory exists" column is wrong
on Linux (always 0, found by the scenario), so the folder is asked about itself.

**54. No overwrite unless asked.** Checked before anything runs, so the refusal is an answer to the
request (1009), not a later failure. The check and the run are not atomic: a file that appears in
between is overwritten by `pg_dump`, appended to by SQL Server (`NOINIT`).

**55. One operation per connection; operations do not hold the query lease.** Queries keep working
during a backup. A second backup or a drop is refused (`connection_busy`), disconnect is refused, and
shutdown cancels what runs and waits up to 10 s (then logs the ones that did not end). Cancel ends the
whole process tree (PostgreSQL) or sends the protocol-level cancel, re-sent as for queries (SQL Server).
What the model cannot cover:
- A **killed** backend (SIGKILL, crash) leaves `pg_dump` running to its end: nothing ties the child to its parent
  (Linux parent-death signal, Windows job object are not used). It finishes a valid dump or dies on its own; a
  SQL Server BACKUP ends with its session. Not handled.
- SQL Server's partial file after a cancel or failure stays on the server and cannot be removed from here;
  `backup/done` says so and names it (`note`). PostgreSQL's partial file is removed.
- Every `pg_dump --verbose` line becomes a notification (not throttled): a database with very many objects sends very many.

**56. Drop builds no name itself.** The schema and name go to the server as parameters and it answers with
its own quoted spelling (`regclass`, `regprocedure`, `quote_ident`, `QUOTENAME`); that spelling is what is
put in the statement, so a name cannot end it (the scenario drops a table called `we"ird; DROP TABLE ...; --`).
The kind asked for must be what the server found: a view is not dropped as a table. Kinds: database, table,
view, function, procedure (materialized and foreign tables count as view / table). Not dropped from here:
schemas, indexes, sequences, triggers, types, roles. A PostgreSQL function needs its identity (argument
types) when overloaded. The drop runs on a session of its own, so it neither joins nor ends the user's transaction.

**57. Nothing is forced.** No `CASCADE`, no `WITH (FORCE)`, no `SINGLE_USER`. A table other objects
depend on, or a database with a session in it, is refused by the server and its words are shown. End the
sessions with `:Dbbliss sessions` (Phase 3) and drop again.

**58. The prod rule.** A connection tagged `prod`, **or not tagged, or tagged with anything else**, needs
`backup_id`: a completed, verified backup of **the same database**, made through **this backend process on
this connection**, **within 30 minutes**. Otherwise the drop is refused (1009). Consequences to review:
- A connection without `env` used to have no prod rules (Phase 1 asks only when `env = 'prod'`); for backup
  and drop it is now treated as prod. The plugin sends the tag on connect (`env`).
- The backup is of the whole database, also for one table; on a large database that is long. A reconnect, a
  backend restart or 30 minutes starts it over.
- Dev and test connections are offered a backup (default yes) and need none.
Modelled as `ProdDropBacked` (a drop without a fresh backup is the counterexample of two variants).

**59. The window and the questions.** A floating window opens when a backup starts (focus enters it; `q`
closes, `c` cancels), shows the last 12 progress lines with percent where the server gives one, and the end
state in words: completed (size, path, what was checked), FAILED (the tool's exit code and the end of its
error output), cancelled, or the refusal. The end is also a notification, at ERROR level for a failure.
The drop asks for the name with `input()`; escape, an empty line or a different spelling drops nothing and
sends nothing. The backup path is asked with a default: PostgreSQL the working directory, SQL Server the
server's folder.

**60. Mutation checks:** 34 mutations of the backend and the Lua UI, each caught by the test named in the
commit; one first showed a weak mutation (a second `backup/done` made of the same JSON node throws, so
nothing was sent) and was redone with a copy; a hang counts as a failure.

**Found by the real servers, not by reading:** `xp_fileexist`'s parent column; a cancelled SQL Server
BACKUP leaves its file; `pg_dump` 16 cannot dump a 17 server (CI installs the 17 client).

**Not checked:** a restore by the product itself; non-ASCII messages of `pg_dump` on Windows (its output is read as UTF-8; a tool writing the console's code page can show wrong characters in a progress line, the exit code and the file are unaffected); Docker on Windows; `pg_dump` older than 12; a database
with thousands of objects (notification volume); SQL Server below 2016 (`DATEDIFF_BIG` is Phase 3's; here
`InstanceDefaultBackupPath` needs 2019); network paths and UNC backup targets; interactive use on a real
Windows desktop; the Windows job-object / parent-death case in decision 55.
