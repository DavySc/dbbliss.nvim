------------------------------ MODULE Operations ------------------------------
(***************************************************************************)
(* Phase 4: a long management operation (a backup: pg_dump as a child     *)
(* process, or BACKUP DATABASE on a session of its own) next to the       *)
(* things that can end its world: disconnect, shutdown, a drop.           *)
(*                                                                         *)
(* One connection, one database, one operation at a time. The tool        *)
(* (proc) is the child process or the running server statement. Boolean   *)
(* constants select design variants, as in QueryLifecycle.tla: every      *)
(* variant except Ops_Proposed must fail (see the *.cfg files and         *)
(* spec/check.sh).                                                         *)
(***************************************************************************)
EXTENDS Naturals

CONSTANTS
    DisconnectRefusedWhileBusy, \* disconnect fails with connection_busy while an operation runs
    DropRefusedWhileBusy,       \* a drop is refused while an operation of the connection runs
    ShutdownCancelsOps,         \* shutdown cancels the running operation and waits for the tool to end
    ProdDropNeedsBackup,        \* on prod (or unknown) a drop needs a verified backup
    StaleCounts                 \* a backup that has gone stale (time passed) still satisfies that rule

VARIABLES
    conn,       \* "open" | "closed"
    op,         \* "none" | "running" | "cancelling" | "done"
    proc,       \* "none" | "alive" | "dead": the child process / server-side statement
    backend,    \* "up" | "stopping" | "exited"
    env,        \* "dev" | "prod": fixed when the connection is made
    bk,         \* "none" | "fresh" | "stale": the newest verified backup
    dropped,    \* the database was dropped
    dropBk,     \* the newest backup's state when the drop ran ("none" before any drop)
    dropBusy    \* the drop ran while an operation of the connection was running

vars == <<conn, op, proc, backend, env, bk, dropped, dropBk, dropBusy>>

Running == op \in {"running", "cancelling"}

TypeOK ==
    /\ conn \in {"open", "closed"}
    /\ op \in {"none", "running", "cancelling", "done"}
    /\ proc \in {"none", "alive", "dead"}
    /\ backend \in {"up", "stopping", "exited"}
    /\ env \in {"dev", "prod"}
    /\ bk \in {"none", "fresh", "stale"}
    /\ {dropped, dropBusy} \subseteq BOOLEAN
    /\ dropBk \in {"none", "fresh", "stale"}

Init ==
    /\ conn = "open" /\ op = "none" /\ proc = "none" /\ backend = "up"
    /\ env \in {"dev", "prod"}
    /\ bk = "none" /\ dropped = FALSE /\ dropBk = "none" /\ dropBusy = FALSE

StartBackup ==
    /\ backend = "up" /\ conn = "open" /\ ~dropped /\ op \in {"none", "done"}
    /\ op' = "running" /\ proc' = "alive"
    /\ UNCHANGED <<conn, backend, env, bk, dropped, dropBk, dropBusy>>

\* The tool ends by itself. The backup may have failed, then no new verified backup exists.
ToolEnds ==
    /\ op = "running" /\ proc = "alive"
    /\ proc' = "dead" /\ op' = "done"
    /\ bk' \in {"fresh", bk}
    /\ UNCHANGED <<conn, backend, env, dropped, dropBk, dropBusy>>

UserCancel ==
    /\ op = "running" /\ backend = "up"
    /\ op' = "cancelling"
    /\ UNCHANGED <<conn, proc, backend, env, bk, dropped, dropBk, dropBusy>>

\* The cancel reaches the tool (kill of the process tree, or the protocol-level cancel).
CancelTakesEffect ==
    /\ op = "cancelling" /\ proc = "alive"
    /\ proc' = "dead" /\ op' = "done"
    /\ UNCHANGED <<conn, backend, env, bk, dropped, dropBk, dropBusy>>

\* Time passes: the data the backup captured is no longer the data in the database.
Age ==
    /\ bk = "fresh"
    /\ bk' = "stale"
    /\ UNCHANGED <<conn, op, proc, backend, env, dropped, dropBk, dropBusy>>

Disconnect ==
    /\ conn = "open" /\ backend = "up"
    /\ DisconnectRefusedWhileBusy => ~Running
    /\ conn' = "closed"
    /\ UNCHANGED <<op, proc, backend, env, bk, dropped, dropBk, dropBusy>>

BeginShutdown ==
    /\ backend = "up"
    /\ backend' = "stopping"
    /\ op' = IF ShutdownCancelsOps /\ op = "running" THEN "cancelling" ELSE op
    /\ UNCHANGED <<conn, proc, env, bk, dropped, dropBk, dropBusy>>

FinishShutdown ==
    /\ backend = "stopping"
    /\ ShutdownCancelsOps => proc # "alive"
    /\ backend' = "exited"
    /\ UNCHANGED <<conn, op, proc, env, bk, dropped, dropBk, dropBusy>>

BackupRuleMet == env = "dev" \/ ~ProdDropNeedsBackup \/ bk = "fresh" \/ (StaleCounts /\ bk = "stale")

Drop ==
    /\ backend = "up" /\ conn = "open" /\ ~dropped
    /\ DropRefusedWhileBusy => ~Running
    /\ BackupRuleMet
    /\ dropped' = TRUE
    /\ dropBk' = bk
    /\ dropBusy' = Running
    /\ UNCHANGED <<conn, op, proc, backend, env, bk>>

Next ==
    \/ StartBackup \/ ToolEnds \/ UserCancel \/ CancelTakesEffect \/ Age
    \/ Disconnect \/ BeginShutdown \/ FinishShutdown \/ Drop

Spec == Init /\ [][Next]_vars /\ WF_vars(ToolEnds) /\ WF_vars(CancelTakesEffect) /\ WF_vars(FinishShutdown)

\* --- properties -----------------------------------------------------------------------------

\* No tool outlives the backend: no orphaned pg_dump, no BACKUP statement nobody watches.
NoOrphanTool == backend = "exited" => proc # "alive"

\* An operation never runs on a connection that was closed under it.
NoOpOnClosedConn == Running => conn = "open"

\* On prod only a fresh verified backup justifies a drop: none, and a stale one, are not enough.
\* Stated on what happened, not on the design's own rule, so a design without the rule fails it.
ProdDropBacked == dropped /\ env = "prod" => dropBk = "fresh"
\* No drop while an operation of the connection runs.
NoDropDuringOp == ~dropBusy

\* The tool that was told to stop does stop, and a stopping backend does exit.
CancelStops == op = "cancelling" ~> proc # "alive"
ShutdownCompletes == backend = "stopping" ~> backend = "exited"
=============================================================================
