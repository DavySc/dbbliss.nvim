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
    function b:on_exit() end
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
vim.fn.confirm = function()
  stub.confirms = stub.confirms + 1
  return 1
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
  dbbliss._state.backend = nil
  dbbliss._state.queries = {}
  dbbliss._state.scripts = {}
  stub.handlers = {}
  stub.requests = {}
  stub.open = {}
  stub.server_tx = {}
  stub.confirms = 0
  next_connection = 0
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

local tests = {
  {
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
