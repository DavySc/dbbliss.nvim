---- MODULE QueryLifecycle_TTrace_1791544287 ----
EXTENDS Sequences, QueryLifecycle, TLCExt, QueryLifecycle_TEConstants, Toolbox, Naturals, TLC

_expression ==
    LET QueryLifecycle_TEExpression == INSTANCE QueryLifecycle_TEExpression
    IN QueryLifecycle_TEExpression!expression
----

_trace ==
    LET QueryLifecycle_TETrace == INSTANCE QueryLifecycle_TETrace
    IN QueryLifecycle_TETrace!trace
----

_prop ==
    ~(([]<>(
            rt = ("reading")
            /\
            cancelReq = (TRUE)
            /\
            doneCount = (0)
            /\
            connClosed = (FALSE)
            /\
            hasRow = (TRUE)
            /\
            truncated = (TRUE)
            /\
            rowAfterDone = (FALSE)
            /\
            sd = ("no")
            /\
            slots = (1)
            /\
            srvCancel = (TRUE)
            /\
            overflow = (0)
            /\
            srv = ("running")
            /\
            lost = (FALSE)
            /\
            client = ("stalled")
            /\
            pipe = (<<>>)
            /\
            credit = (0)
            /\
            net = (2)
            /\
            queue = (<<>>)
    ))/\([]<>(
            rt = ("reading")
            /\
            cancelReq = (TRUE)
            /\
            doneCount = (0)
            /\
            connClosed = (FALSE)
            /\
            hasRow = (TRUE)
            /\
            truncated = (TRUE)
            /\
            rowAfterDone = (FALSE)
            /\
            sd = ("no")
            /\
            slots = (1)
            /\
            srvCancel = (TRUE)
            /\
            overflow = (0)
            /\
            srv = ("running")
            /\
            lost = (FALSE)
            /\
            client = ("reading")
            /\
            pipe = (<<>>)
            /\
            credit = (0)
            /\
            net = (2)
            /\
            queue = (<<>>)
    )))
----

_init ==
    /\ srvCancel = _TETrace[1].srvCancel
    /\ doneCount = _TETrace[1].doneCount
    /\ srv = _TETrace[1].srv
    /\ rowAfterDone = _TETrace[1].rowAfterDone
    /\ slots = _TETrace[1].slots
    /\ credit = _TETrace[1].credit
    /\ net = _TETrace[1].net
    /\ pipe = _TETrace[1].pipe
    /\ rt = _TETrace[1].rt
    /\ connClosed = _TETrace[1].connClosed
    /\ sd = _TETrace[1].sd
    /\ truncated = _TETrace[1].truncated
    /\ overflow = _TETrace[1].overflow
    /\ queue = _TETrace[1].queue
    /\ hasRow = _TETrace[1].hasRow
    /\ lost = _TETrace[1].lost
    /\ client = _TETrace[1].client
    /\ cancelReq = _TETrace[1].cancelReq
----

_next ==
    /\ \E i,j \in DOMAIN _TETrace:
        /\ \/ /\ j = i + 1
              /\ i = TLCGet("level")
           \/ /\ i = _TTraceLassoEnd
              /\ j = _TTraceLassoStart
        /\ srvCancel  = _TETrace[i].srvCancel
        /\ srvCancel' = _TETrace[j].srvCancel
        /\ doneCount  = _TETrace[i].doneCount
        /\ doneCount' = _TETrace[j].doneCount
        /\ srv  = _TETrace[i].srv
        /\ srv' = _TETrace[j].srv
        /\ rowAfterDone  = _TETrace[i].rowAfterDone
        /\ rowAfterDone' = _TETrace[j].rowAfterDone
        /\ slots  = _TETrace[i].slots
        /\ slots' = _TETrace[j].slots
        /\ credit  = _TETrace[i].credit
        /\ credit' = _TETrace[j].credit
        /\ net  = _TETrace[i].net
        /\ net' = _TETrace[j].net
        /\ pipe  = _TETrace[i].pipe
        /\ pipe' = _TETrace[j].pipe
        /\ rt  = _TETrace[i].rt
        /\ rt' = _TETrace[j].rt
        /\ connClosed  = _TETrace[i].connClosed
        /\ connClosed' = _TETrace[j].connClosed
        /\ sd  = _TETrace[i].sd
        /\ sd' = _TETrace[j].sd
        /\ truncated  = _TETrace[i].truncated
        /\ truncated' = _TETrace[j].truncated
        /\ overflow  = _TETrace[i].overflow
        /\ overflow' = _TETrace[j].overflow
        /\ queue  = _TETrace[i].queue
        /\ queue' = _TETrace[j].queue
        /\ hasRow  = _TETrace[i].hasRow
        /\ hasRow' = _TETrace[j].hasRow
        /\ lost  = _TETrace[i].lost
        /\ lost' = _TETrace[j].lost
        /\ client  = _TETrace[i].client
        /\ client' = _TETrace[j].client
        /\ cancelReq  = _TETrace[i].cancelReq
        /\ cancelReq' = _TETrace[j].cancelReq

