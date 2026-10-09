--------------------------- MODULE QueryLifecycle ---------------------------
(***************************************************************************)
(* One query in dbbliss-backend, from execute to query/done, with cancel,  *)
(* backpressure and shutdown. Actors:                                      *)
(*   - query thread (rt): registers the protocol cancel, sends the         *)
(*     statement, reads rows from the socket, hands them to the streamer;  *)
(*   - RPC thread: cancel requests, the re-fire watchdog, shutdown;        *)
(*   - pump: writes queued notifications to the stdout pipe;               *)
(*   - Neovim: reads the pipe, may stall at any time, may die;             *)
(*   - the database server: runs the statement, fills the socket, reacts   *)
(*     to the protocol cancel and to a dead client.                        *)
(*                                                                         *)
(* Pull paging (Phase 1 M2): the client allows rows with fetch; the query *)
(* thread waits when it has none (the server is held by backpressure).     *)
(* Rows are not counted (the server may produce forever, like a 100M-row   *)
(* query or a sleep); loss is tracked by the only actions that can drop a  *)
(* row. Boolean constants select design variants, so the bugs found in     *)
(* Phase 0 are reproduced by the model (see the *.cfg files).              *)
(***************************************************************************)
EXTENDS Naturals, Sequences

CONSTANTS
    NetCap,                 \* rows the socket buffers hold before the server blocks on send
    Slots,                  \* row pages allowed in flight between query thread and pump
    PipeCap,                \* notifications the stdout pipe holds before the pump blocks
    OverflowCap,            \* after a cancel: rows queued without a slot before truncating
    CancellableSlotWait,    \* a cancel wakes a query thread waiting for a page slot
    OverflowAfterCancel,    \* after a cancel, rows bypass backpressure instead of being dropped
    Refire,                 \* the watchdog re-sends the protocol cancel until the query ends
    AttentionNeedsSendRoom, \* the server only notices a cancel when it is not blocked on send (SQL Server)
    ConnCheck,              \* the server notices a closed client while executing (PG client_connection_check_interval)
    Pull,                   \* the client allows rows (execute window, fetch); FALSE: no limit
    Window,                 \* rows allowed up front (a small number stands for the window)
    CreditCap,              \* most rows allowed and not yet used (bounds the model)
    CancellableCreditWait   \* a cancel ends the wait for credit, as it ends the wait for a slot

VARIABLES
    rt,           \* query thread: idle | registered | checked | reading | blocked | disposing | completing | done
    hasRow,       \* the query thread holds a row it has not handed to the streamer yet
    cancelReq,    \* the backend has accepted a cancel for this query
    srv,          \* server: notstarted | running | ended
    srvCancel,    \* a protocol cancel has reached the server and is pending
    net,          \* rows in the socket buffers
    slots,        \* free page slots
    queue,        \* streamer -> pump queue: sequence of [k |-> "rows"/"done", slot |-> BOOLEAN]
    overflow,     \* slot-less row pages in the queue
    truncated,    \* rows were dropped AND the client will be told (reported loss)
    lost,         \* a row was dropped silently while the client was alive (must never happen)
    pipe,         \* stdout pipe contents: sequence of "rows"/"done"
    client,       \* Neovim: reading | stalled | gone
    doneCount,    \* query/done notifications Neovim has read
    rowAfterDone, \* Neovim read rows after query/done
    sd,           \* backend shutdown: no | requested | waiting | exited
    connClosed,   \* the database connection's socket is closed
    credit        \* rows the client has allowed and the query thread has not used yet

vars == <<rt, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow, truncated,
          lost, pipe, client, doneCount, rowAfterDone, sd, connClosed, credit>>

Alive  == sd # "exited"
\* The statement is on the wire: only now does a driver cancel (attention / CancelRequest / SQLCancel) do anything.
OnWire == rt \in {"reading", "blocked", "disposing"} /\ srv = "running"

SlotHeld == Len(SelectSeq(queue, LAMBDA m : m.slot))
Msg      == [k : {"rows", "done"}, slot : BOOLEAN]

