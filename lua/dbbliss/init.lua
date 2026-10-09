-- dbbliss.nvim — connect, execute, cancel, transactions. Results rendering is deliberately minimal
-- until Phase 1's results buffer (M3).
local backend_mod = require('dbbliss.backend')
local script = require('dbbliss.script')
local results = require('dbbliss.results')
local info = require('dbbliss.info')
local names = require('dbbliss.names')
local tree = require('dbbliss.tree')

local M = {}

--- Backend error code: disconnect refused because the server has an open transaction.
local TRANSACTION_OPEN = 1003

---@class dbbliss.ConnectionConfig
---@field engine 'postgres'|'sqlserver'|'db2i'
---@field connection_string string  without the password
---@field password? { env: string }|{ credman: string }|{ pass: string }|{ libsecret: table<string,string> }  where to find the password, never the password; leave out for integrated auth
---@field env? 'dev'|'test'|'prod'
---@field options? table

---@class dbbliss.Config
---@field connections table<string, dbbliss.ConnectionConfig>
---@field backend { cmd: string[]?, shutdown_timeout_ms: integer }
---@field results { window_rows: integer, max_col_width: integer }
---@field info { max_col_width: integer }
---@field tree { show_system: boolean }
---@field mappings { info: string|false }  buffer-local keys in SQL buffers

---@type dbbliss.Config
local defaults = {
  connections = {},
  backend = { cmd = nil, shutdown_timeout_ms = 7000 },
  results = { window_rows = 1000, max_col_width = 40 },
  info = { max_col_width = 200 },
  tree = { show_system = false },
  mappings = { info = '<M-F1>' },
}

local state = {
  ---@type dbbliss.Config
  config = vim.deepcopy(defaults),
  ---@type dbbliss.Backend?
  backend = nil,
  backend_info = nil,
  --- name → { id, server_session_id, engine, transaction }
  connections = {},
  --- names with a connect request in flight
  connecting = {},
  current = nil, ---@type string?
  --- query id → { connection (name), connection_id, status, rows, result_sets, on_done }
  queries = {},
  seq = 0,
  --- connection id → true while a script (split, then its statements one by one) is running
  scripts = {},
  guard_buf = nil, ---@type integer?
}
M._state = state

local diagnostics = vim.api.nvim_create_namespace('dbbliss')

local function notify(msg, level)
  vim.notify('dbbliss: ' .. msg, level or vim.log.levels.INFO)
end

--- Redraws the statusline: connection, transaction and running state changed.
local function refresh()
  vim.schedule(function()
    pcall(vim.cmd, 'redrawstatus!')
  end)
end

--- Backend replies are matched to connections by backend id, never by name: a name can be
--- disconnected and connected again while an old reply is still on its way.
---@return string? name, table? conn
local function connection_by_id(id)
  for name, c in pairs(state.connections) do
    if c.id == id then
      return name, c
    end
  end
end

local function plugin_root()
  local src = debug.getinfo(1, 'S').source:sub(2)
  return vim.fn.fnamemodify(src, ':p:h:h:h')
end

local function default_backend_cmd()
  local uname = (vim.uv or vim.loop).os_uname()
  local arch = (uname.machine == 'aarch64' or uname.machine == 'arm64') and 'arm64' or 'x64'
  local is_win = vim.fn.has('win32') == 1
  local rid = (is_win and 'win-' or 'linux-') .. arch
  local exe = is_win and 'dbbliss-backend.exe' or 'dbbliss-backend'
  return { vim.fs.joinpath(plugin_root(), 'bin', rid, exe) }
end

---@param opts dbbliss.Config?
function M.setup(opts)
  if vim.fn.has('nvim-0.10') == 0 then
    local v = vim.version()
    error(('dbbliss: Neovim 0.10 or newer is required (this is %d.%d.%d)'):format(v.major, v.minor, v.patch), 0)
  end
  state.config = vim.tbl_deep_extend('force', vim.deepcopy(defaults), opts or {})
  for name, c in pairs(state.config.connections) do
    if c.password ~= nil then
      if type(c.password) ~= 'table' then
        error(
          ('dbbliss: connection %s: password must be a reference like { env = "NAME" }, { credman = "target" }, { pass = "path" } or { libsecret = { service = "x" } }, not a literal'):format(name),
          0
        )
      end
      local keys = vim.tbl_keys(c.password)
      if #keys ~= 1 or not vim.tbl_contains({ 'env', 'credman', 'pass', 'libsecret' }, keys[1]) then
        error(
          ('dbbliss: connection %s: password must name exactly one of env, credman, pass, libsecret (got %s)'):format(
            name,
            #keys == 0 and 'none' or table.concat(keys, ', ')
          ),
          0
        )
      end
    end
    if not c.engine or not c.connection_string then
      error(('dbbliss: connection %s needs engine and connection_string'):format(name), 0)
    end
  end
  info.setup({ max_col_width = state.config.info.max_col_width })
  tree.setup({
    request = function(conn_id, method, params, cb)
      M._catalog_request(conn_id, method, params, cb)
    end,
    info = function(_, conn_name, node)
      M._describe(conn_name, M._object_params(node))
    end,
    script = function(_, conn_name, node)
      M._script(conn_name, M._object_params(node))
    end,
    show_system = state.config.tree.show_system,
  })
  M._apply_mappings()
  results.setup({
    window_rows = state.config.results.window_rows,
    max_col_width = state.config.results.max_col_width,
    on_fetch = function(query_id, rows)
      M.fetch(query_id, rows)
    end,
  })
