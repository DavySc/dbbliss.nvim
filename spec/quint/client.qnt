// -*- mode: Bluespec; -*-
/**
 * dbbliss.nvim: the Lua client, the backend's per-connection lease, and transaction bookkeeping,
 * for PostgreSQL and SQL Server. Companion to spec/tla/QueryLifecycle.tla, which covers row
 * streaming, cancel and shutdown for one query; here a query is one atomic server step.
 *
 * Actors:
 *   - the user, through the Lua API (connect, exec, cancel, begin/commit/rollback, disconnect);
 *   - the Lua client: state.connections / state.queries, request callbacks, notification handlers;
 *   - the backend: requests are read in order and their synchronous part (lease TryAcquire, query
 *     registration) runs in that order; everything after the first await interleaves freely;
 *   - the database server: the real transaction state of each session.
 *
 * Statements typed by the user may themselves be BEGIN / COMMIT / ROLLBACK. Without the
 * ServerTruth fix the backend only knows about transactions it opened through the driver (its
 * local bookkeeping), which is where most of what this model finds comes from.
 *
 * Driver behaviour that is assumed rather than verified is marked ASSUMPTION. Each one is a test
 * to write against the real drivers before relying on a result that depends on it.
 *
 * Quitting Neovim (Phase 1 M4): the backend's shutdown closes every connection, which rolls back
 * whatever is open. Lua asks first (ExitPre), so a quit is only ever one the user agreed to.
 *
 * Five constants select design fixes; see the variant modules at the end of the file.
 * State is kept in many small variables: Apalache (used by `quint verify`, also for TLC) rejects
 * the model when large record types are repeated on every expression.
 */
module client {
  // The backend takes the transaction state from the server (or the driver's view of it, which
  // the server keeps current) instead of from its own bookkeeping, for the probe, for the
  // disconnect guard and for begin/commit/rollback.
  const ServerTruth: bool
  // The backend releases a connection's lease only after the query/done or the transaction
  // response is written, so no later operation on that connection can overtake the report.
  const LeaseAfterWrite: bool
  // Lua: refuse to connect a name that is connected or connecting, and apply callbacks only to
  // the connection they were made for (by id, not by name).
  const LuaById: bool

  // Lua: quitting asks the user when its view shows an open transaction.
  const AskOnQuit: bool
  // Lua: ... and also when a query or transaction call is in flight, because its report is not
  // here yet and the server may already hold a transaction Lua has not heard of.
  const AskWhileRunning: bool

  const MaxActions: int
  const MaxConnects: int

  // ---------------------------------------------------------------------------------------------
  // Types

  type Engine = PG | MSSQL
  type SrvTx = TNone | TActive | TAborted
  // The backend's transaction object: Npgsql/SqlClient transaction, if any. Zombie: SqlClient
  // transaction whose server transaction is gone (SqlTransaction.Connection == null).
  type TxObj = NoTx | Held | Zombie
  type Sql = Plain | Fail | BeginText | CommitText | RollbackText
  type TxOp = OpBegin | OpCommit | OpRollback

  type LuaConn = { id: int, tx: str }
  type LuaConnOpt = NoConn | Conn(LuaConn)
  // What a pending request's callback closes over.
  type Cb = CbConnect | CbExecute(int) | CbCancel | CbTx(int) | CbDisconnect(int)

  type Req =
    | RConnect(int)
    | RExecute({ req: int, conn: int, qid: int, sql: Sql })
    | RCancel({ req: int, qid: int })
    | RTx({ req: int, conn: int, op: TxOp })
    | RDisconnect({ req: int, conn: int, rollback: bool })

  type Msg =
    | MResp({ req: int, ok: bool, tx: str, conn: int })
    | MDone({ qid: int, tx: str })

  type BeConn = { engine: Engine, obj: TxObj, busy: bool }
  // finished: executed and probed; query/done not written yet.
  type Run = { conn: int, sql: Sql, cancel: bool, finished: bool, tx: str }
  type TxJob = { conn: int, op: TxOp, finished: bool, ok: bool, tx: str }
  type DiscJob = { conn: int, rollback: bool, ready: bool }