TypeOK ==
    /\ rt \in {"idle", "registered", "checked", "reading", "blocked", "disposing", "completing", "done"}
    /\ hasRow \in BOOLEAN /\ cancelReq \in BOOLEAN /\ srvCancel \in BOOLEAN
    /\ srv \in {"notstarted", "running", "ended"}
    /\ net \in 0..NetCap
    /\ slots \in 0..Slots
    /\ queue \in Seq(Msg)
    /\ overflow \in 0..OverflowCap
    /\ truncated \in BOOLEAN /\ lost \in BOOLEAN /\ rowAfterDone \in BOOLEAN /\ connClosed \in BOOLEAN
    /\ pipe \in Seq({"rows", "done"}) /\ Len(pipe) <= PipeCap
    /\ client \in {"reading", "stalled", "gone"}
    /\ doneCount \in 0..2
    /\ sd \in {"no", "requested", "waiting", "exited"}
    /\ credit \in 0..CreditCap

Init ==
    /\ rt = "idle" /\ hasRow = FALSE /\ cancelReq = FALSE
    /\ srv = "notstarted" /\ srvCancel = FALSE /\ net = 0
    /\ slots = Slots /\ queue = <<>> /\ overflow = 0
    /\ truncated = FALSE /\ lost = FALSE
    /\ pipe = <<>> /\ client = "reading" /\ doneCount = 0 /\ rowAfterDone = FALSE
    /\ sd = "no" /\ connClosed = FALSE
    /\ credit = Window

-----------------------------------------------------------------------------
(* Query thread *)

\* RunQueryAsync checks the token, then the engine registers its protocol cancel.
\* A cancel that arrived earlier is fired at registration: a no-op, nothing is on the wire yet.
Register ==
    /\ Alive /\ rt = "idle"
    /\ rt' = IF cancelReq THEN "completing" ELSE "registered"
    /\ UNCHANGED <<credit, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow, truncated,
                   lost, pipe, client, doneCount, rowAfterDone, sd, connClosed>>

\* The engine checks the token right before sending (ThrowIfCancellationRequested).
CheckToken ==
    /\ Alive /\ rt = "registered"
    /\ rt' = IF cancelReq THEN "completing" ELSE "checked"
    /\ UNCHANGED <<credit, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow, truncated,
                   lost, pipe, client, doneCount, rowAfterDone, sd, connClosed>>

\* ExecuteReaderAsync: the statement goes on the wire. The drivers do not look at the token
\* here, so a cancel between CheckToken and Send is a no-op unless re-fired.
Send ==
    /\ Alive /\ rt = "checked"
    /\ rt' = "reading" /\ srv' = "running"
    /\ UNCHANGED <<credit, hasRow, cancelReq, srvCancel, net, slots, queue, overflow, truncated,
                   lost, pipe, client, doneCount, rowAfterDone, sd, connClosed>>

ReadRow ==
    /\ Alive /\ rt = "reading" /\ ~hasRow /\ net > 0
    /\ net' = net - 1 /\ hasRow' = TRUE
    /\ UNCHANGED <<credit, rt, cancelReq, srv, srvCancel, slots, queue, overflow, truncated,
                   lost, pipe, client, doneCount, rowAfterDone, sd, connClosed>>

\* Hand the held row to the pump with a page slot, using one row of credit if there is any
\* (rows handed over after a cancel need none).
TakeSlot(nextRt) ==
    /\ slots > 0
    /\ slots' = slots - 1
    /\ queue' = Append(queue, [k |-> "rows", slot |-> TRUE])
    /\ credit' = IF Pull /\ credit > 0 THEN credit - 1 ELSE credit
    /\ hasRow' = FALSE /\ rt' = nextRt
    /\ UNCHANGED <<cancelReq, srv, srvCancel, net, overflow, truncated, lost, pipe, client,
                   doneCount, rowAfterDone, sd, connClosed>>

\* The query thread may hand the held row over: a slot is free, and the client has allowed it
\* (AcquireCreditAsync), unless a cancel ended the wait for credit.
CanHand == slots > 0 /\ (~Pull \/ credit > 0 \/ (cancelReq /\ CancellableCreditWait))

\* After a cancel: queue the row without a slot, or drop it and mark the result truncated.
Overflow(nextRt) ==
    /\ IF overflow < OverflowCap
         THEN /\ queue' = Append(queue, [k |-> "rows", slot |-> FALSE])
              /\ overflow' = overflow + 1 /\ UNCHANGED truncated
         ELSE /\ truncated' = TRUE /\ UNCHANGED <<credit, queue, overflow>>
    /\ hasRow' = FALSE /\ rt' = nextRt
    /\ UNCHANGED <<credit, cancelReq, srv, srvCancel, net, slots, lost, pipe, client,
                   doneCount, rowAfterDone, sd, connClosed>>

