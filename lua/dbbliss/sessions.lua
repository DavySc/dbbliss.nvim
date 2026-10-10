-- The sessions buffer: the server's other sessions as the backend lists them (sessions/list), with
-- keys to cancel a running statement or terminate a session. Both ask first, and the question names
-- the session. Nothing here knows an engine: the backend decides what a session is and what it can do.
local render = require('dbbliss.render')

local M = {}

local ns = vim.api.nvim_create_namespace('dbbliss_sessions')

local FIXABLE = 1008

local deps = {
  --- request(conn_id, method, params, callback(err, result))
  request = nil, ---@type fun(conn_id: string, method: string, params: table, cb: fun(err: table?, result: table?))?
  notify = function(msg, level)
    vim.notify('dbbliss: ' .. msg, level)
  end,
  --- Asks a yes/no question; the answer is true only for an explicit yes.
  confirm = function(text, yes_label, default_yes)
    return vim.fn.confirm(text, '&' .. yes_label .. '\n&No', default_yes and 1 or 2) == 1
  end,
}

local S = {
  data = {}, ---@type table<integer, { sessions: table[], can_cancel: boolean }>
  ctx = {}, ---@type table<integer, { id: string, name: string, env: string? }>
  line_rows = {}, ---@type table<integer, table<integer, table>>  buffer -> line -> session
}
M._state = S

---@param opts table  see `deps`
function M.setup(opts)
  for k, v in pairs(opts) do
    deps[k] = v
  end
end

--- Time in a state, short: 480ms, 4.2s, 3m05s, 1h02m, 2d03h.
---@param ms number|vim.NIL|nil
function M.format_duration(ms)
  if ms == nil or ms == vim.NIL then
    return ''
  elseif ms < 1000 then
    return ('%dms'):format(math.max(0, ms))
  elseif ms < 60000 then
    return ('%.1fs'):format(ms / 1000)
  elseif ms < 3600000 then
    return ('%dm%02ds'):format(math.floor(ms / 60000), math.floor(ms % 60000 / 1000))
  elseif ms < 86400000 then
    return ('%dh%02dm'):format(math.floor(ms / 3600000), math.floor(ms % 3600000 / 60000))
  end
  return ('%dd%02dh'):format(math.floor(ms / 86400000), math.floor(ms % 86400000 / 3600000))
end

local function text(v)
  if v == nil or v == vim.NIL then
    return ''
  end
  return tostring(v)
end

local function one_line(v)
  return (text(v):gsub('%s+', ' '))
end

local COLUMNS = {
  { name = ' ' }, { name = 'id' }, { name = 'user' }, { name = 'database' }, { name = 'host' }, { name = 'application' },
  { name = 'state' }, { name = 'duration' }, { name = 'wait' }, { name = 'blocked' }, { name = 'tx' }, { name = 'query' },
}