\* Uncomment the ASSUME below to write the states of the error trace
\* to the given file in Json format. Note that you can pass any tuple
\* to `JsonSerialize`. For example, a sub-sequence of _TETrace.
    \* ASSUME
    \*     LET J == INSTANCE Json
    \*         IN J!JsonSerialize("QueryLifecycle_TTrace_1791544287.json", _TETrace)


_view ==
    <<srvCancel, doneCount, srv, rowAfterDone, slots, credit, net, pipe, rt, connClosed, sd, truncated, overflow, queue, hasRow, lost, client, cancelReq, IF TLCGet("level") = _TTraceLassoEnd + 1 THEN _TTraceLassoStart ELSE TLCGet("level")>>
=============================================================================

 Note that you can extract this module `QueryLifecycle_TEExpression`
  to a dedicated file to reuse `expression` (the module in the 
  dedicated `QueryLifecycle_TEExpression.tla` file takes precedence 
  over the module `QueryLifecycle_TEExpression` below).

---- MODULE QueryLifecycle_TEExpression ----
EXTENDS Sequences, QueryLifecycle, TLCExt, QueryLifecycle_TEConstants, Toolbox, Naturals, TLC

expression == 
    [
        \* To hide variables of the `QueryLifecycle` spec from the error trace,
        \* remove the variables below.  The trace will be written in the order
        \* of the fields of this record.
        srvCancel |-> srvCancel
        ,doneCount |-> doneCount
        ,srv |-> srv
        ,rowAfterDone |-> rowAfterDone
        ,slots |-> slots
        ,credit |-> credit
        ,net |-> net
        ,pipe |-> pipe
        ,rt |-> rt
        ,connClosed |-> connClosed
        ,sd |-> sd
        ,truncated |-> truncated
        ,overflow |-> overflow
        ,queue |-> queue
        ,hasRow |-> hasRow
        ,lost |-> lost
        ,client |-> client
        ,cancelReq |-> cancelReq
        
        \* Put additional constant-, state-, and action-level expressions here:
        \* ,_stateNumber |-> _TEPosition
        \* ,_srvCancelUnchanged |-> srvCancel = srvCancel'
        
        \* Format the `srvCancel` variable as Json value.
        \* ,_srvCancelJson |->
        \*     LET J == INSTANCE Json
        \*     IN J!ToJson(srvCancel)
        
        \* Lastly, you may build expressions over arbitrary sets of states by
        \* leveraging the _TETrace operator.  For example, this is how to
        \* count the number of times a spec variable changed up to the current
        \* state in the trace.
        \* ,_srvCancelModCount |->
        \*     LET F[s \in DOMAIN _TETrace] ==
        \*         IF s = 1 THEN 0
        \*         ELSE IF _TETrace[s].srvCancel # _TETrace[s-1].srvCancel
        \*             THEN 1 + F[s-1] ELSE F[s-1]
        \*     IN F[_TEPosition - 1]
    ]

=============================================================================



Parsing and semantic processing can take forever if the trace below is long.
 In this case, it is advised to uncomment the module below to deserialize the
 trace from a generated binary file.

\*
\*---- MODULE QueryLifecycle_TETrace ----
\*EXTENDS IOUtils, QueryLifecycle, QueryLifecycle_TEConstants, TLC
\*
\*trace == IODeserialize("QueryLifecycle_TTrace_1791544287.bin", TRUE)
\*
\*=============================================================================
\*

---- MODULE QueryLifecycle_TETrace ----
EXTENDS QueryLifecycle, QueryLifecycle_TEConstants, TLC

