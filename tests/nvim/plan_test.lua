-- Plan viewer tests (Phase 5): the tree buffer, its keys, the confirmation before an actual plan of a
-- statement that is not plainly a read, with the backend replaced by a function. No database.
--
--   nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/plan_test.lua
vim.fs.joinpath = vim.fs.joinpath or function(...)
  return table.concat({ ... }, '/')
end
vim.notify = function() end

local plan = require('dbbliss.plan')

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

local function node(id, operator, over, children)
  return vim.tbl_extend('force', {
    id = id,
    operator = operator,
    detail = vim.NIL,
    estimated_rows = vim.NIL,
    cost = vim.NIL,
    actual_rows = vim.NIL,
    loops = vim.NIL,
    time_ms = vim.NIL,
    buffers = vim.NIL,
    self_cost = vim.NIL,
    self_time_ms = vim.NIL,
    warnings = {},
    children = children or {},
  }, over or {})
end

--- An actual plan as the backend sends it: the join is the root, the index scan under it is the hottest.
local function actual_doc()
  return {
    engine = 'postgres',
    mode = 'actual',
    statement = 'select * from t join u on u.t_id = t.id',
    hottest = 3,
    totals = { planning_ms = 0.2, execution_ms = 40.5, cost = 120 },
    notes = { 'Missing index on u(t_id)' },
    raw = '[{"Plan": {"Node Type": "Hash Join"}}]',
    root = node(1, 'Hash Join', { detail = 'hash cond: (u.t_id = t.id)', estimated_rows = 100, cost = 120, actual_rows = 5000, loops = 1, time_ms = 40, self_cost = 30, self_time_ms = 5 }, {
      node(2, 'Seq Scan', { detail = 'on public.t', estimated_rows = 1000, cost = 60, actual_rows = 1000, loops = 1, time_ms = 10, self_cost = 60, self_time_ms = 10 }),
      node(3, 'Index Scan', { detail = 'on public.u · index u_pkey', estimated_rows = 100, cost = 30, actual_rows = 5000, loops = 1, time_ms = 25, self_cost = 30, self_time_ms = 25, buffers = 'shared hit=45', warnings = { 'sorted on disk' } }),
    }),
  }
end

local function estimated_doc()
  local d = actual_doc()
  d.mode, d.totals, d.hottest, d.notes = 'estimated', { cost = 120 }, 1, {}
  local function strip(n)
    n.actual_rows, n.loops, n.time_ms, n.self_time_ms, n.buffers = vim.NIL, vim.NIL, vim.NIL, vim.NIL, vim.NIL
    for _, c in ipairs(n.children) do
      strip(c)
    end
  end
  strip(d.root)
  return d
end

