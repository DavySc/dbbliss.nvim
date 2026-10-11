------------------------------ MODULE CompletionCache -------------------------
(***************************************************************************)
(* The completion cache of one connection (lua/dbbliss/completion.lua)     *)
(* against a user who runs DDL, with and without a transaction.            *)
(*                                                                         *)
(* The catalog session is a second session. It sees only committed        *)
(* schema changes, and on SQL Server a catalog read WAITS for the locks of *)
(* another session's uncommitted DDL (measured: lock timeout on a CREATE   *)
(* TABLE inside an open transaction), holding the connection's catalog     *)
(* lock for up to the 30 s catalog timeout. Hence the rule: no catalog     *)
(* request for completion while a transaction that changed the schema is   *)
(* open, and a reload waits for the transaction to end.                    *)
(*                                                                         *)
(* gen counts the schema changes the client has seen complete. A load     *)
(* is asked at some gen (rGen) and its answer describes the server as it   *)
(* was when asked (rVer); the cache may be called fresh (cGen = gen) only  *)
(* if nothing completed since. Boolean constants select design variants,   *)
(* as in the other models: every variant except CC_Proposed must fail (see *)
(* the CC_*.cfg files and spec/check.sh).                                  *)
(***************************************************************************)
EXTENDS Naturals

CONSTANTS
    MaxDdl, MaxTx,        \* bounds on schema changes and transactions
    ReloadWaitsForTx,     \* a reload is not sent while a transaction is open
    FirstLoadSkipsDdlTx,  \* the first load is not sent while an open transaction changed the schema
    GenerationTracked,    \* an answer marks the cache with the gen it was asked at (else with the current one)
    RetryAfterTx          \* a schema change inside a transaction is remembered, so the reload follows the end of it

VARIABLES
    txOpen,       \* a transaction is open on the connection
    ddlInTx,      \* a schema change completed inside it
    pend,         \* uncommitted schema changes
    ver,          \* committed schema version (what the catalog session can see)
    ddls, txs,    \* how many schema changes / transactions so far (bounds)
    gen,          \* schema changes the client has seen complete
    cache,        \* "none" | "ready"
    cVer, cGen,   \* the version the cached data describes / the gen it was asked at
    inflight,     \* a catalog request is out
    rVer, rGen,   \* its version and gen, taken when it was sent
    want,         \* the client means to reload
    blockedReq    \* a request was sent while a transaction with schema changes was open

vars == <<txOpen, ddlInTx, pend, ver, ddls, txs, gen, cache, cVer, cGen, inflight, rVer, rGen, want, blockedReq>>

TypeOK ==
    /\ {txOpen, ddlInTx, inflight, want, blockedReq} \subseteq BOOLEAN
    /\ cache \in {"none", "ready"}
    /\ {pend, ver, ddls, txs, gen, cVer, cGen, rVer, rGen} \subseteq Nat

Init ==
    /\ txOpen = FALSE /\ ddlInTx = FALSE /\ pend = 0 /\ ver = 0 /\ ddls = 0 /\ txs = 0 /\ gen = 0
    /\ cache = "none" /\ cVer = 0 /\ cGen = 0 /\ inflight = FALSE /\ rVer = 0 /\ rGen = 0
    /\ want = FALSE /\ blockedReq = FALSE

Begin ==
    /\ ~txOpen /\ txs < MaxTx
    /\ txOpen' = TRUE /\ txs' = txs + 1
    /\ UNCHANGED <<ddlInTx, pend, ver, ddls, gen, cache, cVer, cGen, inflight, rVer, rGen, want, blockedReq>>

\* A CREATE/ALTER/DROP completes: at once visible under autocommit, pending inside a transaction.
Ddl ==
    /\ ddls < MaxDdl
    /\ ddls' = ddls + 1
    /\ gen' = gen + 1
    /\ IF txOpen
          THEN /\ pend' = pend + 1 /\ ddlInTx' = TRUE /\ ver' = ver /\ want' = (want \/ RetryAfterTx)
          ELSE /\ ver' = ver + 1 /\ UNCHANGED <<pend, ddlInTx>> /\ want' = TRUE
    /\ UNCHANGED <<txOpen, txs, cache, cVer, cGen, inflight, rVer, rGen, blockedReq>>

Commit ==
    /\ txOpen
    /\ txOpen' = FALSE /\ ddlInTx' = FALSE /\ pend' = 0 /\ ver' = ver + pend
    /\ UNCHANGED <<ddls, txs, gen, cache, cVer, cGen, inflight, rVer, rGen, want, blockedReq>>

Rollback ==
    /\ txOpen
    /\ txOpen' = FALSE /\ ddlInTx' = FALSE /\ pend' = 0
    /\ UNCHANGED <<ver, ddls, txs, gen, cache, cVer, cGen, inflight, rVer, rGen, want, blockedReq>>

Send ==
    /\ inflight' = TRUE /\ rVer' = ver /\ rGen' = gen
    /\ blockedReq' = (blockedReq \/ (txOpen /\ ddlInTx))

\* The user types and there is no cache yet.
FirstLoad ==
    /\ cache = "none" /\ ~inflight
    /\ (~FirstLoadSkipsDdlTx \/ ~ddlInTx)
    /\ Send
    /\ UNCHANGED <<txOpen, ddlInTx, pend, ver, ddls, txs, gen, cache, cVer, cGen, want>>

Reload ==
    /\ cache = "ready" /\ want /\ ~inflight
    /\ (~ReloadWaitsForTx \/ ~txOpen)
    /\ Send
    /\ want' = FALSE
    /\ UNCHANGED <<txOpen, ddlInTx, pend, ver, ddls, txs, gen, cache, cVer, cGen>>

Respond ==
    /\ inflight
    /\ cache' = "ready" /\ cVer' = rVer
    /\ cGen' = IF GenerationTracked THEN rGen ELSE gen
    /\ want' = (cGen' < gen)   \* a change completed meanwhile: the answer is used, and another reload follows
    /\ inflight' = FALSE
    /\ UNCHANGED <<txOpen, ddlInTx, pend, ver, ddls, txs, gen, rVer, rGen, blockedReq>>

Next == Begin \/ Ddl \/ Commit \/ Rollback \/ FirstLoad \/ Reload \/ Respond

Spec == Init /\ [][Next]_vars /\ WF_vars(Reload) /\ WF_vars(Respond) /\ WF_vars(Commit \/ Rollback)

(* Safety: no catalog request waits on locks of this client's own uncommitted DDL. *)
NoBlockedRequest == ~blockedReq

(* Safety: a cache that claims to be current is: it describes the committed version and nothing is pending. *)
NeverFalselyFresh == (cache = "ready" /\ cGen = gen) => (cVer = ver /\ pend = 0)

(* Liveness: a cache that is out of date is brought up to date. *)
StaleReloads == [](cache = "ready" => <>(cGen = gen))
=============================================================================
