-- Management UI tests (Phase 4): the progress window, the typed-name confirmation, the backup-first
-- flow, with the backend replaced by a function. No database.
--
--   nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/management_test.lua
vim.fs.joinpath = vim.fs.joinpath or function(...)
  return table.concat({ ... }, '/')
end
vim.notify = function() end

local management = require('dbbliss.management')
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

local function lines_of(win)
  return table.concat(vim.api.nvim_buf_get_lines(vim.api.nvim_win_get_buf(win), 0, -1, false), '\n')
end

--- A management UI whose backend is a function. `answers` maps a method to its result or error.
local function fake(opts)
  opts = opts or {}
  local f = { calls = {}, notes = {}, inputs = {}, confirms = {}, refreshed = 0 }
  f.typed = opts.typed -- what the user types for the name; nil: the right name
  f.path = opts.path or '/backups/x.dump'
  f.backup_yes = opts.backup_yes ~= false
  f.next_id = 0
  management.setup({
    request = function(conn_id, method, params, cb)
      f.calls[#f.calls + 1] = { conn_id = conn_id, method = method, params = params }
      local answer = opts.answers and opts.answers[method]
      if answer and answer.err then
        return cb(answer.err, nil)
      end
      if method == 'backup/start' then
        f.next_id = f.next_id + 1
        return cb(nil, { backup_id = 'b' .. f.next_id })
      elseif method == 'backup/defaults' then
        return cb(nil, { directory = opts.directory or vim.NIL })
      elseif method == 'management/drop' then
        return cb(nil, { dropped = true })
      end
      cb(nil, {})
    end,
    notify = function(msg, level)
      f.notes[#f.notes + 1] = { msg = msg, level = level }
    end,
    input = function(prompt, default)
      f.inputs[#f.inputs + 1] = { prompt = prompt, default = default }
      if prompt:find('Type the name', 1, true) then
        return f.typed or opts.name or 't'
      end
      return f.path
    end,
    confirm = function(text, yes_label, default_yes)
      f.confirms[#f.confirms + 1] = { text = text, yes = yes_label, default_yes = default_yes }
      return f.backup_yes
    end,
  })
  function f.methods()
    return vim.tbl_map(function(c)
      return c.method
    end, f.calls)
  end
  function f.progress(id, phase, text, percent)
    management.handle_notification('backup/progress', { backup_id = id, phase = phase, text = text, percent = percent == nil and vim.NIL or percent })
  end
  function f.done(id, over)
    management.handle_notification('backup/done', vim.tbl_extend('force', {
      backup_id = id, status = 'completed', verified = true, path = f.path, bytes = 2048, detail = 'pg_dump 17', error = vim.NIL, note = vim.NIL,
    }, over or {}))
  end
  function f.window()
    for _, win in ipairs(vim.api.nvim_list_wins()) do
      if vim.api.nvim_win_get_config(win).relative ~= '' then
        return win
      end
    end
  end
  return f
end

local function target(over)
  local t = vim.tbl_extend('force', { conn_id = 'c1', conn_name = 'main', engine = 'postgres', env = 'dev', database = 'app' }, over or {})
  if t.env == false then
    t.env = nil -- an untagged connection
  end
  return t
end

local function drop_target(over)
  return vim.tbl_extend('force', target(), { kind = 'table', schema = 'public', name = 't' }, over or {})
end

local function close_windows()
  for _, win in ipairs(vim.api.nvim_list_wins()) do
    if vim.api.nvim_win_get_config(win).relative ~= '' then
      pcall(vim.api.nvim_win_close, win, true)
    end
  end
end

local tests = {
  {
    -- Verifies: LLR-MGT-15
    'management_progress_window',
    function()
      local f = fake()
      management.backup(target())
      local win = f.window()
      expect(win, 'no floating window')
      eq(f.calls[1].method, 'backup/start', 'starts the backup')
      eq(f.calls[1].params, { database = 'app', path = '/backups/x.dump' }, 'with the database and the path')
      f.progress('b1', 'dump', 'pg_dump: dumping contents of table "public.t"', vim.NIL)
      f.progress('b1', 'backup', '45 percent processed.', 45)
      local text = lines_of(win)
      expect(text:find('dumping contents of table', 1, true), 'phase text: ' .. text)
      expect(text:find('45%', 1, true), 'percent: ' .. text)
      expect(text:find('running', 1, true) or text:find('Running', 1, true), 'says it runs: ' .. text)
      f.done('b1')
      text = lines_of(win)
      expect(text:find('completed', 1, true) and text:find('/backups/x.dump', 1, true) and text:find('2048', 1, true) and text:find('verified', 1, true), 'result: ' .. text)
      eq(f.notes[#f.notes].level, vim.log.levels.INFO, 'a completed backup is info')
      -- q closes it
      vim.api.nvim_set_current_win(win)
      vim.fn.maparg('q', 'n', false, true).callback()
      expect(not vim.api.nvim_win_is_valid(win), 'q closes the window')
    end,
  },
  {
    -- Verifies: LLR-MGT-15
    'management_failure_cancel_and_note',
    function()
      local f = fake()
      management.backup(target())
      f.done('b1', { status = 'failed', verified = false, error = 'pg_dump exited with code 1: boom', path = vim.NIL, bytes = vim.NIL, note = 'The server may have left a partial file at /x.bak.' })
      local text = lines_of(f.window())
      expect(text:find('FAILED', 1, true) and text:find('boom', 1, true), 'the error is in the window: ' .. text)
      expect(text:find('partial file', 1, true), 'the note is in the window: ' .. text)
      eq(f.notes[#f.notes].level, vim.log.levels.ERROR, 'a failure is an error notification too')
      expect(f.notes[#f.notes].msg:find('boom', 1, true), 'with the reason')
      close_windows()

      local g = fake()
      management.backup(target())
      g.done('b1', { status = 'cancelled', verified = false, path = vim.NIL, bytes = vim.NIL })
      expect(lines_of(g.window()):find('cancelled', 1, true), 'cancelled is said')
      eq(g.notes[#g.notes].level, vim.log.levels.WARN, 'cancelled is a warning')
      close_windows()
    end,
  },
  {
    -- Verifies: LLR-MGT-15
    'management_c_cancels_a_running_backup',
    function()
      local f = fake()
      management.backup(target())
      local win = f.window()
      vim.api.nvim_set_current_win(win)
      vim.fn.maparg('c', 'n', false, true).callback()
      eq(f.calls[#f.calls].method, 'backup/cancel', 'cancel is sent')
      eq(f.calls[#f.calls].params, { backup_id = 'b1' }, 'for this backup')
      f.done('b1')
      local before = #f.calls
      vim.fn.maparg('c', 'n', false, true).callback()
      eq(#f.calls, before, 'nothing to cancel once it has ended')
      close_windows()
    end,
  },
  {
    -- Verifies: LLR-MGT-16
    'management_refusals_and_errors_are_shown',
    function()
      local f = fake({ answers = { ['backup/start'] = { err = { code = 1009, message = 'pg_dump was not found on PATH.' } } } })
      management.backup(target())
      eq(f.notes[#f.notes].level, vim.log.levels.WARN, 'a refusal is a warning')
      expect(f.notes[#f.notes].msg:find('pg_dump was not found', 1, true), 'with its text')
      expect(f.window() and lines_of(f.window()):find('pg_dump was not found', 1, true), 'and in the window')
      close_windows()

      local g = fake({ answers = { ['management/drop'] = { err = { code = 1005, message = 'database is being accessed by other users' } } } })
      local refreshed = 0
      management.drop(drop_target({ refresh = function() refreshed = refreshed + 1 end, backup_first = false }))
      eq(g.notes[#g.notes].level, vim.log.levels.ERROR, 'a server error is an error')
      expect(g.notes[#g.notes].msg:find('accessed by other users', 1, true), 'with the server text')
      eq(refreshed, 0, 'the tree is not refreshed after a failure')
      close_windows()
    end,
  },
  {
    -- Verifies: LLR-MGT-16
    'management_drop_needs_the_typed_name',
    function()
      local f = fake({ typed = 'T' })
      management.drop(drop_target())
      eq(f.methods(), {}, 'a wrong name sends nothing, not even a question about a backup')
      expect(f.notes[#f.notes].msg:find('did not match', 1, true), 'the user is told: ' .. f.notes[#f.notes].msg)
      f.typed = ''
      management.drop(drop_target())
      eq(f.methods(), {}, 'an empty answer sends nothing')
      f.typed = vim.NIL
      management.drop(drop_target())
      eq(f.methods(), {}, 'escape sends nothing')

      local g = fake({ backup_yes = false })
      local refreshed = 0
      management.drop(drop_target({ refresh = function() refreshed = refreshed + 1 end }))
      expect(g.inputs[1].prompt:find('Type the name', 1, true) and g.inputs[1].prompt:find('"t"', 1, true), 'the prompt names the object: ' .. g.inputs[1].prompt)
      eq(#g.confirms, 1, 'a backup is offered')
      eq(g.confirms[1].default_yes, true, 'the offer defaults to yes')
      eq(g.methods(), { 'management/drop' }, 'declined: the drop goes without a backup')
      eq(g.calls[1].params, { kind = 'table', database = 'app', schema = 'public', name = 't', identity = vim.NIL, confirm_name = 't' }, 'with the typed name')
      eq(refreshed, 1, 'the tree is refreshed after the drop')
      eq(g.notes[#g.notes].level, vim.log.levels.INFO, 'and the user is told')
    end,
  },
  {
    -- Verifies: LLR-MGT-16
    'management_backup_first_then_drop',
    function()
      local f = fake()
      management.drop(drop_target())
      eq(f.methods(), { 'backup/start' }, 'accepted: the backup starts, the drop waits')
      f.done('b1')
      eq(f.methods(), { 'backup/start', 'management/drop' }, 'the drop follows the completed backup')
      eq(f.calls[2].params.backup_id, 'b1', 'carrying the backup id')
      close_windows()

      local g = fake()
      management.drop(drop_target())
      g.done('b1', { status = 'failed', verified = false, error = 'boom', path = vim.NIL })
      eq(g.methods(), { 'backup/start' }, 'a failed backup does not lead to a drop')
      expect(g.notes[#g.notes].msg:find('not dropped', 1, true) or g.notes[#g.notes].msg:find('boom', 1, true), 'the user is told: ' .. g.notes[#g.notes].msg)
      close_windows()

      local h = fake()
      management.drop(drop_target())
      h.done('b1', { status = 'cancelled', verified = false })
      eq(h.methods(), { 'backup/start' }, 'a cancelled backup does not lead to a drop')
      close_windows()

      -- A database drop backs up that database; an object drop backs up the database it is in.
      local i = fake({ name = 'other' })
      management.drop(drop_target({ kind = 'database', name = 'other', database = 'other', schema = vim.NIL }))
      eq(i.calls[1].params.database, 'other', 'the database being dropped is backed up')
      close_windows()
    end,
  },
  {
    -- Verifies: LLR-MGT-16
    'management_prod_backs_up_without_asking',
    function()
      for _, env in ipairs({ 'prod', vim.NIL }) do
        local f = fake({ backup_yes = false })
        management.drop(drop_target({ env = env ~= vim.NIL and env or false }))
        eq(#f.confirms, 0, 'no choice on prod or an untagged connection')
        eq(f.methods(), { 'backup/start' }, 'the backup is made')
        f.done('b1')
        eq(f.methods(), { 'backup/start', 'management/drop' }, 'then the drop')
        eq(f.calls[2].params.backup_id, 'b1', 'with the id')
        close_windows()
      end
    end,
  },
  {
    -- Verifies: LLR-MGT-15
    'management_default_paths',
    function()
      local f = fake({ path = '' })
      management.backup(target())
      eq(#f.inputs, 1, 'the path is asked')
      expect(f.inputs[1].default:find('app-', 1, true) and f.inputs[1].default:match('%.dump$'), 'PostgreSQL: a .dump in the working directory named after the database: ' .. f.inputs[1].default)
      eq(f.methods(), {}, 'an empty path sends nothing')

      local g = fake({ directory = 'C:\\SQL\\Backup', path = 'C:\\SQL\\Backup\\x.bak' })
      management.backup(target({ engine = 'sqlserver' }))
      eq(g.methods(), { 'backup/defaults', 'backup/start' }, 'SQL Server asks the server for its folder')
      expect(g.inputs[1].default:find('C:\\SQL\\Backup\\app-', 1, true) and g.inputs[1].default:match('%.bak$'), 'a Windows folder keeps its separators: ' .. g.inputs[1].default)
      close_windows()

      local h = fake({ directory = '/var/opt/mssql/data', path = '/b/x.bak' })
      management.backup(target({ engine = 'sqlserver' }))
      expect(h.inputs[1].default:find('/var/opt/mssql/data/app-', 1, true), 'a Linux folder too: ' .. h.inputs[1].default)
      close_windows()

      local i = fake({ directory = vim.NIL, path = '/b/x.bak' })
      management.backup(target({ engine = 'sqlserver' }))
      expect(i.inputs[1].default:match('^app%-') or i.inputs[1].default:match('app%-'), 'no default folder: just a file name to complete: ' .. i.inputs[1].default)
      close_windows()
    end,
  },
  {
    -- Verifies: LLR-MGT-17
    'management_database_of_a_connection_string',
    function()
      local of = require('dbbliss')._database_of
      eq(of('Host=h;Port=1;Username=u;Database=app'), 'app', 'PostgreSQL')
      eq(of('Server=s;Initial Catalog=Sales;Trusted_Connection=yes'), 'Sales', 'SQL Server initial catalog')
      eq(of('Server=s; database = x y ;'), 'x y', 'spaces around the key and value')
      eq(of('Server=s;User Id=sa'), nil, 'none named')
      eq(of('Server=s;Database='), nil, 'an empty one is none')
      eq(of(nil), nil, 'no string')
    end,
  },
  {
    -- Verifies: LLR-MGT-17
    'management_commands_pass_the_connections_tag_and_database',
    function()
      local dbbliss = require('dbbliss')
      local st = dbbliss._state
      local saved = { config = st.config, connections = st.connections, current = st.current, backup = management.backup, drop = management.drop, refresh_all = tree.refresh_all, tree_drop = tree.drop, notify = vim.notify, tree_buf = tree._state.buf }
      st.config = { connections = { main = { engine = 'postgres', connection_string = 'Host=h;Database=app', env = 'prod' } } }
      st.connections = { main = { id = 'c1' } }
      st.current = 'main'
      local got, notes
      management.backup = function(t)
        got = t
      end
      management.drop = function(t)
        got = t
      end
      vim.notify = function(msg)
        notes = msg
      end
      dbbliss.backup('/x.dump')
      eq({ got.conn_id, got.conn_name, got.engine, got.env, got.database, got.path }, { 'c1', 'main', 'postgres', 'prod', 'app', '/x.dump' }, 'backup passes the connection and its tag')
      dbbliss.backup('')
      eq(got.path, nil, 'an empty path is none: the plugin asks')
      -- No database in the connection string: it is asked; giving none backs up nothing.
      st.config.connections.main.connection_string = 'Server=s'
      got = nil
      local input = vim.fn.input
      vim.fn.input = function()
        return ''
      end
      dbbliss.backup('/x.dump')
      expect(got == nil, 'no database, nothing backed up')
      vim.fn.input = function()
        return 'chosen'
      end
      dbbliss.backup('/x.dump')
      eq(got.database, 'chosen', 'the database that was typed')
      vim.fn.input = input
      -- A node of the tree.
      local refreshed = 0
      tree.refresh_all = function()
        refreshed = refreshed + 1
      end
      dbbliss._drop_node('main', { kind = 'table', name = 't', identity = nil, path = { database = 'app', schema = 'public' } })
      eq({ got.kind, got.database, got.schema, got.name, got.env, got.conn_id }, { 'table', 'app', 'public', 't', 'prod', 'c1' }, 'a tree node becomes a drop')
      got.refresh()
      eq(refreshed, 1, 'the tree is refreshed after a drop')
      st.config.connections.main.connection_string = 'Server=s;Initial Catalog=Sales'
      dbbliss._drop_node('main', { kind = 'database', name = 'other', path = { database = 'other' } })
      eq(got.database, 'other', 'a database drop names the database')
      dbbliss._drop_node('gone', { kind = 'table', name = 't', path = {} })
      expect(notes and notes:find('not connected', 1, true), 'a connection that is not there: ' .. tostring(notes))
      -- :Dbbliss drop outside the tree says how; inside it, it is the D key.
      notes = nil
      dbbliss.drop()
      expect(notes and notes:find('press D', 1, true), 'outside the tree: ' .. tostring(notes))
      local dropped
      tree.drop = function()
        dropped = true
      end
      vim.api.nvim_set_current_buf(tree._state.buf or vim.api.nvim_create_buf(false, true))
      tree._state.buf = vim.api.nvim_get_current_buf()
      dbbliss.drop()
      expect(dropped, 'inside the tree it is the tree\'s drop')
      st.config, st.connections, st.current = saved.config, saved.connections, saved.current
      management.backup, management.drop, tree.refresh_all, tree.drop, vim.notify = saved.backup, saved.drop, saved.refresh_all, saved.tree_drop, saved.notify
      tree._state.buf = saved.tree_buf
    end,
  },
  {
    -- Verifies: LLR-MGT-17
    'management_tree_key_and_commands',
    function()
      local dropped
      tree.forget('c1')
      tree.setup({
        request = function(_, _, params, cb)
          if not params.database then
            cb(nil, { nodes = { { name = 'app', kind = 'database', expandable = true, browsable = true } } })
          elseif not params.schema then
            cb(nil, { nodes = { { name = 'public', kind = 'schema', expandable = true } } })
          elseif not params.folder then
            cb(nil, { nodes = { { name = 'Tables', kind = 'folder', expandable = true, folder = 'table' } } })
          else
            cb(nil, { nodes = { { name = 'customer', kind = 'table' } } })
          end
        end,
        info = function() end,
        script = function() end,
        drop = function(conn_id, conn_name, node)
          dropped = { conn_id = conn_id, conn_name = conn_name, node = node }
        end,
        notify = function() end,
        show_system = false,
      })
      tree.open('c1', 'main')
      for _ = 1, 4 do
        for _, node in pairs(tree._state.line_nodes) do
          if node.expandable and not node.expanded then
            tree.expand(node)
          end
        end
      end
      local win = vim.fn.win_findbuf(tree._state.buf)[1]
      local found
      for l, n in pairs(tree._state.line_nodes) do
        if n.name == 'customer' then
          vim.api.nvim_win_set_cursor(win, { l, 0 })
          found = true
        end
      end
      expect(found, 'the table is in the tree')
      vim.api.nvim_set_current_win(win)
      vim.fn.maparg('D', 'n', false, true).callback()
      expect(dropped and dropped.node.name == 'customer' and dropped.conn_id == 'c1' and dropped.conn_name == 'main', 'D drops the node under the cursor')
      eq(dropped.node.path.schema, 'public', 'with its path')
      for l, n in pairs(tree._state.line_nodes) do
        if n.kind == 'folder' then
          vim.api.nvim_win_set_cursor(win, { l, 0 })
        end
      end
      dropped = nil
      vim.fn.maparg('D', 'n', false, true).callback()
      expect(dropped == nil, 'a folder is not droppable')

      local dbbliss = require('dbbliss')
      local saved = { dbbliss.backup, dbbliss.drop }
      local got = {}
      dbbliss.backup = function(path)
        got.backup = path
      end
      dbbliss.drop = function()
        got.drop = true
      end
      dbbliss.command({ args = 'backup /tmp/x.dump', range = 0 })
      eq(got.backup, '/tmp/x.dump', 'backup passes the path')
      dbbliss.command({ args = 'drop', range = 0 })
      expect(got.drop, 'drop is dispatched')
      for _, sub in ipairs({ 'backup', 'drop' }) do
        expect(vim.tbl_contains(dbbliss.complete('', 'Dbbliss '), sub), sub .. ' is not completed')
      end
      dbbliss.backup, dbbliss.drop = saved[1], saved[2]
      tree.forget('c1')
    end,
  },
}

local failed = 0
for _, t in ipairs(tests) do
  close_windows()
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