end

-- Backend lifecycle -----------------------------------------------------------------------

local function on_backend_exit(code, signal)
  local had = next(state.connections) ~= nil
  for id, q in pairs(state.queries) do
    q.status = 'lost'
    results.note({ ('-- query %s LOST: backend exited (code %d, signal %d)'):format(id, code, signal) }, 'DbblissError')
    if q.on_done then
      q.on_done({ query_id = id, status = 'lost' })
    end
  end
  state.queries = {}
  state.scripts = {}
  tree._state.trees = {}
  tree._state.current = nil
  state.connections = {}
  state.connecting = {}
  state.current = nil
  state.backend = nil
  refresh()
  if had or code ~= 0 then
    notify(('backend exited (code %d, signal %d); all connections are gone'):format(code, signal), vim.log.levels.ERROR)
  end
end

local function register_handlers(b)
  b:on('query/resultset', function(p)
    local q = state.queries[p.query_id]
    if q then
      q.result_sets = q.result_sets + 1
    end
    results.resultset(p)
  end)
  b:on('query/rows', function(p)
    local q = state.queries[p.query_id]
    if q then
      q.rows = q.rows + #p.rows
      if q.discard then
        -- Cancelled while paused at the window's end: the rows that were already on their way are
        -- not appended to the table (there can be hundreds of thousands). They are counted and
        -- reported at the end, never dropped unnoticed.
        q.discarded = (q.discarded or 0) + #p.rows
        return
      end
    end
    results.rows(p)
  end)
  b:on('query/resultset_done', function(p)
    results.resultset_done(p)
  end)
  b:on('query/paused', function(p)
    local q = state.queries[p.query_id]
    if q then
      q.paused = true
    end
    results.paused(p)
  end)
  b:on('query/message', function(p)
    results.message(p)
    if p.severity == 'warning' or p.severity == 'error' then
      notify(p.text, vim.log.levels.WARN)
    end
  end)
  b:on('connection/message', function(p)
    notify(('[%s] %s'):format(p.severity, p.text), p.severity == 'warning' and vim.log.levels.WARN or nil)
  end)
  b:on('query/done', function(p)
    local q = state.queries[p.query_id]
    state.queries[p.query_id] = nil
    results.query_ended(p.query_id)
    refresh()
    local line = ('-- %s in %d ms'):format(p.status, p.elapsed_ms)
    if p.cancel and p.cancel ~= vim.NIL then
      line = line .. (' (cancel acknowledged after %d ms)'):format(p.cancel.ack_ms)
    end
    results.note({ line }, 'DbblissInfo')
    if q and q.discarded and q.discarded > 0 then
      results.note({ ('-- %d more rows had already arrived when the cancel was sent; they are not shown'):format(q.discarded) }, 'DbblissWarn')
    end
    if p.truncated_rows and p.truncated_rows ~= vim.NIL then
      results.note({ ('-- %d rows were not shown (cancelled while more were arriving)'):format(p.truncated_rows) }, 'DbblissWarn')
    end
    if p.export and p.export ~= vim.NIL then
      local e = p.export
      results.note({
        ('-- exported %d rows to %s%s'):format(e.rows, e.path, e.complete and '' or ' (INCOMPLETE: the file ends where the query stopped)'),
      }, e.complete and 'DbblissInfo' or 'DbblissError')
      if e.ignored_result_sets and e.ignored_result_sets > 0 then
        results.note({ ('-- %d further result set(s) were not exported'):format(e.ignored_result_sets) }, 'DbblissWarn')
      end
    end
    if p.error and p.error ~= vim.NIL then
      local e = p.error
      -- The buffer line the statement came from, else the engine's line within the statement.
      local line = (e.buffer_line and e.buffer_line ~= vim.NIL) and e.buffer_line or e.line
      local where = (line and line ~= vim.NIL) and (' (line %s)'):format(line) or ''
      results.note({ '-- error' .. where .. ': ' .. tostring(e.message) }, 'DbblissError')
      if p.status == 'error' then
        notify(tostring(e.message), vim.log.levels.ERROR)
      end
    end
    if q then
      local name, conn = connection_by_id(q.connection_id)
      if conn and p.transaction then
        if conn.transaction ~= 'none' and p.transaction == 'none' then
          if p.status == 'completed' then
            notify('the transaction on ' .. name .. ' ended')
          else
            notify('the server rolled back the transaction on ' .. name, vim.log.levels.ERROR)
          end
        elseif p.transaction == 'aborted' then
          notify('the transaction on ' .. name .. ' is aborted: only rollback is possible', vim.log.levels.ERROR)
        end
        conn.transaction = p.transaction
      end
      if q.on_done then
        q.on_done(p)
      end
    end
  end)
