### Linux X64 — 2026-10-02 17:51 UTC

| Engine | Scenario | Result | Server stopped after | Detail |
|---|---|---|---|---|
| postgres | sleep_cancel | PASS | 30 ms | cancel=cancel_sent done=cancelled ack=15ms server: state=idle wait=ClientRead query=SELECT pg_sleep(60) |
| postgres | cancel_immediately | PASS | 133 ms | 20 rounds, all cancelled; worst server stop 133 ms |
| postgres | batch_cancel_keeps_results | PASS | 86 ms | done=cancelled first_result_rows=1 server: state=idle wait=ClientRead query=SELECT pg_sleep(60) |
| postgres | streaming_cancel | PASS | 110 ms | done=cancelled rows_before_cancel=273944 ack=60ms server: state=idle wait=ClientRead query=SELECT generate_series(1, 100000000) AS n, repeat('x', 200)  |
| postgres | streaming_cancel_stalled_client | PASS | 57 ms | done=cancelled server stopped while client stalled: yes server: state=idle wait=ClientRead query=SELECT generate_series(1, 100000000) AS n, repeat('x', 200)  |
| postgres | tx_cancel | PASS | — | done=cancelled backend_tx=aborted/aborted server_tx=aborted rows_visible_before_rollback=0 after_rollback=0 server_after=none |
| postgres | backend_stdin_closed | PASS | 54 ms | backend exited=True server: state=idle wait=ClientRead query=SELECT pg_sleep(60) |
| postgres | backend_sigterm | PASS | 62 ms | backend exited=True server: state=idle wait=ClientRead query=SELECT pg_sleep(60) |
| postgres | backend_killed | PASS | 1924 ms | backend exited=True server: session gone |
| postgres | backend_killed_no_conncheck | INFO | — | server kept running (expected): backend exited=True server: state=active wait=PgSleep query=SELECT pg_sleep(60) |
| postgres | nvim_quit | PASS | 51 ms | nvim exited=True backend exited=True server: session gone |
| postgres | nvim_killed | PASS | 52 ms | nvim exited=True backend exited=True server: state=idle wait=ClientRead query=SELECT pg_sleep(60) |
| sqlserver | sleep_cancel | PASS | 33 ms | cancel=cancel_sent done=cancelled ack=15ms server: no request; session status=sleeping open_tx=0 |
| sqlserver | cancel_immediately | PASS | 129 ms | 20 rounds, all cancelled; worst server stop 129 ms |
| sqlserver | batch_cancel_keeps_results | PASS | 43 ms | done=cancelled first_result_rows=1 server: no request; session status=sleeping open_tx=0 |
| sqlserver | streaming_cancel | PASS | 367 ms | done=cancelled rows_before_cancel=194851 ack=357ms server: no request; session status=sleeping open_tx=0 |
| sqlserver | streaming_cancel_stalled_client | PASS | 53 ms | done=cancelled server stopped while client stalled: yes server: no request; session status=sleeping open_tx=0 |
| sqlserver | tx_cancel | PASS | — | done=cancelled backend_tx=active/active server_tx=open rows_visible_before_rollback=n/a after_rollback=0 server_after=none |
| sqlserver | tx_cancel_xact_abort | PASS | — | done=cancelled backend_tx=none/none server_tx=none rows_visible_before_rollback=n/a after_rollback=0 server_after=none |
| sqlserver | backend_stdin_closed | PASS | 53 ms | backend exited=True server: no request; session status=sleeping open_tx=0 |
| sqlserver | backend_sigterm | PASS | 54 ms | backend exited=True server: no request; session status=sleeping open_tx=0 |
| sqlserver | backend_killed | PASS | 92 ms | backend exited=True server: session gone |
| sqlserver | nvim_quit | PASS | 53 ms | nvim exited=True backend exited=True server: session gone |
| sqlserver | nvim_killed | PASS | 51 ms | nvim exited=True backend exited=True server: session gone |

