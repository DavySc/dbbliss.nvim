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
