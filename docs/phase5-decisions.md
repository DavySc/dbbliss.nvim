# Phase 5 decisions

For the user's review, continuing the numbering of `phase4-decisions.md` (51–60). Built
specification-first: requirements `HLR-PLAN-1..8`, `LLR-PLAN-9..12`, `HLR-COMP-1`, `LLR-COMP-2..5` before
the tests; every test was run and seen to fail (`NotImplementedException`, `Unknown method catalog/names`,
`module 'dbbliss.plan' not found`) before the code existed. An actual plan runs a statement, so it was
modelled first: `spec/tla/Operations.tla` gained `StartPlan` and three invariants (`PlanNeverCommits`,
`PlanEndsUndone`, `NoUnconfirmedWritePlan`); two new variants (`Ops_PlanAutocommit`, `Ops_PlanNoConfirm`)
must fail, and do (`spec/check.sh`).

This phase also began with an inspection of the quality harness, which found two defects in the tooling
itself and changed how the rest of the phase was verified: see 67.

**61. A plan runs on a session of its own, as an operation of its connection.** Not on the user's session:
an actual plan must not touch the user's transaction, and an aborted transaction must not stop a plan. The
price: **what exists only in the user's session is invisible to the plan** (temporary tables, `SET` options,
`search_path` changes, session variables, a `USE` on SQL Server). A statement that depends on them fails in
the plan with the server's own words. The plan session carries the connection's `application_name`
(PostgreSQL, so the orphan sweep knows it). A plan is an operation like a backup (decision 55): one per
connection, refused while another runs, cancelled and awaited on shutdown, and it does not hold the query
lease (queries keep working while a plan runs).

**62. An actual plan always ends undone, and runs a write only with the user's word.** The plan session
sends `BEGIN`, runs `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` (PostgreSQL) or the statement under
`SET STATISTICS XML ON` (SQL Server), and sends `ROLLBACK` in a `finally`. A rollback that cannot be sent
is logged; closing the session rolls it back (the model's `PlanEndsUndone`). The backend refuses an actual
plan of a statement `StatementClassifier` does not call a read unless the request says `confirm_execute`;
the client asks first, **naming the statement and saying it will be run and rolled back**, and sends nothing on No.
A statement the backend did not classify counts as a write. The backend is the authority: a client that
believes a statement is a read is still refused. An estimated plan never needs it (nothing runs).
What a rollback does **not** undo: sequence values consumed (`nextval`, identity), calls to the outside
(`dblink`, linked servers, `xp_cmdshell`, `COPY ... PROGRAM`), and anything a trigger or function does outside
the database; locks are held while it runs. Statements that cannot run inside a transaction (`VACUUM`,
`CREATE DATABASE`, `CREATE INDEX CONCURRENTLY`) fail in an actual plan, as the server says. The question
says the first two; the rest is here.

**63. Own cost and own time.** Servers report cost and time cumulatively (a node includes its children).
A node's *own* figure is its figure minus its children's, never below 0. PostgreSQL: time and rows are
per loop, and are multiplied by the loops to totals. SQL Server: rows are summed over threads, time is the
largest thread's. A node without a time (SQL Server's Compute Scalar) is looked through to what is below
it. **The hottest node** is the one with the greatest own time (actual plan) or own cost (estimated);
a tie goes to the first node in plan order. It is a pointer to where to look, not a diagnosis: a
parallel plan's own times are not additive, and cost units differ between engines.

**64. The viewer.** One buffer per plan (`filetype dbbliss-plan`), a tree with a line per node:
operator, detail (relation, index, join and filter conditions), then cost, rows (`estimated → actual`,
with a factor `×50` when they differ by more than 5%), loops, time (own time in brackets), and `◀ hottest`.
Keys: `<CR>`/`o` collapse or expand a node, `zR`/`zM` all, `x` the engine's raw plan (JSON or XML) in a
scratch buffer, `c` cancel a running plan, `q` close. Warnings (sort spilled to disk, hash batches,
fewer workers than planned) sit under their node; the engine's plan-level notes (missing index) at the
end. `:Dbbliss plan [estimated|actual]` takes **exactly one statement** (under the cursor, or a range
holding one); several is an error, not a guess. A SQL Server batch can yield several plans: all are shown.
Plans are not saved; the raw text can be saved from the scratch buffer.