--- The buffer's lines for a result of sessions/list.
---@return string[] lines, { [1]: integer, [2]: integer, [3]: integer, [4]: string }[] marks, integer first_row_line, table[] rows
function M.render(result, conn_name)
  local rows, cells = {}, {}
  for _, s in ipairs(result.sessions) do
    rows[#rows + 1] = s
    cells[#cells + 1] = {
      s.own and '●' or '', text(s.id), text(s.user), text(s.database), text(s.host), text(s.application), text(s.state),
      M.format_duration(s.duration_ms), text(s.wait), text(s.blocked_by), s.in_transaction and 'tx' or '', one_line(s.query),
    }
  end
  local widths = render.widths(COLUMNS, cells, 60)
  local header, rule = render.format_header(COLUMNS, widths)
  local lines = {
    ('Sessions on %s (%d)   r refresh · c cancel statement · x terminate · s status · <CR> query · q close'):format(conn_name, #rows),
    '',
    header,
    rule,
  }
  local marks = { { 0, 0, #lines[1], 'DbblissHeader' }, { 2, 0, #header, 'DbblissHeader' }, { 3, 0, #rule, 'DbblissRule' } }
  for i, row in ipairs(cells) do
    local line = render.format_row(row, widths)
    lines[#lines + 1] = line
    local s = rows[i]
    if s.own then
      marks[#marks + 1] = { #lines - 1, 0, #line, 'DbblissInfo' }
    elseif s.blocked_by ~= nil and s.blocked_by ~= vim.NIL then
      marks[#marks + 1] = { #lines - 1, 0, #line, 'DbblissError' }
    end
  end
  return lines, marks, 5, rows
end

local function valid(buf)
  return buf ~= nil and vim.api.nvim_buf_is_valid(buf)
end

local function draw(buf)
  local ctx, data = S.ctx[buf], S.data[buf]
  if not valid(buf) or not ctx or not data then
    return
  end
  local lines, marks, first, rows = M.render(data, ctx.name)
  vim.bo[buf].modifiable = true
  vim.api.nvim_buf_set_lines(buf, 0, -1, false, lines)
  vim.bo[buf].modifiable = false
  vim.api.nvim_buf_clear_namespace(buf, ns, 0, -1)
  for _, m in ipairs(marks) do
    pcall(vim.api.nvim_buf_set_extmark, buf, ns, m[1], m[2], { end_col = m[3], hl_group = m[4] })
  end
  S.line_rows[buf] = {}
  for i, s in ipairs(rows) do
    S.line_rows[buf][first + i - 1] = s
  end
end

local function fail(err)
  local level = err.code == FIXABLE and vim.log.levels.WARN or vim.log.levels.ERROR
  deps.notify(err.message or tostring(err), level)
end

local function load(buf)
  local ctx = S.ctx[buf]
  deps.request(ctx.id, 'sessions/list', {}, function(err, result)
    if err then
      return fail(err)
    end
    S.data[buf] = result
    draw(buf)
  end)
end

---@return table? session
local function session_under_cursor(buf)
  local line = vim.api.nvim_win_get_cursor(0)[1]
  local s = (S.line_rows[buf] or {})[line]
  if not s then
    deps.notify('no session on this line', vim.log.levels.INFO)
  end
  return s
end

--- The question before an action: names the session, says what happens, and is louder on prod.
---@param session table
---@param action 'cancel'|'terminate'
---@param env string?
---@param conn_name string
function M.confirm_text(session, action, env, conn_name)
  local lines = {}
  if env == 'prod' then
    lines[#lines + 1] = ('PROD connection "%s"!'):format(conn_name)
  end
  if action == 'cancel' then
    lines[#lines + 1] = ('Cancel the running statement of session %s? The session stays.'):format(text(session.id))
  else
    lines[#lines + 1] = ('Terminate session %s? It is disconnected.'):format(text(session.id))
  end
  lines[#lines + 1] = ('  user %s · database %s · host %s'):format(text(session.user), text(session.database), text(session.host))
  lines[#lines + 1] = ('  %s for %s'):format(text(session.state), M.format_duration(session.duration_ms))
  local query = one_line(session.query)
  if query ~= '' then
    lines[#lines + 1] = '  ' .. (#query > 120 and query:sub(1, 120) .. '…' or query)
  end
  if action == 'terminate' and session.in_transaction then
    lines[#lines + 1] = 'It has an open transaction: terminating rolls it back.'
  end
  return table.concat(lines, '\n')
end

local function act(buf, action)
  local ctx, data = S.ctx[buf], S.data[buf]
  local session = session_under_cursor(buf)
  if not session or not data then
    return
  end
  if session.own then
    return deps.notify(
      ('session %s is this connection\'s own; cancel its query with :Dbbliss cancel, end it with :Dbbliss disconnect'):format(text(session.id)),
      vim.log.levels.WARN
    )
  end
  if action == 'cancel' and not data.can_cancel then
    return deps.notify('this engine cannot cancel another session\'s statement without ending the session; terminate it (x) instead', vim.log.levels.WARN)
  end
  local prod = ctx.env == 'prod'
  local label = (action == 'cancel' and 'Cancel statement' or 'Terminate') .. (prod and ' on PROD' or '')
  if not deps.confirm(M.confirm_text(session, action, ctx.env, ctx.name), label, false) then
    return
  end
  deps.request(ctx.id, 'sessions/' .. action, { id = session.id, identity = session.identity }, function(err, result)
    if err then
      fail(err)
    elseif result.done then
      deps.notify(('%s session %s'):format(action == 'cancel' and 'cancelled the statement of' or 'terminated', text(session.id)), vim.log.levels.INFO)
    else
      deps.notify(('the server did not %s session %s (nothing was signalled; it may have ended)'):format(action, text(session.id)), vim.log.levels.WARN)
    end
    if valid(buf) then
      load(buf)
    end
  end)
end

local function status(buf)
  local ctx = S.ctx[buf]
  local session = session_under_cursor(buf)
  if not session then
    return
  end
  deps.request(ctx.id, 'sessions/status', { id = session.id, identity = session.identity }, function(err, result)
    if err then
      return fail(err)
    end
    if not result.present then
      deps.notify(('session %s is gone'):format(text(session.id)), vim.log.levels.INFO)
    elseif result.detail ~= nil and result.detail ~= vim.NIL then
      deps.notify(result.detail, vim.log.levels.INFO)
    else
      deps.notify(('session %s is still present'):format(text(session.id)), vim.log.levels.INFO)
    end
  end)
end

local function show_query(buf)
  local session = session_under_cursor(buf)
  if not session then
    return
  end
  if session.query == nil or session.query == vim.NIL or session.query == '' then
    return deps.notify(('no query text for session %s'):format(text(session.id)), vim.log.levels.INFO)
  end
  vim.cmd('new')
  local lines = vim.split((session.query:gsub('\r', '')), '\n', { plain = true })
  vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)
  vim.bo.buftype = 'nofile'
  vim.bo.filetype = 'sql'
  vim.bo.modified = false
end

local function find_buffer(name)
  local nr = vim.fn.bufnr('^' .. vim.fn.escape(name, '[]*.\\') .. '$')
  return nr > 0 and vim.api.nvim_buf_is_valid(nr) and nr or nil
end

--- Shows the sessions of a connection in a buffer of its own (reused) and loads them.
---@param ctx { id: string, name: string, env: string? }
---@return integer bufnr
function M.open(ctx)
  local name = 'dbbliss://sessions/' .. ctx.name
  local buf = find_buffer(name)
  if not buf then
    buf = vim.api.nvim_create_buf(false, true)
    vim.api.nvim_buf_set_name(buf, name)
    vim.bo[buf].buftype = 'nofile'
    vim.bo[buf].bufhidden = 'hide'
    vim.bo[buf].swapfile = false
    vim.bo[buf].filetype = 'dbbliss-sessions'
    vim.bo[buf].modifiable = false
    local function map(lhs, fn, desc)
      vim.keymap.set('n', lhs, fn, { buffer = buf, silent = true, desc = 'dbbliss: ' .. desc })
    end
    map('q', function()
      for _, win in ipairs(vim.fn.win_findbuf(buf)) do
        pcall(vim.api.nvim_win_close, win, true)
      end
    end, 'close')
    map('r', function()
      load(buf)
    end, 'refresh sessions')
    map('c', function()
      act(buf, 'cancel')
    end, 'cancel the session\'s statement')
    map('x', function()
      act(buf, 'terminate')
    end, 'terminate the session')
    map('s', function()
      status(buf)
    end, 'session status')
    map('<CR>', function()
      show_query(buf)
    end, 'show the session\'s query')
  end
  S.ctx[buf] = ctx
  if #vim.fn.win_findbuf(buf) == 0 then
    vim.cmd('botright 15split')
    vim.api.nvim_win_set_buf(0, buf)
    vim.wo.wrap = false
    vim.wo.number = false
  else
    vim.api.nvim_set_current_win(vim.fn.win_findbuf(buf)[1])
  end
  load(buf)
  return buf
end

return M
