-- Sessions UI tests (Phase 3): the sessions buffer, its keys and the confirmations, with the backend
-- replaced by a function. No database.
--
--   nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/sessions_test.lua
vim.fs.joinpath = vim.fs.joinpath or function(...)
  return table.concat({ ... }, '/')
end
vim.notify = function() end

local sessions = require('dbbliss.sessions')

local function expect(cond, msg)
  if not cond then
    error(msg, 0)
  end
end

local function eq(actual, expected, what)
  if not vim.deep_equal(actual, expected) then
    error(('%s:\n  got      %s\n  expected %s'):format(what, vim.inspect(actual), vim.inspect(expected)), 0)
  end
end

local function row(over)
  return vim.tbl_extend('force', {
    id = '101',
    identity = 't101',
    user = 'alice',
    database = 'sales',
    host = '10.0.0.7',
    application = 'psql',
    state = 'active',
    duration_ms = 1500,
    wait = vim.NIL,
    blocked_by = vim.NIL,
    in_transaction = false,
    query = 'SELECT *\n  FROM orders',
    own = false,
  }, over or {})
end

--- A sessions buffer whose backend is a function. `answers` is the list the next list call returns.
local function fake(opts)
  opts = opts or {}
  local f = { calls = {}, notes = {}, confirms = {}, answer = opts.confirm ~= false }
  f.list = opts.rows or { row(), row({ id = '102', identity = 't102', user = 'bob', in_transaction = true, state = 'idle in transaction' }), row({ id = '7', identity = 'own7', own = true, user = 'me' }) }
  f.can_cancel = opts.can_cancel ~= false
  sessions.setup({
    request = function(conn_id, method, params, cb)
      f.calls[#f.calls + 1] = { conn_id = conn_id, method = method, params = params }
      if method == 'sessions/list' then
        cb(nil, { sessions = vim.deepcopy(f.list), can_cancel = f.can_cancel })
      elseif opts.fail and opts.fail[method] then
        cb(opts.fail[method], nil)
      elseif method == 'sessions/status' then
        cb(nil, { present = true, detail = opts.detail })
      else
        cb(nil, { done = opts.done ~= false })
      end
    end,
    notify = function(msg, level)
      f.notes[#f.notes + 1] = { msg = msg, level = level }
    end,
    confirm = function(text, yes_label, default_yes)
      f.confirms[#f.confirms + 1] = { text = text, yes = yes_label, default_yes = default_yes }
      return f.answer
    end,
  })
  function f.open(env)
    return sessions.open({ id = 'c1', name = 'main', env = env })
  end
  function f.methods()
    return vim.tbl_map(function(c)
      return c.method
    end, f.calls)
  end
  function f.cursor_on_id(buf, id)
    local win = vim.fn.win_findbuf(buf)[1]
    vim.api.nvim_set_current_win(win)
    for line, r in pairs(sessions._state.line_rows[buf]) do
      if r.id == id then
        vim.api.nvim_win_set_cursor(win, { line, 0 })
        return
      end
    end
    error('no line for session ' .. id, 0)
  end
  function f.press(buf, key)
    vim.api.nvim_set_current_buf(buf)
    local m = vim.fn.maparg(key, 'n', false, true)
    expect(m.callback, 'no mapping for ' .. key)
    m.callback()
  end
  return f
end

local tests = {
  {
    -- Verifies: LLR-ADM-11
    'sessions_format_duration',
    function()
      local d = sessions.format_duration
      eq(d(vim.NIL), '', 'null')
      eq(d(0), '0ms', 'zero')
      eq(d(480), '480ms', 'milliseconds')
      eq(d(4200), '4.2s', 'seconds')
      eq(d(59999), '60.0s', 'just under a minute')
      eq(d(185000), '3m05s', 'minutes')
      eq(d(3720000), '1h02m', 'hours')
      eq(d(2 * 86400000 + 3 * 3600000), '2d03h', 'days')
      eq(d(-5), '0ms', 'a clock that ran backwards')
    end,
  },
  {
    -- Verifies: LLR-ADM-11
    'sessions_render_table',
    function()
      local lines, _, first = sessions.render({ sessions = { row(), row({ id = '7', own = true, host = vim.NIL, query = vim.NIL }) }, can_cancel = true }, 'main')
      expect(lines[1]:find('main', 1, true) and lines[1]:find('(2)', 1, true), 'title: ' .. lines[1])
      local header = lines[first - 2]
      for _, name in ipairs({ 'id', 'user', 'database', 'host', 'application', 'state', 'duration', 'wait', 'blocked', 'query' }) do
        expect(header:find(name, 1, true), 'header lacks ' .. name .. ': ' .. header)
      end
      expect(lines[first]:find('alice', 1, true) and lines[first]:find('1.5s', 1, true), 'first row: ' .. lines[first])
      expect(lines[first]:find('SELECT * FROM orders', 1, true), 'the query is one line: ' .. lines[first])
      expect(not lines[first]:find('NULL', 1, true), 'a null cell is blank, not NULL: ' .. lines[first])
      expect(lines[first + 1]:find('●', 1, true), 'own session is marked: ' .. lines[first + 1])
      expect(not lines[first]:find('●', 1, true), 'a foreign session is not marked')
      eq(#lines, first + 1, 'one line per session')
    end,
  },
  {
    -- Verifies: LLR-ADM-11
    'sessions_open_reuses_buffer_and_refreshes',
    function()
      local f = fake()
      local buf = f.open()
      eq(f.methods(), { 'sessions/list' }, 'open lists')
      eq(f.calls[1].conn_id, 'c1', 'for the connection')
      expect(vim.api.nvim_buf_get_name(buf):find('dbbliss://sessions/main', 1, true), 'name')
      eq(vim.bo[buf].modifiable, false, 'read-only')
      f.list = { row({ id = '555', identity = 'x', user = 'carol' }) }
      f.press(buf, 'r')
      eq(f.methods(), { 'sessions/list', 'sessions/list' }, 'r lists again')
      local text = table.concat(vim.api.nvim_buf_get_lines(buf, 0, -1, false), '\n')
      expect(text:find('carol', 1, true) and not text:find('alice', 1, true), 'refresh replaced the rows')
      eq(f.open(), buf, 'a second open reuses the buffer')
    end,
  },
  {
    -- Verifies: LLR-ADM-11
    'sessions_terminate_asks_first_and_defaults_to_no',
    function()
      local f = fake({ confirm = false })
      local buf = f.open()
      f.cursor_on_id(buf, '101')
      f.press(buf, 'x')
      eq(#f.confirms, 1, 'asked once')
      local c = f.confirms[1]
      for _, part in ipairs({ '101', 'alice', 'SELECT * FROM orders', 'terminate' }) do
        expect(c.text:lower():find(part:lower(), 1, true), 'the question names ' .. part .. ': ' .. c.text)
      end
      eq(c.default_yes, false, 'the default is No')
      eq(f.methods(), { 'sessions/list' }, 'declined: nothing sent')
      f.answer = true
      f.press(buf, 'x')
      local sent = f.calls[#f.calls - 1]
      eq(sent.method, 'sessions/terminate', 'confirmed: terminate is sent')
      eq(sent.params, { id = '101', identity = 't101' }, 'with the id and identity seen')
      eq(f.methods()[#f.methods()], 'sessions/list', 'then the list is refreshed')
    end,
  },
  {
    -- Verifies: LLR-ADM-11
    'sessions_confirmation_warns_about_transactions_and_prod',
    function()
      local f = fake({ confirm = false })
      local buf = f.open()
      f.cursor_on_id(buf, '102')
      f.press(buf, 'x')
      expect(f.confirms[1].text:lower():find('roll', 1, true), 'an open transaction is rolled back: ' .. f.confirms[1].text)
      f.cursor_on_id(buf, '101')
      f.press(buf, 'x')
      expect(not f.confirms[2].text:lower():find('roll', 1, true), 'no warning without a transaction')
      expect(not f.confirms[2].text:find('PROD', 1, true), 'no prod wording on a dev connection')
      local pbuf = sessions.open({ id = 'c1', name = 'prodconn', env = 'prod' })
      f.cursor_on_id(pbuf, '101')
      f.press(pbuf, 'c')
      expect(f.confirms[3].text:find('PROD', 1, true), 'prod wording: ' .. f.confirms[3].text)
      expect(f.confirms[3].yes:find('PROD', 1, true), 'the yes label says PROD too: ' .. f.confirms[3].yes)
      eq(f.confirms[3].default_yes, false, 'prod defaults to No')
    end,
  },
  {
    -- Verifies: LLR-ADM-11
    'sessions_own_session_is_never_sent',
    function()
      local f = fake()
      local buf = f.open()
      f.cursor_on_id(buf, '7')
      f.press(buf, 'x')
      f.press(buf, 'c')
      eq(#f.confirms, 0, 'not even asked')
      eq(f.methods(), { 'sessions/list' }, 'nothing sent')
      expect(#f.notes >= 1 and f.notes[#f.notes].msg:find('own', 1, true), 'the user is told why')
    end,
  },
  {
    -- Verifies: LLR-ADM-11
    'sessions_cancel_where_the_engine_cannot',
    function()
      local f = fake({ can_cancel = false })
      local buf = f.open()
      f.cursor_on_id(buf, '101')
      f.press(buf, 'c')
      eq(#f.confirms, 0, 'not asked')
      eq(f.methods(), { 'sessions/list' }, 'nothing sent')
      expect(f.notes[#f.notes].msg:lower():find('terminate', 1, true), 'points to terminate: ' .. f.notes[#f.notes].msg)
    end,
  },
  {
    -- Verifies: LLR-ADM-11
    'sessions_cancel_is_sent_when_confirmed',
    function()
      local f = fake()
      local buf = f.open()
      f.cursor_on_id(buf, '101')
      f.press(buf, 'c')
      expect(f.confirms[1].text:lower():find('cancel', 1, true), 'asks about cancelling')
      eq(f.calls[#f.calls - 1].method, 'sessions/cancel', 'sent')
      expect(f.notes[#f.notes].msg:find('101', 1, true), 'the user is told: ' .. f.notes[#f.notes].msg)
    end,
  },
  {
    -- Verifies: LLR-ADM-11
    'sessions_failures_are_shown',
    function()
      local warn = vim.log.levels.WARN
      local f = fake({ fail = { ['sessions/terminate'] = { code = 1008, message = 'Session 101 no longer exists.' } } })
      local buf = f.open()
      f.cursor_on_id(buf, '101')
      f.press(buf, 'x')
      eq(f.notes[#f.notes - 0].level, warn, 'a user-fixable error is a warning')
      expect(f.notes[#f.notes].msg:find('no longer exists', 1, true), 'with the server text')
      eq(f.methods()[#f.methods()], 'sessions/list', 'and the list is refreshed')

      local g = fake({ fail = { ['sessions/terminate'] = { code = 1005, message = 'boom' } } })
      local b2 = g.open()
      g.cursor_on_id(b2, '101')
      g.press(b2, 'x')
      eq(g.notes[#g.notes].level, vim.log.levels.ERROR, 'a database error is an error')

      local h = fake({ done = false })
      local b3 = h.open()
      h.cursor_on_id(b3, '101')
      h.press(b3, 'x')
      eq(h.notes[#h.notes].level, warn, 'a signal that reached nothing is a warning, not a success')
      expect(h.notes[#h.notes].msg:find('not', 1, true), 'says it was not done: ' .. h.notes[#h.notes].msg)

      local l = fake()
      l.list = {}
      local b4 = l.open()
      l.press(b4, 'x')
      eq(#l.confirms, 0, 'no session under the cursor: nothing to ask')
    end,
  },
  {
    -- Verifies: LLR-ADM-11
    'sessions_status_shows_progress',
    function()
      local f = fake({ detail = 'SPID 102: transaction rollback in progress. Estimated rollback completion: 40%.' })
      local buf = f.open()
      f.cursor_on_id(buf, '102')
      f.press(buf, 's')
      eq(f.calls[#f.calls].method, 'sessions/status', 'asks the backend')
      expect(f.notes[#f.notes].msg:find('rollback in progress', 1, true), 'shows the server text: ' .. f.notes[#f.notes].msg)
      local g = fake()
      local b = g.open()
      g.cursor_on_id(b, '101')
      g.press(b, 's')
      expect(g.notes[#g.notes].msg:find('present', 1, true) or g.notes[#g.notes].msg:find('still', 1, true), 'plain presence: ' .. g.notes[#g.notes].msg)
    end,
  },
  {
    -- Verifies: LLR-ADM-11
    'sessions_enter_shows_the_full_query',
    function()
      local f = fake()
      local buf = f.open()
      f.cursor_on_id(buf, '101')
      f.press(buf, '<CR>')
      local shown = vim.api.nvim_get_current_buf()
      expect(shown ~= buf, 'a new buffer')
      eq(vim.api.nvim_buf_get_lines(shown, 0, -1, false), { 'SELECT *', '  FROM orders' }, 'the query as it is, lines kept')
      eq(vim.bo[shown].filetype, 'sql', 'as SQL')
      vim.cmd('bwipeout!')
    end,
  },
  {
    -- Verifies: LLR-ADM-11
    'sessions_small_cases',
    function()
      -- A failed list is shown, and the buffer stays empty of rows.
      local f = fake({ fail = { ['sessions/list'] = { code = 1005, message = 'cannot list' } } })
      -- (the fake answers list before the failure table is consulted, so build the failing one by hand)
      sessions.setup({
        request = function(_, method, _, cb)
          if method == 'sessions/list' then
            cb({ code = 1005, message = 'cannot list' }, nil)
          end
        end,
        notify = function(msg, level)
          f.notes[#f.notes + 1] = { msg = msg, level = level }
        end,
      })
      local lone = sessions.open({ id = 'c1', name = 'broken' })
      eq(f.notes[#f.notes].level, vim.log.levels.ERROR, 'a list failure is an error')
      expect(f.notes[#f.notes].msg:find('cannot list', 1, true), 'with the server text')
      vim.api.nvim_buf_delete(lone, { force = true })

      -- A blocked session is highlighted; a gone session is said to be gone; no text is said so.
      local g = fake({ rows = { row({ id = '1', blocked_by = '9' }), row({ id = '2', query = vim.NIL }) } })
      local buf = g.open()
      local marks = vim.api.nvim_buf_get_extmarks(buf, vim.api.nvim_create_namespace('dbbliss_sessions'), 0, -1, { details = true })
      local blocked = false
      for _, m in ipairs(marks) do
        blocked = blocked or (m[2] == 4 and m[4].hl_group == 'DbblissError')
      end
      expect(blocked, 'the blocked session is highlighted')
      g.cursor_on_id(buf, '2')
      g.press(buf, '<CR>')
      expect(g.notes[#g.notes].msg:find('no query text', 1, true), 'no text: ' .. g.notes[#g.notes].msg)
      g.press(buf, '<CR>')
      vim.api.nvim_win_set_cursor(0, { 1, 0 })
      g.press(buf, 'x')
      expect(g.notes[#g.notes].msg:find('no session', 1, true), 'a line that is no session: ' .. g.notes[#g.notes].msg)

      -- status: a session that is gone, and a status request that fails.
      local calls = 0
      sessions.setup({
        request = function(_, method, _, cb)
          if method == 'sessions/list' then
            cb(nil, { sessions = { row() }, can_cancel = true })
          else
            calls = calls + 1
            if calls == 1 then
              cb(nil, { present = false })
            else
              cb({ code = 1005, message = 'status failed' }, nil)
            end
          end
        end,
        notify = function(msg, level)
          g.notes[#g.notes + 1] = { msg = msg, level = level }
        end,
      })
      local b2 = g.open()
      g.cursor_on_id(b2, '101')
      g.press(b2, 's')
      expect(g.notes[#g.notes].msg:find('gone', 1, true), 'gone: ' .. g.notes[#g.notes].msg)
      g.press(b2, 's')
      expect(g.notes[#g.notes].msg:find('status failed', 1, true), 'a status failure is shown')

      -- q closes the window.
      g.press(b2, 'q')
      eq(#vim.fn.win_findbuf(b2), 0, 'q closes the sessions window')
    end,
  },
  {
    -- Verifies: LLR-ADM-12
    'sessions_command_dispatch',
    function()
      local dbbliss = require('dbbliss')
      local saved = dbbliss.sessions
      local got
      dbbliss.sessions = function()
        got = true
      end
      dbbliss.command({ args = 'sessions', range = 0 })
      expect(got, 'sessions is not dispatched')
      expect(vim.tbl_contains(dbbliss.complete('', 'Dbbliss '), 'sessions'), 'sessions is not completed')
      eq(dbbliss.complete('ses', 'Dbbliss ses'), { 'sessions' }, 'completion narrows')
      dbbliss.sessions = saved
    end,
  },
}

local failed = 0
for _, t in ipairs(tests) do
  local ok, err = pcall(t[2])
  if ok then
    io.stdout:write(('%-46s PASS\n'):format(t[1]))
  else
    failed = failed + 1
    io.stdout:write(('%-46s FAIL  %s\n'):format(t[1], tostring(err)))
  end
end
io.stdout:write(('\n%d tests, %d failed\n'):format(#tests, failed))
os.exit(failed == 0 and 0 or 1)
