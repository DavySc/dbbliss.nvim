# Problem reports

Every defect that was found after code was written gets an entry here, with the requirement it
violated, how it was found, the fix and the test that now guards it. A defect with no requirement
means a requirement was missing: add it to `requirements.md` in the same commit. Entries are
append-only; a wrong entry is corrected by a new line, not rewritten.

Process: find → write the failing test first when possible (name it in the entry) → fix → the entry
is added in the commit that fixes it. Test-only defects (a flaky or wrong assertion) are logged too
because they weaken the evidence.

| ID | Found by | Defect | Requirement | Fix and guard |
|---|---|---|---|---|
| PR-001 | cancel spike | SQL Server ignored a cancel while Neovim was not reading: the query thread blocked on a full stdout pipe, the server sat in ASYNC_NETWORK_IO | HLR-CANCEL-4 | bounded page queue, cancellable slot wait; `streaming_cancel_stalled_client` |
| PR-002 | cancel spike | Npgsql token cancel never reached the server while rows were buffered | HLR-CANCEL-1, HLR-CANCEL-2 | explicit `NpgsqlCommand.Cancel()`; `streaming_cancel` |
| PR-003 | cancel spike | Results lost after a cancel (`WaitAsync(token)` threw on a cancelled token) | HLR-CANCEL-3 | `batch_cancel_keeps_results` |
| PR-004 | cancel spike | SqlClient cancel race: a cancel before the statement is on the wire does nothing | HLR-CANCEL-6, HLR-CANCEL-5 | protocol cancel is re-sent; `cancel_immediately` |
| PR-005 | formal model | A `BEGIN` typed as SQL was invisible on PostgreSQL; a plain disconnect rolled it back silently | HLR-TX-1, HLR-TX-2 | state asked from the server; `tx_typed_begin`, `typed_begin_blocks_disconnect` |
| PR-006 | formal model | API begin then typed COMMIT broke PostgreSQL transactions on the connection | HLR-TX-2 | no driver transaction objects; `tx_typed_commit_after_api_begin` |
| PR-007 | formal model | A stale transaction report could arrive last (lease released before the write) | LLR-CONC-2 | release under the output lock; `no_stale_report_after_rollback` |
| PR-008 | formal model | Connecting a name twice orphaned the first session | HLR-CONC-3 | `connect_twice_while_connected`, `connect_twice_while_connecting` |
| PR-009 | CI | A fast next `execute` after `query/done` failed as busy (lease released after the write) | LLR-CONC-2 | `execute_right_after_query_done`, `execute_right_after_transaction_call` |
| PR-010 | end-to-end test | A number was shown as `24…`: column widths came from the first page only | HLR-UI-1 | columns widen; `later_pages_widen_the_table` |
| PR-011 | end-to-end test (SQL Server) | A newline in a server error text crashed the results buffer and left the script marked running | HLR-UI-2 | notes split on newlines; `notes_with_line_breaks_become_lines` |
| PR-012 | CI (SQL Server) | SQL Server catalog SQL used the reserved words `identity` and `output` as unbracketed column aliases | HLR-CAT-1 | bracketed; `catalog_browse_describe_script` |
| PR-013 | CI (SQL Server) | A script for an object in another database ran in the connection's database | HLR-CAT-3 | scripts start with `USE [db]` and `GO`; `catalog_browse_describe_script` round trip |
| PR-014 | assurance (test of HLR-CONC-1) | `disconnect` waited for a running or paused query instead of failing as `connection_busy`; the request stayed unanswered and then disconnected later | HLR-CONC-1 | `disconnect` uses `TryAcquire`; `second_operation_is_busy` |
| PR-015 | review | The plugin gave a cryptic `joinpath` error on Neovim older than 0.10 | HLR-PLAT-1 | `setup()` refuses; `setup_refuses_an_old_neovim`; CI job `lua-min-version` |
| PR-016 | CI | Test defect: the end-to-end tree step did not show `master`, a system database | (test) | the step shows system databases on SQL Server |
| PR-017 | CI | Test defect: the catalog-session-ended check read the server before it dropped the logged-out session (flake) | HLR-CAT-4 | the check waits, bounded to 5 s |
| PR-018 | process | TLC trace files were committed by mistake | (process) | `.gitignore`; reviewed in `reviews/review-checklist.md` |
| PR-019 | assurance (shutdown failure test) | A session whose close failed during shutdown was logged as "closed (any open transaction rolled back)": the close task's exception was never observed | HLR-TX-4 | the result is awaited and a failure is logged as such; `shutdown_closes_every_session_even_if_one_fails` |

## Open

None.