HandWithSlot == Alive /\ rt = "reading" /\ hasRow /\ CanHand /\ TakeSlot("reading")

HandOverflow ==
    /\ Alive /\ rt = "reading" /\ hasRow /\ slots = 0
    /\ cancelReq /\ OverflowAfterCancel
    /\ Overflow("reading")

\* No slot (or no credit) and no reason to bypass backpressure: wait. With no credit this is the
\* paused query: query/paused has been sent and the driver stops reading the socket.
Block ==
    /\ Alive /\ rt = "reading" /\ hasRow /\ ~CanHand
    /\ ~(cancelReq /\ OverflowAfterCancel)
    /\ rt' = "blocked"
    /\ UNCHANGED <<credit, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow, truncated,
                   lost, pipe, client, doneCount, rowAfterDone, sd, connClosed>>

SlotFreed == Alive /\ rt = "blocked" /\ CanHand /\ TakeSlot("reading")

\* The cancel wakes the slot wait. Current code: OperationCanceledException, ExecuteAsync unwinds
\* and disposes the reader, which drains (discards) the socket. Proposed: overflow and keep reading.
CancelWake ==
    /\ Alive /\ rt = "blocked" /\ cancelReq /\ CancellableSlotWait /\ slots = 0
    /\ IF OverflowAfterCancel
         THEN Overflow("reading")
         ELSE /\ rt' = "disposing"
              /\ UNCHANGED <<credit, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow,
                             truncated, lost, pipe, client, doneCount, rowAfterDone, sd, connClosed>>

\* Reader.Dispose drains the socket until the server's end marker, discarding rows.
DisposeDiscard ==
    /\ Alive /\ rt = "disposing" /\ net > 0
    /\ net' = net - 1
    /\ lost' = (lost \/ client # "gone")
    /\ UNCHANGED <<credit, rt, hasRow, cancelReq, srv, srvCancel, slots, queue, overflow, truncated,
                   pipe, client, doneCount, rowAfterDone, sd, connClosed>>

DisposeEnd ==
    /\ Alive /\ rt = "disposing" /\ net = 0 /\ srv = "ended"
    /\ rt' = "completing"
    /\ UNCHANGED <<credit, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow, truncated,
                   lost, pipe, client, doneCount, rowAfterDone, sd, connClosed>>

EndOfStream ==
    /\ Alive /\ rt = "reading" /\ ~hasRow /\ net = 0 /\ srv = "ended"
    /\ rt' = "completing"
    /\ UNCHANGED <<credit, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow, truncated,
                   lost, pipe, client, doneCount, rowAfterDone, sd, connClosed>>

\* CompleteAsync: flush the held page with an uncancellable slot wait (the server is done by now).
FlushHeld == Alive /\ rt = "completing" /\ hasRow /\ TakeSlot("completing")

\* query/done is written only after the pump has drained everything before it.
Complete ==
    /\ Alive /\ rt = "completing" /\ ~hasRow /\ queue = <<>>
    /\ queue' = <<[k |-> "done", slot |-> FALSE]>>
    /\ rt' = "done"
    /\ UNCHANGED <<credit, hasRow, cancelReq, srv, srvCancel, net, slots, overflow, truncated,
                   lost, pipe, client, doneCount, rowAfterDone, sd, connClosed>>

-----------------------------------------------------------------------------
(* RPC thread *)

\* Cancel(): fire the registered protocol cancel (effective only on the wire), cancel the token.
CancelRequest ==
    /\ Alive /\ sd = "no" /\ ~cancelReq /\ rt \notin {"completing", "done"}
    /\ cancelReq' = TRUE
    /\ srvCancel' = (srvCancel \/ OnWire)
    /\ UNCHANGED <<credit, rt, hasRow, srv, net, slots, queue, overflow, truncated, lost, pipe,
                   client, doneCount, rowAfterDone, sd, connClosed>>

\* WatchCancelAsync: re-send the protocol cancel while the query runs.
RefireCancel ==
    /\ Refire /\ Alive /\ cancelReq /\ OnWire /\ ~srvCancel
    /\ srvCancel' = TRUE
    /\ UNCHANGED <<credit, rt, hasRow, cancelReq, srv, net, slots, queue, overflow, truncated, lost,
                   pipe, client, doneCount, rowAfterDone, sd, connClosed>>

\* stdin EOF, a signal, or a shutdown request.
ShutdownRequest ==
    /\ sd = "no"
    /\ sd' = "requested"
    /\ UNCHANGED <<credit, rt, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow, truncated,
                   lost, pipe, client, doneCount, rowAfterDone, connClosed>>

