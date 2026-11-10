# Editor integration decisions

For the user's review. Numbered in the one sequence of decisions; 77 and 78 are used on another branch and 79 is the
install decision, so this one is 80.

**80. Keywords without a connection, and statements for a user's config.** The request (2026-11-10, a review of the
user's Neovim config): most SQL is written with no connection open, and execution often happens elsewhere, so
completion that needs a connection mostly never shows; and a config wants the statement under the cursor (to yank
it, format it, jump between statements) without reaching into private functions.
Done: `completion.offline_engine` (HLR-COMP-13), `dbbliss.statements(bufnr)` (LLR-COMP-14) and
`dbbliss.connection_of(bufnr)` (LLR-UI-10).
Decisions: *(a)* offline completion is off by default: an unconnected buffer still gets nothing unless the user names
a dialect, so nothing changes for anyone who has not asked. *(b)* Offline means keywords only; object names from the
project's DDL files would be a later, separate feature. *(c)* `statements` is a Lua splitter on the completion lexer,
not `script/split`: it must answer at once and without the backend (a text object cannot wait for a process), so it
can differ from the backend's split in corner cases the lexer does not know (PostgreSQL dollar quoting, nested
`/* */` comments). The backend remains the authority for what is run. *(d)* A blank line ends a statement, as it does
for completion's `statement_at`, so the two agree on what "the statement under the cursor" is.
Not checked: dollar-quoted bodies (a `;` inside `$$ ... $$` splits the statement), and Windows line endings beyond
treating `\r` as whitespace.
