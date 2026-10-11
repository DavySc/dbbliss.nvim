-- Lua client tests: the client side of spec/quint/client.qnt, with a stub in place of the backend
-- process. The stub records requests; each test answers them in the order it wants.
--
--   nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/client_test.lua
--
-- Exits 1 if any test fails.

local stub = { requests = {}, handlers = {} }

package.loaded['dbbliss.backend'] = {
  new = function()
    local b = { exited = false, pid = 0 }
    function b:on(method, fn)
      stub.handlers[method] = fn
    end
    function b:on_exit(fn)
      stub.exit_handler = fn
    end
    function b:start() end
    function b:shutdown_sync()
      return true
    end
    function b:request(method, params, cb)
      if method ~= 'initialize' then
        table.insert(stub.requests, { method = method, params = params, cb = cb or function() end })
      end
    end
    return b
  end,
}

local dbbliss = require('dbbliss')
vim.notify = function() end
-- Every rollback prompt is answered "Rollback".
-- (stub.confirm_answer picks another choice: 2 is Cancel.)
vim.fn.confirm = function()
  stub.confirms = stub.confirms + 1
  return stub.confirm_answer or 1
end

local next_connection = 0

--- Answers the oldest unanswered request for `method` the way the backend would.
local function answer(method)
  for i, r in ipairs(stub.requests) do
    if r.method == method then
      table.remove(stub.requests, i)
      if method == 'connect' then
        next_connection = next_connection + 1
        local id = 'c' .. next_connection
        stub.open[id] = true
        r.cb(nil, { connection_id = id, engine = 'postgres', server_session_id = tostring(100 + next_connection) })
      elseif method == 'disconnect' then
        local id = r.params.connection_id
        if stub.server_tx[id] and not r.params.rollback then
          r.cb({ code = 1003, message = 'The connection has an open transaction (active).' }, nil)
        else
          stub.open[id] = nil
          stub.server_tx[id] = nil
          r.cb(nil, { rolled_back = r.params.rollback })
        end
      end
      return true
    end
  end
  return false
end

local function answer_all()
  while answer('connect') or answer('disconnect') do
  end
end

local function reset()
  dbbliss.setup({ connections = { a = { engine = 'postgres', connection_string = 'Host=x' } } })
  dbbliss._state.connections = {}
  dbbliss._state.current = nil
  dbbliss._state.recent = {}
  dbbliss._state.backend = nil
  dbbliss._state.queries = {}
  dbbliss._state.scripts = {}
  stub.handlers = {}
  stub.requests = {}
  stub.open = {}
  stub.server_tx = {}
  stub.confirms = 0
  stub.confirm_answer = nil
  next_connection = 0
  require('dbbliss.completion').reset()
end

--- NoOrphanSession: every connection the backend has open is one the client knows about.
local function check_no_orphan()
  local known = {}
  for _, c in pairs(dbbliss._state.connections) do
    known[c.id] = true
  end
  for id in pairs(stub.open) do
    if not known[id] then
      error(('backend connection %s is open but the client no longer knows it (orphaned session)'):format(id), 0)
    end
  end
end

--- Removes and returns the oldest unanswered request for `method`, without answering it.
local function take(method)
  for i, r in ipairs(stub.requests) do
    if r.method == method then
      table.remove(stub.requests, i)
      return r
    end
  end
end