  // ---------------------------------------------------------------------------------------------
  // State

  // Lua (one connection name, 'dev', is enough)
  var lconn: LuaConnOpt        // state.connections['dev']
  var lconnecting: bool
  var lqueries: int -> int     // state.queries: query id -> backend connection id it ran on
  var lpending: int -> Cb      // backend.pending: request id -> callback
  var nextReq: int
  var nextQid: int
  var connects: int

  // Backend
  var conns: int -> BeConn
  var runs: int -> Run         // by query id
  var txJobs: int -> TxJob     // by request id
  var discs: int -> DiscJob    // by request id; ready = holds the lease
  var nextConn: int

  var srv: int -> SrvTx        // server sessions that are open, and their transaction state
  var toBe: List[Req]          // Neovim -> backend stdin
  var toLua: List[Msg]         // backend stdout -> Neovim (one ordered writer)
  var silentRollback: bool     // a disconnect rolled back a transaction nobody asked about
  var budget: int              // user actions left
  var quitLog: int             // 0: Neovim has not quit; 1: it quit with nothing open; 2: it quit and the user agreed to a rollback

  // ---------------------------------------------------------------------------------------------
  // Helpers

  pure def remove(m: a -> b, k: a): a -> b = m.keys().exclude(Set(k)).mapBy(x => m.get(x))
  pure def minOf(s: Set[int]): int = s.fold(1000000, (acc, x) => if (x < acc) x else acc)

  pure def txName(t: SrvTx): str = match t {
    | TNone => "none"
    | TActive => "active"
    | TAborted => "aborted"
  }

  pure def connId(o: LuaConnOpt): int = match o {
    | NoConn => -1
    | Conn(c) => c.id
  }

  pure def hasConn(o: LuaConnOpt): bool = connId(o) >= 0

  // A transaction call is in flight (its report has not reached Lua).
  pure def isTxCall(c: Cb): bool = match c {
    | CbConnect => false
    | CbExecute(_) => false
    | CbCancel => false
    | CbTx(_) => true
    | CbDisconnect(_) => false
  }

  pure def connTx(o: LuaConnOpt): str = match o {
    | NoConn => ""
    | Conn(c) => c.tx
  }

  pure def setConnTx(o: LuaConnOpt, tx: str): LuaConnOpt = match o {
    | NoConn => NoConn
    | Conn(c) => Conn({ ...c, tx: tx })
  }

  // What the server does with a statement: (ok, new transaction state) options.
  pure def sqlOutcomes(e: Engine, t: SrvTx, s: Sql, cancel: bool): Set[{ ok: bool, tx: SrvTx }] = {
    val cancelled =
      if (not(cancel)) Set()
      else if (e == PG) Set({ ok: false, tx: if (t == TActive) TAborted else t })
      // SQL Server: the transaction survives an attention unless XACT_ABORT is ON.
      else if (t == TActive) Set({ ok: false, tx: TActive }, { ok: false, tx: TNone })
      else Set({ ok: false, tx: t })
    match s {
      | Plain =>
        if (e == PG and t == TAborted) Set({ ok: false, tx: TAborted })
        else Set({ ok: true, tx: t }).union(cancelled)
      | Fail =>
        if (e == PG) Set({ ok: false, tx: if (t == TNone) TNone else TAborted })
        // XACT_ABORT OFF keeps it, ON rolls it back, some errors make it uncommittable.
        else if (t == TActive) Set({ ok: false, tx: TActive }, { ok: false, tx: TNone }, { ok: false, tx: TAborted })
        else Set({ ok: false, tx: t })
      | BeginText =>
        if (t == TAborted) Set({ ok: false, tx: TAborted })
        else Set({ ok: true, tx: TActive })   // PG: warning if already open; MSSQL: nests
      | CommitText =>
        if (e == PG) Set({ ok: true, tx: TNone })   // PG turns COMMIT of an aborted tx into ROLLBACK
        else if (t == TActive) Set({ ok: true, tx: TNone })
        else Set({ ok: false, tx: t })
      | RollbackText =>
        if (e == MSSQL and t == TNone) Set({ ok: false, tx: TNone })
        else Set({ ok: true, tx: TNone })
    }
  }

