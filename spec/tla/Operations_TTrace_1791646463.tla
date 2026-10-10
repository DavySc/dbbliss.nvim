---- MODULE Operations_TTrace_1791646463 ----
EXTENDS Sequences, TLCExt, Toolbox, Naturals, TLC, Operations

_expression ==
    LET Operations_TEExpression == INSTANCE Operations_TEExpression
    IN Operations_TEExpression!expression
----

_trace ==
    LET Operations_TETrace == INSTANCE Operations_TETrace
    IN Operations_TETrace!trace
----

_inv ==
    ~(
        TLCGet("level") = Len(_TETrace)
        /\
        op = ("done")
        /\
        proc = ("dead")
        /\
        conn = ("open")
        /\
        dropBusy = (FALSE)
        /\
        dropBk = ("none")
        /\
        dropped = (TRUE)
        /\
        bk = ("none")
        /\
        backend = ("up")
        /\
        env = ("prod")
    )
----

_init ==
    /\ proc = _TETrace[1].proc
    /\ conn = _TETrace[1].conn
    /\ dropped = _TETrace[1].dropped
    /\ bk = _TETrace[1].bk
    /\ op = _TETrace[1].op
    /\ env = _TETrace[1].env
    /\ dropBusy = _TETrace[1].dropBusy
    /\ backend = _TETrace[1].backend
    /\ dropBk = _TETrace[1].dropBk
----

_next ==
    /\ \E i,j \in DOMAIN _TETrace:
        /\ \/ /\ j = i + 1
              /\ i = TLCGet("level")
        /\ proc  = _TETrace[i].proc
        /\ proc' = _TETrace[j].proc
        /\ conn  = _TETrace[i].conn
        /\ conn' = _TETrace[j].conn
        /\ dropped  = _TETrace[i].dropped
        /\ dropped' = _TETrace[j].dropped
        /\ bk  = _TETrace[i].bk
        /\ bk' = _TETrace[j].bk
        /\ op  = _TETrace[i].op
        /\ op' = _TETrace[j].op
        /\ env  = _TETrace[i].env
        /\ env' = _TETrace[j].env
        /\ dropBusy  = _TETrace[i].dropBusy
        /\ dropBusy' = _TETrace[j].dropBusy
        /\ backend  = _TETrace[i].backend
        /\ backend' = _TETrace[j].backend
        /\ dropBk  = _TETrace[i].dropBk
        /\ dropBk' = _TETrace[j].dropBk

\* Uncomment the ASSUME below to write the states of the error trace
\* to the given file in Json format. Note that you can pass any tuple
\* to `JsonSerialize`. For example, a sub-sequence of _TETrace.
    \* ASSUME
    \*     LET J == INSTANCE Json
    \*         IN J!JsonSerialize("Operations_TTrace_1791646463.json", _TETrace)

=============================================================================

 Note that you can extract this module `Operations_TEExpression`
  to a dedicated file to reuse `expression` (the module in the 
  dedicated `Operations_TEExpression.tla` file takes precedence 
  over the module `Operations_TEExpression` below).

---- MODULE Operations_TEExpression ----
EXTENDS Sequences, TLCExt, Toolbox, Naturals, TLC, Operations

expression == 
    [
        \* To hide variables of the `Operations` spec from the error trace,
        \* remove the variables below.  The trace will be written in the order
        \* of the fields of this record.
        proc |-> proc
        ,conn |-> conn
        ,dropped |-> dropped
        ,bk |-> bk
        ,op |-> op
        ,env |-> env
        ,dropBusy |-> dropBusy
        ,backend |-> backend
        ,dropBk |-> dropBk
        
        \* Put additional constant-, state-, and action-level expressions here:
        \* ,_stateNumber |-> _TEPosition
        \* ,_procUnchanged |-> proc = proc'
        
        \* Format the `proc` variable as Json value.
        \* ,_procJson |->
        \*     LET J == INSTANCE Json
        \*     IN J!ToJson(proc)
        
        \* Lastly, you may build expressions over arbitrary sets of states by
        \* leveraging the _TETrace operator.  For example, this is how to
        \* count the number of times a spec variable changed up to the current
        \* state in the trace.
        \* ,_procModCount |->
        \*     LET F[s \in DOMAIN _TETrace] ==
        \*         IF s = 1 THEN 0
        \*         ELSE IF _TETrace[s].proc # _TETrace[s-1].proc
        \*             THEN 1 + F[s-1] ELSE F[s-1]
        \*     IN F[_TEPosition - 1]
    ]

=============================================================================



Parsing and semantic processing can take forever if the trace below is long.
 In this case, it is advised to uncomment the module below to deserialize the
 trace from a generated binary file.

\*
\*---- MODULE Operations_TETrace ----
\*EXTENDS IOUtils, TLC, Operations
\*
\*trace == IODeserialize("Operations_TTrace_1791646463.bin", TRUE)
\*
\*=============================================================================
\*

---- MODULE Operations_TETrace ----
EXTENDS TLC, Operations

trace == 
    <<
    ([op |-> "none",proc |-> "none",conn |-> "open",dropBusy |-> FALSE,dropBk |-> "none",dropped |-> FALSE,bk |-> "none",backend |-> "up",env |-> "prod"]),
    ([op |-> "running",proc |-> "alive",conn |-> "open",dropBusy |-> FALSE,dropBk |-> "none",dropped |-> FALSE,bk |-> "none",backend |-> "up",env |-> "prod"]),
    ([op |-> "done",proc |-> "dead",conn |-> "open",dropBusy |-> FALSE,dropBk |-> "none",dropped |-> FALSE,bk |-> "none",backend |-> "up",env |-> "prod"]),
    ([op |-> "done",proc |-> "dead",conn |-> "open",dropBusy |-> FALSE,dropBk |-> "none",dropped |-> TRUE,bk |-> "none",backend |-> "up",env |-> "prod"])
    >>
----


=============================================================================

---- CONFIG Operations_TTrace_1791646463 ----
CONSTANTS
    DisconnectRefusedWhileBusy = TRUE
    DropRefusedWhileBusy = TRUE
    ShutdownCancelsOps = TRUE
    ProdDropNeedsBackup = FALSE
    StaleCounts = FALSE

INVARIANT
    _inv

CHECK_DEADLOCK
    \* CHECK_DEADLOCK off because of PROPERTY or INVARIANT above.
    FALSE

INIT
    _init

NEXT
    _next

CONSTANT
    _TETrace <- _trace

ALIAS
    _expression
=============================================================================
\* Generated on Sat Oct 10 15:34:24 UTC 2026