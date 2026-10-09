-- End to end through the real plugin and the published backend, against a real database:
-- scopes, paging, fetch-more, cancel, export. Needs the backend built (scripts/build.sh) and
--
--   DBBLISS_E2E_ENGINE=postgres|sqlserver  DBBLISS_E2E_CS=<connection string>
--   and, for the password, one of DBBLISS_E2E_PW_ENV=<env var> | DBBLISS_E2E_CREDMAN=<credential target> | DBBLISS_E2E_PASS=<pass entry>
--
--   nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/e2e_test.lua
--
-- Exits 1 if any step fails.
vim.fs.joinpath = vim.fs.joinpath or function(...)
  return table.concat({ ... }, '/')
end
local env = vim.env
local engine = assert(env.DBBLISS_E2E_ENGINE, 'DBBLISS_E2E_ENGINE not set')

local dbbliss = require('dbbliss')
local results = require('dbbliss.results')
local tree = require('dbbliss.tree')
vim.notify = function() end

dbbliss.setup({
  results = { window_rows = 1000, max_col_width = 40 },
  connections = {
    db = {
      engine = engine,
      connection_string = assert(env.DBBLISS_E2E_CS, 'DBBLISS_E2E_CS not set'),
      -- One of: an environment variable, Windows Credential Manager, or the pass store.
      password = (env.DBBLISS_E2E_PW_ENV and { env = env.DBBLISS_E2E_PW_ENV })
        or (env.DBBLISS_E2E_CREDMAN and { credman = env.DBBLISS_E2E_CREDMAN })
        or (env.DBBLISS_E2E_PASS and { pass = env.DBBLISS_E2E_PASS })
        or nil,
    },
  },
})

local function expect(cond, msg)
  if not cond then
    error(msg, 0)
  end
end

local function text()
  return table.concat(results.lines(), '\n')
end

