-- Catalog UI tests (Phase 2): the name under the cursor, the info buffer, the schema tree and
-- scripting, with the backend replaced by a function. No database.
--
--   nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/catalog_test.lua
vim.fs.joinpath = vim.fs.joinpath or function(...)
  return table.concat({ ... }, '/')
end
vim.notify = function() end

local names = require('dbbliss.names')
local info = require('dbbliss.info')
local tree = require('dbbliss.tree')

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

--- A tree whose backend is a table of answers; requests wait until `flush` so loading can be seen.
local function fake_tree(opts)
  opts = opts or {}
  local t = { calls = {}, pending = {} }
  local data = {
    [''] = {
      { name = 'app', kind = 'database', expandable = true, browsable = true },
      { name = 'other', kind = 'database', expandable = false, browsable = false, detail = 'connect to it to browse' },
    },
    ['app'] = { { name = 'public', kind = 'schema', expandable = true }, { name = 'pg_catalog', kind = 'schema', expandable = true, system = true } },
    ['app/public'] = {
      { name = 'Tables', kind = 'folder', expandable = true, folder = 'table' },
      { name = 'Functions', kind = 'folder', expandable = true, folder = 'function' },
    },
    ['app/public/table'] = { { name = 'customer', kind = 'table' }, { name = 'Order Line', kind = 'table' } },
    ['app/public/function'] = { { name = 'add_one', kind = 'function', identity = 'a integer', detail = 'a integer' } },
  }
  local function key(p)
    return (table.concat({ p.database or '', p.schema or '', p.folder or '' }, '/'):gsub('/+$', ''))
  end
  tree.forget('c1')
  tree.setup({
    request = function(_, _, params, cb)
      local k = key(params)
      t.calls[#t.calls + 1] = k
      t.pending[#t.pending + 1] = function()
        if opts.fail == k then
          cb({ message = 'permission denied' }, nil)
        else
          cb(nil, { nodes = vim.deepcopy(data[k] or {}) })
        end
      end
    end,
    info = function() end,
    script = function() end,
    notify = function() end,
    show_system = false,
  })
  function t.flush()
    local batch = t.pending
    t.pending = {}
    for _, f in ipairs(batch) do
      f()
    end
  end
  function t.cursor_on(name)
    local win = vim.fn.win_findbuf(tree._state.buf)[1]
    for l, n in pairs(tree._state.line_nodes) do
      if n.name == name then
        vim.api.nvim_win_set_cursor(win, { l, 0 })
        return
      end
    end
    error('no line for ' .. name, 0)
  end
  return t
end

local tests = {
  {
    'name_under_cursor',
    function()
      local at = names.at_cursor
      eq(at('select * from customer where id = 1', 16), 'customer', 'plain word')
      eq(at('select * from public.customer c', 14), 'public.customer', 'cursor on the schema')
      eq(at('select * from public.customer c', 22), 'public.customer', 'cursor on the table')
      eq(at('select * from public.customer c', 20), 'public.customer', 'cursor on the dot')
      eq(at('select * from db.dbo.t', 18), 'db.dbo.t', 'three parts')
      eq(at('from "Order Line" ol', 8), '"Order Line"', 'quoted name with a space')
      eq(at('from dbbliss_cat."Order Line" x', 25), 'dbbliss_cat."Order Line"', 'qualified, second part quoted')
      eq(at('from [dbo].[Order Line] x', 8), '[dbo].[Order Line]', 'bracketed')
      eq(at('from [a]]b].t', 8), '[a]]b].t', 'bracket with an escaped bracket')
      eq(at('select name from t', 8), 'name', 'a column is a word too')
      eq(at('from t where x', 6), 't', 'one letter')
      eq(at('from customer', 13), 'customer', 'cursor just after the last letter')
      eq(at('select ö_col from t', 9), 'ö_col', 'non-ASCII letters')
      eq(at('f(a, b)', 1), 'f', 'before a parenthesis')
      eq(at('   ', 1), nil, 'nothing there')
      eq(at('select 1 ;', 9), nil, 'a semicolon is not a name')
    end,
  },
  {
    'info_renders_sections',
    function()
      info.setup({ max_col_width = 60 })
      local lines = info.render({
        title = 'table public.customer',
        kind = 'table',
        sections = {
          { title = 'Summary', columns = { 'property', 'value' }, rows = { { 'kind', 'table' }, { 'owner', 'me' } }, text = false },
          { title = 'Columns', columns = { '#', 'name', 'nullable', 'default' }, rows = { { 1, 'id', false, vim.NIL }, { 2, 'email', true, "'x'::text" } }, text = false },
          { title = 'Indexes', columns = { 'name' }, rows = {}, text = false },
          { title = 'Definition', columns = { 'text' }, rows = { { 'SELECT id' }, { '  FROM t;' } }, text = true },
        },
      })
      eq(lines, {
        'table public.customer',
        '',
        '── Summary (2) ──',
        '  property │ value',
        '  ─────────┼──────',
        '  kind     │ table',
        '  owner    │ me   ',
        '',
        '── Columns (2) ──',
        '  # │ name  │ nullable │ default  ',
        '  ──┼───────┼──────────┼──────────',
        '  1 │ id    │ false    │ NULL     ',
        '  2 │ email │ true     │ \'x\'::text',
        '',
        '── Indexes (0) ──',
        '  (none)',
        '',
        '── Definition ──',
        '  SELECT id',
        '    FROM t;',
      }, 'info buffer')
    end,
  },
  {
    'info_buffer_is_reused_and_has_keys',
    function()
      local result = { title = 'table t', kind = 'table', sections = { { title = 'Summary', columns = { 'property', 'value' }, rows = { { 'kind', 'table' } }, text = false } } }
      local refreshed, scripted = 0, 0
      local ctx = {
        connection = 'a',
        refresh = function()
          refreshed = refreshed + 1
        end,
        script = function()
          scripted = scripted + 1
        end,
      }
      local b1 = info.open(result, ctx)
      local b2 = info.open(result, ctx)
      expect(b1 == b2, 'a second open of the same object made a second buffer')
      vim.api.nvim_set_current_buf(b1)
      vim.api.nvim_feedkeys('r', 'x', false)
      vim.api.nvim_feedkeys('s', 'x', false)
      expect(refreshed == 1 and scripted == 1, ('r and s: refreshed=%d scripted=%d'):format(refreshed, scripted))
    end,
  },
  {
    'tree_loads_lazily_caches_and_refreshes',
    function()
      local t = fake_tree()
      tree.open('c1', 'a')
      expect(table.concat(tree.lines(), '\n'):find('loading', 1, true), 'loading is not shown while the roots load')
      t.flush()
      -- One database can be browsed: it opened by itself, and loaded its schemas.
      eq(t.calls, { '', 'app' }, 'requests so far')
      t.flush()
      eq(tree.lines(), {
        'a: schema browser   <CR> open  i info  s script  r refresh  S system objects  q close',
        '▾ app',
        '  ▸ public',
        '  other  connect to it to browse',
      }, 'tree after the first loads (the system schema is hidden)')

      t.cursor_on('public')
      tree.activate()
      t.flush()
      eq(t.calls, { '', 'app', 'app/public' }, 'opening a schema loads only it')
      t.cursor_on('Tables')
      tree.activate()
      t.flush()
      t.cursor_on('Functions')
      tree.activate()
      t.flush()
      eq(tree.lines(), {
        'a: schema browser   <CR> open  i info  s script  r refresh  S system objects  q close',
        '▾ app',
        '  ▾ public',
        '    ▾ Tables',
        '        customer',
        '        Order Line',
        '    ▾ Functions',
        '        add_one(a integer)',
        '  other  connect to it to browse',
      }, 'tree fully open')

      -- Closing and opening again is not another round trip.
      local before = #t.calls
      t.cursor_on('Tables')
      tree.activate()
      tree.activate()
      t.flush()
      eq(#t.calls, before, 'a cached node loaded again')

      -- A refresh loads again, and what was open stays open.
      t.cursor_on('public')
      tree.refresh()
      t.flush()
      t.flush()
      t.flush()
      eq(t.calls[before + 1], 'app/public', 'refresh reloads the node')
      expect(table.concat(tree.lines(), '\n'):find('customer', 1, true), 'the open Tables folder was not opened again after a refresh')
      expect(table.concat(tree.lines(), '\n'):find('add_one', 1, true), 'the open Functions folder was not opened again after a refresh')
    end,
  },
  {
    'tree_system_objects_toggle',
    function()
      local t = fake_tree()
      tree.open('c1', 'a')
      t.flush()
      t.flush()
      expect(not table.concat(tree.lines(), '\n'):find('pg_catalog', 1, true), 'a system schema is shown by default')
      tree.toggle_system()
      expect(table.concat(tree.lines(), '\n'):find('pg_catalog', 1, true), 'a system schema stays hidden after the toggle')
      tree.toggle_system()
      expect(not table.concat(tree.lines(), '\n'):find('pg_catalog', 1, true), 'toggling twice did not hide it again')
    end,
  },
  {
    'tree_object_actions',
    function()
      local t = fake_tree()
      local got = {}
      tree.setup({
        info = function(conn, name, node)
          got.info = { conn, name, node.name, node.kind }
        end,
        script = function(conn, name, node)
          got.script = { conn, name, node.name, node.identity }
        end,
      })
      tree.open('c1', 'a')
      t.flush()
      t.flush()
      t.cursor_on('public')
      tree.activate()
      t.flush()
      t.cursor_on('Functions')
      tree.activate()
      t.flush()
      t.cursor_on('add_one')
      tree.activate() -- Enter on an object opens its info
      eq(got.info, { 'c1', 'a', 'add_one', 'function' }, 'info from Enter')
      tree.script()
      eq(got.script, { 'c1', 'a', 'add_one', 'a integer' }, 'script carries the overload identity')
      got = {}
      t.cursor_on('public')
      tree.info()
      tree.script()
      eq(got, {}, 'info and script on a schema must do nothing')
    end,
  },
  {
    'tree_shows_errors_and_stays_closed',
    function()
      local t = fake_tree({ fail = 'app/public' })
      local shown
      tree.setup({
        notify = function(msg)
          shown = msg
        end,
      })
      tree.open('c1', 'a')
      t.flush()
      t.flush()
      t.cursor_on('public')
      tree.activate()
      t.flush()
      eq(shown, 'permission denied', 'the failure is shown')
      expect(not table.concat(tree.lines(), '\n'):find('Tables', 1, true), 'a failed node showed children')
      expect(table.concat(tree.lines(), '\n'):find('▸ public', 1, true), 'a failed node should stay closed so it can be tried again')
      t.fail_next = nil
    end,
  },
  {
    'info_key_maps_only_sql_buffers',
    function()
      local dbbliss = require('dbbliss')
      local called = {}
      local original = dbbliss.info
      dbbliss.info = function(...)
        called[#called + 1] = { ... }
      end
      local function map_for(buf, lhs)
        for _, km in ipairs(vim.api.nvim_buf_get_keymap(buf, 'n')) do
          if km.lhs:lower() == lhs:lower() then
            return km
          end
        end
        return {}
      end
      local sql = vim.api.nvim_create_buf(true, false)
      vim.bo[sql].filetype = 'sql'
      local lua_buf = vim.api.nvim_create_buf(true, false)
      vim.bo[lua_buf].filetype = 'lua'

      dbbliss.setup({ mappings = { info = '<M-F1>' } })
      local m = map_for(sql, '<M-F1>')
      expect(m.buffer == sql, 'the key is not buffer-local in an existing SQL buffer')
      expect(next(map_for(lua_buf, '<M-F1>')) == nil, 'the key leaked into a non-SQL buffer')
      m.callback()
      eq(#called, 1, 'the key calls info once')

      -- A buffer that becomes SQL later is mapped too.
      local later = vim.api.nvim_create_buf(true, false)
      vim.bo[later].filetype = 'sql'
      expect(map_for(later, '<M-F1>').buffer == later, 'a buffer that turned SQL later has no key')

      local later_lua = vim.api.nvim_create_buf(true, false)
      vim.bo[later_lua].filetype = 'lua'
      expect(next(map_for(later_lua, '<M-F1>')) == nil, 'the key leaked into a non-SQL buffer opened later')

      -- Another key replaces the first for buffers set up after it; false turns the feature off.
      dbbliss.setup({ mappings = { info = '<M-F2>' } })
      local fresh = vim.api.nvim_create_buf(true, false)
      vim.bo[fresh].filetype = 'sql'
      expect(map_for(fresh, '<M-F2>').buffer == fresh, 'the configured key is not mapped')
      expect(next(map_for(fresh, '<M-F1>')) == nil, 'the old key is still mapped in a new buffer')
      dbbliss.setup({ mappings = { info = false } })
      local off = vim.api.nvim_create_buf(true, false)
      vim.bo[off].filetype = 'sql'
      expect(next(map_for(off, '<M-F1>')) == nil and next(map_for(off, '<M-F2>')) == nil, 'mappings = false still mapped a key')
      dbbliss.info = original
    end,
  },
  {
    'catalog_subcommands_dispatch',
    function()
      local dbbliss = require('dbbliss')
      local saved = { dbbliss.info, dbbliss.tree, dbbliss.script_object }
      local got = {}
      dbbliss.info = function(name)
        got.info = name
      end
      dbbliss.tree = function()
        got.tree = true
      end
      dbbliss.script_object = function(name)
        got.script = name
      end
      dbbliss.command({ args = 'info public.customer', range = 0 })
      eq(got.info, 'public.customer', 'info passes the name')
      dbbliss.command({ args = 'info "Order Line"', range = 0 })
      eq(got.info, '"Order Line"', 'info keeps a quoted name together')
      dbbliss.command({ args = 'info', range = 0 })
      eq(got.info, '', 'info without a name asks for the one under the cursor')
      dbbliss.command({ args = 'tree', range = 0 })
      expect(got.tree, 'tree is not dispatched')
      dbbliss.command({ args = 'script dbo.T', range = 0 })
      eq(got.script, 'dbo.T', 'script passes the name')
      for _, sub in ipairs({ 'info', 'tree', 'script' }) do
        expect(vim.tbl_contains(dbbliss.complete('', 'Dbbliss '), sub), sub .. ' is not completed')
      end
      eq(dbbliss.complete('sc', 'Dbbliss sc'), { 'script' }, 'completion narrows')
      dbbliss.info, dbbliss.tree, dbbliss.script_object = saved[1], saved[2], saved[3]
    end,
  },
  {
    'tree_forgets_a_disconnected_connection',
    function()
      local t = fake_tree()
      tree.open('c1', 'a')
      t.flush()
      t.flush()
      tree.forget('c1')
      eq(tree.lines(), { 'no connection' }, 'after forget')
      tree.open('c1', 'a')
      eq(t.calls[#t.calls], '', 'a reconnected connection loads from the roots again')
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