trace == 
    <<
    ([rt |-> "idle",cancelReq |-> FALSE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> FALSE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 1,srvCancel |-> FALSE,overflow |-> 0,srv |-> "notstarted",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 1,net |-> 0,queue |-> <<>>]),
    ([rt |-> "registered",cancelReq |-> FALSE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> FALSE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 1,srvCancel |-> FALSE,overflow |-> 0,srv |-> "notstarted",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 1,net |-> 0,queue |-> <<>>]),
    ([rt |-> "checked",cancelReq |-> FALSE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> FALSE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 1,srvCancel |-> FALSE,overflow |-> 0,srv |-> "notstarted",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 1,net |-> 0,queue |-> <<>>]),
    ([rt |-> "reading",cancelReq |-> FALSE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> FALSE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 1,srvCancel |-> FALSE,overflow |-> 0,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 1,net |-> 0,queue |-> <<>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> FALSE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 1,srvCancel |-> TRUE,overflow |-> 0,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 1,net |-> 0,queue |-> <<>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> FALSE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 1,srvCancel |-> TRUE,overflow |-> 0,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 1,net |-> 1,queue |-> <<>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> FALSE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 1,srvCancel |-> TRUE,overflow |-> 0,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 1,net |-> 2,queue |-> <<>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> TRUE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 1,srvCancel |-> TRUE,overflow |-> 0,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 1,net |-> 1,queue |-> <<>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> FALSE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 0,srvCancel |-> TRUE,overflow |-> 0,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 0,net |-> 1,queue |-> <<[slot |-> TRUE, k |-> "rows"]>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> FALSE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 0,srvCancel |-> TRUE,overflow |-> 0,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 0,net |-> 2,queue |-> <<[slot |-> TRUE, k |-> "rows"]>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> TRUE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 0,srvCancel |-> TRUE,overflow |-> 0,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 0,net |-> 1,queue |-> <<[slot |-> TRUE, k |-> "rows"]>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> TRUE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 0,srvCancel |-> TRUE,overflow |-> 0,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 0,net |-> 2,queue |-> <<[slot |-> TRUE, k |-> "rows"]>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> FALSE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 0,srvCancel |-> TRUE,overflow |-> 1,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 0,net |-> 2,queue |-> <<[slot |-> TRUE, k |-> "rows"], [slot |-> FALSE, k |-> "rows"]>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> TRUE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 0,srvCancel |-> TRUE,overflow |-> 1,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 0,net |-> 1,queue |-> <<[slot |-> TRUE, k |-> "rows"], [slot |-> FALSE, k |-> "rows"]>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> TRUE,truncated |-> FALSE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 0,srvCancel |-> TRUE,overflow |-> 1,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 0,net |-> 2,queue |-> <<[slot |-> TRUE, k |-> "rows"], [slot |-> FALSE, k |-> "rows"]>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> FALSE,truncated |-> TRUE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 0,srvCancel |-> TRUE,overflow |-> 1,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 0,net |-> 2,queue |-> <<[slot |-> TRUE, k |-> "rows"], [slot |-> FALSE, k |-> "rows"]>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> TRUE,truncated |-> TRUE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 0,srvCancel |-> TRUE,overflow |-> 1,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 0,net |-> 1,queue |-> <<[slot |-> TRUE, k |-> "rows"], [slot |-> FALSE, k |-> "rows"]>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> TRUE,truncated |-> TRUE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 1,srvCancel |-> TRUE,overflow |-> 1,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<"rows">>,credit |-> 0,net |-> 1,queue |-> <<[slot |-> FALSE, k |-> "rows"]>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> TRUE,truncated |-> TRUE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 1,srvCancel |-> TRUE,overflow |-> 1,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 0,net |-> 1,queue |-> <<[slot |-> FALSE, k |-> "rows"]>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> TRUE,truncated |-> TRUE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 1,srvCancel |-> TRUE,overflow |-> 1,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 0,net |-> 2,queue |-> <<[slot |-> FALSE, k |-> "rows"]>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> TRUE,truncated |-> TRUE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 1,srvCancel |-> TRUE,overflow |-> 0,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<"rows">>,credit |-> 0,net |-> 2,queue |-> <<>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> TRUE,truncated |-> TRUE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 1,srvCancel |-> TRUE,overflow |-> 0,srv |-> "running",lost |-> FALSE,client |-> "reading",pipe |-> <<>>,credit |-> 0,net |-> 2,queue |-> <<>>]),
    ([rt |-> "reading",cancelReq |-> TRUE,doneCount |-> 0,connClosed |-> FALSE,hasRow |-> TRUE,truncated |-> TRUE,rowAfterDone |-> FALSE,sd |-> "no",slots |-> 1,srvCancel |-> TRUE,overflow |-> 0,srv |-> "running",lost |-> FALSE,client |-> "stalled",pipe |-> <<>>,credit |-> 0,net |-> 2,queue |-> <<>>])
    >>
----


=============================================================================

---- MODULE QueryLifecycle_TEConstants ----
EXTENDS QueryLifecycle

CONSTANTS _TTraceLassoStart, _TTraceLassoEnd

=============================================================================

---- CONFIG QueryLifecycle_TTrace_1791544287 ----
CONSTANTS
    NetCap = 2
    Slots = 1
    PipeCap = 1
    OverflowCap = 1
    CancellableSlotWait = TRUE
    OverflowAfterCancel = TRUE
    Refire = TRUE
    AttentionNeedsSendRoom = TRUE
    ConnCheck = TRUE
    Pull = TRUE
    Window = 1
    CreditCap = 2
    CancellableCreditWait = FALSE
_TTraceLassoStart = 22
_TTraceLassoEnd = 23

PROPERTY
    _prop

CHECK_DEADLOCK
    \* CHECK_DEADLOCK off because of PROPERTY or INVARIANT above.
    FALSE

INIT
    _init

NEXT
    _next

VIEW
    _view

CONSTANT
    _TETrace <- _trace

ALIAS
    _expression
=============================================================================
\* Generated on Fri Oct 09 11:11:31 UTC 2026