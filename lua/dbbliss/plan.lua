-- Plan viewer. The backend plans a statement on a session of its own (PostgreSQL EXPLAIN, SQL Server
-- SHOWPLAN_XML / STATISTICS XML) and answers with one `plan/done` that holds the tree, own cost and
-- time per node and the hottest node. This module shows it as a collapsible tree and, before an
-- actual plan of a statement that is not plainly a read, asks: an actual plan runs the statement (and
-- rolls it back). Nothing here knows an engine beyond the syntax of the raw plan.
local M = {}

local ns = vim.api.nvim_create_namespace('dbbliss_plan')

local USER_ERROR = 1009

vim.api.nvim_set_hl(0, 'DbblissHot', { link = 'WarningMsg', default = true })

local deps = {
  --- request(conn_id, method, params, callback(err, result))
  request = nil, ---@type fun(conn_id: string, method: string, params: table, cb: fun(err: table?, result: table?))?
  notify = function(msg, level)
    vim.notify('dbbliss: ' .. msg, level)
  end,
  --- Asks a yes/no question; true only for an explicit yes.
  confirm = function(text)
    return vim.fn.confirm(text, '&Run and roll back\n&No', 2) == 1
  end,
}

local S = {
  ops = {}, ---@type table<string, table>  plan id -> operation
  by_buf = {}, ---@type table<integer, table>
}
M._state = S

---@param opts table  see `deps`
function M.setup(opts)
  for k, v in pairs(opts) do
    deps[k] = v
  end
end

local function present(v)
  return v ~= nil and v ~= vim.NIL
end

local function num(v)
  if v == math.floor(v) and math.abs(v) < 1e15 then
    return ('%d'):format(v)
  end
  local s = ('%.2f'):format(v):gsub('0+$', ''):gsub('%.$', '')
  return s
end

--- How far the actual rows are from the estimate, as a factor ("×50" more, "×0.2" fewer); nil when
--- they agree or the estimate is 0.
local function factor(est, act)
  if not present(est) or not present(act) or est <= 0 then
    return nil
  end
  local r = act / est
  if r > 0.95 and r < 1.05 then
    return nil
  end
  return '×' .. (r >= 10 and ('%d'):format(r + 0.5) or num(tonumber(('%.1f'):format(r))))
end

local TOTALS = {
  { 'cost', 'cost %s' },
  { 'statement_cost', 'cost %s' },
  { 'planning_ms', 'planning %s ms' },
  { 'compile_ms', 'compile %s ms' },
  { 'execution_ms', 'execution %s ms' },
  { 'dop', 'parallelism %s' },
  { 'memory_grant_kb', 'memory grant %s KB' },
}

