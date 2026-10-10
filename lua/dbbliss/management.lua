-- Backup and drop. A backup is long, so it has a floating window that shows what the backend reports
-- (backup/progress, backup/done), whose end is always said: in the window and as a notification. A
-- drop asks for the object's name typed out, offers a backup first (on prod: makes one, and does not
-- drop when it did not complete). Nothing here knows an engine beyond where its default backup file goes.
local M = {}

local ns = vim.api.nvim_create_namespace('dbbliss_management')

local USER_ERROR = 1009

local deps = {
  --- request(conn_id, method, params, callback(err, result))
  request = nil, ---@type fun(conn_id: string, method: string, params: table, cb: fun(err: table?, result: table?))?
  notify = function(msg, level)
    vim.notify('dbbliss: ' .. msg, level)
  end,
  --- Asks for a line of text; nil or '' when the user gives up.
  input = function(prompt, default)
    local ok, answer = pcall(vim.fn.input, { prompt = prompt, default = default or '', cancelreturn = vim.NIL })
    return ok and answer or nil
  end,
  --- Asks a yes/no question; true only for an explicit yes.
  confirm = function(text, yes_label, default_yes)
    return vim.fn.confirm(text, '&' .. yes_label .. '\n&No', default_yes and 1 or 2) == 1
  end,
}

local S = {
  ops = {}, ---@type table<string, table>   backup id -> operation
  by_buf = {}, ---@type table<integer, table>  window buffer -> operation
}
M._state = S

---@param opts table  see `deps`
function M.setup(opts)
  for k, v in pairs(opts) do
    deps[k] = v
  end
end

local function is_text(v)
  return type(v) == 'string' and v ~= ''
end

local function present(v)
  return v ~= nil and v ~= vim.NIL
end

local function human_size(bytes)
  if bytes < 1024 * 1024 then
    return ('%d bytes'):format(bytes)
  end
  return ('%.1f MB (%d bytes)'):format(bytes / 1024 / 1024, bytes)
end

local function bar(percent)
  local filled = math.floor(percent / 10 + 0.5)
  return ('[%s%s] %d%%'):format(('#'):rep(filled), ('·'):rep(10 - filled), percent)
end

local MAX_LINES = 12