  // SqlClient zombies its transaction object when the server transaction ends.
  pure def updObj(e: Engine, o: TxObj, t: SrvTx): TxObj =
    if (e == MSSQL and o == Held and t == TNone) Zombie else o

  // Backend.ProbeTransactionAsync -> GetTransactionStateAsync.
  pure def probe(serverTruth: bool, e: Engine, o: TxObj, t: SrvTx): Set[str] =
    if (serverTruth or e == MSSQL) Set(txName(t))   // MSSQL: @@TRANCOUNT / XACT_STATE()
    else if (o == NoTx) Set("none")                 // PG: `if (_tx is null) return None`
    else if (t == TNone)
      // ASSUMPTION: `SELECT 1` on a completed NpgsqlTransaction either succeeds or throws (unknown).
      Set("active", "unknown")
    else Set(txName(t))

  // IEngineSession.InTransaction, the disconnect guard.
  pure def inTx(serverTruth: bool, o: TxObj, t: SrvTx): bool =
    if (serverTruth) t != TNone else o == Held

  // begin/commit/rollback through the API.
  pure def txOutcomes(serverTruth: bool, e: Engine, o: TxObj, t: SrvTx, op: TxOp)
      : Set[{ ok: bool, obj: TxObj, tx: SrvTx }] = {
    val err = Set({ ok: false, obj: o, tx: t })
    if (serverTruth) match op {
      | OpBegin => if (t == TNone) Set({ ok: true, obj: NoTx, tx: TActive }) else err
      | OpCommit => if (t == TActive) Set({ ok: true, obj: NoTx, tx: TNone }) else err
      | OpRollback => Set({ ok: true, obj: NoTx, tx: TNone })
    }
    else if (e == PG) match op {
      | OpBegin =>
        // ASSUMPTION: Npgsql refuses BeginTransaction when the server reports a transaction.
        if (o == Held or t != TNone) err else Set({ ok: true, obj: Held, tx: TActive })
      | OpCommit =>
        if (o == NoTx or t == TAborted) err
        // ASSUMPTION: Npgsql refuses to commit a transaction the server already ended.
        else if (t == TNone) err
        else Set({ ok: true, obj: NoTx, tx: TNone })
      | OpRollback =>
        if (o == NoTx) Set({ ok: true, obj: NoTx, tx: t })   // `if (_tx is null) return;`
        // ASSUMPTION: rolling back an NpgsqlTransaction the server already ended may throw.
        else if (t == TNone) Set({ ok: true, obj: NoTx, tx: TNone }, { ok: false, obj: Held, tx: TNone })
        else Set({ ok: true, obj: NoTx, tx: TNone })
    }
    else match op {
      | OpBegin => if (o == Held) err else Set({ ok: true, obj: Held, tx: TActive })
      | OpCommit => if (o == Held and t == TActive) Set({ ok: true, obj: NoTx, tx: TNone }) else err
      | OpRollback =>
        if (o == Held) Set({ ok: true, obj: NoTx, tx: TNone })
        else Set({ ok: true, obj: NoTx, tx: t })   // zombie disposed, or nothing to do
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Frame helpers

  action keepLua = all {
    quitLog' = quitLog,
    lconn' = lconn, lconnecting' = lconnecting, lqueries' = lqueries, lpending' = lpending,
    nextReq' = nextReq, nextQid' = nextQid, connects' = connects,
  }

  action keepBe = all {
    conns' = conns, runs' = runs, txJobs' = txJobs, discs' = discs, nextConn' = nextConn,
  }

  // Lease.Dispose on connection c, given the backend maps after the step: SemaphoreSlim hands the
  // lease straight to the oldest AcquireAsync waiter.
  action release(c: int, cs: int -> BeConn, ds: int -> DiscJob): bool = {
    val waiting = ds.keys().filter(r => ds.get(r).conn == c and not(ds.get(r).ready))
    if (waiting.size() > 0) all {
      discs' = ds.set(minOf(waiting), { ...ds.get(minOf(waiting)), ready: true }),
      conns' = cs,
    }
    else all {
      discs' = ds,
      conns' = if (cs.keys().contains(c)) cs.set(c, { ...cs.get(c), busy: false }) else cs,
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Init

  action init = all {
    lconn' = NoConn, lconnecting' = false, lqueries' = Map(), lpending' = Map(),
    nextReq' = 1, nextQid' = 1, connects' = 0,
    conns' = Map(), runs' = Map(), txJobs' = Map(), discs' = Map(), nextConn' = 1,
    srv' = Map(), toBe' = [], toLua' = [], silentRollback' = false, budget' = MaxActions, quitLog' = 0,
  }

  // ---------------------------------------------------------------------------------------------
  // User, through the Lua API

  // Writes one request and registers its callback; the caller sets lconn/lconnecting/lqueries/
  // nextQid/connects.
  action userSends(r: Req, cb: Cb): bool = all {
    budget > 0,
    lpending' = lpending.put(nextReq, cb),
    nextReq' = nextReq + 1,
    toBe' = toBe.append(r),
    budget' = budget - 1,
    keepBe, srv' = srv, toLua' = toLua, silentRollback' = silentRollback, quitLog' = quitLog,
  }

  // M.connect
  action UConnect = all {
    connects < MaxConnects,
    not(LuaById) or (not(hasConn(lconn)) and not(lconnecting)),
    userSends(RConnect(nextReq), CbConnect),
    lconnecting' = true, connects' = connects + 1,
    lconn' = lconn, lqueries' = lqueries, nextQid' = nextQid,
  }

  // M.execute
  action UExecute(s: Sql): bool = all {
    hasConn(lconn),
    userSends(RExecute({ req: nextReq, conn: connId(lconn), qid: nextQid, sql: s }), CbExecute(nextQid)),
    lqueries' = lqueries.put(nextQid, connId(lconn)), nextQid' = nextQid + 1,
    lconn' = lconn, lconnecting' = lconnecting, connects' = connects,
  }

  // M.cancel: some query on the current connection.
  action UCancel(qid: int): bool = all {
    hasConn(lconn),
    lqueries.keys().contains(qid),
    userSends(RCancel({ req: nextReq, qid: qid }), CbCancel),
    lconn' = lconn, lconnecting' = lconnecting, lqueries' = lqueries, nextQid' = nextQid, connects' = connects,
  }

  // M.begin / M.commit / M.rollback
  action UTx(op: TxOp): bool = all {
    hasConn(lconn),
    userSends(RTx({ req: nextReq, conn: connId(lconn), op: op }), CbTx(connId(lconn))),
    lconn' = lconn, lconnecting' = lconnecting, lqueries' = lqueries, nextQid' = nextQid, connects' = connects,
  }

  // M.disconnect: if Lua believes a transaction is open, the user is asked and agrees to roll
  // back (declining sends nothing).
  action UDisconnect = all {
    hasConn(lconn),
    userSends(RDisconnect({ req: nextReq, conn: connId(lconn), rollback: connTx(lconn) != "none" }),
              CbDisconnect(connId(lconn))),
    lconn' = lconn, lconnecting' = lconnecting, lqueries' = lqueries, nextQid' = nextQid, connects' = connects,
  }

  // Quitting Neovim, once it goes ahead. Lua asks first when its view shows a transaction (or, with
  // AskWhileRunning, when something is in flight); this action is the quit the user agreed to, or
  // the one nothing made Lua ask about. The backend's shutdown closes every connection, so the
  // world is gone afterwards. A transaction that was open and not asked about is a silent rollback.
  action UQuit = {
    val viewOpen = hasConn(lconn) and connTx(lconn) != "none"
    val inflight = lqueries.keys().size() > 0 or
      lpending.keys().exists(r => isTxCall(lpending.get(r)))
    val asked = AskOnQuit and (viewOpen or (AskWhileRunning and inflight))
    val serverOpen = srv.keys().exists(c => srv.get(c) != TNone)
    all {
      budget > 0,
      quitLog == 0,
      lconn' = NoConn, lconnecting' = false, lqueries' = Map(), lpending' = Map(),
      nextReq' = nextReq, nextQid' = nextQid, connects' = connects,
      conns' = Map(), runs' = Map(), txJobs' = Map(), discs' = Map(), nextConn' = nextConn,
      srv' = Map(), toBe' = [], toLua' = [],
      silentRollback' = (silentRollback or (serverOpen and not(asked))),
      budget' = budget - 1,
      quitLog' = if (asked and serverOpen) 2 else 1,
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Lua: one message from the backend, in order (vim.schedule keeps the order)

  action luaFrame = all {
    toLua' = toLua.tail(),
    keepBe, srv' = srv, toBe' = toBe, silentRollback' = silentRollback, budget' = budget,
    nextReq' = nextReq, nextQid' = nextQid, connects' = connects, quitLog' = quitLog,
  }

  action LuaResp(r: { req: int, ok: bool, tx: str, conn: int }): bool = all {
    luaFrame,
    lpending' = remove(lpending, r.req),
    match lpending.get(r.req) {
      | CbConnect => all {
        // state.connections[name] = { ..., transaction = 'none' }
        lconn' = Conn({ id: r.conn, tx: "none" }), lconnecting' = false, lqueries' = lqueries,
      }
      | CbExecute(qid) => all {
        lqueries' = if (r.ok) lqueries else remove(lqueries, qid),
        lconn' = lconn, lconnecting' = lconnecting,
      }
      | CbCancel => all { lconn' = lconn, lconnecting' = lconnecting, lqueries' = lqueries }
      | CbTx(cid) => all {
        // conn.transaction = result.transaction, on the conn table the closure captured
        lconn' = if (r.ok and connId(lconn) == cid) setConnTx(lconn, r.tx) else lconn,
        lconnecting' = lconnecting, lqueries' = lqueries,
      }
      | CbDisconnect(cid) => all {
        // state.connections[name] = nil: by name, whichever connection has it now
        lconn' = if (r.ok and (not(LuaById) or connId(lconn) == cid)) NoConn else lconn,
        lconnecting' = lconnecting, lqueries' = lqueries,
      }
    },
  }

  action LuaDone(d: { qid: int, tx: str }): bool = all {
    luaFrame,
    lpending' = lpending, lconnecting' = lconnecting,
    if (lqueries.keys().contains(d.qid)) all {
      lqueries' = remove(lqueries, d.qid),
      // state.connections[q.connection]: by name
      lconn' = if (hasConn(lconn) and (not(LuaById) or connId(lconn) == lqueries.get(d.qid)))
                 setConnTx(lconn, d.tx)
               else lconn,
    }
    else all { lqueries' = lqueries, lconn' = lconn },
  }

  action LuaRecv = all {
    toLua.length() > 0,
    match toLua.head() {
      | MResp(r) => LuaResp(r)
      | MDone(d) => LuaDone(d)
    },
  }

  // ---------------------------------------------------------------------------------------------
  // Backend: synchronous part of each request, in arrival order

  action beFrame = all {
    toBe' = toBe.tail(),
    keepLua, srv' = srv, silentRollback' = silentRollback, budget' = budget, nextConn' = nextConn,
  }

  action BeConnect(req: int): bool = all {
    nondet e = Set(PG, MSSQL).oneOf()
    all {
      conns' = conns.put(nextConn, { engine: e, obj: NoTx, busy: false }),
      srv' = srv.put(nextConn, TNone),
      nextConn' = nextConn + 1,
      toLua' = toLua.append(MResp({ req: req, ok: true, tx: "none", conn: nextConn })),
      toBe' = toBe.tail(),
      runs' = runs, txJobs' = txJobs, discs' = discs,
      keepLua, silentRollback' = silentRollback, budget' = budget,
    },
  }

  action BeExecute(x: { req: int, conn: int, qid: int, sql: Sql }): bool = all {
    beFrame, txJobs' = txJobs, discs' = discs,
    if (not(conns.keys().contains(x.conn)) or conns.get(x.conn).busy) all {
      // unknown connection / connection_busy
      toLua' = toLua.append(MResp({ req: x.req, ok: false, tx: "", conn: x.conn })),
      conns' = conns, runs' = runs,
    }
    else all {
      // The response is written before the query starts.
      toLua' = toLua.append(MResp({ req: x.req, ok: true, tx: "", conn: x.conn })),
      conns' = conns.set(x.conn, { ...conns.get(x.conn), busy: true }),
      runs' = runs.put(x.qid, { conn: x.conn, sql: x.sql, cancel: false, finished: false, tx: "" }),
    },
  }

  action BeCancel(x: { req: int, qid: int }): bool = all {
    beFrame, conns' = conns, txJobs' = txJobs, discs' = discs,
    runs' = if (runs.keys().contains(x.qid) and not(runs.get(x.qid).finished))
              runs.set(x.qid, { ...runs.get(x.qid), cancel: true })
            else runs,
    toLua' = toLua.append(MResp({ req: x.req, ok: true, tx: "", conn: -1 })),
  }

  action BeTx(x: { req: int, conn: int, op: TxOp }): bool = all {
    beFrame, runs' = runs, discs' = discs,
    if (not(conns.keys().contains(x.conn)) or conns.get(x.conn).busy) all {
      toLua' = toLua.append(MResp({ req: x.req, ok: false, tx: "", conn: x.conn })),
      conns' = conns, txJobs' = txJobs,
    }
    else all {
      toLua' = toLua,
      conns' = conns.set(x.conn, { ...conns.get(x.conn), busy: true }),
      txJobs' = txJobs.put(x.req, { conn: x.conn, op: x.op, finished: false, ok: false, tx: "" }),
    },
  }

  action BeDisconnect(x: { req: int, conn: int, rollback: bool }): bool = all {
    beFrame, runs' = runs, txJobs' = txJobs,
    if (not(conns.keys().contains(x.conn))) all {
      toLua' = toLua.append(MResp({ req: x.req, ok: false, tx: "", conn: x.conn })),
      conns' = conns, discs' = discs,
    }
    else all {
      // AcquireAsync: take the lease now or wait for it.
      toLua' = toLua,
      conns' = if (conns.get(x.conn).busy) conns else conns.set(x.conn, { ...conns.get(x.conn), busy: true }),
      discs' = discs.put(x.req, { conn: x.conn, rollback: x.rollback, ready: not(conns.get(x.conn).busy) }),
    },
  }

  action BeRecv = all {
    toBe.length() > 0,
    match toBe.head() {
      | RConnect(req) => BeConnect(req)
      | RExecute(x) => BeExecute(x)
      | RCancel(x) => BeCancel(x)
      | RTx(x) => BeTx(x)
      | RDisconnect(x) => BeDisconnect(x)
    },
  }

  // ---------------------------------------------------------------------------------------------
  // Backend: asynchronous steps

  action asyncFrame = all {
    keepLua, toBe' = toBe, budget' = budget, nextConn' = nextConn,
  }

  // RunQueryAsync up to and including the transaction probe.
  action RunExec(qid: int): bool = all {
    runs.keys().contains(qid),
    not(runs.get(qid).finished),
    asyncFrame, toLua' = toLua, txJobs' = txJobs, silentRollback' = silentRollback,
    nondet out = sqlOutcomes(conns.get(runs.get(qid).conn).engine, srv.get(runs.get(qid).conn),
                             runs.get(qid).sql, runs.get(qid).cancel).oneOf()
    nondet tx = probe(ServerTruth, conns.get(runs.get(qid).conn).engine,
                      updObj(conns.get(runs.get(qid).conn).engine, conns.get(runs.get(qid).conn).obj, out.tx),
                      out.tx).oneOf()
    all {
      srv' = srv.set(runs.get(qid).conn, out.tx),
      runs' = runs.set(qid, { ...runs.get(qid), finished: true, tx: tx }),
      {
        val cs = conns.set(runs.get(qid).conn, { ...conns.get(runs.get(qid).conn),
          obj: updObj(conns.get(runs.get(qid).conn).engine, conns.get(runs.get(qid).conn).obj, out.tx) })
        // Current code: lease.Dispose() before query/done is written.
        if (LeaseAfterWrite) all { conns' = cs, discs' = discs } else release(runs.get(qid).conn, cs, discs)
      },
    },
  }

  action RunEmit(qid: int): bool = all {
    runs.keys().contains(qid),
    runs.get(qid).finished,
    asyncFrame, srv' = srv, txJobs' = txJobs, silentRollback' = silentRollback,
    toLua' = toLua.append(MDone({ qid: qid, tx: runs.get(qid).tx })),
    runs' = remove(runs, qid),
    if (LeaseAfterWrite) release(runs.get(qid).conn, conns, discs) else all { conns' = conns, discs' = discs },
  }

  // TransactionAsync: the action and the probe; `using var lease` ends before the response is written.
  action TxExec(req: int): bool = all {
    txJobs.keys().contains(req),
    not(txJobs.get(req).finished),
    asyncFrame, toLua' = toLua, runs' = runs, silentRollback' = silentRollback,
    nondet out = txOutcomes(ServerTruth, conns.get(txJobs.get(req).conn).engine, conns.get(txJobs.get(req).conn).obj,
                            srv.get(txJobs.get(req).conn), txJobs.get(req).op).oneOf()
    nondet tx = probe(ServerTruth, conns.get(txJobs.get(req).conn).engine, out.obj, out.tx).oneOf()
    all {
      srv' = srv.set(txJobs.get(req).conn, out.tx),
      txJobs' = txJobs.set(req, { ...txJobs.get(req), finished: true, ok: out.ok, tx: if (out.ok) tx else "" }),
      {
        val cs = conns.set(txJobs.get(req).conn, { ...conns.get(txJobs.get(req).conn), obj: out.obj })
        if (LeaseAfterWrite) all { conns' = cs, discs' = discs } else release(txJobs.get(req).conn, cs, discs)
      },
    },
  }

  action TxEmit(req: int): bool = all {
    txJobs.keys().contains(req),
    txJobs.get(req).finished,
    asyncFrame, srv' = srv, runs' = runs, silentRollback' = silentRollback,
    toLua' = toLua.append(MResp({ req: req, ok: txJobs.get(req).ok, tx: txJobs.get(req).tx, conn: txJobs.get(req).conn })),
    txJobs' = remove(txJobs, req),
    if (LeaseAfterWrite) release(txJobs.get(req).conn, conns, discs) else all { conns' = conns, discs' = discs },
  }

  // DisconnectAsync, once it holds the lease.
  action DiscExec(req: int): bool = all {
    discs.keys().contains(req),
    discs.get(req).ready,
    asyncFrame, runs' = runs, txJobs' = txJobs,
    if (not(conns.keys().contains(discs.get(req).conn))) all {
      // An earlier disconnect already closed it.
      toLua' = toLua.append(MResp({ req: req, ok: true, tx: "", conn: discs.get(req).conn })),
      srv' = srv, silentRollback' = silentRollback,
      release(discs.get(req).conn, conns, remove(discs, req)),
    }
    else if (inTx(ServerTruth, conns.get(discs.get(req).conn).obj, srv.get(discs.get(req).conn))
             and not(discs.get(req).rollback)) all {
      // TransactionOpen: commit or roll back first.
      toLua' = toLua.append(MResp({ req: req, ok: false, tx: "", conn: discs.get(req).conn })),
      srv' = srv, silentRollback' = silentRollback,
      release(discs.get(req).conn, conns, remove(discs, req)),
    }
    else all {
      // Closing the session rolls back whatever the server still had open.
      toLua' = toLua.append(MResp({ req: req, ok: true, tx: "", conn: discs.get(req).conn })),
      srv' = remove(srv, discs.get(req).conn),
      silentRollback' = (silentRollback or (srv.get(discs.get(req).conn) != TNone and not(discs.get(req).rollback))),
      release(discs.get(req).conn, remove(conns, discs.get(req).conn), remove(discs, req)),
    },
  }

  action stutter = all {
    keepLua, keepBe, srv' = srv, toBe' = toBe, toLua' = toLua, silentRollback' = silentRollback, budget' = budget,
  }

  action step = any {
    UConnect,
    nondet s = Set(Plain, Fail, BeginText, CommitText, RollbackText).oneOf()
    UExecute(s),
    nondet q = lqueries.keys().union(Set(0)).oneOf()
    UCancel(q),
    nondet op = Set(OpBegin, OpCommit, OpRollback).oneOf()
    UTx(op),
    UDisconnect,
    UQuit,
    LuaRecv,
    BeRecv,
    nondet q = runs.keys().union(Set(0)).oneOf()
    any { RunExec(q), RunEmit(q) },
    nondet r = txJobs.keys().union(Set(0)).oneOf()
    any { TxExec(r), TxEmit(r) },
    nondet r = discs.keys().union(Set(0)).oneOf()
    DiscExec(r),
    stutter,
  }

  // ---------------------------------------------------------------------------------------------
  // Properties. "Quiescent": nothing in flight, so Lua has heard everything the backend did.

  val quiescent = and {
    toBe.length() == 0,
    toLua.length() == 0,
    runs.keys().size() == 0,
    txJobs.keys().size() == 0,
    discs.keys().size() == 0,
  }

  // disconnect never rolls back a transaction the user was not asked about.
  val NoSilentRollback = not(silentRollback)

  // Every open server session is one Lua knows about (an orphan keeps its locks until exit).
  val NoOrphanSession = quiescent implies conns.keys().forall(c => connId(lconn) == c)

  // Lua's connection is open in the backend.
  val NoDeadConn = (quiescent and hasConn(lconn)) implies conns.keys().contains(connId(lconn))

  // Lua never states a transaction state the server does not have ("unknown" is honest).
  val HonestTxView = (quiescent and hasConn(lconn) and srv.keys().contains(connId(lconn))) implies
    (connTx(lconn) == "unknown" or connTx(lconn) == txName(srv.get(connId(lconn))))

  // Every query Lua is waiting for is known to the backend.
  val NoGhostQuery = quiescent implies lqueries.keys().size() == 0

  val Safe = and { NoSilentRollback, NoOrphanSession, NoDeadConn, HonestTxView, NoGhostQuery }

  // Reachability witnesses: each MUST be violated, or the properties above hold vacuously.
  val NeverActiveView = not(quiescent and connTx(lconn) == "active")
  val NeverAbortedView = not(quiescent and connTx(lconn) == "aborted")
  val NeverReconnected = not(quiescent and connId(lconn) == 2)
  // Quitting with a transaction open, after the user was asked and agreed.
  val NeverAskedQuitRollback = quitLog != 2
  val NeverTxRolledBackOnDisconnect = not(discs.keys().exists(r => discs.get(r).ready and discs.get(r).rollback
    and srv.keys().contains(discs.get(r).conn) and srv.get(discs.get(r).conn) != TNone))
}

// -----------------------------------------------------------------------------------------------
// Variants. Each fix is needed: dropping any one of them from Proposed breaks a property.

module Current {
  import client(ServerTruth = false, LeaseAfterWrite = false, LuaById = false, AskOnQuit = true, AskWhileRunning = true, MaxActions = 6, MaxConnects = 2).*
}

module Proposed {
  import client(ServerTruth = true, LeaseAfterWrite = true, LuaById = true, AskOnQuit = true, AskWhileRunning = true, MaxActions = 6, MaxConnects = 2).*
}

module NoServerTruth {
  import client(ServerTruth = false, LeaseAfterWrite = true, LuaById = true, AskOnQuit = true, AskWhileRunning = true, MaxActions = 6, MaxConnects = 2).*
}

module LeaseBeforeWrite {
  import client(ServerTruth = true, LeaseAfterWrite = false, LuaById = true, AskOnQuit = true, AskWhileRunning = true, MaxActions = 6, MaxConnects = 2).*
}

module LuaByName {
  import client(ServerTruth = true, LeaseAfterWrite = true, LuaById = false, AskOnQuit = true, AskWhileRunning = true, MaxActions = 6, MaxConnects = 2).*
}