local function node_line(n, depth, collapsed, hottest)
  local mark = #n.children == 0 and ' ' or (collapsed and '▸' or '▾')
  local text = ('%s%s %s'):format(('  '):rep(depth), mark, n.operator)
  if present(n.detail) and n.detail ~= '' then
    text = text .. '  ' .. n.detail
  end
  local parts = {}
  if present(n.cost) then
    parts[#parts + 1] = 'cost ' .. num(n.cost)
  end
  if present(n.actual_rows) then
    local rows = present(n.estimated_rows) and ('rows %s → %s'):format(num(n.estimated_rows), num(n.actual_rows)) or ('rows %s'):format(num(n.actual_rows))
    local f = factor(n.estimated_rows, n.actual_rows)
    parts[#parts + 1] = f and (rows .. ' ' .. f) or rows
  elseif present(n.estimated_rows) then
    parts[#parts + 1] = 'rows ' .. num(n.estimated_rows)
  end
  if present(n.loops) then
    parts[#parts + 1] = 'loops ' .. num(n.loops)
  end
  if present(n.time_ms) then
    local t = num(n.time_ms) .. ' ms'
    if present(n.self_time_ms) and #n.children > 0 then
      t = t .. (' (self %s ms)'):format(num(n.self_time_ms))
    end
    parts[#parts + 1] = t
  end
  if #parts > 0 then
    text = text .. '   ' .. table.concat(parts, ' · ')
  end
  if hottest then
    text = text .. '   ◀ hottest'
  end
  return text
end

local function title_of(sql)
  local first = (sql:gsub('%s+', ' '))
  return #first > 80 and first:sub(1, 77) .. '...' or first
end

--- The viewer's lines for an operation (pure, tested).
---@return string[] lines
---@return { [1]: integer, [2]: string }[] marks  line (0-based), highlight group
---@return table<integer, { plan: integer, id: integer }> rows  line (1-based) -> node
function M.render(op)
  local lines, marks, rows = {}, {}, {}
  local function add(text, group, row)
    lines[#lines + 1] = text
    if group then
      marks[#marks + 1] = { #lines - 1, group }
    end
    if row then
      rows[#lines] = row
    end
  end
  add(('Plan · %s · %s'):format(op.mode, title_of(op.sql)), 'DbblissHeader')
  if op.status == 'running' then
    add('Running…   c cancel · q close', 'DbblissInfo')
  elseif op.status == 'failed' then
    add('FAILED: ' .. tostring(op.error or 'unknown error'), 'DbblissError')
  elseif op.status == 'cancelled' then
    add('cancelled: no plan', 'DbblissError')
  else
    add('<CR>/o toggle · zR/zM expand/collapse all · x raw plan · q close', 'DbblissInfo')
  end
  for pi, doc in ipairs(op.plans or {}) do
    add('')
    local head = {}
    for _, t in ipairs(TOTALS) do
      if present(doc.totals) and present(doc.totals[t[1]]) then
        head[#head + 1] = t[2]:format(num(doc.totals[t[1]]))
      end
    end
    local label = #op.plans > 1 and ('Statement %d of %d · '):format(pi, #op.plans) or ''
    add(('%s%s plan · %s%s'):format(label, doc.mode, doc.engine, #head > 0 and (' · ' .. table.concat(head, ' · ')) or ''), 'DbblissRule')
    local function walk(n, depth)
      local key = pi .. ':' .. n.id
      local collapsed = op.collapsed[key] and #n.children > 0
      local hot = present(doc.hottest) and doc.hottest == n.id
      add(node_line(n, depth, collapsed, hot), hot and 'DbblissHot' or nil, { plan = pi, id = n.id })
      local pad = ('  '):rep(depth) .. '    '
      if present(n.buffers) then
        add(pad .. 'buffers ' .. n.buffers, 'DbblissInfo')
      end
      for _, w in ipairs(n.warnings or {}) do
        add(pad .. '⚠ ' .. w, 'DbblissError')
      end
      if not collapsed then
        for _, c in ipairs(n.children) do
          walk(c, depth + 1)
        end
      end
    end
    walk(doc.root, 0)
    for _, note in ipairs(doc.notes or {}) do
      add('note: ' .. note, 'DbblissInfo')
    end
  end
  return lines, marks, rows
end

local function valid_buf(op)
  return op.buf and vim.api.nvim_buf_is_valid(op.buf)
end

local function draw(op)
  if not valid_buf(op) then
    return
  end
  local lines, marks, rows = M.render(op)
  op.rows = rows
  vim.bo[op.buf].modifiable = true
  vim.api.nvim_buf_set_lines(op.buf, 0, -1, false, lines)
  vim.bo[op.buf].modifiable = false
  vim.api.nvim_buf_clear_namespace(op.buf, ns, 0, -1)
  for _, m in ipairs(marks) do
    pcall(vim.api.nvim_buf_set_extmark, op.buf, ns, m[1], 0, { end_row = m[1] + 1, hl_group = m[2], hl_eol = true })
  end
end

local function node_at(op)
  return op.rows and op.rows[vim.api.nvim_win_get_cursor(0)[1]]
end

--- Collapses or expands the node under the cursor (a leaf has nothing to hide).
function M.toggle(op)
  local at = node_at(op)
  if not at then
    return
  end
  local key = at.plan .. ':' .. at.id
  op.collapsed[key] = not op.collapsed[key] or nil
  draw(op)
end

local function each_parent(op, fn)
  for pi, doc in ipairs(op.plans or {}) do
    local function walk(n)
      if #n.children > 0 then
        fn(pi .. ':' .. n.id)
      end
      for _, c in ipairs(n.children) do
        walk(c)
      end
    end
    walk(doc.root)
  end
end

function M.expand_all(op)
  op.collapsed = {}
  draw(op)
end

function M.collapse_all(op)
  each_parent(op, function(key)
    op.collapsed[key] = true
  end)
  draw(op)
end

--- Opens the engine's own plan text in a scratch buffer (JSON or XML).
function M.raw(op)
  local at = node_at(op)
  local doc = op.plans and op.plans[at and at.plan or 1]
  if not doc or not present(doc.raw) then
    return deps.notify('no raw plan to show', vim.log.levels.INFO)
  end
  vim.cmd('botright new')
  local buf = vim.api.nvim_get_current_buf()
  vim.bo[buf].buftype = 'nofile'
  vim.bo[buf].bufhidden = 'wipe'
  vim.bo[buf].swapfile = false
  vim.api.nvim_buf_set_lines(buf, 0, -1, false, vim.split(doc.raw, '\n', { plain = true }))
  vim.bo[buf].filetype = doc.engine == 'sqlserver' and 'xml' or 'json'
  vim.bo[buf].modifiable = false
end

local function close(op)
  if valid_buf(op) then
    for _, win in ipairs(vim.fn.win_findbuf(op.buf)) do
      if #vim.api.nvim_list_wins() > 1 then
        pcall(vim.api.nvim_win_close, win, true)
      end
    end
    if vim.api.nvim_buf_is_valid(op.buf) then
      pcall(vim.api.nvim_buf_delete, op.buf, { force = true })
    end
  end
end

local function open_window(op)
  vim.cmd('botright new')
  local buf = vim.api.nvim_get_current_buf()
  vim.bo[buf].buftype = 'nofile'
  vim.bo[buf].bufhidden = 'wipe'
  vim.bo[buf].swapfile = false
  vim.bo[buf].filetype = 'dbbliss-plan'
  vim.wo.wrap = false
  op.buf = buf
  S.by_buf[buf] = op
  local function map(lhs, fn, desc)
    vim.keymap.set('n', lhs, fn, { buffer = buf, silent = true, desc = 'dbbliss plan: ' .. desc })
  end
  map('<CR>', function()
    M.toggle(op)
  end, 'collapse or expand the node')
  map('o', function()
    M.toggle(op)
  end, 'collapse or expand the node')
  map('zR', function()
    M.expand_all(op)
  end, 'expand all')
  map('zM', function()
    M.collapse_all(op)
  end, 'collapse all')
  map('x', function()
    M.raw(op)
  end, 'raw plan')
  map('q', function()
    close(op)
  end, 'close')
  map('c', function()
    if op.id and op.status == 'running' then
      deps.request(op.conn_id, 'plan/cancel', { plan_id = op.id }, function(err)
        if err then
          deps.notify(err.message or tostring(err), vim.log.levels.ERROR)
        end
      end)
    end
  end, 'cancel the plan')
  draw(op)
end

local function fail(err)
  deps.notify(err.message or tostring(err), err.code == USER_ERROR and vim.log.levels.WARN or vim.log.levels.ERROR)
end

--- Plans a statement and shows it. An actual plan of a statement that is not plainly a read is
--- confirmed first; the answer No sends nothing.
---@param t { conn_id: string, conn_name: string, engine: string?, sql: string, mode: 'estimated'|'actual', kind: string? }
function M.start(t)
  local mode = t.mode or 'estimated'
  local params = { sql = t.sql, mode = mode }
  if mode == 'actual' and t.kind ~= 'read' then
    local text = ('An actual plan RUNS this statement, on a separate session, inside a transaction that is rolled back:\n\n%s\n\n'
      .. 'The statement will be run and rolled back. Effects a rollback does not undo (sequences, calls to the outside) still happen. Run it?'):format(title_of(t.sql))
    if not deps.confirm(text) then
      return deps.notify('plan not started: nothing was run', vim.log.levels.INFO)
    end
    params.confirm_execute = true
  end
  deps.request(t.conn_id, 'plan/start', params, function(err, result)
    if err then
      return fail(err)
    end
    local op = { id = result.plan_id, conn_id = t.conn_id, sql = t.sql, mode = mode, status = 'running', plans = {}, collapsed = {} }
    S.ops[op.id] = op
    open_window(op)
  end)
end

--- Routes `plan/done` to the operation's buffer.
---@param method string
---@param p table
function M.handle_notification(method, p)
  local op = method == 'plan/done' and S.ops[p.plan_id]
  if not op then
    return
  end
  op.status, op.plans = p.status, p.plans or {}
  op.error = present(p.error) and p.error or nil
  draw(op)
  if p.status == 'completed' then
    deps.notify(('plan ready: %s'):format(title_of(op.sql)), vim.log.levels.INFO)
  elseif p.status == 'cancelled' then
    deps.notify(('plan cancelled: %s'):format(title_of(op.sql)), vim.log.levels.WARN)
  else
    deps.notify(('plan FAILED: %s'):format(tostring(op.error)), vim.log.levels.ERROR)
  end
end

return M