end

---@return dbbliss.Backend
function M.ensure_backend()
  if state.backend and not state.backend.exited then
    return state.backend
  end
  local b = backend_mod.new(state.config.backend.cmd or default_backend_cmd())
  register_handlers(b)
  b:on_exit(on_backend_exit)
  b:start()
  state.backend = b
  b:request('initialize', nil, function(err, result)
    if err then
      notify('initialize failed: ' .. err.message, vim.log.levels.ERROR)
    else
      state.backend_info = result
    end
  end)
  return b
end

-- API ---------------------------------------------------------------------------------------

---@param name string
---@param cb fun(err: table?, conn: table?)?
function M.connect(name, cb)
  local cfg = state.config.connections[name]
  if not cfg then
    notify('unknown connection ' .. tostring(name), vim.log.levels.ERROR)
    return
  end
  if state.connections[name] or state.connecting[name] then
    -- A second session under the same name would orphan the first, with its transaction and locks.
    local err = { message = name .. ' is already connected or connecting; disconnect first' }
    notify(err.message, vim.log.levels.ERROR)
    if cb then
      cb(err, nil)
    end
    return
  end
  local b = M.ensure_backend()
  state.connecting[name] = true
  b:request('connect', {
    engine = cfg.engine,
    connection_string = cfg.connection_string,
    password = cfg.password,
    options = cfg.options,
  }, function(err, result)
    state.connecting[name] = nil
    if err then
      notify(('connect %s failed: %s'):format(name, err.message), vim.log.levels.ERROR)
    else
      state.connections[name] = {
        id = result.connection_id,
        engine = result.engine,
        server_session_id = result.server_session_id,
        transaction = 'none',
      }
      state.current = name
      refresh()
      notify(('connected to %s (%s, session %s)'):format(name, result.engine, result.server_session_id))
    end
    if cb then
      cb(err, state.connections[name])
    end
  end)
end

local function current_connection()
  local name = state.current
  local conn = name and state.connections[name]
  if not conn then
    notify('not connected; use :Dbbliss connect <name>', vim.log.levels.ERROR)
    return nil
  end
  return name, conn
end

---@param sql string
---@param opts { on_done: fun(p: table)?, connection: string?, line_offset: integer?, window: integer|false?, export: { path: string, overwrite: boolean? }? }?
---   connection: run on this connection instead of the current one. line_offset: the 0-based buffer
---   line the sql starts on, so an error's line can be reported in the buffer.
---   window: rows to fetch at a time (default config.results.window_rows; false for no paging).
---   export: write the first result set to a CSV file in the backend instead of showing it.
---   on_done is also called, with status 'not_started', when the backend refuses the request.
---@return string? query_id
function M.execute(sql, opts)
  opts = opts or {}
  local name, conn
  if opts.connection then
    name, conn = opts.connection, state.connections[opts.connection]
  else
    name, conn = current_connection()
  end
  if not conn then
    if opts.connection then
      notify(opts.connection .. ' is no longer connected', vim.log.levels.ERROR)
    end
    return nil
  end
  state.seq = state.seq + 1
  local query_id = ('q%d-%d'):format((vim.uv or vim.loop).os_getpid(), state.seq)
  state.queries[query_id] = {
    connection = name,
    connection_id = conn.id,
    status = 'running',
    rows = 0,
    result_sets = 0,
    on_done = opts.on_done,
  }
  refresh()
  results.show()
  results.note({ '', ('-- %s on %s'):format(query_id, name) }, 'DbblissInfo')
  local window = opts.window
  if window == nil then
    window = state.config.results.window_rows
  end
  state.backend:request('execute', {
    connection_id = conn.id,
    query_id = query_id,
    sql = sql,
    line_offset = opts.line_offset,
    window = (window and not opts.export) and window or nil,
    export = opts.export,
  }, function(err)
    if err then
      state.queries[query_id] = nil
      results.note({ '-- not started: ' .. err.message }, 'DbblissError')
      if not (opts.export and err.data and err.data ~= vim.NIL and err.data.exists) then
        notify('execute failed: ' .. err.message, vim.log.levels.ERROR)
      end
      if opts.on_done then
        opts.on_done({ query_id = query_id, status = 'not_started', error = { message = err.message }, request_error = err })
      end
    end
  end)
  return query_id