--- The start and end of the results buffer, for failure messages (it can hold thousands of rows).
local function excerpt()
  local lines = results.lines()
  if #lines <= 40 then
    return table.concat(lines, '\n')
  end
  return table.concat(vim.list_slice(lines, 1, 15), '\n')
    .. ('\n... %d lines ...\n'):format(#lines - 30)
    .. table.concat(vim.list_slice(lines, #lines - 14), '\n')
end

local function wait(what, cond, timeout)
  if not vim.wait(timeout or 20000, cond, 20) then
    error('timed out waiting for ' .. what .. '\n' .. excerpt(), 0)
  end
end

local function buffer(lines)
  local b = vim.api.nvim_create_buf(false, true)
  vim.api.nvim_set_current_buf(b)
  vim.api.nvim_buf_set_lines(b, 0, -1, false, lines)
  return b
end

local function idle()
  return next(dbbliss._state.scripts) == nil and next(dbbliss._state.queries) == nil
end

local pg = engine == 'postgres'
local series = pg and 'select n, repeat(\'x\', 3) as pad from generate_series(1, 2500) n'
  or 'select top 2500 row_number() over (order by (select 1)) as n, replicate(\'x\', 3) as pad from sys.all_objects a cross join sys.all_objects b'
local big = pg and "select n, repeat('x', 3) as pad from generate_series(1, 5000000) n"
  or "select row_number() over (order by (select 1)) as n, replicate('x', 3) as pad from sys.all_objects a cross join sys.all_objects b cross join sys.all_objects c"
local sleep = pg and 'select pg_sleep(30)' or "waitfor delay '00:00:30'"
local sep = pg and ';\n' or '\nGO\n'

local steps = {
  {
    'connect',
    function()
      local done
      dbbliss.connect('db', function(err)
        expect(not err, 'connect failed: ' .. tostring(err and err.message))
        done = true
      end)
      wait('connect', function()
        return done
      end)
    end,
  },
  {
    'script_runs_every_statement_and_stops_at_an_error',
    function()
      buffer(vim.split('select 1 as a, \'x\' as b' .. sep .. 'select * from dbbliss_no_such_table' .. sep .. 'select 3 as never', '\n'))
      dbbliss.run('buffer')
      wait('the script', idle)
      local t = text()
      expect(t:find('a │ b', 1, true), 'first table missing:\n' .. excerpt())
      expect(t:find('(1 row)', 1, true), 'row count missing:\n' .. excerpt())
      expect(t:find('-- error (line 3)', 1, true) or t:find('-- error (line 2)', 1, true), 'error line missing:\n' .. excerpt())
      expect(t:find('stopped: ', 1, true), 'stop notice missing:\n' .. excerpt())
      expect(not t:find('never', 1, true), 'a statement after the failing one ran:\n' .. excerpt())
      expect(#vim.diagnostic.get(0) == 1, 'the error was not marked in the buffer')
    end,
  },
  {
    'statement_under_cursor',
    function()
      buffer(vim.split('select 11 as first_one' .. sep .. 'select 22 as second_one', '\n'))
      vim.api.nvim_win_set_cursor(0, { pg and 1 or 3, 0 })
      -- PostgreSQL: line 1 is the first statement. SQL Server: line 3 is the second batch.
      dbbliss.run('statement')
      wait('the statement', idle)
      local t = text()
      expect(t:find(pg and 'first_one' or 'second_one', 1, true), 'the wrong statement ran:\n' .. excerpt())
      expect(not t:find(pg and 'second_one' or 'first_one', 1, true), 'both statements ran:\n' .. excerpt())
    end,
  },
  {
    'paging_pauses_and_fetch_continues',
    function()
      buffer({ series })
      dbbliss.run('buffer')
      wait('the pause', function()
        return text():find('gm to fetch', 1, true)
      end)
      local set = results._state.sets[#results._state.sets]
      expect(#set.rows == 1000, 'expected the window of 1000 rows, got ' .. #set.rows)
      results.fetch_more()
      wait('the second pause', function()
        return #set.rows == 2000 and text():find('gm to fetch', 1, true)
      end)
      results.fetch_more()
      wait('the end', idle)
      expect(#set.rows == 2500, 'expected 2500 rows in the end, got ' .. #set.rows)
      expect(text():find('(2500 rows)', 1, true), 'row count missing')
      expect(set.rows[2500][1] == 2500, 'the last row is not 2500')
    end,
  },
  {
    'cancel_a_paused_query',
    function()
      buffer({ big })
      dbbliss.run('buffer')
      wait('the pause', function()
        return text():find('gm to fetch', 1, true)
      end)
      dbbliss.cancel()
      wait('the cancel', idle)
      expect(text():find('cancelled in', 1, true), 'cancelled status missing:\n' .. excerpt())
    end,
  },
  {
    'cancel_a_sleeping_query',
    function()
      buffer({ sleep })
      dbbliss.run('buffer')
      wait('the query to start', function()
        return next(dbbliss._state.queries) ~= nil
      end)
      vim.wait(300)
      local started = vim.uv.hrtime()
      dbbliss.cancel()
      wait('the cancel', idle, 5000)
      expect((vim.uv.hrtime() - started) / 1e6 < 3000, 'the cancel took too long')
      expect(text():find('cancelled in', 1, true), 'cancelled status missing')
    end,
  },
  {
    'export_to_csv',
    function()
      local dir = vim.fn.tempname()
      vim.fn.mkdir(dir, 'p')
      local path = dir .. '/out.csv'
      buffer({ series })
      dbbliss.export(path, 'statement')
      wait('the export', idle)
      local lines = vim.fn.readfile(path)
      expect(#lines == 2501, 'expected a header and 2500 rows, got ' .. #lines)
      expect(lines[1] == 'n,pad', 'header is ' .. lines[1])
      expect(lines[2] == '1,xxx', 'first row is ' .. lines[2])
      expect(text():find('exported 2500 rows', 1, true), 'export notice missing:\n' .. excerpt())
    end,
  },
  {
    'catalog_setup',
    function()
      -- A small table of our own, in the schema the connection reaches without qualification.
      buffer(vim.split(pg and 'drop table if exists e2e_cat_t;\ncreate table e2e_cat_t (id int primary key, v text not null default \'x\')'
        or "if object_id('dbo.e2e_cat_t') is not null drop table dbo.e2e_cat_t\nGO\ncreate table dbo.e2e_cat_t (id int not null primary key, v nvarchar(10) not null default N'x')", '\n'))
      dbbliss.run('buffer')
      wait('the table', idle)
      expect(not text():find('stopped:', 1, true), 'setup failed:\n' .. excerpt())
    end,
  },
  {
    'info_for_the_name_under_the_cursor',
    function()
      buffer({ 'select * from e2e_cat_t where id = 1' })
      vim.api.nvim_win_set_cursor(0, { 1, 17 })
      dbbliss.info()
      wait('the info buffer', function()
        return vim.fn.bufnr('dbbliss://info/db/', false) ~= -1 or #vim.tbl_filter(function(b)
          return vim.api.nvim_buf_get_name(b):find('dbbliss://info/db/', 1, true) ~= nil
        end, vim.api.nvim_list_bufs()) > 0
      end)
      local info_buf
      for _, b in ipairs(vim.api.nvim_list_bufs()) do
        if vim.api.nvim_buf_get_name(b):find('dbbliss://info/db/', 1, true) then
          info_buf = b
        end
      end
      local lines = table.concat(vim.api.nvim_buf_get_lines(info_buf, 0, -1, false), '\n')
      expect(lines:find('e2e_cat_t', 1, true), 'the title does not name the table:\n' .. lines)
      expect(lines:find('── Columns (2) ──', 1, true), 'two columns expected:\n' .. lines)
      expect(lines:find('── Indexes (1) ──', 1, true), 'the primary key index is missing:\n' .. lines)
      expect(lines:find('id', 1, true) and lines:find('v', 1, true), 'column names missing')
    end,
  },
  {
    'schema_tree_to_an_object',
    function()
      dbbliss.tree()
      if not pg then
        -- master is a system database, hidden by default
        tree.toggle_system()
      end
      wait('the databases', function()
        return #tree.lines() > 1 and not table.concat(tree.lines(), '\n'):find('loading', 1, true)
      end)
      local path = pg and { 'public', 'Tables' } or { 'master', 'dbo', 'Tables' }
      for _, name in ipairs(path) do
        wait('the line ' .. name, function()
          for _, n in pairs(tree._state.line_nodes) do
            if n.name == name then
              return true
            end
          end
        end)
        for l, n in pairs(tree._state.line_nodes) do
          if n.name == name then
            vim.api.nvim_win_set_cursor(vim.fn.win_findbuf(tree._state.buf)[1], { l, 0 })
            if not n.expanded then
              tree.activate()
            end
          end
        end
      end
      wait('the table in the tree', function()
        for _, n in pairs(tree._state.line_nodes) do
          if n.name == 'e2e_cat_t' then
            return true
          end
        end
      end)
      for l, n in pairs(tree._state.line_nodes) do
        if n.name == 'e2e_cat_t' then
          vim.api.nvim_win_set_cursor(vim.fn.win_findbuf(tree._state.buf)[1], { l, 0 })
        end
      end
      local before = #vim.api.nvim_list_bufs()
      tree.script()
      wait('the script buffer', function()
        return #vim.api.nvim_list_bufs() > before
      end)
      local first = vim.api.nvim_buf_get_lines(0, 0, 1, false)[1]
      expect(first:upper():find('CREATE TABLE', 1, true), 'the script does not start with CREATE TABLE: ' .. tostring(first))
      expect(vim.bo.filetype == 'sql', 'the script buffer is not SQL')
    end,
  },
  {
    'script_by_name_round_trip',
    function()
      local before = #vim.api.nvim_list_bufs()
      dbbliss.script_object('e2e_cat_t')
      wait('the script buffer', function()
        return #vim.api.nvim_list_bufs() > before
      end)
      local script = table.concat(vim.api.nvim_buf_get_lines(0, 0, -1, false), '\n')
      -- Drop the table, run the script from the buffer, and look at the table again.
      buffer({ pg and 'drop table e2e_cat_t' or 'drop table dbo.e2e_cat_t' })
      dbbliss.run('buffer')
      wait('the drop', idle)
      buffer(vim.split(script, '\n'))
      dbbliss.run('buffer')
      wait('the script to run', idle)
      expect(not text():find('stopped:', 1, true), 'the script did not run:\n' .. excerpt())
      buffer({ 'select count(*) as n from ' .. (pg and 'e2e_cat_t' or 'dbo.e2e_cat_t') })
      dbbliss.run('buffer')
      wait('the select', idle)
      expect(text():find('(1 row)', 1, true), 'the recreated table cannot be read:\n' .. excerpt())
    end,
  },
  {
    'catalog_cleanup',
    function()
      buffer({ pg and 'drop table if exists e2e_cat_t' or "if object_id('dbo.e2e_cat_t') is not null drop table dbo.e2e_cat_t" })
      dbbliss.run('buffer')
      wait('the cleanup', idle)
    end,
  },
}

local failed = 0
for _, step in ipairs(steps) do
  local ok, err = pcall(step[2])
  if ok then
    io.stdout:write(('%-52s PASS\n'):format(step[1]))
  else
    failed = failed + 1
    io.stdout:write(('%-52s FAIL  %s\n'):format(step[1], tostring(err)))
  end
end
pcall(dbbliss.on_exit)
io.stdout:write(('\n%d steps, %d failed\n'):format(#steps, failed))
os.exit(failed == 0 and 0 or 1)
