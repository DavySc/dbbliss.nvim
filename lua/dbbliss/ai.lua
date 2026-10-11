-- The AI hook: gathers what you would otherwise paste into a chat (the statement, the definitions of the tables it names,
-- the last plan and error of that statement), shows you exactly what will be sent, and on your yes pipes it to a command
-- you configured, showing the answer in a scratch buffer. The plugin has no model or API client of its own; the command is
-- run without a shell with the text on its standard input.
--
-- What leaves the machine is the point of every rule here: it is opt-in per connection, refused on prod and untagged
-- connections unless they say otherwise, always shown first, bounded, and never includes result rows.
local completion = require('dbbliss.completion')

local M = {}

local DEFAULTS = {
  command = nil, ---@type string[]?  e.g. { 'claude', '-p' }
  timeout_ms = 120000,
  max_bytes = 200000,
  max_tables = 10,
  confirm = true, -- false skips the question, on dev and test connections only
  preamble = 'You are helping with SQL for a {engine} database. Answer the question about the statement below. '
    .. 'Be concrete and short. The table definitions, plan and error are the only context you have.',
}

local S = {
  cfg = vim.deepcopy(DEFAULTS),
  running = {}, ---@type table[]  ops with a handle
}
M._state = S

local deps = {
  --- request(conn_id, method, params, callback(err, result))
  request = nil,
  notify = function(msg, level)
    vim.notify('dbbliss: ' .. msg, level)
  end,
  --- Asks what to do with the text about to be sent: 'send', 'show' (look at it first) or 'cancel'.
  confirm = function(summary, _, shown)
    local choice = vim.fn.confirm(summary, shown and '&Send\n&Cancel' or '&Send\n&Show the text first\n&Cancel', shown and 2 or 3)
    if shown then
      return choice == 1 and 'send' or 'cancel'
    end
    return choice == 1 and 'send' or (choice == 2 and 'show' or 'cancel')
  end,
  run = nil, -- set below to M.default_run
}

function M.setup(opts)
  for k, v in pairs(opts) do
    deps[k] = v
  end
end

---@param c table?  see DEFAULTS
function M.configure(c)
  for k, v in pairs(c or {}) do
    if v == vim.NIL then
      v = nil
    end
    S.cfg[k] = v
  end
end

function M.reset()
  S.cfg = vim.deepcopy(DEFAULTS)
  S.running = {}
end

-- Running the command ---------------------------------------------------------------------------------

--- Runs `cmd` (an argument list, no shell) with `stdin`, ends it after opts.timeout_ms, and calls back once with
--- { code, stdout, stderr, timed_out?, start_error? }. Returns a handle with `cancel()`.
function M.default_run(cmd, stdin, opts, cb)
  local finished, timed_out = false, false
  local obj
  local ok, err = pcall(function()
    obj = vim.system(cmd, { stdin = stdin, text = true }, function(res)
      vim.schedule(function()
        if finished then
          return
        end
        finished = true
        cb({ code = res.code, signal = res.signal, stdout = res.stdout or '', stderr = res.stderr or '', timed_out = timed_out })
      end)
    end)
  end)
  if not ok then
    vim.schedule(function()
      cb({ code = -1, start_error = tostring(err), stdout = '', stderr = '' })
    end)
    return { cancel = function() end }
  end
  local timer = vim.uv.new_timer()
  timer:start(opts.timeout_ms, 0, function()
    timer:close()
    if finished then
      return
    end
    timed_out = true
    pcall(obj.kill, obj, 15)
    -- a process that ignores the request to end is made to
    vim.defer_fn(function()
      if not finished then
        pcall(obj.kill, obj, 9)
      end
    end, 2000)
  end)
  return {
    cancel = function()
      finished = true
      if not timer:is_closing() then
        timer:close()
      end
      pcall(obj.kill, obj, 9)
    end,
  }
end
deps.run = M.default_run

--- Neovim is quitting: whatever runs is ended.
function M.stop()
  for _, op in ipairs(S.running) do
    op.cancelled = true
    if op.handle then
      op.handle.cancel()
    end
  end
  S.running = {}
end

-- Building the payload ----------------------------------------------------------------------------------

local function squash(sql)
  return (vim.trim(sql or ''):gsub('%s+', ' '))
end