end

--- Lets a paused query send `rows` more rows.
---@param query_id string
---@param rows integer
function M.fetch(query_id, rows)
  if not state.backend then
    return
  end
  local q = state.queries[query_id]
  if q then
    q.paused = false
  end
  state.backend:request('fetch', { query_id = query_id, rows = rows }, function(err, result)
    if err then
      notify('fetch failed: ' .. err.message, vim.log.levels.ERROR)
    elseif result.state == 'not_running' then
      notify('the query had already ended')
    end
  end)
end

--- Fetches the next window of rows of the paused result (also on moving to the end of the buffer).
function M.fetch_more()
  results.fetch_more()
end

-- Running buffer text ---------------------------------------------------------------------

--- A byte column as the UTF-16 column the backend counts in.
local function utf16_col(bufnr, row, byte_col)
  local line = vim.api.nvim_buf_get_lines(bufnr, row, row + 1, false)[1] or ''
  local ok, idx = pcall(vim.str_utfindex, line, 'utf-16', math.min(byte_col, #line))
  return (ok and type(idx) == 'number') and idx or byte_col
end

--- Runs the queue one statement at a time on one connection and stops at the first one that does
--- not complete, saying which it was. Whatever is left is not run: a script must not carry on after
--- the statement it depends on failed.
local function run_queue(name, conn_id, bufnr, queue)
  local function finish()
    state.scripts[conn_id] = nil
  end
  local noun = (state.connections[name] and state.connections[name].engine == 'sqlserver') and 'batch' or 'statement'
  local function step(i)
    local unit = queue[i]
    if not unit then
      return finish()
    end
    local qid = M.execute(unit.text, {
      connection = name,
      line_offset = unit.line_offset,
      on_done = function(p)
        if p.status == 'completed' then
          return step(i + 1)
        end
        local e = p.error
        if e and e ~= vim.NIL and e.buffer_line and e.buffer_line ~= vim.NIL and vim.api.nvim_buf_is_valid(bufnr) then
          vim.diagnostic.set(diagnostics, bufnr, {
            { lnum = e.buffer_line - 1, col = 0, severity = vim.diagnostic.severity.ERROR, source = 'dbbliss', message = tostring(e.message) },
          })
        end
        local left = #queue - i
        results.note({
          ('-- stopped: %s %d of %d (buffer line %d) %s%s'):format(
            noun,
            unit.index,
            unit.count,
            unit.line_offset + 1,
            p.status == 'not_started' and 'was not started' or p.status,
            left > 0 and ('; %d not run'):format(left) or ''
          ),
        }, 'DbblissError')
        finish()
      end,
    })
    if not qid then
      finish()
    end
  end
  step(1)
end

--- On a prod connection, statements that can change data are confirmed first, all at once. The
--- backend classifies them (script/split, `kind`); a statement it did not classify counts as a write.
---@param name string
---@param statements dbbliss.Statement[]
---@return boolean
local function confirm_writes(name, statements)
  local cfg = state.config.connections[name]
  if not cfg or cfg.env ~= 'prod' then
    return true
  end
  local writes = vim.tbl_filter(function(st)
    return st.kind ~= 'read'
  end, statements)
  if #writes == 0 then
    return true
  end
  local lines = {}
  for i, st in ipairs(writes) do
    if i > 5 then
      lines[#lines + 1] = ('  ... and %d more'):format(#writes - 5)
      break
    end
    local first = (st.text:gsub('%s+', ' '))
    lines[#lines + 1] = ('  line %d: %s'):format(st.start.line + 1, #first > 70 and first:sub(1, 67) .. '...' or first)
  end
  local msg = ('PROD %s: %d of %d statement(s) can change data:\n%s\nRun?'):format(name, #writes, #statements, table.concat(lines, '\n'))
  return vim.fn.confirm(msg, '&Run\n&Cancel', 2) == 1
end

--- Splits the text of `scope` (the backend does it) and hands the statements over. The connection is
--- marked busy with a script from here until `release` is called, which `cb` must arrange.
---@param scope 'statement'|'buffer'|'range'
---@param range { [1]: integer, [2]: integer }?  1-based first and last line, for 'range'
---@param cb fun(name: string, conn_id: string, bufnr: integer, statements: dbbliss.Statement[], base_line: integer)
local function with_statements(scope, range, cb)
  local name, conn = current_connection()
  if not conn then
    return
  end
  if state.scripts[conn.id] then
    notify(('a script is already running on %s; cancel it first'):format(name), vim.log.levels.WARN)
    return
  end
  local bufnr = vim.api.nvim_get_current_buf()
  local cursor = vim.api.nvim_win_get_cursor(0)
  local first, last = 1, vim.api.nvim_buf_line_count(bufnr)
  if scope == 'range' and range then
    first, last = range[1], range[2]
  end
  local text = table.concat(vim.api.nvim_buf_get_lines(bufnr, first - 1, last, false), '\n')
  local cursor_line, cursor_col = cursor[1] - first, utf16_col(bufnr, cursor[1] - 1, cursor[2])
  local conn_id = conn.id
  vim.diagnostic.reset(diagnostics, bufnr)
  state.scripts[conn_id] = true
  state.backend:request('script/split', { engine = conn.engine, text = text }, function(err, result)
    if err then
      state.scripts[conn_id] = nil
      notify('could not split the script: ' .. err.message, vim.log.levels.ERROR)
      return
    end
    local statements = result.statements
    if scope == 'statement' then
      statements = { script.pick(statements, cursor_line, cursor_col) }
    end
    if #statements == 0 then
      state.scripts[conn_id] = nil
      notify('nothing to run')
      return
    end
    vim.b[bufnr].dbbliss_connection = name
    if not confirm_writes(name, statements) then
      state.scripts[conn_id] = nil
      notify('cancelled: nothing was run on ' .. name)
      return
    end
    cb(name, conn_id, bufnr, statements, first - 1)
  end)
end

--- Runs buffer text on the current connection: the statement under the cursor, the whole buffer, or
--- a line range. Splitting is the backend's job (script/split); a GO count repeats its batch.
---@param scope 'statement'|'buffer'|'range'
---@param range { [1]: integer, [2]: integer }?  1-based first and last line, for 'range'
function M.run(scope, range)
  with_statements(scope, range, function(name, conn_id, bufnr, statements, base_line)
    results.clear()
    run_queue(name, conn_id, bufnr, script.expand(statements, base_line))
  end)
end

--- Writes one statement's first result set to a CSV file, in the backend: the rows never pass
--- through Neovim. Asks before replacing a file.
---@param path string?  asked for when nil
---@param scope 'statement'|'buffer'|'range'?  default 'statement'
---@param range { [1]: integer, [2]: integer }?
function M.export(path, scope, range)
  with_statements(scope or 'statement', range, function(name, conn_id, bufnr, statements, base_line)
    local function release()
      state.scripts[conn_id] = nil
    end
    if #statements ~= 1 then
      release()
      notify(('export takes one statement, got %d; put the cursor in one or select just it'):format(#statements), vim.log.levels.ERROR)
      return
    end
    if not path or path == '' then
      path = vim.fn.input('Export to (absolute path): ', '', 'file')
    end
    if path == '' then
      release()
      return
    end
    path = vim.fn.fnamemodify(vim.fn.expand(path), ':p')
    local unit = script.expand(statements, base_line)[1]
    local function go(overwrite)
      M.execute(unit.text, {
        connection = name,
        line_offset = unit.line_offset,
        export = { path = path, overwrite = overwrite or nil },
        on_done = function(p)
          local exists = p.request_error and p.request_error.data and p.request_error.data ~= vim.NIL and p.request_error.data.exists
          if exists and not overwrite then
            if vim.fn.confirm(('%s exists. Replace it?'):format(path), '&Replace\n&Cancel', 2) == 1 then
              return go(true)
            end
          end
          release()
        end,
      })
    end
    go(false)
  end)
end

--- Cancels the running query on the current connection (or the given query id).
---@param query_id string?
function M.cancel(query_id)
  if not query_id then
    for id, q in pairs(state.queries) do
      if q.connection == state.current then
        query_id = id
      end
    end
  end
  if not query_id or not state.backend then
    notify('no running query', vim.log.levels.WARN)
    return
  end
  local q = state.queries[query_id]
  if q and q.paused then
    q.discard = true
  end
  state.backend:request('cancel', { query_id = query_id }, function(err, result)
    if err then
      notify('cancel failed: ' .. err.message, vim.log.levels.ERROR)
    elseif result.state == 'not_running' then
      notify('query ' .. query_id .. ' was no longer running')
    else
      notify('cancel sent for ' .. query_id)
    end
  end)
end

local function transaction(method)
  local _, conn = current_connection()
  if not conn then
    return
  end
  local id = conn.id
  conn.tx_calls = (conn.tx_calls or 0) + 1
  state.backend:request('transaction/' .. method, { connection_id = id }, function(err, result)
    conn.tx_calls = conn.tx_calls - 1
    if err then
      notify(method .. ' failed: ' .. err.message, vim.log.levels.ERROR)
      return
    end
    local name, c = connection_by_id(id)
    if c then
      c.transaction = result.transaction
      refresh()
      notify(('%s on %s: transaction %s'):format(method, name, result.transaction))
    end
  end)
end

function M.begin()
  transaction('begin')
end
function M.commit()
  transaction('commit')
end
function M.rollback()
  transaction('rollback')
end

function M.disconnect()
  local name, conn = current_connection()
  if not conn then
    return
  end
  local id = conn.id
  local function confirm_rollback()
    local choice = vim.fn.confirm(('%s has an open transaction. Roll it back and disconnect?'):format(name), '&Rollback\n&Cancel', 2)
    return choice == 1
  end
  local close
  function close(rollback)
    state.backend:request('disconnect', { connection_id = id, rollback = rollback }, function(err)
      if err then
        -- The backend asks the server; it may know of a transaction this client has not heard of.
        if err.code == TRANSACTION_OPEN and not rollback and confirm_rollback() then
          close(true)
        elseif err.code ~= TRANSACTION_OPEN or rollback then
          notify('disconnect failed: ' .. err.message, vim.log.levels.ERROR)
        end
        return
      end
      tree.forget(id)
      local n = connection_by_id(id)
      if n then
        state.connections[n] = nil
        if state.current == n then
          state.current = nil
        end
      end
      refresh()
      notify('disconnected from ' .. name)
    end)
  end
  if conn.transaction ~= 'none' then
    if confirm_rollback() then
      close(true)
    end
  else
    close(false)
  end
end

function M.status()
  local lines = {}
  for name, c in pairs(state.connections) do
    local cfg = state.config.connections[name] or {}
    lines[#lines + 1] = ('%s%s [%s/%s] session %s, transaction %s'):format(
      name == state.current and '* ' or '  ',
      name,
      c.engine,
      cfg.env or '?',
      c.server_session_id,
      c.transaction
    )
  end
  for id, q in pairs(state.queries) do
    lines[#lines + 1] = ('  running %s on %s (%d rows so far)'):format(id, q.connection, q.rows)
  end
  notify(#lines > 0 and table.concat(lines, '\n') or 'no connections')
end

--- VimLeavePre: let the backend cancel queries and close connections before Neovim exits.
function M.on_exit()
  local b = state.backend
  if b and not b.exited then
    if not b:shutdown_sync(state.config.backend.shutdown_timeout_ms) then
      io.stderr:write('dbbliss: backend did not shut down in time\n')
    end
  end
end

-- Catalog: object info, the schema tree, scripting ----------------------------------------------

--- A request on the catalog session of a connection. Failures are shown, never swallowed: a name that
--- matches nothing is a warning the user can fix, anything else an error.
function M._catalog_request(conn_id, method, params, cb)
  if not state.backend then
    return
  end
  params = vim.tbl_extend('force', params, { connection_id = conn_id })
  state.backend:request(method, params, cb)
end

local CATALOG_USER_ERROR = 1007

local function catalog_error(err)
  notify(err.message, err.code == CATALOG_USER_ERROR and vim.log.levels.WARN or vim.log.levels.ERROR)
end

--- The request parameters that name a tree node exactly.
function M._object_params(node)
  return {
    database = node.path.database,
    schema = node.path.schema,
    object = node.name,
    kind = node.kind,
    identity = node.identity,
  }
end

---@param conn_name string
---@param params table  { name = typed text } or a tree node's M._object_params
function M._describe(conn_name, params)
  local conn = state.connections[conn_name]
  if not conn then
    return notify(conn_name .. ' is not connected', vim.log.levels.ERROR)
  end
  M._catalog_request(conn.id, 'catalog/describe', params, function(err, result)
    if err then
      return catalog_error(err)
    end
    info.open(result, {
      connection = conn_name,
      refresh = function()
        M._describe(conn_name, params)
      end,
      script = function()
        M._script(conn_name, params)
      end,
    })
  end)
end

--- A CREATE script in a new buffer, ready to read, edit or run.
function M._script(conn_name, params)
  local conn = state.connections[conn_name]
  if not conn then
    return notify(conn_name .. ' is not connected', vim.log.levels.ERROR)
  end
  M._catalog_request(conn.id, 'catalog/script', params, function(err, result)
    if err then
      return catalog_error(err)
    end
    vim.cmd('new')
    local lines = vim.split(result.text, '\n', { plain = true })
    if lines[#lines] == '' then
      lines[#lines] = nil
    end
    vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)
    vim.bo.filetype = 'sql'
    vim.b.dbbliss_connection = conn_name
    vim.bo.modified = false
  end)
end

--- The name to look up: the argument, or the (schema-qualified) name under the cursor.
local function name_or_cursor(arg)
  if arg and arg ~= '' then
    return arg
  end
  local line = vim.api.nvim_get_current_line()
  local col = vim.api.nvim_win_get_cursor(0)[2]
  local name = names.at_cursor(line, col)
  if not name then
    notify('no object name under the cursor', vim.log.levels.INFO)
  end
  return name
end

--- Object info: columns, types, nullability, defaults, indexes, constraints, foreign keys in and out,
--- triggers, row estimate (what the engine has; the sections come from the backend).
---@param name string?  the object as written in SQL; default: the name under the cursor
function M.info(name)
  local cname = current_connection()
  if not cname then
    return
  end
  name = name_or_cursor(name)
  if name then
    M._describe(cname, { name = name })
  end
end

---@param name string?
function M.script_object(name)
  local cname = current_connection()
  if not cname then
    return
  end
  name = name_or_cursor(name)
  if name then
    M._script(cname, { name = name })
  end
end

--- The schema browser for the current connection.
function M.tree()
  local cname, conn = current_connection()
  if conn then
    tree.open(conn.id, cname)
  end
end

--- Buffer-local keys in SQL buffers (config.mappings).
function M._apply_mappings()
  local lhs = state.config.mappings and state.config.mappings.info
  local group = vim.api.nvim_create_augroup('dbbliss_mappings', { clear = true })
  if not lhs then
    return
  end
  local function map(buf)
    vim.keymap.set('n', lhs, function()
      M.info()
    end, { buffer = buf, silent = true, desc = 'dbbliss: object info under the cursor' })
  end
  vim.api.nvim_create_autocmd('FileType', {
    group = group,
    pattern = { 'sql', 'plsql', 'pgsql', 'mysql' },
    callback = function(args)
      map(args.buf)
    end,
  })
  for _, buf in ipairs(vim.api.nvim_list_bufs()) do
    if vim.api.nvim_buf_is_loaded(buf) and vim.bo[buf].filetype == 'sql' then
      map(buf)
    end
  end
end

-- Guards: quitting, closing a buffer ----------------------------------------------------------

---@return string[]  what quitting would roll back or cancel, per connection
local function open_transactions()
  local list = {}
  for name, c in pairs(state.connections) do
    if c.transaction and c.transaction ~= 'none' then
      list[#list + 1] = ('%s (transaction %s)'):format(name, c.transaction)
    else
      -- Something in flight whose report has not arrived: the server may already hold a transaction
      -- this client has not heard of (spec/quint/client.qnt, QuitViewOnly).
      local busy = (c.tx_calls or 0) > 0
      for _, q in pairs(state.queries) do
        busy = busy or q.connection == name
      end
      if busy then
        list[#list + 1] = ('%s (a query or transaction call is running)'):format(name)
      end
    end
  end
  table.sort(list)
  return list
end

--- ExitPre: Neovim is about to quit, and quitting closes every connection, which rolls back what
--- is open. Ask first. An autocmd cannot cancel a quit, but a modified buffer does (E37), so
--- "Cancel" leaves one modified for the moment it takes the quit to fail.
function M.on_exit_pre()
  local open = open_transactions()
  if #open == 0 then
    return
  end
  local msg = ('Quitting cancels running queries and rolls back open transactions:\n  %s'):format(table.concat(open, '\n  '))
  if vim.fn.confirm(msg, '&Quit and roll back\n&Cancel', 2) == 1 then
    return
  end
  if not (state.guard_buf and vim.api.nvim_buf_is_valid(state.guard_buf)) then
    state.guard_buf = vim.api.nvim_create_buf(false, false)
    vim.bo[state.guard_buf].bufhidden = 'hide'
  end
  local guard = state.guard_buf
  vim.bo[guard].modified = true
  vim.schedule(function()
    if vim.api.nvim_buf_is_valid(guard) then
      vim.bo[guard].modified = false
    end
    notify('quit cancelled: ' .. table.concat(open, ', ') .. '; finish or cancel first', vim.log.levels.WARN)
  end)
end

--- BufDelete/BufWipeout: closing a buffer that ran statements on a connection with an open
--- transaction. The transaction is not the buffer's and stays open; the prompt is so that the
--- statements that opened it are not closed out of sight by accident. Raising an error cancels the close.
---@param bufnr integer
function M.on_buf_close(bufnr)
  local name = vim.b[bufnr].dbbliss_connection
  local conn = name and state.connections[name]
  if not conn or conn.transaction == 'none' then
    return
  end
  local msg = ('This buffer ran statements on %s, which has an open transaction (%s).\nClosing the buffer does not end it.'):format(
    name,
    conn.transaction
  )
  if vim.fn.confirm(msg, '&Close buffer\n&Cancel', 2) ~= 1 then
    error('dbbliss: buffer not closed: transaction open on ' .. name, 0)
  end
end

-- Statusline -----------------------------------------------------------------------------------

--- Connection, environment, transaction and running state for the current connection.
---@param plain boolean?  true: no statusline highlight escapes
---@return string
function M.statusline(plain)
  local name = state.current
  local conn = name and state.connections[name]
  if not conn then
    return ''
  end
  local cfg = state.config.connections[name] or {}
  local function hl(group, text)
    return plain and text or ('%%#%s#%s%%*'):format(group, text)
  end
  local parts = { name }
  if cfg.env then
    parts[#parts + 1] = cfg.env == 'prod' and hl('DbblissProd', ' PROD ') or ('[' .. cfg.env .. ']')
  end
  if conn.transaction == 'aborted' then
    parts[#parts + 1] = hl('DbblissTx', 'TX aborted')
  elseif conn.transaction ~= 'none' then
    parts[#parts + 1] = hl('DbblissTx', 'TX')
  end
  for _, q in pairs(state.queries) do
    if q.connection == name then
      parts[#parts + 1] = q.paused and '⏸ paused' or '⏵ running'
      break
    end
  end
  return table.concat(parts, ' ')
end

-- :Dbbliss command ----------------------------------------------------------------------------

local subcommands = {
  connect = function(args)
    M.connect(args[1])
  end,
  -- The statement under the cursor, or every statement in the lines of a range.
  exec = function(_, range)
    M.run(range and 'range' or 'statement', range)
  end,
  exec_all = function()
    M.run('buffer')
  end,
  -- Writes the statement under the cursor (or the range) to a CSV file; the path may be left out.
  export = function(args, range)
    M.export(args[1], range and 'range' or 'statement', range)
  end,
  -- Object info for a name (default: the one under the cursor).
  info = function(args)
    M.info(table.concat(args, ' '))
  end,
  -- The schema browser.
  tree = function()
    M.tree()
  end,
  -- A CREATE script for a name (default: the one under the cursor).
  script = function(args)
    M.script_object(table.concat(args, ' '))
  end,
  -- Fetches the next window of rows of a paused result.
  fetch = function()
    M.fetch_more()
  end,
  cancel = function()
    M.cancel()
  end,
  begin = M.begin,
  commit = M.commit,
  rollback = M.rollback,
  disconnect = M.disconnect,
  status = M.status,
}

function M.command(opts)
  local args = vim.split(vim.trim(opts.args), '%s+', { trimempty = true })
  local sub = table.remove(args, 1)
  local fn = sub and subcommands[sub]
  if not fn then
    notify('usage: :Dbbliss {' .. table.concat(vim.tbl_keys(subcommands), '|') .. '}', vim.log.levels.ERROR)
    return
  end
  local range = opts.range > 0 and { opts.line1, opts.line2 } or nil
  fn(args, range)
end

function M.complete(arglead, cmdline)
  local words = vim.split(cmdline, '%s+', { trimempty = true })
  local candidates
  if #words <= 1 or (#words == 2 and not cmdline:match('%s$')) then
    candidates = vim.tbl_keys(subcommands)
  elseif words[2] == 'connect' then
    candidates = vim.tbl_keys(state.config.connections)
  else
    candidates = {}
  end
  table.sort(candidates)
  return vim.tbl_filter(function(c)
    return c:find(arglead, 1, true) == 1
  end, candidates)
end

return M