--- A plan UI whose backend is a function.
local function fake(opts)
  opts = opts or {}
  local f = { calls = {}, notes = {}, confirms = {} }
  f.yes = opts.yes ~= false
  plan.setup({
    request = function(conn_id, method, params, cb)
      f.calls[#f.calls + 1] = { conn_id = conn_id, method = method, params = params }
      if opts.refuse and method == 'plan/start' then
        return cb({ code = 1009, message = opts.refuse }, nil)
      end
      if method == 'plan/start' then
        return cb(nil, { plan_id = 'p1' })
      end
      cb(nil, {})
    end,
    notify = function(msg, level)
      f.notes[#f.notes + 1] = { msg = msg, level = level }
    end,
    confirm = function(text)
      f.confirms[#f.confirms + 1] = text
      return f.yes
    end,
  })
  function f.done(over)
    plan.handle_notification('plan/done', vim.tbl_extend('force', { plan_id = 'p1', connection_id = 'c1', status = 'completed', plans = { actual_doc() }, error = vim.NIL }, over or {}))
  end
  function f.buf()
    for _, b in ipairs(vim.api.nvim_list_bufs()) do
      if vim.bo[b].filetype == 'dbbliss-plan' then
        return b
      end
    end
  end
  function f.text()
    local b = f.buf()
    return b and table.concat(vim.api.nvim_buf_get_lines(b, 0, -1, false), '\n') or ''
  end
  return f
end

local function target(over)
  return vim.tbl_extend('force', { conn_id = 'c1', conn_name = 'a', engine = 'postgres', sql = 'select * from t', mode = 'estimated', kind = 'read' }, over or {})
end

local function close_all()
  for _, b in ipairs(vim.api.nvim_list_bufs()) do
    if vim.bo[b].filetype == 'dbbliss-plan' or vim.bo[b].filetype == 'json' or vim.bo[b].filetype == 'xml' then
      pcall(vim.api.nvim_buf_delete, b, { force = true })
    end
  end
end

local function line_with(text, needle)
  for line in text:gmatch('[^\n]+') do
    if line:find(needle, 1, true) then
      return line
    end
  end
end

local function key(lhs)
  vim.fn.maparg(lhs, 'n', false, true).callback()
end

local function put_cursor_on(needle)
  local b = vim.api.nvim_get_current_buf()
  for i, l in ipairs(vim.api.nvim_buf_get_lines(b, 0, -1, false)) do
    if l:find(needle, 1, true) then
      vim.api.nvim_win_set_cursor(0, { i, 0 })
      return
    end
  end
  error('no line with ' .. needle, 0)
end

local tests = {
  {
    -- Verifies: LLR-PLAN-11
    'plan_tree_shows_cost_rows_factor_loops_and_time',
    function()
      local f = fake()
      plan.start(target({ mode = 'actual' }))
      f.done()
      local text = f.text()
      local join = line_with(text, 'Hash Join')
      expect(join, 'no line for the root: ' .. text)
      expect(join:find('cost 120', 1, true), 'cost: ' .. join)
      expect(join:find('rows 100 → 5000', 1, true), 'estimated and actual rows: ' .. join)
      expect(join:find('×50', 1, true), 'how far apart (a factor): ' .. join)
      expect(join:find('loops 1', 1, true), 'loops: ' .. join)
      expect(join:find('40 ms', 1, true), 'time: ' .. join)
      local seq = line_with(text, 'Seq Scan')
      expect(seq and seq:find('rows 1000 → 1000', 1, true) and not seq:find('×', 1, true), 'rows that match show no factor: ' .. tostring(seq))
      expect(text:find('on public.u · index u_pkey', 1, true), 'detail')
      expect(text:find('shared hit=45', 1, true), 'buffers')
      expect(text:find('sorted on disk', 1, true), 'node warnings')
      expect(text:find('Missing index on u(t_id)', 1, true), 'plan notes')
      expect(text:find('execution 40.5 ms', 1, true) and text:find('planning 0.2 ms', 1, true), 'totals in the header: ' .. text)
      expect(text:find('actual', 1, true), 'says it is an actual plan')
    end,
  },
  {
    -- Verifies: LLR-PLAN-11
    'plan_estimated_shows_no_actual_figures',
    function()
      local f = fake()
      plan.start(target())
      f.done({ plans = { estimated_doc() } })
      local text = f.text()
      local join = line_with(text, 'Hash Join')
      expect(join:find('rows 100', 1, true) and not join:find('→', 1, true) and not join:find('ms', 1, true) and not join:find('loops', 1, true), 'estimate only: ' .. join)
      expect(text:find('estimated', 1, true), 'says it is an estimate')
    end,
  },
  {
    -- Verifies: LLR-PLAN-11
    'plan_hottest_node_is_marked_and_highlighted',
    function()
      local f = fake()
      plan.start(target({ mode = 'actual' }))
      f.done()
      local text = f.text()
      expect(line_with(text, 'Index Scan'):find('hottest', 1, true), 'the marked line is the hottest node')
      expect(not line_with(text, 'Hash Join'):find('hottest', 1, true) and not line_with(text, 'Seq Scan'):find('hottest', 1, true), 'only one node is marked')
      local b = f.buf()
      local row
      for i, l in ipairs(vim.api.nvim_buf_get_lines(b, 0, -1, false)) do
        if l:find('Index Scan', 1, true) then
          row = i - 1
        end
      end
      local marks = vim.api.nvim_buf_get_extmarks(b, vim.api.nvim_create_namespace('dbbliss_plan'), { row, 0 }, { row, -1 }, { details = true })
      local hot = false
      for _, m in ipairs(marks) do
        hot = hot or m[4].hl_group == 'DbblissHot'
      end
      expect(hot, 'the hottest line carries the DbblissHot highlight')
    end,
  },
  {
    -- Verifies: LLR-PLAN-11
    'plan_nodes_collapse_and_expand_with_the_keys',
    function()
      local f = fake()
      plan.start(target({ mode = 'actual' }))
      f.done()
      vim.api.nvim_set_current_buf(f.buf())
      put_cursor_on('Hash Join')
      key('<CR>')
      local text = f.text()
      expect(not text:find('Seq Scan', 1, true) and not text:find('Index Scan', 1, true), 'the children are hidden: ' .. text)
      expect(line_with(text, 'Hash Join'):find('▸', 1, true), 'a collapsed node says so')
      key('o')
      expect(f.text():find('Seq Scan', 1, true), 'o expands it again')
      key('zM')
      expect(not f.text():find('Seq Scan', 1, true), 'zM collapses all')
      key('zR')
      expect(f.text():find('Index Scan', 1, true), 'zR expands all')
      put_cursor_on('Seq Scan')
      key('<CR>')
      expect(f.text():find('Seq Scan', 1, true), 'a leaf has nothing to collapse')
    end,
  },
  {
    -- Verifies: LLR-PLAN-11
    'plan_raw_plan_is_one_key_away',
    function()
      local f = fake()
      plan.start(target({ mode = 'actual' }))
      f.done()
      vim.api.nvim_set_current_buf(f.buf())
      key('x')
      local b = vim.api.nvim_get_current_buf()
      eq(vim.bo[b].filetype, 'json', 'a PostgreSQL plan opens as JSON')
      expect(table.concat(vim.api.nvim_buf_get_lines(b, 0, -1, false), '\n'):find('Hash Join', 1, true), 'the raw text')
      close_all()
      local g = fake()
      plan.start(target({ engine = 'sqlserver' }))
      local d = estimated_doc()
      d.engine, d.raw = 'sqlserver', '<ShowPlanXML/>'
      g.done({ plans = { d } })
      vim.api.nvim_set_current_buf(g.buf())
      key('x')
      eq(vim.bo[vim.api.nvim_get_current_buf()].filetype, 'xml', 'a SQL Server plan opens as XML')
    end,
  },
  {
    -- Verifies: LLR-PLAN-11
    'plan_failed_and_cancelled_plans_name_their_end',
    function()
      local f = fake()
      plan.start(target({ mode = 'actual' }))
      f.done({ status = 'failed', plans = {}, error = 'relation "t" does not exist' })
      local text = f.text()
      expect(text:find('FAILED', 1, true) and text:find('relation "t" does not exist', 1, true), 'failure with the server words: ' .. text)
      eq(f.notes[#f.notes].level, vim.log.levels.ERROR, 'a failure is an error notification')
      close_all()
      local g = fake()
      plan.start(target({ mode = 'actual' }))
      g.done({ status = 'cancelled', plans = {} })
      expect(g.text():find('cancelled', 1, true), 'cancelled is said: ' .. g.text())
      eq(g.notes[#g.notes].level, vim.log.levels.WARN, 'cancelled is a warning')
    end,
  },
  {
    -- Verifies: LLR-PLAN-11
    'plan_shows_that_it_runs_and_c_cancels_it',
    function()
      local f = fake()
      plan.start(target({ mode = 'actual' }))
      expect(f.text():find('Running', 1, true) or f.text():find('running', 1, true), 'a running plan says so: ' .. f.text())
      vim.api.nvim_set_current_buf(f.buf())
      key('c')
      eq(f.calls[#f.calls].method, 'plan/cancel', 'c cancels')
      eq(f.calls[#f.calls].params, { plan_id = 'p1' }, 'this plan')
      f.done()
      local before = #f.calls
      key('c')
      eq(#f.calls, before, 'nothing to cancel once it has ended')
      key('q')
      expect(f.buf() == nil, 'q closes the viewer')
    end,
  },
  {
    -- Verifies: LLR-PLAN-12
    'plan_estimated_is_sent_without_a_question',
    function()
      local f = fake()
      plan.start(target({ kind = 'write', sql = 'delete from t' }))
      eq(#f.confirms, 0, 'an estimate runs nothing: no question')
      eq(f.calls[1].method, 'plan/start', 'sent')
      eq(f.calls[1].params, { sql = 'delete from t', mode = 'estimated' }, 'estimated, without the word')
    end,
  },
  {
    -- Verifies: LLR-PLAN-12
    'plan_actual_of_a_read_is_sent_without_a_question',
    function()
      local f = fake()
      plan.start(target({ mode = 'actual' }))
      eq(#f.confirms, 0, 'a read: no question')
      eq(f.calls[1].params, { sql = 'select * from t', mode = 'actual' }, 'actual, without the word')
    end,
  },
  {
    -- Verifies: LLR-PLAN-12
    'plan_actual_of_a_write_asks_first_and_then_says_so',
    function()
      local f = fake()
      plan.start(target({ mode = 'actual', kind = 'write', sql = 'delete from t where id < 10' }))
      eq(#f.confirms, 1, 'one question')
      expect(f.confirms[1]:find('delete from t where id < 10', 1, true), 'names the statement: ' .. f.confirms[1])
      expect(f.confirms[1]:find('run', 1, true) and f.confirms[1]:find('rolled back', 1, true), 'says it will be run and rolled back: ' .. f.confirms[1])
      eq(f.calls[1].params, { sql = 'delete from t where id < 10', mode = 'actual', confirm_execute = true }, 'sent with the word')
      -- A statement the backend did not classify counts as a write.
      local g = fake()
      plan.start(target({ mode = 'actual', kind = vim.NIL, sql = 'call p()' }))
      eq(#g.confirms, 1, 'unclassified: asked')
    end,
  },
  {
    -- Verifies: LLR-PLAN-12
    'plan_actual_of_a_write_declined_sends_nothing',
    function()
      local f = fake({ yes = false })
      plan.start(target({ mode = 'actual', kind = 'write', sql = 'update t set a = 1' }))
      eq(#f.confirms, 1, 'asked')
      eq(#f.calls, 0, 'nothing was sent')
      expect(f.buf() == nil, 'no viewer opened')
      expect(f.notes[#f.notes].msg:find('nothing was run', 1, true), 'the user is told: ' .. f.notes[#f.notes].msg)
    end,
  },
  {
    -- Verifies: LLR-PLAN-12
    'plan_refused_by_the_backend_is_shown_and_opens_nothing',
    function()
      local f = fake({ refuse = 'An actual plan runs the statement. Ask the user.' })
      plan.start(target({ mode = 'actual' }))
      expect(f.buf() == nil, 'no viewer for a refused plan')
      expect(f.notes[#f.notes].msg:find('Ask the user', 1, true), 'the backend words reach the user')
      eq(f.notes[#f.notes].level, vim.log.levels.WARN, 'a user error is a warning')
    end,
  },
  {
    -- Verifies: LLR-PLAN-11
    'plan_done_of_an_unknown_plan_is_ignored',
    function()
      local f = fake()
      plan.handle_notification('plan/done', { plan_id = 'nope', connection_id = 'c1', status = 'completed', plans = {}, error = vim.NIL })
      expect(f.buf() == nil, 'nothing opened')
    end,
  },
  {
    -- Verifies: LLR-PLAN-11
    'plan_without_hottest_or_figures_renders',
    function()
      local f = fake()
      plan.start(target())
      local d = estimated_doc()
      d.hottest, d.totals = vim.NIL, {}
      d.root.cost = vim.NIL
      f.done({ plans = { d } })
      expect(f.text():find('Hash Join', 1, true), 'renders with no figures: ' .. f.text())
      expect(not f.text():find('hottest', 1, true), 'no hottest node, none marked')
    end,
  },
}

local failed = 0
for _, t in ipairs(tests) do
  close_all()
  local ok, err = pcall(t[2])
  if ok then
    io.stdout:write(('%-52s PASS\n'):format(t[1]))
  else
    failed = failed + 1
    io.stdout:write(('%-52s FAIL  %s\n'):format(t[1], tostring(err)))
  end
end
io.stdout:write(('\n%d tests, %d failed\n'):format(#tests, failed))
os.exit(failed == 0 and 0 or 1)