local function section_cut(text, room)
  local marker = ('\n[cut: %d of %d bytes]'):format(#text - math.max(room - 40, 0), #text)
  return text:sub(1, math.max(room - #marker, 0)) .. marker
end

--- Assembles the text. The question, preamble and statement are never cut; the rest is added in order of use until the
--- budget is spent, then cut or left out, and each cut is recorded.
---@return string payload
---@return table info  { parts = {...}, cut = {...}, bytes = integer }
local function build(a, cfg, tables)
  local head = cfg.preamble:gsub('{engine}', a.conn.engine or 'SQL') .. '\n\nQuestion: ' .. a.question .. '\n\n## Statement\n' .. a.statement .. '\n'
  local info = { parts = { 'question', 'statement' }, cut = {}, tables = {}, missing = {}, left_out = tables.left_out }
  local room = cfg.max_bytes - #head
  if room < 0 then
    return nil, info
  end
  local out = { head }
  local function add(title, text)
    local block = '\n## ' .. title .. '\n' .. text .. '\n'
    if #block <= room then
      out[#out + 1] = block
      room = room - #block
      return true
    end
    if room > 120 then
      out[#out + 1] = section_cut(block, room)
      info.cut[#info.cut + 1] = title .. ' (cut)'
      room = 0
    else
      out[#out + 1] = '\n[omitted, no room: ' .. title .. ']\n'
      info.cut[#info.cut + 1] = title .. ' (left out)'
    end
    return false
  end
  local e = a.last_error
  if e and squash(e.sql) == squash(a.statement) then
    add('Last error of this statement', tostring(e.message) .. ((e.line and e.line ~= vim.NIL) and ('\n(line ' .. tostring(e.line) .. ')') or ''))
    info.parts[#info.parts + 1] = 'last error'
  end
  for _, t in ipairs(tables.read) do
    if t.text then
      add('Table definition: ' .. t.name, t.text)
      info.tables[#info.tables + 1] = t.name
    else
      add('Table ' .. t.name, '[its definition could not be read: ' .. tostring(t.err) .. ']')
      info.missing[#info.missing + 1] = t.name
    end
  end
  if #tables.left_out > 0 then
    add(('Tables not included (ai.max_tables = %d)'):format(cfg.max_tables), table.concat(tables.left_out, ', '))
  end
  local p = a.last_plan
  if p and squash(p.sql) == squash(a.statement) then
    add('Last plan of this statement (' .. tostring(p.mode) .. ')', table.concat(p.lines or {}, '\n'))
    info.parts[#info.parts + 1] = 'last plan'
  end
  local payload = table.concat(out)
  info.bytes = #payload
  return payload, info
end

local function summarize(a, cfg, info, protected)
  local lines = { ('Send this to `%s`?'):format(table.concat(cfg.command, ' ')), '' }
  lines[#lines + 1] = '  question and statement (' .. #a.statement .. ' bytes of SQL)'
  if #info.tables > 0 then
    lines[#lines + 1] = '  table definitions: ' .. table.concat(info.tables, ', ')
  end
  if #info.missing > 0 then
    lines[#lines + 1] = '  could not be read: ' .. table.concat(info.missing, ', ')
  end
  if #info.left_out > 0 then
    lines[#lines + 1] = '  not included: ' .. table.concat(info.left_out, ', ')
  end
  for _, part in ipairs({ 'last error', 'last plan' }) do
    if vim.tbl_contains(info.parts, part) then
      lines[#lines + 1] = '  ' .. part .. ' of this statement'
    end
  end
  lines[#lines + 1] = ('  total %d bytes (limit %d)'):format(info.bytes, cfg.max_bytes)
  if #info.cut > 0 then
    lines[#lines + 1] = '  cut to fit: ' .. table.concat(info.cut, ', ')
  end
  lines[#lines + 1] = ''
  lines[#lines + 1] = 'Result rows are never sent. Literals in the SQL, the plan and the error are sent as they are.'
  if protected then
    lines[#lines + 1] = 'THIS IS A ' .. ((a.conn.env == 'prod') and 'PRODUCTION' or 'PROTECTED (prod or not tagged)') .. ' CONNECTION.'
  end
  return table.concat(lines, '\n')
end

-- Showing ---------------------------------------------------------------------------------------------------

local function scratch(filetype, lines)
  vim.cmd('botright new')
  local buf = vim.api.nvim_get_current_buf()
  vim.bo[buf].buftype = 'nofile'
  vim.bo[buf].bufhidden = 'wipe'
  vim.bo[buf].swapfile = false
  vim.bo[buf].filetype = filetype
  vim.api.nvim_buf_set_lines(buf, 0, -1, false, lines)
  vim.bo[buf].modifiable = false
  return buf
end

local function set_lines(buf, lines)
  if vim.api.nvim_buf_is_valid(buf) then
    vim.bo[buf].modifiable = true
    vim.api.nvim_buf_set_lines(buf, 0, -1, false, lines)
    vim.bo[buf].modifiable = false
  end
end

local function first_line(s)
  return (vim.split(vim.trim(s), '\n', { plain = true })[1] or '')
end

local function remove_op(op)
  for i, x in ipairs(S.running) do
    if x == op then
      table.remove(S.running, i)
      return
    end
  end
end

local function start(a, cfg, payload)
  local op = { cancelled = false }
  local cmdline = table.concat(cfg.command, ' ')
  local header = ('-- asked `%s` about: %s'):format(cfg.command[1], first_line(a.statement))
  local buf = scratch('dbbliss-ai', { header, '', ('Asking %s … (q cancels)'):format(cmdline) })
  vim.bo[buf].syntax = 'markdown'
  op.buf = buf
  vim.keymap.set('n', 'q', function()
    op.cancelled = true
    if op.handle then
      op.handle.cancel()
    end
    remove_op(op)
    if vim.api.nvim_buf_is_valid(buf) then
      pcall(vim.api.nvim_buf_delete, buf, { force = true })
    end
  end, { buffer = buf, silent = true, desc = 'dbbliss ask: cancel and close' })
  S.running[#S.running + 1] = op
  op.handle = deps.run(cfg.command, payload, { timeout_ms = cfg.timeout_ms }, function(res)
    if op.cancelled then
      return
    end
    remove_op(op)
    local body, failed
    local stderr = vim.trim(res.stderr or '')
    if res.start_error then
      body, failed = { 'FAILED: the command could not be started: ' .. res.start_error }, true
    elseif res.timed_out then
      body, failed = { ('FAILED: the command timed out after %s s and was ended.'):format(tostring(cfg.timeout_ms / 1000)) }, true
    elseif res.code ~= 0 then
      body, failed = { ('FAILED: `%s` exited with exit code %s.'):format(cfg.command[1], tostring(res.code)) }, true
    elseif vim.trim(res.stdout or '') == '' then
      body, failed = { 'no answer: the command printed nothing.' }, true
    else
      body = vim.split(vim.trim(res.stdout), '\n', { plain = true })
    end
    if failed and stderr ~= '' then
      body[#body + 1] = ''
      body[#body + 1] = 'stderr:'
      vim.list_extend(body, vim.split(stderr, '\n', { plain = true }))
    end
    set_lines(buf, vim.list_extend({ header, '' }, body))
    if failed then
      deps.notify('ask: ' .. body[1], vim.log.levels.ERROR)
    end
  end)
end

-- The entry point -------------------------------------------------------------------------------------------

--- Asks the configured command about a statement. Nothing is sent unless every rule allows it and the user says yes.
---@param a { conn: table, question: string, statement: string, last_error: table?, last_plan: table? }
---  conn: { id, name, engine, env, ai }  ai: true | { prod = true } | nil
---  last_error: { sql, message, line }   last_plan: { sql, mode, lines }   (used only for the same statement)
function M.ask(a)
  local cfg = S.cfg
  local notify = function(msg)
    deps.notify('ask: ' .. msg, vim.log.levels.WARN)
  end
  if cfg.command == nil then
    return notify('no ai.command is configured (setup({ ai = { command = { "claude", "-p" } } }))')
  end
  if type(cfg.command) ~= 'table' or #cfg.command == 0 then
    return notify('ai.command must be a list of arguments, e.g. { "claude", "-p" }: it is run without a shell')
  end
  local opt = a.conn.ai
  if opt == nil or opt == vim.NIL or opt == false then
    return notify(('%s has not opted in: set ai = true on the connection'):format(a.conn.name))
  end
  local env = a.conn.env
  local protected = env ~= 'dev' and env ~= 'test'
  if protected and not (type(opt) == 'table' and opt.prod == true) then
    return notify(('%s is prod or not tagged, and its SQL and schema would leave this machine: set ai = { prod = true } on it to allow that'):format(a.conn.name))
  end
  local fixed = #cfg.preamble + #a.question + #a.statement + 100
  if fixed > cfg.max_bytes then
    return notify(('the question and the statement alone are %d bytes; ai.max_bytes is %d'):format(fixed, cfg.max_bytes))
  end

  -- The tables the statement names, read from the catalog session (definitions only, never rows).
  local names, seen = {}, {}
  for _, t in ipairs(completion.analyze(a.statement, a.statement, a.conn.engine).tables) do
    local key = t.name:lower()
    if not seen[key] then
      seen[key] = true
      names[#names + 1] = t.name
    end
  end
  local tables = { read = {}, left_out = {} }
  local wanted = {}
  for i, name in ipairs(names) do
    if i <= cfg.max_tables then
      wanted[#wanted + 1] = name
    else
      tables.left_out[#tables.left_out + 1] = name
    end
  end
  local pending = #wanted
  local function proceed()
    local payload, info = build(a, cfg, tables)
    if not payload then
      return notify('nothing fits in ai.max_bytes')
    end
    local need = cfg.confirm ~= false or protected
    if need then
      local summary = summarize(a, cfg, info, protected)
      local choice = deps.confirm(summary, payload)
      local preview
      if choice == 'show' then
        preview = scratch('text', vim.split(payload, '\n', { plain = true }))
        choice = deps.confirm(summary, payload, true)
        if vim.api.nvim_buf_is_valid(preview) then
          pcall(vim.api.nvim_buf_delete, preview, { force = true })
        end
      end
      if choice ~= 'send' then
        return deps.notify('ask cancelled: nothing was sent', vim.log.levels.INFO)
      end
    end
    start(a, cfg, payload)
  end
  if pending == 0 then
    return proceed()
  end
  for i, name in ipairs(wanted) do
    deps.request(a.conn.id, 'catalog/script', { name = name }, function(err, result)
      tables.read[i] = (err or not result) and { name = name, err = err and err.message or 'no answer' } or { name = name, text = result.text }
      pending = pending - 1
      if pending == 0 then
        proceed()
      end
    end)
  end
end

return M
