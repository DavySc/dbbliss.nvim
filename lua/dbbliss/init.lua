-- dbbliss.nvim — connect, execute, cancel, transactions. Results rendering is deliberately minimal
-- until Phase 1's results buffer (M3).
local backend_mod = require('dbbliss.backend')
local script = require('dbbliss.script')

local M = {}

--- Backend error code: disconnect refused because the server has an open transaction.
local TRANSACTION_OPEN = 1003

---@class dbbliss.ConnectionConfig
---@field engine 'postgres'|'sqlserver'|'db2i'
---@field connection_string string  without the password
---@field password? { env: string }  a reference, never a literal
---@field env? 'dev'|'test'|'prod'
---@field options? table

---@class dbbliss.Config
---@field connections table<string, dbbliss.ConnectionConfig>
---@field backend { cmd: string[]?, shutdown_timeout_ms: integer }
---@field max_rows_shown integer

---@type dbbliss.Config
local defaults = {
  connections = {},
  backend = { cmd = nil, shutdown_timeout_ms = 7000 },
  max_rows_shown = 1000,
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
  results_buf = nil, ---@type integer?
  --- connection id → true while a script (split, then its statements one by one) is running
  scripts = {},
}
M._state = state

local diagnostics = vim.api.nvim_create_namespace('dbbliss')

local function notify(msg, level)
  vim.notify('dbbliss: ' .. msg, level or vim.log.levels.INFO)
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
  state.config = vim.tbl_deep_extend('force', vim.deepcopy(defaults), opts or {})
  for name, c in pairs(state.config.connections) do
    if type(c.password) == 'string' then
      error(('dbbliss: connection %s: password must be a reference like { env = "NAME" }, not a literal'):format(name), 0)
    end
    if not c.engine or not c.connection_string then
      error(('dbbliss: connection %s needs engine and connection_string'):format(name), 0)
    end
  end
end

-- Results buffer (minimal) ---------------------------------------------------------------

local function results_buf()
  if state.results_buf and vim.api.nvim_buf_is_valid(state.results_buf) then
    return state.results_buf
  end
  local buf = vim.api.nvim_create_buf(false, true)
  vim.api.nvim_buf_set_name(buf, 'dbbliss://results')
  vim.bo[buf].buftype = 'nofile'
  vim.bo[buf].bufhidden = 'hide'
  vim.bo[buf].swapfile = false
  state.results_buf = buf
  return buf
end

local function append(lines)
  local buf = results_buf()
  local count = vim.api.nvim_buf_line_count(buf)
  local first = vim.api.nvim_buf_get_lines(buf, 0, 1, false)[1]
  if count == 1 and first == '' then
    vim.api.nvim_buf_set_lines(buf, 0, 1, false, lines)
  else
    vim.api.nvim_buf_set_lines(buf, count, count, false, lines)
  end
  for _, win in ipairs(vim.fn.win_findbuf(buf)) do
    vim.api.nvim_win_set_cursor(win, { vim.api.nvim_buf_line_count(buf), 0 })
  end
end

local function show_results()
  local buf = results_buf()
  if #vim.fn.win_findbuf(buf) == 0 then
    vim.cmd('botright split')
    vim.api.nvim_win_set_buf(0, buf)
    vim.cmd('wincmd p')
  end
end

local function cell(v)
  if v == vim.NIL or v == nil then
    return 'NULL'
  end
  return tostring(v)
end

-- Backend lifecycle -----------------------------------------------------------------------

local function on_backend_exit(code, signal)
  local had = next(state.connections) ~= nil
  for id, q in pairs(state.queries) do
    q.status = 'lost'
    append({ ('-- query %s LOST: backend exited (code %d, signal %d)'):format(id, code, signal) })
    if q.on_done then
      q.on_done({ query_id = id, status = 'lost' })
    end
  end
  state.queries = {}
  state.scripts = {}
  state.connections = {}
  state.connecting = {}
  state.current = nil
  state.backend = nil
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
    local names = vim.tbl_map(function(c)
      return c.name
    end, p.columns)
    append({ ('-- result set %d'):format(p.result_set + 1), table.concat(names, ' | ') })
  end)
  b:on('query/rows', function(p)
    local q = state.queries[p.query_id]
    local lines = {}
    for _, row in ipairs(p.rows) do
      if q then
        q.rows = q.rows + 1
        if q.rows > state.config.max_rows_shown then
          break
        end
      end
      lines[#lines + 1] = table.concat(vim.tbl_map(cell, row), ' | ')
    end
    if #lines > 0 then
      append(lines)
    end
  end)
  b:on('query/resultset_done', function(p)
    append({ ('-- %d row(s)'):format(p.rows) })
  end)
  b:on('query/message', function(p)
    append({ ('-- [%s] %s'):format(p.severity, p.text) })
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
    local line = ('-- %s in %d ms'):format(p.status, p.elapsed_ms)
    if p.cancel and p.cancel ~= vim.NIL then
      line = line .. (' (cancel acknowledged after %d ms)'):format(p.cancel.ack_ms)
    end
    append({ line })
    if p.error and p.error ~= vim.NIL then
      local e = p.error
      -- The buffer line the statement came from, else the engine's line within the statement.
      local line = (e.buffer_line and e.buffer_line ~= vim.NIL) and e.buffer_line or e.line
      local where = (line and line ~= vim.NIL) and (' (line %s)'):format(line) or ''
      append({ '-- error' .. where .. ': ' .. tostring(e.message) })
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
---@param opts { on_done: fun(p: table)?, connection: string?, line_offset: integer? }?
---   connection: run on this connection instead of the current one. line_offset: the 0-based buffer
---   line the sql starts on, so an error's line can be reported in the buffer.
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
  show_results()
  append({ '', ('-- %s on %s'):format(query_id, name) })
  state.backend:request('execute', {
    connection_id = conn.id,
    query_id = query_id,
    sql = sql,
    line_offset = opts.line_offset,
  }, function(err)
    if err then
      state.queries[query_id] = nil
      append({ '-- not started: ' .. err.message })
      notify('execute failed: ' .. err.message, vim.log.levels.ERROR)
      if opts.on_done then
        opts.on_done({ query_id = query_id, status = 'not_started', error = { message = err.message } })
      end
    end
  end)
  return query_id
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
        append({
          ('-- stopped: %s %d of %d (buffer line %d) %s%s'):format(
            noun,
            unit.index,
            unit.count,
            unit.line_offset + 1,
            p.status == 'not_started' and 'was not started' or p.status,
            left > 0 and ('; %d not run'):format(left) or ''
          ),
        })
        finish()
      end,
    })
    if not qid then
      finish()
    end
  end
  step(1)
end

--- Runs buffer text on the current connection: the statement under the cursor, the whole buffer, or
--- a line range. Splitting is the backend's job (script/split); a GO count repeats its batch.
---@param scope 'statement'|'buffer'|'range'
---@param range { [1]: integer, [2]: integer }?  1-based first and last line, for 'range'
function M.run(scope, range)
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
    run_queue(name, conn_id, bufnr, script.expand(statements, first - 1))
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
  state.backend:request('transaction/' .. method, { connection_id = id }, function(err, result)
    if err then
      notify(method .. ' failed: ' .. err.message, vim.log.levels.ERROR)
      return
    end
    local name, c = connection_by_id(id)
    if c then
      c.transaction = result.transaction
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
      local n = connection_by_id(id)
      if n then
        state.connections[n] = nil
        if state.current == n then
          state.current = nil
        end
      end
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