--- The window's lines for an operation (pure, tested).
---@return string[] lines, { [1]: integer, [2]: string }[] marks  line (0-based), highlight group
function M.render(op)
  local lines = { ('Backup of %s → %s'):format(op.database, op.path or '?'), '' }
  local marks = { { 0, 'DbblissHeader' } }
  local function add(text, group)
    lines[#lines + 1] = text
    if group then
      marks[#marks + 1] = { #lines - 1, group }
    end
  end
  if op.status == 'starting' then
    add('Starting…', 'DbblissInfo')
  elseif op.status == 'running' then
    add('Running…   c cancel · q close', 'DbblissInfo')
  elseif op.status == 'completed' then
    local size = present(op.bytes) and (' · ' .. human_size(op.bytes)) or ''
    add(('completed%s · verified'):format(size), 'DbblissInfo')
    add(op.path or '')
    if present(op.detail) then
      add(op.detail)
    end
  elseif op.status == 'failed' then
    add('FAILED: ' .. tostring(op.error or 'unknown error'), 'DbblissError')
  elseif op.status == 'cancelled' then
    add('cancelled', 'DbblissError')
  elseif op.status == 'refused' then
    add(tostring(op.error), 'DbblissError')
  end
  if present(op.note) then
    add(op.note, 'DbblissError')
  end
  if #op.log > 0 then
    add('')
    add('── progress ──', 'DbblissRule')
    for i = math.max(1, #op.log - MAX_LINES + 1), #op.log do
      local e = op.log[i]
      add(('%s: %s%s'):format(e.phase, e.text, present(e.percent) and ('   ' .. bar(e.percent)) or ''))
    end
  end
  return lines, marks
end

local function valid_win(win)
  return win ~= nil and vim.api.nvim_win_is_valid(win)
end

local function draw(op)
  if not op.buf or not vim.api.nvim_buf_is_valid(op.buf) then
    return
  end
  local lines, marks = M.render(op)
  vim.bo[op.buf].modifiable = true
  vim.api.nvim_buf_set_lines(op.buf, 0, -1, false, lines)
  vim.bo[op.buf].modifiable = false
  vim.api.nvim_buf_clear_namespace(op.buf, ns, 0, -1)
  for _, m in ipairs(marks) do
    pcall(vim.api.nvim_buf_set_extmark, op.buf, ns, m[1], 0, { end_row = m[1] + 1, hl_group = m[2], hl_eol = true })
  end
  if valid_win(op.win) then
    vim.api.nvim_win_set_config(op.win, { height = math.min(#lines, 20) })
  end
end

local function open_window(op)
  local buf = vim.api.nvim_create_buf(false, true)
  vim.bo[buf].buftype = 'nofile'
  vim.bo[buf].bufhidden = 'wipe'
  vim.bo[buf].swapfile = false
  vim.bo[buf].filetype = 'dbbliss-progress'
  local width = math.min(90, math.max(40, vim.o.columns - 10))
  local win = vim.api.nvim_open_win(buf, true, {
    relative = 'editor',
    width = width,
    height = 6,
    row = math.max(0, math.floor((vim.o.lines - 8) / 3)),
    col = math.max(0, math.floor((vim.o.columns - width) / 2)),
    style = 'minimal',
    border = 'rounded',
    title = ' dbbliss: ' .. op.title .. ' ',
  })
  vim.wo[win].wrap = true
  op.buf, op.win = buf, win
  S.by_buf[buf] = op
  local function map(lhs, fn, desc)
    vim.keymap.set('n', lhs, fn, { buffer = buf, silent = true, desc = 'dbbliss: ' .. desc })
  end
  map('q', function()
    if valid_win(op.win) then
      vim.api.nvim_win_close(op.win, true)
    end
  end, 'close')
  map('c', function()
    if op.id and (op.status == 'running' or op.status == 'starting') then
      deps.request(op.conn_id, 'backup/cancel', { backup_id = op.id }, function(err)
        if err then
          deps.notify(err.message or tostring(err), vim.log.levels.ERROR)
        end
      end)
    end
  end, 'cancel the backup')
  draw(op)
end

local function fail(err)
  deps.notify(err.message or tostring(err), err.code == USER_ERROR and vim.log.levels.WARN or vim.log.levels.ERROR)
end

local function timestamp()
  return os.date('%Y%m%d-%H%M%S')
end

--- Where the backup goes by default: this machine's working directory for PostgreSQL, the server's own
--- backup folder for SQL Server (the file is written by the server). Calls back with the default.
local function default_path(t, cb)
  local name = ('%s-%s'):format(t.database, timestamp())
  if t.engine ~= 'sqlserver' then
    local sep = vim.fn.has('win32') == 1 and '\\' or '/'
    return cb(vim.fn.getcwd() .. sep .. name .. '.dump')
  end
  deps.request(t.conn_id, 'backup/defaults', {}, function(err, result)
    if err then
      fail(err)
      return cb(name .. '.bak')
    end
    local dir = result and result.directory
    if not is_text(dir) then
      return cb(name .. '.bak')
    end
    local sep = dir:find('\\', 1, true) and '\\' or '/'
    cb(dir:gsub('[/\\]+$', '') .. sep .. name .. '.bak')
  end)
end

--- Backs up a database and shows it. `t.on_done(done)` is called with the final report.
---@param t { conn_id: string, conn_name: string, engine: string?, env: string?, database: string, path: string?, overwrite: boolean?, on_done: fun(done: table)? }
function M.backup(t)
  local function start(path)
    if not is_text(path) then
      return
    end
    local op = { title = 'backup of ' .. t.database, database = t.database, path = path, conn_id = t.conn_id, status = 'starting', log = {}, on_done = t.on_done }
    open_window(op)
    local params = { database = t.database, path = path }
    if t.overwrite then
      params.overwrite = true
    end
    deps.request(t.conn_id, 'backup/start', params, function(err, result)
      if err then
        op.status, op.error = 'refused', err.message or tostring(err)
        draw(op)
        fail(err)
        if op.on_done then
          op.on_done({ status = 'refused', error = op.error })
        end
        return
      end
      op.id, op.status = result.backup_id, 'running'
      S.ops[op.id] = op
      draw(op)
    end)
  end
  if is_text(t.path) then
    return start(t.path)
  end
  default_path(t, function(default)
    start(deps.input(('Backup file for "%s": '):format(t.database), default))
  end)
end

--- Routes the backend's backup notifications to the operation's window.
---@param method string
---@param p table
function M.handle_notification(method, p)
  local op = S.ops[p.backup_id]
  if not op then
    return
  end
  if method == 'backup/progress' then
    op.log[#op.log + 1] = { phase = p.phase, text = p.text, percent = p.percent }
    draw(op)
    return
  end
  op.status = p.status
  op.path = present(p.path) and p.path or op.path
  op.bytes, op.detail, op.note, op.error = p.bytes, p.detail, p.note, present(p.error) and p.error or nil
  draw(op)
  if p.status == 'completed' then
    deps.notify(('backup of %s completed and verified: %s'):format(op.database, op.path), vim.log.levels.INFO)
  elseif p.status == 'cancelled' then
    deps.notify(('backup of %s cancelled'):format(op.database), vim.log.levels.WARN)
  else
    deps.notify(('backup of %s FAILED: %s'):format(op.database, tostring(op.error)), vim.log.levels.ERROR)
  end
  if op.on_done then
    op.on_done(p)
  end
end

--- Drops an object or a database. The name must be typed; a backup is offered first, and made first on
--- prod (and on a connection with no tag). Nothing is dropped unless the backup completed.
---@param t { conn_id: string, conn_name: string, engine: string?, env: string?, kind: string, database: string, schema: string?, name: string, identity: string?, backup_first: boolean?, refresh: fun()? }
function M.drop(t)
  local typed = deps.input(('Type the name of the %s to drop ("%s") to confirm: '):format(t.kind, t.name))
  if not is_text(typed) or typed ~= t.name then
    return deps.notify(('not dropped: the name typed did not match "%s"'):format(t.name), vim.log.levels.INFO)
  end
  local function drop(backup_id)
    local params = {
      kind = t.kind,
      database = t.database,
      schema = t.schema,
      name = t.name,
      identity = t.identity == nil and vim.NIL or t.identity,
      confirm_name = typed,
    }
    if backup_id then
      params.backup_id = backup_id
    end
    deps.request(t.conn_id, 'management/drop', params, function(err)
      if err then
        return fail(err)
      end
      deps.notify(('dropped %s %s'):format(t.kind, t.name), vim.log.levels.INFO)
      if t.refresh then
        t.refresh()
      end
    end)
  end
  local protected = t.env ~= 'dev' and t.env ~= 'test'
  local database = t.kind == 'database' and t.name or t.database
  local function back_up_then_drop()
    M.backup({
      conn_id = t.conn_id,
      conn_name = t.conn_name,
      engine = t.engine,
      env = t.env,
      database = database,
      on_done = function(done)
        if done.status == 'completed' then
          drop(done.backup_id)
        else
          deps.notify(('not dropped: the backup of %s did not complete (%s)'):format(database, tostring(done.status)), vim.log.levels.WARN)
        end
      end,
    })
  end
  if protected then
    deps.notify(('%s is %s: a verified backup of %s is made first'):format(t.conn_name, t.env or 'not tagged (treated as prod)', database), vim.log.levels.INFO)
    return back_up_then_drop()
  end
  if t.backup_first ~= false and deps.confirm(('Back up database %s first?'):format(database), 'Back up', true) then
    return back_up_then_drop()
  end
  drop(nil)
end

return M