ShutdownCancel ==
    /\ sd = "requested"
    /\ sd' = "waiting"
    /\ cancelReq' = (cancelReq \/ rt \notin {"completing", "done"})
    /\ srvCancel' = (srvCancel \/ OnWire)
    /\ UNCHANGED <<credit, rt, hasRow, srv, net, slots, queue, overflow, truncated, lost, pipe,
                   client, doneCount, rowAfterDone, connClosed>>

\* After the query ended or the wait timed out (modelled as: at any time): close the
\* connection and exit. Closing ends the server session.
CloseAndExit ==
    /\ sd = "waiting"
    /\ sd' = "exited" /\ connClosed' = TRUE
    /\ UNCHANGED <<credit, rt, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow, truncated,
                   lost, pipe, client, doneCount, rowAfterDone>>

\* SIGKILL / TerminateProcess: nothing runs any more; the OS closes the socket.
HardKill ==
    /\ Alive
    /\ sd' = "exited" /\ connClosed' = TRUE
    /\ UNCHANGED <<credit, rt, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow, truncated,
                   lost, pipe, client, doneCount, rowAfterDone>>

-----------------------------------------------------------------------------
(* Pump: one notification at a time, in order. A write to a dead Neovim fails and is skipped. *)

Pump ==
    /\ Alive /\ queue # <<>>
    /\ client = "gone" \/ Len(pipe) < PipeCap
    /\ LET m == Head(queue) IN
         /\ queue' = Tail(queue)
         /\ pipe' = IF client = "gone" THEN pipe ELSE Append(pipe, m.k)
         /\ slots' = IF m.slot THEN slots + 1 ELSE slots
         /\ overflow' = IF m.k = "rows" /\ ~m.slot THEN overflow - 1 ELSE overflow
    /\ UNCHANGED <<credit, rt, hasRow, cancelReq, srv, srvCancel, net, truncated, lost, client,
                   doneCount, rowAfterDone, sd, connClosed>>

-----------------------------------------------------------------------------
(* Neovim *)

ClientRead ==
    /\ client = "reading" /\ pipe # <<>>
    /\ pipe' = Tail(pipe)
    /\ doneCount' = IF Head(pipe) = "done" THEN doneCount + 1 ELSE doneCount
    /\ rowAfterDone' = (rowAfterDone \/ (Head(pipe) = "rows" /\ doneCount > 0))
    /\ UNCHANGED <<credit, rt, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow,
                   truncated, lost, client, sd, connClosed>>

Stall ==
    /\ client = "reading" /\ client' = "stalled"
    /\ UNCHANGED <<credit, rt, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow, truncated,
                   lost, pipe, doneCount, rowAfterDone, sd, connClosed>>

Resume ==
    /\ client = "stalled" /\ client' = "reading"
    /\ UNCHANGED <<credit, rt, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow, truncated,
                   lost, pipe, doneCount, rowAfterDone, sd, connClosed>>

\* Neovim killed: its pipe is gone and the backend sees stdin EOF.
ClientDies ==
    /\ client # "gone"
    /\ client' = "gone" /\ pipe' = <<>>
    /\ sd' = IF sd = "no" THEN "requested" ELSE sd
    /\ UNCHANGED <<credit, rt, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow, truncated,
                   lost, doneCount, rowAfterDone, connClosed>>

\* fetch: Neovim allows more rows (a request on stdin; the backend adds credit). It needs a client
\* that is reading; a stalled or dead Neovim never fetches, so a paused query stays paused.
ClientFetch ==
    /\ Pull /\ client = "reading" /\ credit < CreditCap
    /\ credit' = credit + 1
    /\ UNCHANGED <<rt, hasRow, cancelReq, srv, srvCancel, net, slots, queue, overflow, truncated,
                   lost, pipe, client, doneCount, rowAfterDone, sd, connClosed>>

-----------------------------------------------------------------------------
(* Database server *)

\* Not fair: a sleep produces nothing, a big SELECT produces forever.
ServerProduce ==
    /\ srv = "running" /\ net < NetCap /\ ~connClosed
    /\ net' = net + 1
    /\ UNCHANGED <<credit, rt, hasRow, cancelReq, srv, srvCancel, slots, queue, overflow, truncated,
                   lost, pipe, client, doneCount, rowAfterDone, sd, connClosed>>