local function stmt(text, line, repeat_n)
  return { text = text, start = { line = line, col = 0 }, ['end'] = { line = line, col = #text }, ['repeat'] = repeat_n or 1 }
end

--- A fresh buffer holding `lines`, current, with a connection open.
local function script_setup(lines)
  local b = vim.api.nvim_create_buf(false, true)
  vim.api.nvim_set_current_buf(b)
  vim.api.nvim_buf_set_lines(b, 0, -1, false, lines)
  dbbliss.connect('a')
  answer_all()
  return b
end

--- The backend accepts the oldest execute, then reports how it ended.
local function finish_query(done)
  local r = take('execute')
  if not r then
    error('no execute request was sent', 0)
  end
  r.cb(nil, { query_id = r.params.query_id })
  done = vim.tbl_extend('force', { query_id = r.params.query_id, status = 'completed', elapsed_ms = 1, transaction = 'none' }, done or {})
  stub.handlers['query/done'](done)
  return r
end

local function expect(cond, msg)
  if not cond then
    error(msg, 0)
  end
end

--- A completion cache whose names are loaded, as after a first completion request.
local function primed_cache()
  return { gen = 0, columns = {}, names = { state = 'ready', waiters = {}, items = {}, gen = 0, at = 0 } }
end

--- Two (or more) configured connections named a, b, ... for the case that needs them.
local function two_connections(extra)
  local conns = {}
  for _, n in ipairs(vim.list_extend({ 'a', 'b' }, extra or {})) do
    conns[n] = { engine = 'postgres', connection_string = 'Host=x' }
  end
  dbbliss.setup({ connections = conns })
end

--- Replaces vim.notify with a collector; returns the list it fills.
local function capture_notify()
  local msgs = {}
  vim.notify = function(m)
    msgs[#msgs + 1] = m
  end
  return msgs
end

--- A listed, named buffer (as a file would be) holding `lines`, made current.
local function sql_buffer(lines)
  local b = vim.api.nvim_create_buf(true, false)
  vim.api.nvim_set_current_buf(b)
  vim.api.nvim_buf_set_lines(b, 0, -1, false, lines)
  return b
end

--- Runs the whole buffer and returns the connection id of the execute it sent (nil: nothing was sent).
local function run_and_see_connection()
  local read = stmt('select 1;', 0)
  read.kind = 'read'
  dbbliss.run('buffer')
  local split = take('script/split')
  if not split then
    return nil
  end
  split.cb(nil, { statements = { read } })
  local r = take('execute')
  return r and r.params.connection_id or nil
end

local tests = {
  {
    -- Verifies: none (basic lifecycle; no requirement beyond those of the cases below)
    'connect_disconnect',
    '',
    function()
      dbbliss.connect('a')
      answer_all()
      dbbliss.disconnect()
      answer_all()
      check_no_orphan()
    end,
  },
  {
    -- The client heard nothing of the transaction (typed BEGIN still running, say); the backend
    -- refuses the plain disconnect and the user is asked, not left with an error.
    -- Verifies: HLR-TX-1
    'disconnect_refused_asks_user',
    '',
    function()
      dbbliss.connect('a')
      answer_all()
      stub.server_tx.c1 = true
      dbbliss.disconnect()
      answer_all()
      if stub.confirms ~= 1 then
        error(('expected one rollback prompt, got %d'):format(stub.confirms), 0)
      end
      if stub.open.c1 or dbbliss._state.connections.a then
        error('the connection should be closed after the user chose rollback', 0)
      end
      check_no_orphan()
    end,
  },
  {
    -- Verifies: HLR-CONC-3
    'connect_twice_while_connected',
    '3: NoOrphanSession',
    function()
      dbbliss.connect('a')
      answer_all()
      dbbliss.connect('a')
      answer_all()
      check_no_orphan()
    end,
  },
  {
    -- Verifies: HLR-CONC-3
    'connect_twice_while_connecting',
    '3: NoOrphanSession',
    function()
      dbbliss.connect('a')
      dbbliss.connect('a')
      answer_all()
      check_no_orphan()
    end,
  },
  {
    -- Verifies: LLR-UI-6
    'connection_disconnecting_the_current_one_falls_back_to_the_only_other',
    '',
    function()
      two_connections()
      dbbliss.connect('a')
      answer_all()
      dbbliss.connect('b')
      answer_all()
      expect(dbbliss._state.current == 'b', 'the last connect is current')
      dbbliss.command({ args = 'disconnect', range = 0 })
      answer_all()
      expect(dbbliss._state.connections.b == nil, 'b is closed')
      expect(dbbliss._state.current == 'a', 'a is the only one left and becomes current: ' .. tostring(dbbliss._state.current))
      check_no_orphan()
    end,
  },
  {
    -- Verifies: LLR-UI-6
    'connection_disconnecting_walks_back_through_the_order_they_were_current',
    '',
    function()
      two_connections({ 'c' })
      for _, n in ipairs({ 'a', 'b', 'c' }) do
        dbbliss.connect(n)
        answer_all()
      end
      dbbliss.command({ args = 'use a', range = 0 })
      dbbliss.command({ args = 'use c', range = 0 }) -- order now b, a, c
      local seen = {}
      for _ = 1, 3 do
        dbbliss.command({ args = 'disconnect', range = 0 })
        answer_all()
        seen[#seen + 1] = tostring(dbbliss._state.current)
      end
      expect(table.concat(seen, ',') == 'a,b,nil', 'each disconnect falls back to the one before: ' .. table.concat(seen, ','))
      check_no_orphan()
    end,
  },
  {
    -- Verifies: LLR-UI-6
    'connection_disconnect_by_name_keeps_the_current_one',
    '',
    function()
      two_connections({ 'c' })
      for _, n in ipairs({ 'a', 'b', 'c' }) do
        dbbliss.connect(n)
        answer_all()
      end
      local closing = capture_notify()
      dbbliss.command({ args = 'disconnect a', range = 0 })
      answer_all()
      expect(not table.concat(closing, '\n'):find('default is now', 1, true), 'no switch is announced: ' .. vim.inspect(closing))
      expect(dbbliss._state.connections.a == nil, 'a is closed')
      expect(dbbliss._state.connections.b ~= nil and dbbliss._state.current == 'c', 'c stays current: ' .. tostring(dbbliss._state.current))
      local msgs = capture_notify()
      dbbliss.command({ args = 'disconnect a', range = 0 })
      expect(table.concat(msgs, '\n'):find('not connected', 1, true), 'a name that is not open is refused: ' .. vim.inspect(msgs))
      check_no_orphan()
    end,
  },
  {
    -- Verifies: LLR-UI-5
    'connection_use_switches_the_current_one_and_refuses_unknown',
    '',
    function()
      two_connections()
      dbbliss.connect('a')
      answer_all()
      dbbliss.connect('b')
      answer_all()
      dbbliss.command({ args = 'use a', range = 0 })
      expect(dbbliss._state.current == 'a', 'use switches to a')
      expect(dbbliss.statusline(true):find('a', 1, true) == 1, 'the statusline follows: ' .. dbbliss.statusline(true))
      local msgs = capture_notify()
      dbbliss.command({ args = 'use nope', range = 0 })
      expect(dbbliss._state.current == 'a', 'an unknown name changes nothing')
      expect(table.concat(msgs, '\n'):find('not connected', 1, true), 'and says so: ' .. vim.inspect(msgs))
      local got = dbbliss.complete('', 'Dbbliss use ')
      expect(vim.deep_equal(got, { 'a', 'b' }), 'completes open connections: ' .. vim.inspect(got))
      dbbliss.command({ args = 'disconnect b', range = 0 })
      answer_all()
      expect(vim.deep_equal(dbbliss.complete('', 'Dbbliss use '), { 'a' }), 'a closed connection is no longer offered')
    end,
  },
  {
    -- Verifies: HLR-CONC-3
    'connection_connect_of_an_open_name_makes_it_current_without_a_second_session',
    '',
    function()
      two_connections()
      dbbliss.connect('a')
      answer_all()
      dbbliss.connect('b')
      answer_all()
      local calls = 0
      dbbliss.connect('a', function(err, conn)
        calls = calls + 1
        expect(err == nil and conn and conn.id == 'c1', 'the callback gets the open connection: ' .. vim.inspect(err))
      end)
      answer_all()
      expect(calls == 1, 'callback called once')
      expect(dbbliss._state.current == 'a', 'a is current again')
      expect(vim.tbl_count(stub.open) == 2, 'no third session was opened')
      check_no_orphan()
    end,
  },
  {
    -- Verifies: LLR-UI-7
    'buffer_stays_on_its_connection_when_another_buffer_connects',
    '28: same rule for exec, completion and statusline',
    function()
      two_connections()
      local b1 = sql_buffer({ 'select 1;' })
      dbbliss.connect('a')
      answer_all()
      local b2 = sql_buffer({ 'select 2;' })
      dbbliss.connect('b')
      answer_all()
      expect(dbbliss._state.current == 'b', 'b is the default')
      vim.api.nvim_set_current_buf(b1)
      expect(dbbliss.statusline(true):find('a', 1, true) == 1, 'the statusline shows a here: ' .. dbbliss.statusline(true))
      expect(dbbliss._completion_connection(b1).id == 'c1', 'completion reads from a')
      expect(run_and_see_connection() == 'c1', 'the statement ran on a')
      vim.api.nvim_set_current_buf(b2)
      expect(dbbliss.statusline(true):find('b', 1, true) == 1, 'and b here: ' .. dbbliss.statusline(true))
      expect(run_and_see_connection() == 'c2', 'the statement in the other buffer ran on b')
    end,
  },
  {
    -- Verifies: LLR-UI-5
    'buffer_use_rebinds_this_buffer_only',
    '',
    function()
      two_connections()
      local b1 = sql_buffer({ 'select 1;' })
      dbbliss.connect('a')
      answer_all()
      local b2 = sql_buffer({ 'select 2;' })
      dbbliss.connect('b')
      answer_all()
      vim.api.nvim_set_current_buf(b1)
      dbbliss.command({ args = 'use b', range = 0 })
      expect(vim.b[b1].dbbliss_connection == 'b' and vim.b[b2].dbbliss_connection == 'b', 'b1 is rebound')
      dbbliss.command({ args = 'use -', range = 0 })
      expect(vim.b[b1].dbbliss_connection == 'a' and dbbliss._state.current == 'a', 'use - goes back to a: ' .. tostring(vim.b[b1].dbbliss_connection))
      expect(vim.b[b2].dbbliss_connection == 'b', 'the other buffer is untouched')
      local msgs = capture_notify()
      dbbliss.command({ args = 'use -', range = 0 })
      dbbliss.command({ args = 'use -', range = 0 })
      expect(dbbliss._state.current == 'a' and #msgs == 2, 'use - toggles between the last two: ' .. tostring(dbbliss._state.current))
    end,
  },
  {
    -- Verifies: LLR-UI-5
    'buffer_use_dash_without_a_previous_connection_is_refused',
    '',
    function()
      sql_buffer({ 'select 1;' })
      dbbliss.connect('a')
      answer_all()
      local msgs = capture_notify()
      dbbliss.command({ args = 'use -', range = 0 })
      expect(dbbliss._state.current == 'a', 'nothing changed')
      expect(#msgs == 1 and msgs[1]:find('previous', 1, true), 'and it says why: ' .. vim.inspect(msgs))
    end,
  },
  {
    -- Verifies: LLR-UI-8
    'buffer_unbound_binds_to_the_default_on_first_run_and_says_so',
    '',
    function()
      two_connections()
      sql_buffer({ 'x' })
      dbbliss.connect('a')
      answer_all()
      local b = sql_buffer({ 'select 1;' }) -- opened after connecting: unbound
      expect(vim.b[b].dbbliss_connection == nil, 'a new buffer is not bound')
      local msgs = capture_notify()
      expect(run_and_see_connection() == 'c1', 'it ran on the default')
      expect(vim.b[b].dbbliss_connection == 'a', 'and is bound now')
      expect(table.concat(msgs, '\n'):find('this buffer now uses a', 1, true), 'and said so: ' .. vim.inspect(msgs))
    end,
  },
  {
    -- Verifies: LLR-UI-8
    'buffer_unbound_on_a_prod_default_asks_and_declining_runs_nothing',
    '',
    function()
      dbbliss.setup({ connections = { a = { engine = 'postgres', connection_string = 'Host=x', env = 'prod' } } })
      sql_buffer({ 'x' })
      dbbliss.connect('a')
      answer_all()
      local b = sql_buffer({ 'select 1;' })
      stub.confirm_answer = 2
      expect(run_and_see_connection() == nil, 'nothing ran')
      expect(stub.confirms == 1, 'one question: ' .. stub.confirms)
      expect(vim.b[b].dbbliss_connection == nil, 'and nothing was bound')
      stub.confirm_answer = 1
      expect(run_and_see_connection() == 'c1', 'accepted: it runs')
      expect(vim.b[b].dbbliss_connection == 'a', 'and binds')
    end,
  },
  {
    -- Verifies: LLR-UI-8
    'buffer_unbound_without_a_default_is_not_connected',
    '',
    function()
      local b = sql_buffer({ 'select 1;' })
      local msgs = capture_notify()
      expect(run_and_see_connection() == nil, 'nothing ran')
      expect(table.concat(msgs, '\n'):find('not connected', 1, true), 'says so: ' .. vim.inspect(msgs))
      expect(vim.b[b].dbbliss_connection == nil, 'nothing bound')
    end,
  },
  {
    -- Verifies: LLR-UI-9
    'buffer_disconnect_unbinds_its_buffers_and_never_retargets_them',
    '',
    function()
      two_connections()
      local b1 = sql_buffer({ 'select 1;' })
      dbbliss.connect('a')
      answer_all()
      local b2 = sql_buffer({ 'select 2;' })
      dbbliss.connect('b')
      answer_all()
      dbbliss.command({ args = 'disconnect b', range = 0 })
      answer_all()
      expect(vim.b[b2].dbbliss_connection == nil, 'the buffer of the closed connection is unbound')
      expect(vim.b[b1].dbbliss_connection == 'a', 'the other keeps its connection')
      dbbliss.connect('b')
      answer_all()
      vim.api.nvim_set_current_buf(b1)
      expect(run_and_see_connection() == 'c1', 'b1 still runs on a, though b is the default again')
    end,
  },
  {
    -- Verifies: LLR-UI-9
    'buffer_backend_exit_unbinds',
    '',
    function()
      local b = sql_buffer({ 'select 1;' })
      dbbliss.connect('a')
      answer_all()
      expect(vim.b[b].dbbliss_connection == 'a', 'bound')
      stub.exit_handler(1, 0)
      expect(vim.b[b].dbbliss_connection == nil, 'the backend ended: unbound')
    end,
  },
  {
    -- Verifies: LLR-UI-7
    'buffer_plugin_buffers_are_not_bound',
    '',
    function()
      local b = vim.api.nvim_create_buf(false, true)
      vim.api.nvim_buf_set_name(b, 'dbbliss://schema')
      vim.api.nvim_set_current_buf(b)
      dbbliss.connect('a')
      answer_all()
      expect(vim.b[b].dbbliss_connection == nil, 'not bound')
    end,
  },
  {
    -- Verifies: LLR-UI-8
    'buffer_unbound_begin_binds_to_the_default',
    '',
    function()
      sql_buffer({ 'x' })
      dbbliss.connect('a')
      answer_all()
      local b = sql_buffer({ 'y' })
      dbbliss.begin()
      expect(take('transaction/begin') ~= nil, 'begin was sent')
      expect(vim.b[b].dbbliss_connection == 'a', 'and the buffer is bound')
    end,
  },
  {
    -- Verifies: LLR-UI-7
    'buffer_transaction_commands_act_on_the_buffers_connection',
    '',
    function()
      two_connections()
      local b1 = sql_buffer({ 'x' })
      dbbliss.connect('a')
      answer_all()
      sql_buffer({ 'y' })
      dbbliss.connect('b')
      answer_all()
      vim.api.nvim_set_current_buf(b1)
      dbbliss.begin()
      local r = take('transaction/begin')
      expect(r and r.params.connection_id == 'c1', 'begin went to a: ' .. vim.inspect(r and r.params))
    end,
  },
  {
    -- Verifies: LLR-UI-6
    'buffer_status_lists_the_most_recent_first_and_marks_default_and_this_buffer',
    '',
    function()
      two_connections({ 'c' })
      sql_buffer({ 'x' })
      for _, n in ipairs({ 'a', 'b', 'c' }) do
        dbbliss.connect(n)
        answer_all()
      end
      sql_buffer({ 'y' })
      dbbliss.command({ args = 'use a', range = 0 })
      local msgs = capture_notify()
      dbbliss.status()
      local text = msgs[1]:gsub('^dbbliss: ', '')
      local lines = vim.split(text, '\n', { plain = true })
      local names = {}
      for _, l in ipairs(lines) do
        names[#names + 1] = l:match('^[> ][* ] (%w+) ')
      end
      expect(table.concat(names, ',') == 'a,c,b', 'most recent first: ' .. text)
      expect(lines[1]:sub(1, 2) == '>*', 'a is this buffer\'s and the default: ' .. lines[1])
      expect(lines[2]:sub(1, 2) == '  ', 'c is neither: ' .. lines[2])
    end,
  },
  {
    -- Verifies: HLR-SCRIPT-1
    'pick_statement_under_cursor',
    '',
    function()
      local script = require('dbbliss.script')
      local a, b = stmt('select 1;', 0), stmt('select 2;', 2)
      expect(script.pick({ a, b }, 0, 3) == a, 'cursor inside the first statement')
      expect(script.pick({ a, b }, 1, 0) == a, 'a blank line picks the statement above')
      expect(script.pick({ a, b }, 2, 0) == b, 'the start of the second statement')
      expect(script.pick({ a, b }, 9, 0) == b, 'below the last statement picks the last')
      expect(script.pick({ { text = 'x', start = { line = 5, col = 0 } } }, 0, 0).text == 'x', 'above the first picks the first')
      expect(script.pick({}, 0, 0) == nil, 'no statements')
    end,
  },
  {
    -- Verifies: HLR-SCRIPT-2
    'run_buffer_stops_at_first_error',
    '',
    function()
      local b = script_setup({ 'select 1;', 'select 2;', 'select 3;' })
      dbbliss.run('buffer')
      take('script/split').cb(nil, { statements = { stmt('select 1;', 0), stmt('select 2;', 1), stmt('select 3;', 2) } })
      expect(finish_query().params.line_offset == 0, 'first statement starts on buffer line 0')
      local second = finish_query({ status = 'error', error = { message = 'boom', line = 1, buffer_line = 2 } })
      expect(second.params.line_offset == 1, 'second statement starts on buffer line 1')
      expect(take('execute') == nil, 'a statement was sent after the failed one')
      expect(next(dbbliss._state.scripts) == nil, 'the script is still marked as running')
      local diagnostics = vim.diagnostic.get(b)
      expect(#diagnostics == 1 and diagnostics[1].lnum == 1, 'the error line was not marked in the source buffer')
    end,
  },
  {
    -- Verifies: HLR-SCRIPT-1
    'run_repeats_a_go_count',
    '',
    function()
      script_setup({ 'select 1', 'go 2' })
      dbbliss.run('buffer')
      take('script/split').cb(nil, { statements = { stmt('select 1', 0, 2) } })
      finish_query()
      finish_query()
      expect(take('execute') == nil, 'more than two executions for GO 2')
      expect(next(dbbliss._state.scripts) == nil, 'the script is still marked as running')
    end,
  },
  {
    -- Verifies: HLR-SCRIPT-1
    'run_statement_under_cursor',
    '',
    function()
      script_setup({ 'select 1;', 'select 2;', 'select 3;' })
      vim.api.nvim_win_set_cursor(0, { 2, 3 })
      dbbliss.run('statement')
      take('script/split').cb(nil, { statements = { stmt('select 1;', 0), stmt('select 2;', 1), stmt('select 3;', 2) } })
      local r = finish_query()
      expect(r.params.sql == 'select 2;' and r.params.line_offset == 1, 'expected the second statement, got ' .. tostring(r.params.sql))
      expect(take('execute') == nil, 'more than the one statement under the cursor was run')
    end,
  },
  {
    -- Verifies: HLR-SCRIPT-3
    'run_range_offsets_by_its_first_line',
    '',
    function()
      script_setup({ 'select 1;', 'select 2;', 'select 3;', 'select 4;' })
      dbbliss.run('range', { 2, 3 })
      local split = take('script/split')
      expect(split.params.text == 'select 2;\nselect 3;', 'the split text is the range only: ' .. tostring(split.params.text))
      split.cb(nil, { statements = { stmt('select 2;', 0), stmt('select 3;', 1) } })
      expect(finish_query().params.line_offset == 1, 'first statement of the range is buffer line 1')
      expect(finish_query().params.line_offset == 2, 'second statement of the range is buffer line 2')
    end,
  },
  {
    -- Verifies: HLR-CONC-1
    'run_refused_while_a_script_runs',
    '',
    function()
      script_setup({ 'select 1;' })
      dbbliss.run('buffer')
      dbbliss.run('buffer')
      expect(take('script/split') ~= nil, 'the first run sent no split request')
      expect(take('script/split') == nil, 'a second script was started while the first runs')
    end,
  },
  {
    -- query/paused shows the hint; asking for more sends fetch with the query's id and the window.
    -- Verifies: HLR-PAGE-1
    'paused_query_fetches_more',
    '',
    function()
      script_setup({ 'select 1;' })
      dbbliss.run('buffer')
      take('script/split').cb(nil, { statements = { stmt('select 1;', 0) } })
      local r = take('execute')
      expect(r.params.window == 1000, 'execute carries the row window: ' .. tostring(r.params.window))
      r.cb(nil, { query_id = r.params.query_id })
      stub.handlers['query/resultset']({ query_id = r.params.query_id, result_set = 0, columns = { { name = 'n', type = 'int4' } } })
      stub.handlers['query/rows']({ query_id = r.params.query_id, result_set = 0, rows = { { 1 }, { 2 } } })
      stub.handlers['query/paused']({ query_id = r.params.query_id, rows_sent = 2 })
      dbbliss.fetch_more()
      local f = take('fetch')
      expect(f and f.params.query_id == r.params.query_id and f.params.rows == 1000, 'no fetch request for the paused query')
    end,
  },
  {
    -- Cancelling at the end of a window: rows already on their way are counted, not appended.
    -- Verifies: HLR-PAGE-2
    'cancel_while_paused_counts_late_rows',
    '',
    function()
      script_setup({ 'select 1;' })
      dbbliss.run('buffer')
      take('script/split').cb(nil, { statements = { stmt('select 1;', 0) } })
      local r = take('execute')
      local qid = r.params.query_id
      r.cb(nil, { query_id = qid })
      stub.handlers['query/resultset']({ query_id = qid, result_set = 0, columns = { { name = 'n', type = 'int4' } } })
      stub.handlers['query/rows']({ query_id = qid, result_set = 0, rows = { { 1 } } })
      stub.handlers['query/paused']({ query_id = qid, rows_sent = 1 })
      dbbliss.cancel()
      stub.handlers['query/rows']({ query_id = qid, result_set = 0, rows = { { 2 }, { 3 }, { 4 } } })
      stub.handlers['query/done']({ query_id = qid, status = 'cancelled', elapsed_ms = 5, transaction = 'none' })
      local text = table.concat(require('dbbliss.results').lines(), '\n')
      expect(not text:find(' 4', 1, true), 'a late row was appended to the table:\n' .. text)
      expect(text:find('3 more rows had already arrived', 1, true), 'the late rows were not reported:\n' .. text)
    end,
  },
  {
    -- Prod: statements that can change data are confirmed once, before anything runs.
    -- Verifies: HLR-PROD-1
    'prod_confirms_writes_before_running',
    '',
    function()
      dbbliss.setup({ connections = { a = { engine = 'postgres', connection_string = 'Host=x', env = 'prod' } } })
      script_setup({ 'select 1;', 'delete from t;' })
      stub.confirm_answer = 2
      dbbliss.run('buffer')
      local read = stmt('select 1;', 0)
      read.kind = 'read'
      local write = stmt('delete from t;', 1)
      write.kind = 'write'
      take('script/split').cb(nil, { statements = { read, write } })
      expect(stub.confirms == 1, 'expected one prompt, got ' .. stub.confirms)
      expect(take('execute') == nil, 'a statement ran although the prompt was cancelled')
      expect(next(dbbliss._state.scripts) == nil, 'the script is still marked as running')

      stub.confirm_answer = 1
      dbbliss.run('buffer')
      take('script/split').cb(nil, { statements = { read, write } })
      expect(stub.confirms == 2, 'expected a second prompt')
      expect(take('execute') ~= nil, 'nothing ran after the prompt was accepted')
    end,
  },
  {
    -- Verifies: HLR-PROD-1
    'prod_does_not_ask_for_reads_or_on_other_environments',
    '',
    function()
      dbbliss.setup({
        connections = {
          a = { engine = 'postgres', connection_string = 'Host=x', env = 'prod' },
          b = { engine = 'postgres', connection_string = 'Host=x', env = 'dev' },
        },
      })
      script_setup({ 'select 1;' })
      local read = stmt('select 1;', 0)
      read.kind = 'read'
      dbbliss.run('buffer')
      take('script/split').cb(nil, { statements = { read } })
      expect(stub.confirms == 0, 'asked about a read on prod')
      expect(take('execute') ~= nil, 'the read did not run')
      -- Unclassified counts as a write.
      dbbliss._state.scripts = {}
      dbbliss.run('buffer')
      take('script/split').cb(nil, { statements = { stmt('select 1;', 0) } })
      expect(stub.confirms == 1, 'an unclassified statement was not asked about')
    end,
  },
  {
    -- Verifies: HLR-TX-5
    'quit_with_open_transaction_asks_and_can_be_cancelled',
    '',
    function()
      dbbliss.connect('a')
      answer_all()
      dbbliss._state.connections.a.transaction = 'active'
      stub.confirm_answer = 2
      dbbliss.on_exit_pre()
      local guard = dbbliss._state.guard_buf
      expect(stub.confirms == 1, 'no prompt')
      expect(guard and vim.bo[guard].modified, 'Cancel left no modified buffer to stop the quit')
      -- The real thing: :qa must fail while the guard is up.
      local ok, err = pcall(vim.cmd, 'qa')
      expect(not ok and tostring(err):find('E37', 1, true), 'qa was not stopped: ' .. tostring(err))
      vim.wait(50, function()
        return false
      end)
      expect(not vim.bo[guard].modified, 'the guard stayed modified, so quitting would stay blocked')
      -- Accepting leaves the quit alone.
      stub.confirm_answer = 1
      dbbliss.on_exit_pre()
      expect(not vim.bo[guard].modified, 'accepting still raised the guard')
    end,
  },
  {
    -- The report of a typed BEGIN is not here yet, so the client's view says "none" while the
    -- server holds a transaction (spec/quint/client.qnt, QuitViewOnly).
    -- Verifies: HLR-TX-5
    'quit_while_a_query_runs_asks',
    '',
    function()
      dbbliss.connect('a')
      answer_all()
      dbbliss._state.queries.q1 = { connection = 'a', rows = 0 }
      dbbliss.on_exit_pre()
      expect(stub.confirms == 1, 'no prompt while a query is running')
    end,
  },
  {
    -- Verifies: HLR-TX-5
    'quit_while_a_transaction_call_is_in_flight_asks',
    '',
    function()
      dbbliss.connect('a')
      answer_all()
      dbbliss.begin()
      dbbliss.on_exit_pre()
      expect(stub.confirms == 1, 'no prompt while begin is in flight')
      local r = take('transaction/begin')
      r.cb(nil, { transaction = 'active' })
      stub.confirms = 0
      dbbliss._state.connections.a.transaction = 'none'
      dbbliss.on_exit_pre()
      expect(stub.confirms == 0, 'still asking after the call came back')
    end,
  },
  {
    -- Verifies: HLR-TX-5
    'quit_without_transaction_does_not_ask',
    '',
    function()
      dbbliss.connect('a')
      answer_all()
      dbbliss.on_exit_pre()
      expect(stub.confirms == 0, 'asked although nothing is open')
    end,
  },
  {
    -- Verifies: HLR-TX-6
    'closing_a_buffer_with_an_open_transaction_asks',
    '',
    function()
      local b = script_setup({ 'select 1;' })
      vim.b[b].dbbliss_connection = 'a'
      dbbliss._state.connections.a.transaction = 'active'
      stub.confirm_answer = 2
      local ok = pcall(vim.cmd, 'bwipeout! ' .. b)
      expect(not ok and vim.api.nvim_buf_is_valid(b), 'the buffer was closed although Cancel was chosen')
      stub.confirm_answer = 1
      ok = pcall(vim.cmd, 'bwipeout! ' .. b)
      expect(ok and not vim.api.nvim_buf_is_valid(b), 'the buffer was not closed after accepting')
    end,
  },
  {
    -- Verifies: LLR-UI-3
    'statusline_shows_connection_env_transaction_and_running',
    '',
    function()
      dbbliss.setup({ connections = { a = { engine = 'postgres', connection_string = 'Host=x', env = 'prod' } } })
      expect(dbbliss.statusline(true) == '', 'not empty without a connection')
      dbbliss.connect('a')
      answer_all()
      expect(dbbliss.statusline(true) == 'a  PROD ', 'idle: ' .. dbbliss.statusline(true))
      dbbliss._state.connections.a.transaction = 'active'
      expect(dbbliss.statusline(true) == 'a  PROD  TX', 'transaction: ' .. dbbliss.statusline(true))
      dbbliss._state.connections.a.transaction = 'aborted'
      dbbliss._state.queries.q1 = { connection = 'a', rows = 0 }
      expect(dbbliss.statusline(true) == 'a  PROD  TX aborted ⏵ running', 'aborted and running: ' .. dbbliss.statusline(true))
      dbbliss._state.queries.q1.paused = true
      expect(dbbliss.statusline(true):find('⏸ paused', 1, true), 'paused')
      expect(dbbliss.statusline():find('%#DbblissProd#', 1, true), 'prod is highlighted')
    end,
  },
  {
    -- Verifies: HLR-CRED-1
    'password_references_are_validated',
    '',
    function()
      local function setup_with(password)
        return pcall(dbbliss.setup, { connections = { x = { engine = 'postgres', connection_string = 'Host=x', password = password } } })
      end
      expect(not setup_with('literal'), 'a literal password was accepted')
      expect(not setup_with({}), 'an empty reference was accepted')
      expect(not setup_with({ env = 'A', pass = 'b' }), 'two references were accepted')
      expect(not setup_with({ vault = 'x' }), 'an unknown reference was accepted')
      for _, ok in ipairs({ { env = 'A' }, { credman = 'target' }, { pass = 'a/b' }, { libsecret = { service = 'x' } } }) do
        expect(setup_with(ok), 'rejected ' .. vim.inspect(ok))
      end
      expect(setup_with(nil), 'integrated auth (no password) was rejected')
    end,
  },
  {
    -- Verifies: HLR-SCRIPT-2
    'run_not_started_ends_the_script',
    '',
    function()
      script_setup({ 'select 1;', 'select 2;' })
      dbbliss.run('buffer')
      take('script/split').cb(nil, { statements = { stmt('select 1;', 0), stmt('select 2;', 1) } })
      take('execute').cb({ code = 1002, message = 'busy' }, nil)
      expect(take('execute') == nil, 'the next statement was sent after one was refused')
      expect(next(dbbliss._state.scripts) == nil, 'the script is still marked as running')
    end,
  },
  {
    -- Verifies: LLR-PLAN-12
    'plan_command_plans_the_statement_under_the_cursor',
    '',
    function()
      script_setup({ 'select 1;', 'select 2;' })
      vim.api.nvim_win_set_cursor(0, { 2, 0 })
      dbbliss.plan('estimated', 'statement')
      take('script/split').cb(nil, { statements = { stmt('select 1;', 0), stmt('select 2;', 1) } })
      local r = take('plan/start')
      expect(r ~= nil, 'no plan/start was sent')
      expect(r.params.connection_id == 'c1' and r.params.sql == 'select 2;' and r.params.mode == 'estimated', 'wrong request: ' .. vim.inspect(r.params))
      expect(r.params.confirm_execute == nil, 'an estimate carries no confirm_execute')
      expect(next(dbbliss._state.scripts) == nil, 'planning must not leave the connection marked as running a script')
      expect(take('execute') == nil, 'planning ran the statement')
    end,
  },
  {
    -- Verifies: LLR-PLAN-12
    'plan_command_takes_one_statement',
    '',
    function()
      script_setup({ 'select 1;', 'select 2;' })
      dbbliss.plan('estimated', 'range', { 1, 2 })
      take('script/split').cb(nil, { statements = { stmt('select 1;', 0), stmt('select 2;', 1) } })
      expect(take('plan/start') == nil, 'two statements were planned as one')
      expect(next(dbbliss._state.scripts) == nil, 'the script mark is released')
    end,
  },
  {
    -- Verifies: LLR-PLAN-12
    'plan_actual_of_a_write_on_prod_asks_once_and_not_for_the_run',
    '',
    function()
      dbbliss.setup({ connections = { a = { engine = 'postgres', connection_string = 'Host=x', env = 'prod' } } })
      script_setup({ 'delete from t;' })
      local write = stmt('delete from t;', 0)
      write.kind = 'write'
      stub.confirm_answer = 2
      dbbliss.plan('actual', 'statement')
      take('script/split').cb(nil, { statements = { write } })
      expect(stub.confirms == 1, 'expected one question (about the plan), got ' .. stub.confirms)
      expect(take('plan/start') == nil, 'planned although the user said No')
      stub.confirm_answer = 1
      dbbliss.plan('actual', 'statement')
      take('script/split').cb(nil, { statements = { write } })
      expect(stub.confirms == 2, 'one question per plan')
      local r = take('plan/start')
      expect(r and r.params.confirm_execute == true and r.params.mode == 'actual', 'sent with the word: ' .. vim.inspect(r and r.params))
    end,
  },
  {
    -- Verifies: LLR-PLAN-12
    'plan_subcommand_is_dispatched_and_completed',
    '',
    function()
      local saved = dbbliss.plan
      local got
      dbbliss.plan = function(mode, scope, range)
        got = { mode, scope, range }
      end
      dbbliss.command({ args = 'plan', range = 0 })
      expect(vim.deep_equal(got, { 'estimated', 'statement', nil }), 'plain plan is an estimate of the statement: ' .. vim.inspect(got))
      dbbliss.command({ args = 'plan actual', range = 2, line1 = 3, line2 = 4 })
      expect(vim.deep_equal(got, { 'actual', 'range', { 3, 4 } }), 'plan actual with a range: ' .. vim.inspect(got))
      got = nil
      dbbliss.command({ args = 'plan sideways', range = 0 })
      expect(got == nil, 'an unknown mode plans nothing')
      dbbliss.plan = saved
      expect(vim.tbl_contains(dbbliss.complete('', 'Dbbliss '), 'plan'), 'plan is completed')
      expect(vim.deep_equal(dbbliss.complete('', 'Dbbliss plan '), { 'actual', 'estimated' }), 'modes are completed: ' .. vim.inspect(dbbliss.complete('', 'Dbbliss plan ')))
    end,
  },
  {
    -- Verifies: LLR-PLAN-11
    'plan_done_notification_reaches_the_viewer',
    '',
    function()
      script_setup({ 'select 1;' })
      expect(stub.handlers['plan/done'] ~= nil, 'plan/done has no handler')
      local plan = require('dbbliss.plan')
      plan._state.ops.px = { id = 'px', sql = 'select 1', mode = 'estimated', status = 'running', plans = {}, collapsed = {} }
      stub.handlers['plan/done']({ plan_id = 'px', connection_id = 'c1', status = 'failed', plans = {}, error = 'boom' })
      expect(plan._state.ops.px.status == 'failed' and plan._state.ops.px.error == 'boom', 'the operation was not updated')
      plan._state.ops.px = nil
    end,
  },
  {
    -- Verifies: LLR-COMP-4, LLR-COMP-11
    'completion_cache_is_dropped_on_disconnect_and_reloaded_on_a_tree_refresh',
    '',
    function()
      script_setup({ 'select 1;' })
      local completion = require('dbbliss.completion')
      local tree = require('dbbliss.tree')
      completion._state.caches['c1'] = primed_cache()
      tree._state.trees['c1'] = { roots = {} }
      tree._state.current = 'c1'
      tree.refresh_all()
      local cache = completion._state.caches['c1']
      expect(cache ~= nil and cache.names ~= nil, 'a refresh of the schema tree keeps what is cached (stale-while-revalidate)')
      expect(take('catalog/names') ~= nil, 'and reloads the names')
      take('catalog/children')
      dbbliss.disconnect()
      take('disconnect').cb(nil, { rolled_back = false })
      expect(completion._state.caches['c1'] == nil, 'a disconnect drops the cache')
    end,
  },
  {
    -- Verifies: HLR-COMP-8
    'ddl_that_completes_reloads_completion_under_autocommit',
    '',
    function()
      script_setup({ 'create table t (id int);', 'select 1;' })
      local completion = require('dbbliss.completion')
      completion._state.caches['c1'] = primed_cache()
      dbbliss.run('buffer')
      local ddl = stmt('create table t (id int);', 0)
      ddl.ddl = true
      take('script/split').cb(nil, { statements = { ddl, stmt('select 1;', 1) } })
      expect(take('catalog/names') == nil, 'nothing is reloaded before the statement completes')
      finish_query()
      expect(take('catalog/names') ~= nil, 'the names are reloaded when the DDL completed')
      finish_query()
      expect(take('catalog/names') == nil, 'a plain statement reloads nothing')
    end,
  },
  {
    -- Verifies: HLR-COMP-8
    'ddl_reloads_the_schema_tree_when_it_is_shown_and_waits_for_a_transaction',
    '',
    function()
      script_setup({ 'create table t (id int);' })
      local tree = require('dbbliss.tree')
      tree._state.trees['c1'] = { roots = {} }
      tree._state.current = 'c1'
      dbbliss.run('buffer')
      local ddl = stmt('create table t (id int);', 0)
      ddl.ddl = true
      take('script/split').cb(nil, { statements = { ddl } })
      finish_query()
      expect(take('catalog/children') ~= nil, 'under autocommit the tree is reloaded at once')
      dbbliss.run('buffer')
      take('script/split').cb(nil, { statements = { ddl } })
      finish_query({ transaction = 'active' })
      dbbliss._state.connections.a.transaction = 'active'
      expect(take('catalog/children') == nil, 'the tree is not reloaded while the transaction is open')
      dbbliss.run('buffer')
      take('script/split').cb(nil, { statements = { stmt('commit;', 0) } })
      finish_query({ transaction = 'none' })
      expect(take('catalog/children') ~= nil, 'the tree is reloaded when the transaction ends')
      tree._state.trees['c1'] = nil
      tree._state.current = nil
    end,
  },
  {
    -- Verifies: LLR-COMP-12
    'completion_asks_for_nothing_while_this_clients_ddl_transaction_is_open',
    '',
    function()
      script_setup({ 'create table t (id int);', 'select 1;' })
      local completion = require('dbbliss.completion')
      dbbliss.run('buffer')
      local ddl = stmt('create table t (id int);', 0)
      ddl.ddl = true
      take('script/split').cb(nil, { statements = { ddl, stmt('select 1;', 1) } })
      finish_query({ transaction = 'active' })
      dbbliss._state.connections.a.transaction = 'active'
      local got
      completion.complete({ conn = { id = 'c1', engine = 'postgres' }, before = 'select * from ', statement = 'select * from ' }, function(r)
        got = r
      end)
      expect(got ~= nil, 'answered at once')
      expect(take('catalog/names') == nil, 'no catalog request while the transaction that changed the schema is open')
      finish_query({ transaction = 'none' })
      completion.complete({ conn = { id = 'c1', engine = 'postgres' }, before = 'select * from ', statement = 'select * from ' }, function() end)
      expect(take('catalog/names') ~= nil, 'after the transaction ended the names load')
    end,
  },
  {
    -- Verifies: LLR-AI-8
    'ask_hands_the_statement_under_the_cursor_and_the_connections_facts_to_the_hook',
    '',
    function()
      dbbliss.setup({ connections = { a = { engine = 'postgres', connection_string = 'Host=x', env = 'dev', ai = true } }, ai = { command = { 'ai-cli' } } })
      script_setup({ 'select 1;', 'select * from orders;' })
      vim.api.nvim_win_set_cursor(0, { 2, 3 })
      local ai = require('dbbliss.ai')
      local saved, got = ai.ask, nil
      ai.ask = function(a)
        got = a
      end
      dbbliss.ask('why slow?', 'statement')
      take('script/split').cb(nil, { statements = { stmt('select 1;', 0), stmt('select * from orders;', 1) } })
      ai.ask = saved
      expect(got ~= nil, 'the hook was called')
      expect(got.statement == 'select * from orders;' and got.question == 'why slow?', 'statement and question: ' .. vim.inspect(got))
      expect(got.conn.id == 'c1' and got.conn.name == 'a' and got.conn.engine == 'postgres' and got.conn.env == 'dev' and got.conn.ai == true, 'connection facts: ' .. vim.inspect(got.conn))
      expect(take('execute') == nil, 'nothing was run')
      expect(next(dbbliss._state.scripts) == nil, 'the connection is not left marked busy')
    end,
  },
  {
    -- Verifies: LLR-AI-8
    'ask_takes_one_statement_and_asks_for_a_question_when_none_is_given',
    '',
    function()
      script_setup({ 'select 1;', 'select 2;' })
      local ai = require('dbbliss.ai')
      local saved, calls = ai.ask, 0
      ai.ask = function()
        calls = calls + 1
      end
      dbbliss.ask('q', 'range', { 1, 2 })
      take('script/split').cb(nil, { statements = { stmt('select 1;', 0), stmt('select 2;', 1) } })
      expect(calls == 0, 'two statements are not asked about as one')
      expect(next(dbbliss._state.scripts) == nil, 'the mark is released')
      local input = vim.fn.input
      local asked
      vim.fn.input = function(prompt)
        asked = prompt
        return 'what does this do?'
      end
      local got
      ai.ask = function(a)
        got = a
      end
      dbbliss.ask('', 'statement')
      take('script/split').cb(nil, { statements = { stmt('select 1;', 0) } })
      vim.fn.input = input
      ai.ask = saved
      expect(asked ~= nil and got and got.question == 'what does this do?', 'asked for the question: ' .. vim.inspect(got))
      local none
      vim.fn.input = function()
        return ''
      end
      ai.ask = function()
        none = true
      end
      dbbliss.ask('', 'statement')
      take('script/split').cb(nil, { statements = { stmt('select 1;', 0) } })
      vim.fn.input = input
      ai.ask = saved
      expect(none == nil, 'no question, nothing asked')
    end,
  },
  {
    -- Verifies: HLR-AI-4
    'ask_hands_over_the_last_error_and_plan_it_remembers',
    '',
    function()
      script_setup({ 'select * from orders;' })
      dbbliss.run('buffer')
      take('script/split').cb(nil, { statements = { stmt('select * from orders;', 0) } })
      finish_query({ status = 'error', error = { message = 'relation "orders" does not exist', line = 1 } })
      local plan = require('dbbliss.plan')
      plan._state.last['c1'] = { sql = 'select * from orders;', mode = 'estimated', lines = { 'Seq Scan' } }
      local ai = require('dbbliss.ai')
      local saved, got = ai.ask, nil
      ai.ask = function(a)
        got = a
      end
      dbbliss.ask('why?', 'statement')
      take('script/split').cb(nil, { statements = { stmt('select * from orders;', 0) } })
      ai.ask = saved
      expect(got and got.last_error and got.last_error.sql == 'select * from orders;' and got.last_error.message:find('does not exist', 1, true), 'the error: ' .. vim.inspect(got and got.last_error))
      expect(got.last_plan and got.last_plan.sql == 'select * from orders;' and got.last_plan.lines[1] == 'Seq Scan', 'the plan: ' .. vim.inspect(got and got.last_plan))
      plan._state.last = {}
    end,
  },
  {
    -- Verifies: LLR-AI-8
    'ask_subcommand_is_dispatched_and_completed_and_the_hook_stops_on_exit',
    '',
    function()
      local saved, got = dbbliss.ask, nil
      dbbliss.ask = function(question, scope, range)
        got = { question, scope, range }
      end
      dbbliss.command({ args = 'ask why is this slow', range = 0 })
      expect(vim.deep_equal(got, { 'why is this slow', 'statement', nil }), 'question words are joined: ' .. vim.inspect(got))
      dbbliss.command({ args = 'ask', range = 2, line1 = 3, line2 = 4 })
      expect(vim.deep_equal(got, { '', 'range', { 3, 4 } }), 'a range: ' .. vim.inspect(got))
      dbbliss.ask = saved
      expect(vim.tbl_contains(dbbliss.complete('', 'Dbbliss '), 'ask'), 'ask is completed')
      local ai = require('dbbliss.ai')
      local cancelled = false
      ai._state.running = { { handle = { cancel = function() cancelled = true end } } }
      dbbliss.on_exit()
      expect(cancelled, 'quitting ends a running command')
      ai._state.running = {}
    end,
  },
  {
    -- Verifies: HLR-COMP-8
    'ddl_that_fails_reloads_nothing',
    '',
    function()
      script_setup({ 'create table t (id int);' })
      local completion = require('dbbliss.completion')
      completion._state.caches['c1'] = primed_cache()
      dbbliss.run('buffer')
      local ddl = stmt('create table t (id int);', 0)
      ddl.ddl = true
      take('script/split').cb(nil, { statements = { ddl } })
      finish_query({ status = 'error', error = { message = 'exists' } })
      expect(take('catalog/names') == nil, 'a failed DDL changed nothing')
    end,
  },
  {
    -- Verifies: HLR-COMP-8
    'ddl_in_a_transaction_reloads_completion_when_the_transaction_ends',
    '',
    function()
      script_setup({ 'create table t (id int);', 'commit;' })
      local completion = require('dbbliss.completion')
      completion._state.caches['c1'] = primed_cache()
      dbbliss.run('buffer')
      local ddl = stmt('create table t (id int);', 0)
      ddl.ddl = true
      take('script/split').cb(nil, { statements = { ddl, stmt('commit;', 1) } })
      finish_query({ transaction = 'active' })
      dbbliss._state.connections.a.transaction = 'active'
      expect(take('catalog/names') == nil, 'not while the transaction is open: the catalog session cannot see it')
      finish_query({ transaction = 'none' })
      expect(take('catalog/names') ~= nil, 'the commit starts the reload')
    end,
  },
  {
    -- Verifies: LLR-COMP-5
    'completion_reads_from_the_buffers_connection_or_the_current_one',
    '',
    function()
      script_setup({ 'select 1;' })
      local b = vim.api.nvim_get_current_buf()
      local conn = dbbliss._completion_connection(b)
      expect(conn and conn.id == 'c1' and conn.engine == 'postgres', 'the current connection: ' .. vim.inspect(conn))
      vim.b[b].dbbliss_connection = 'nowhere'
      expect(dbbliss._completion_connection(b).id == 'c1', 'a buffer pointing at a connection that is gone falls back to the current one')
      dbbliss._state.current = nil
      expect(dbbliss._completion_connection(b) == nil, 'no connection, no completion')
      expect(dbbliss._completion_connection(99999) == nil, 'a buffer that is gone')
    end,
  },
}

local failed = 0
for _, t in ipairs(tests) do
  local name, bug, run = t[1], t[2], t[3]
  reset()
  local ok, err = pcall(run)
  if ok then
    io.stdout:write(('%-34s PASS\n'):format(name))
  else
    failed = failed + 1
    io.stdout:write(('%-34s FAIL  %s%s\n'):format(name, bug ~= '' and ('[bug ' .. bug .. '] ') or '', tostring(err)))
  end
end
io.stdout:write(('\n%d tests, %d failed\n'):format(#tests, failed))
os.exit(failed == 0 and 0 or 1)