**65. Completion reads the catalog, never waits, and may be stale.** `catalog/names` (tables, views,
functions, procedures; at most 20 000, capped on the backend, `truncated` says so and the user is told
once) and `catalog/columns` (a name the server resolves, so `search_path` and quoting are the server's)
run on the catalog session. System schemas are left out. The Lua core answers **from a per-connection
cache**; the first request starts the load and offers the items when it ends; requests during a load
share it; a failed load offers nothing and is retried by the next request. The cache is dropped on
disconnect and when the schema tree is refreshed (`R`). **There is no expiry: after DDL, until the
tree is refreshed, completion offers the old names.** What it reads: the text of the statement the cursor
is in (found by `;` and blank lines, approximately, because it must be synchronous; the backend's
splitter is asynchronous), the nearest clause keyword for what is wanted (object names after
`FROM`, `JOIN`, `UPDATE`, `INTO`, `TABLE`, `TRUNCATE`, `VIEW`, and after a comma in a `FROM` list;
columns elsewhere), the tables and aliases the statement names, and `x.` as an alias, a table, or a schema.
Identifiers that need quotes are inserted quoted for the engine (a modest reserved-word list; a name
outside `public`/`dbo` is inserted schema-qualified). **Not done:** keywords, functions' parameters,
columns of subqueries, CTEs and derived tables, aliases of `INSERT ... SELECT` targets, `USING`
column lists, completion across databases.

**66. The adapters are thin, and untested against the real plugins.** `dbbliss.completion.blink` and
`dbbliss.completion.cmp` convert the core's result to LSP completion items and require neither plugin; a
request outside an SQL buffer, or in one without a connection, answers nothing. **Neither blink.cmp nor
nvim-cmp is installed in CI or here**: the adapters are tested against the documented contract (blink:
`new`, `enabled`, `get_trigger_characters`, `get_completions`; cmp: `is_available`, `get_trigger_characters`,
`complete`) with fake arguments, and the contract was written from memory of those plugins' documentation.
If a plugin changed a field name (blink's `ctx.cursor`, cmp's `params.context.cursor_before_line`) the
adapter answers nothing rather than wrongly; try it in your setup and tell me.

**67. The harness inspection.** Findings, each fixed with a test that failed first (problem reports
PR-024, PR-025):
- `trace.py` attributed to a test the `Verifies:` tags of the test **above** it, if within three lines.
  134 of 255 test-to-requirement links were claimed by tests that do not verify them. No requirement became
  unverified when this was fixed, but many now rest on fewer tests (the matrix lists the single-test ones).
- The cancel suite passed with an engine not configured or a scenario skipped. CI now sets
  `DBBLISS_REQUIRE_ENGINES` and `DBBLISS_ALLOWED_SKIPS`.
- New checks: a Lua suite or protocol test method that no registry runs, a suite CI or the coverage script does
  not run, a problem report naming a test that does not exist, a requirement with no stated source, a
  new backend file outside the coverage baseline, a lowered baseline, a justification for a non-critical file.
- **Mutation checks are now replayed**: `docs/assurance/mutations.json` + `scripts/assurance/mutate.py`
  (CI job `mutation`) break the code in one stated way and require the named test to report `FAIL`
  (a mutant that only crashes the runner is not a kill); the unmutated tests must pass first. 38 mutations are
  registered: 9 plan backend, 5 older safety rules (classifier, drop backup rules, lease), 11 plan UI,
  10 completion, 3 completion backend. The runner found its own bug on its first run (it ran the next
  baseline against the previous mutant's binary). Mutants are written by hand: a missing one is a missing check.
- The assurance scripts have self-tests (`scripts/assurance/tests/`), each check shown able to fail.
- `catalog/names` SQL was mutation-checked by hand against both engines (limit off by one; system filter
  inverted): both killed by the `completion_names_and_columns` scenario.

**Not checked:** plans of statements that use temporary tables or session state (61); effects a rollback
does not undo (62); an actual plan of a very long statement beyond cancel (there is no timeout); SQL Server
below 2016 or PostgreSQL below 12 (plan formats are those of 2022 and 17, from captured fixtures); the plan
viewer at width on a real terminal and with colour schemes other than the default (the highlight links to
`WarningMsg`); interactive use of completion in a real editor session with either plugin; completion on a
very large catalog's latency (the 20 000 cap is untested at its edge on a real database); Windows (CI
only); the e2e tree step on a SQL Server instance that has a second database with a `dbo` schema (a local
artefact: the test expands every node named `dbo`; not seen in CI).