\* The server acts on a pending cancel and sends its end marker. SQL Server does not see the
\* attention while blocked sending into full socket buffers (ASYNC_NETWORK_IO).
ServerSeesCancel ==
    /\ srv = "running" /\ srvCancel
    /\ ~AttentionNeedsSendRoom \/ net < NetCap
    /\ srv' = "ended" /\ srvCancel' = FALSE
    /\ UNCHANGED <<credit, rt, hasRow, cancelReq, net, slots, queue, overflow, truncated, lost, pipe,
                   client, doneCount, rowAfterDone, sd, connClosed>>

\* A closed client is noticed by an active check, or by a send that fails.
ServerSeesDeadClient ==
    /\ srv = "running" /\ connClosed
    /\ ConnCheck \/ net = NetCap
    /\ srv' = "ended"
    /\ UNCHANGED <<credit, rt, hasRow, cancelReq, srvCancel, net, slots, queue, overflow, truncated,
                   lost, pipe, client, doneCount, rowAfterDone, sd, connClosed>>

-----------------------------------------------------------------------------

Backend ==
    \/ Register \/ CheckToken \/ Send \/ ReadRow \/ HandWithSlot \/ HandOverflow \/ Block \/ SlotFreed
    \/ CancelWake \/ DisposeDiscard \/ DisposeEnd \/ EndOfStream \/ FlushHeld \/ Complete
    \/ CancelRequest \/ RefireCancel \/ ShutdownCancel \/ CloseAndExit \/ Pump

Environment ==
    \/ ShutdownRequest \/ HardKill \/ ClientRead \/ Stall \/ Resume \/ ClientDies \/ ClientFetch
    \/ ServerProduce \/ ServerSeesCancel \/ ServerSeesDeadClient

Next == Backend \/ Environment

\* Fairness for the backend's own threads and for the server's reaction to cancel / dead client.
\* Nothing obliges Neovim to read: these properties must hold with a client stalled forever.
BackendFairness ==
    /\ WF_vars(Register) /\ WF_vars(CheckToken) /\ WF_vars(Send) /\ WF_vars(ReadRow) /\ WF_vars(HandWithSlot)
    /\ WF_vars(HandOverflow) /\ WF_vars(Block) /\ WF_vars(SlotFreed) /\ WF_vars(CancelWake)
    /\ WF_vars(DisposeDiscard) /\ WF_vars(DisposeEnd) /\ WF_vars(EndOfStream)
    /\ WF_vars(FlushHeld) /\ WF_vars(Complete) /\ WF_vars(RefireCancel)
    /\ WF_vars(ShutdownCancel) /\ WF_vars(CloseAndExit) /\ WF_vars(Pump)
    /\ SF_vars(ServerSeesCancel) /\ SF_vars(ServerSeesDeadClient)

StalledSpec == Init /\ [][Next]_vars /\ BackendFairness

\* Neovim keeps coming back to read (it may stall, but not forever).
LiveSpec == StalledSpec /\ SF_vars(ClientRead) /\ WF_vars(Resume)

-----------------------------------------------------------------------------
(* Safety *)

NoSilentLoss      == ~lost
DoneAtMostOnce    == doneCount <= 1
NoRowsAfterDone   == ~rowAfterDone
SlotsConserved    == slots + SlotHeld = Slots
TruncationNeedsCancel == truncated => cancelReq
\* Memory in flight is bounded: slot pages + overflow pages + the done marker.
QueueBounded      == Len(queue) <= Slots + OverflowCap + 1

\* Reachability witness (PausedReachable.cfg expects this to be violated): the model does reach a
\* paused query, so the pull rules above are not vacuous.
PausedUnreachable == ~(rt = "blocked" /\ credit = 0 /\ slots > 0 /\ ~cancelReq)

(* Liveness *)

ServerQuiet == srv = "ended" \/ (srv = "notstarted" /\ (rt \in {"completing", "done"} \/ ~Alive))

\* Holds under StalledSpec: Neovim may never read again.
CancelStopsServer   == cancelReq ~> ServerQuiet
ShutdownStopsServer == (sd # "no") ~> ServerQuiet

\* Holds under LiveSpec.
\* A paused query that is allowed more rows goes on (unless the backend is gone).
PausedQueryResumes  == (rt = "blocked" /\ credit > 0) ~> (rt # "blocked" \/ ~Alive)
CancelledQueryReportsDone == cancelReq ~> (doneCount = 1 \/ client = "gone" \/ ~Alive)
=============================================================================
