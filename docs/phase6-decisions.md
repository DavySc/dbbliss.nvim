# Phase 6 decisions

For the user's review, continuing the numbering of `phase5-decisions.md` (61–72). Built spec-first
(`HLR-AI-1..4`, `LLR-AI-5..8`), tests before code, 20 recorded mutation checks. The plan's text was one line ("pipe selected
SQL + table definitions + last plan/error to a configurable CLI command and show the answer in a scratch buffer"), so the
design below is mine and is the thing to review.

**73. What may leave the machine, and when.** `:Dbbliss ask [question]` sends the question, the statement, the `CREATE`
definitions of the tables that statement names, and the last plan and last error **of that same statement** (compared with
whitespace squashed; another statement's error or plan is never sent). **Result rows are never sent**: nothing in the hook reads
them. Literals in the SQL, in the plan and in the error text are sent as they are (an error can quote a key value); that is said
in the confirmation, and not worked around, because redaction would give a false sense of safety. Rules:
- *Opt-in twice.* `ai.command` must be configured and the connection must say `ai = true`.
- *Prod and untagged connections are refused* unless they say `ai = { prod = true }` (the rule of decision 58: untagged is treated
  as prod). Any tag other than `dev`/`test` counts as protected.
- *Always shown first.* A confirmation names the command, the parts, their sizes and anything cut; "Show the text first" opens the
  exact payload in a scratch buffer and asks again; what was shown is byte for byte what is sent. `ai.confirm = false` skips the
  question on `dev`/`test` connections only; protected connections are always asked.
- *Bounded.* `ai.max_bytes` (default 200 000). The question and the statement are never cut (if they alone do not fit, `ask` refuses);
  then error, table definitions and plan are added in that order until the room is spent, and what does not fit is cut or left out
  with a marker in the text and a line in the confirmation. At most `ai.max_tables` (default 10) tables are read; the rest are named.

**74. The command.** `ai.command` is an argument list (`{ "claude", "-p" }`), run with `vim.system` **without a shell**, the text on
its standard input, ended after `ai.timeout_ms` (default 120 s; SIGTERM, then SIGKILL after 2 s). A string (a shell line) is refused.
A non-zero exit, a timeout, a command that cannot start, and empty output are each shown as what they are, with the command's stderr,
never as an answer. The answer goes to a read-only scratch buffer (`filetype dbbliss-ai`, markdown syntax); `q` cancels and closes; a
running command is ended when Neovim quits. `ask` executes nothing and changes no connection, result or tree state. On Windows a tool
installed as a `.cmd` shim needs `{ "cmd", "/c", "claude", "-p" }` (libuv cannot start a `.cmd` directly); that is the user's choice
of command line, and it is still no shell line built from the SQL.

**75. How the context is found.** The tables are read from the statement text with the same tokenizer completion uses (`FROM`,
`JOIN`, `UPDATE`, `INTO`, with aliases), so subqueries, CTEs and views' underlying tables are not followed; a table that cannot be
read (not found, or the catalog session was busy) is named as missing in the payload and the confirmation instead of being skipped.
The definitions come from `catalog/script` on the catalog session (read-only, no rows). The last plan is the plan viewer's rendered
lines (compact, not the raw JSON/XML); the last error is the one the client last saw for that connection.

**76. Not modelled in `spec/`.** Nothing here involves the lease, transactions or concurrency of the backend; the model of the
cache (`CompletionCache.tla`) is not involved. The rules above are checked by tests with recorded mutations instead: 20 mutations
(opt-in, prod refusal, untagged, confirmation, no skip on prod, same-statement filter for error and plan, bounding, the shell-line
refusal, table limit, failure/timeout/empty/start-error reporting, cancel and quit, last-error hand-over), all killed.

**Not checked:** a real model behind a real command (the tests use Neovim itself as the command, echoing its input); `claude -p`
or any specific CLI's behaviour with a 200 KB stdin; Windows `.cmd` shims; what a model does with the context (quality of answers is
not tested); that no secret is ever in a table definition (default values, comments and check constraints in a `CREATE TABLE` are sent);
interactive use of the confirmation dialog (the tests stub it).
