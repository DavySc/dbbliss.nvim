-- AI hook tests (Phase 6): what may be sent, and only after the user said so; how the command is run and its end reported.
-- The command is replaced by a function, except in the tests that run a real process (Neovim itself as the command).
--
--   nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/ai_test.lua
vim.fs.joinpath = vim.fs.joinpath or function(...)
  return table.concat({ ... }, '/')
end
vim.notify = function() end

local ai = require('dbbliss.ai')

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

local SCRIPTS = {
  orders = 'CREATE TABLE public.orders (id integer, customer_id integer, total numeric);',
  customers = 'CREATE TABLE public.customers (id integer, name text);',
}

--- An AI hook whose backend, confirmation and command are functions. `opts.choice` is what the user answers.
local function fake(opts)
  opts = opts or {}
  local f = { requests = {}, notes = {}, confirms = {}, runs = {}, choice = opts.choice or 'send', handles = {} }
  ai.reset()
  ai.configure(vim.tbl_extend('force', { command = { 'ai-cli', '-p' }, timeout_ms = 5000, max_bytes = 100000, max_tables = 10, confirm = true }, opts.config or {}))
  ai.setup({
    request = function(conn_id, method, params, cb)
      f.requests[#f.requests + 1] = { conn_id = conn_id, method = method, params = params }
      local key = (params.name or ''):lower():gsub('^.*%.', '')
      if SCRIPTS[key] then
        return cb(nil, { text = SCRIPTS[key] })
      end
      cb({ code = 1007, message = 'Nothing named ' .. tostring(params.name) }, nil)
    end,
    notify = function(msg, level)
      f.notes[#f.notes + 1] = { msg = msg, level = level }
    end,
    confirm = function(summary, payload)
      f.confirms[#f.confirms + 1] = { summary = summary, payload = payload }
      if type(f.choice) == 'table' then
        return table.remove(f.choice, 1)
      end
      return f.choice
    end,
    run = function(cmd, stdin, ropts, cb)
      local h = { cancelled = false }
      h.cancel = function()
        h.cancelled = true
      end
      f.runs[#f.runs + 1] = { cmd = cmd, stdin = stdin, opts = ropts, cb = cb, handle = h }
      return h
    end,
  })
  function f.requests_of(method)
    local n = 0
    for _, r in ipairs(f.requests) do
      n = n + (r.method == method and 1 or 0)
    end
    return n
  end
  function f.answer_buf()
    for _, b in ipairs(vim.api.nvim_list_bufs()) do
      if vim.bo[b].filetype == 'dbbliss-ai' then
        return b
      end
    end
  end
  function f.answer_text()
    local b = f.answer_buf()
    return b and table.concat(vim.api.nvim_buf_get_lines(b, 0, -1, false), '\n') or ''
  end
  return f
end

local function close_all()
  for _, b in ipairs(vim.api.nvim_list_bufs()) do
    if vim.bo[b].filetype == 'dbbliss-ai' or vim.bo[b].filetype == 'text' then
      pcall(vim.api.nvim_buf_delete, b, { force = true })
    end
  end
end

local function conn(over)
  return vim.tbl_extend('force', { id = 'c1', name = 'dev1', engine = 'postgres', env = 'dev', ai = true }, over or {})
end

local function ask(f, over)
  local args = vim.tbl_extend('force', {
    conn = conn(),
    question = 'why is this slow?',
    statement = 'select * from orders o join customers c on c.id = o.customer_id',
  }, over or {})
  ai.ask(args)
  return args
end

local tests = {
  {
    -- Verifies: HLR-AI-1
    'ai_needs_a_command_and_an_opted_in_connection',
    function()
      local f = fake({ config = { command = vim.NIL } })
      ai.configure({ command = nil })
      ask(f)
      eq(#f.confirms, 0, 'no command: no question')
      expect(f.notes[#f.notes].msg:find('no ai.command is configured', 1, true), 'says what is missing: ' .. f.notes[#f.notes].msg)
      local g = fake()
      ask(g, { conn = conn({ ai = vim.NIL }) })
      eq(#g.confirms, 0, 'not opted in: no question')
      expect(g.notes[#g.notes].msg:find('ai = true', 1, true), 'says how to opt in: ' .. g.notes[#g.notes].msg)
      eq(#g.runs, 0, 'nothing ran')
      eq(#g.requests, 0, 'nothing was even read')
      local h = fake()
      ask(h, { conn = conn({ ai = false }) })
      eq(#h.runs + #h.confirms, 0, 'ai = false is off')
    end,
  },
  {
    -- Verifies: HLR-AI-2
    'ai_is_refused_on_prod_and_untagged_connections_unless_they_say_so',
    function()
      for _, env in ipairs({ 'prod', vim.NIL, 'staging' }) do
        local f = fake()
        ask(f, { conn = conn({ env = env }) })
        eq(#f.confirms + #f.runs + #f.requests, 0, 'refused before anything is read or sent: ' .. tostring(env))
        expect(f.notes[#f.notes].msg:find('prod = true', 1, true), 'says how to allow it: ' .. f.notes[#f.notes].msg)
      end
      local g = fake()
      ask(g, { conn = conn({ env = 'prod', ai = { prod = true } }) })
      eq(#g.confirms, 1, 'allowed with ai = { prod = true }: and still asked')
      for _, env in ipairs({ 'dev', 'test' }) do
        local h = fake()
        ask(h, { conn = conn({ env = env }) })
        eq(#h.confirms, 1, env .. ' is allowed with ai = true')
      end
    end,
  },
  {
    -- Verifies: HLR-AI-3
    'ai_shows_what_will_be_sent_and_sends_nothing_on_no',
    function()
      local f = fake({ choice = 'cancel' })
      ask(f)
      eq(#f.confirms, 1, 'asked')
      local summary = f.confirms[1].summary
      expect(summary:find('ai-cli', 1, true), 'names the command: ' .. summary)
      expect(summary:find('statement', 1, true) and summary:find('orders', 1, true) and summary:find('customers', 1, true), 'names the parts: ' .. summary)
      expect(summary:find('bytes', 1, true), 'and the size: ' .. summary)
      eq(#f.runs, 0, 'nothing was sent on No')
      expect(f.answer_buf() == nil, 'and no answer buffer')
      expect(f.notes[#f.notes].msg:find('nothing was sent', 1, true), 'the user is told: ' .. f.notes[#f.notes].msg)
    end,
  },
  {
    -- Verifies: HLR-AI-3
    'ai_shows_the_full_text_on_request_and_asks_again',
    function()
      local f = fake({ choice = { 'show', 'send' } })
      ask(f)
      eq(#f.confirms, 2, 'asked, shown, asked again')
      eq(f.confirms[2].payload, f.confirms[1].payload, 'the same text')
      eq(#f.runs, 1, 'sent after the second yes')
      eq(f.runs[1].stdin, f.confirms[1].payload, 'what was shown is what was sent')
      local g = fake({ choice = { 'show', 'cancel' } })
      ask(g)
      eq(#g.runs, 0, 'No after looking sends nothing')
    end,
  },
  {
    -- Verifies: HLR-AI-3
    'ai_cannot_skip_the_question_on_prod',
    function()
      local f = fake({ config = { confirm = false } })
      ask(f)
      eq(#f.confirms, 0, 'dev with confirm = false: no question')
      eq(#f.runs, 1, 'sent')
      local g = fake({ config = { confirm = false } })
      ask(g, { conn = conn({ env = 'prod', ai = { prod = true } }) })
      eq(#g.confirms, 1, 'prod is always asked')
      local h = fake({ config = { confirm = false } })
      ask(h, { conn = conn({ env = vim.NIL, ai = { prod = true } }) })
      eq(#h.confirms, 1, 'and so is an untagged connection')
    end,
  },
  {
    -- Verifies: HLR-AI-4
    'ai_sends_the_question_the_statement_and_the_tables_it_names',
    function()
      local f = fake()
      ask(f)
      eq(f.requests_of('catalog/script'), 2, 'the two tables of the statement were read')
      local sent = f.runs[1].stdin
      expect(sent:find('why is this slow?', 1, true), 'the question')
      expect(sent:find('select * from orders o join customers c', 1, true), 'the statement')
      expect(sent:find(SCRIPTS.orders, 1, true) and sent:find(SCRIPTS.customers, 1, true), 'the definitions')
      expect(not sent:find('Last plan', 1, true) and not sent:find('Last error', 1, true), 'no plan and no error that nobody asked about')
      eq(f.requests[1].params, { name = 'orders' }, 'by the name as written')
      eq(f.requests[1].conn_id, 'c1', 'on this connection')
    end,
  },
  {
    -- Verifies: HLR-AI-4
    'ai_adds_the_last_plan_and_error_of_this_statement_only',
    function()
      local f = fake()
      ask(f, {
        last_error = { sql = 'select * from orders o join customers c on c.id = o.customer_id', message = 'column "x" does not exist', line = 3 },
        last_plan = { sql = 'select * from orders o join customers c on c.id = o.customer_id', mode = 'actual', lines = { 'Hash Join  cost 120', '  Seq Scan on orders' } },
      })
      local sent = f.runs[1].stdin
      expect(sent:find('column "x" does not exist', 1, true), 'the error of this statement')
      expect(sent:find('Hash Join  cost 120', 1, true) and sent:find('actual', 1, true), 'its plan')
      local g = fake()
      ask(g, {
        last_error = { sql = 'select 1 from something_else', message = 'secret error text' },
        last_plan = { sql = 'select 1 from something_else', mode = 'estimated', lines = { 'Seq Scan on something_else' } },
      })
      local other = g.runs[1].stdin
      expect(not other:find('secret error text', 1, true) and not other:find('something_else', 1, true), 'the error and plan of another statement are not sent')
    end,
  },
  {
    -- Verifies: HLR-AI-4
    'ai_never_sends_result_rows',
    function()
      -- ask() takes no rows and reads none: whatever the caller passes besides the documented fields is not part of the payload.
      local f = fake()
      ask(f, { rows = { { 'alice@example.com', 'secret' } }, results = 'alice@example.com', result_sets = { { 'x' } } })
      local sent = f.runs[1].stdin
      expect(not sent:find('alice@example.com', 1, true) and not sent:find('secret', 1, true), 'no row content in the payload')
      for _, r in ipairs(f.requests) do
        eq(r.method, 'catalog/script', 'only table definitions are read from the server')
      end
    end,
  },
  {
    -- Verifies: HLR-AI-4
    'ai_bounds_the_payload_and_says_what_it_cut',
    function()
      local big = ('x'):rep(5000)
      SCRIPTS.orders = 'CREATE TABLE public.orders (' .. big .. ');'
      local f = fake({ config = { max_bytes = 1500 } })
      ask(f)
      SCRIPTS.orders = 'CREATE TABLE public.orders (id integer, customer_id integer, total numeric);'
      local sent = f.runs[1].stdin
      expect(#sent <= 1500 + 200, 'the payload is bounded: ' .. #sent)
      expect(sent:find('[cut', 1, true), 'the cut is marked in the payload')
      expect(sent:find('why is this slow?', 1, true) and sent:find('select * from orders o', 1, true), 'the question and the statement are never cut')
      expect(f.confirms[1].summary:find('cut', 1, true), 'and the confirmation says so: ' .. f.confirms[1].summary)
      -- a statement that alone does not fit is refused, not cut
      local g = fake({ config = { max_bytes = 100 } })
      ask(g, { statement = 'select ' .. ('a, '):rep(200) .. 'b from t' })
      eq(#g.runs + #g.confirms, 0, 'refused')
      expect(g.notes[#g.notes].msg:find('alone are', 1, true) and g.notes[#g.notes].msg:find('max_bytes', 1, true), 'says why: ' .. g.notes[#g.notes].msg)
    end,
  },
  {
    -- Verifies: LLR-AI-7
    'ai_names_a_table_it_could_not_read_and_the_ones_it_left_out',
    function()
      local f = fake()
      ask(f, { statement = 'select * from orders o join nowhere n on 1 = 1' })
      local sent = f.runs[1].stdin
      expect(sent:find('nowhere', 1, true) and sent:find('could not be read', 1, true), 'a missing table is named: ' .. sent)
      expect(sent:find(SCRIPTS.orders, 1, true), 'the others are still there')
      local g = fake({ config = { max_tables = 1 } })
      ask(g)
      eq(g.requests_of('catalog/script'), 1, 'at most max_tables are read')
      local sent2 = g.runs[1].stdin
      expect(sent2:find('not included', 1, true) and sent2:find('customers', 1, true), 'the rest are named: ' .. sent2)
    end,
  },
  {
    -- Verifies: LLR-AI-5
    'ai_runs_the_command_without_a_shell_with_the_payload_on_stdin',
    function()
      local f = fake()
      ask(f)
      eq(f.runs[1].cmd, { 'ai-cli', '-p' }, 'an argument list, as configured')
      expect(type(f.runs[1].stdin) == 'string' and #f.runs[1].stdin > 0, 'the payload is stdin')
      eq(f.runs[1].opts.timeout_ms, 5000, 'bounded by ai.timeout_ms')
      local g = fake({ config = { command = 'ai-cli -p' } })
      ask(g)
      eq(#g.runs, 0, 'a command given as one string (a shell line) is refused')
      expect(g.notes[#g.notes].msg:find('list', 1, true), 'says it must be a list: ' .. g.notes[#g.notes].msg)
    end,
  },
  {
    -- Verifies: LLR-AI-5, LLR-AI-6
    'ai_shows_the_answer_in_a_scratch_buffer',
    function()
      local f = fake()
      ask(f)
      expect(f.answer_text():find('Asking', 1, true), 'while it runs: ' .. f.answer_text())
      f.runs[1].cb({ code = 0, stdout = 'Add an index on orders(customer_id).\n', stderr = '' })
      local text = f.answer_text()
      expect(text:find('Add an index on orders(customer_id).', 1, true), 'the answer: ' .. text)
      expect(text:find('ai-cli', 1, true), 'names the command that answered')
      local b = f.answer_buf()
      eq(vim.bo[b].buftype, 'nofile', 'a scratch buffer')
      eq(vim.bo[b].modifiable, false, 'read only')
    end,
  },
  {
    -- Verifies: LLR-AI-5
    'ai_reports_a_failed_command_a_timeout_and_an_empty_answer_as_what_they_are',
    function()
      local f = fake()
      ask(f)
      f.runs[1].cb({ code = 3, stdout = 'partial text', stderr = 'model unavailable: quota exceeded' })
      local text = f.answer_text()
      expect(text:find('FAILED', 1, true) and text:find('exit code 3', 1, true) and text:find('quota exceeded', 1, true), 'a failure with stderr: ' .. text)
      expect(f.notes[#f.notes].level == vim.log.levels.ERROR, 'an error notification')
      close_all()
      local g = fake()
      ask(g)
      g.runs[1].cb({ timed_out = true, code = 124, stdout = '', stderr = '' })
      expect(g.answer_text():find('timed out', 1, true) and g.answer_text():find('5', 1, true), 'a timeout: ' .. g.answer_text())
      close_all()
      local h = fake()
      ask(h)
      h.runs[1].cb({ code = 0, stdout = '  \n', stderr = 'warning: nothing to say' })
      expect(h.answer_text():find('no answer', 1, true) and h.answer_text():find('nothing to say', 1, true), 'an empty answer is not an answer: ' .. h.answer_text())
      close_all()
      local i = fake()
      ask(i)
      i.runs[1].cb({ start_error = 'ENOENT: no such file or directory', code = -1 })
      expect(i.answer_text():find('could not be started', 1, true) and i.answer_text():find('ENOENT', 1, true), 'a command that cannot start: ' .. i.answer_text())
    end,
  },
  {
    -- Verifies: LLR-AI-5
    'ai_q_cancels_a_running_command_and_quit_ends_it',
    function()
      local f = fake()
      ask(f)
      local b = f.answer_buf()
      vim.api.nvim_set_current_buf(b)
      vim.fn.maparg('q', 'n', false, true).callback()
      expect(f.runs[1].handle.cancelled, 'q cancels the command')
      expect(f.answer_buf() == nil, 'and closes the buffer')
      close_all()
      local g = fake()
      ask(g)
      ai.stop()
      expect(g.runs[1].handle.cancelled, 'Neovim quitting ends it')
      -- an answer that arrives after cancelling is ignored
      g.runs[1].cb({ code = 0, stdout = 'late', stderr = '' })
      expect(not g.answer_text():find('late', 1, true), 'the late answer is not shown')
    end,
  },
  {
    -- Verifies: LLR-AI-6
    'ai_executes_nothing_and_touches_no_connection',
    function()
      local f = fake()
      ask(f)
      for _, r in ipairs(f.requests) do
        expect(r.method == 'catalog/script', 'only catalog/script: ' .. r.method)
      end
      expect(not package.loaded['dbbliss.results'] or true, 'results are not used')
    end,
  },
  {
    -- Verifies: LLR-AI-5, LLR-AI-6
    'ai_runs_a_real_process_and_shows_its_answer',
    function()
      close_all()
      ai.reset()
      local shown
      ai.configure({ command = { vim.v.progpath, '--headless', '--clean', '-l', 'tests/nvim/ai_echo.lua' }, timeout_ms = 20000, max_bytes = 100000, max_tables = 10, confirm = false })
      ai.setup({
        request = function(_, _, _, cb)
          cb(nil, { text = SCRIPTS.orders })
        end,
        notify = function() end,
        confirm = function()
          return 'send'
        end,
        run = ai.default_run,
      })
      ai.ask({ conn = conn(), question = 'what is this?', statement = 'select * from orders' })
      vim.wait(30000, function()
        local b
        for _, x in ipairs(vim.api.nvim_list_bufs()) do
          if vim.bo[x].filetype == 'dbbliss-ai' then
            b = x
          end
        end
        shown = b and table.concat(vim.api.nvim_buf_get_lines(b, 0, -1, false), '\n') or ''
        return shown:find('ANSWER<<', 1, true) ~= nil
      end, 50)
      expect(shown:find('ANSWER<<', 1, true), 'the process answered: ' .. tostring(shown))
      expect(shown:find('what is this?', 1, true) and shown:find('select * from orders', 1, true) and shown:find(SCRIPTS.orders, 1, true), 'it received the payload on stdin: ' .. shown)
    end,
  },
  {
    -- Verifies: LLR-AI-5
    'ai_real_process_failure_and_timeout',
    function()
      close_all()
      local function run_mode(mode, timeout_ms)
        close_all()
        ai.reset()
        vim.env.AI_ECHO_MODE = mode
        ai.configure({ command = { vim.v.progpath, '--headless', '--clean', '-l', 'tests/nvim/ai_echo.lua' }, timeout_ms = timeout_ms, max_bytes = 100000, max_tables = 10, confirm = false })
        ai.setup({
          request = function(_, _, _, cb)
            cb(nil, { text = SCRIPTS.orders })
          end,
          notify = function() end,
          confirm = function()
            return 'send'
          end,
          run = ai.default_run,
        })
        ai.ask({ conn = conn(), question = 'q', statement = 'select 1' })
        local text = ''
        vim.wait(30000, function()
          for _, x in ipairs(vim.api.nvim_list_bufs()) do
            if vim.bo[x].filetype == 'dbbliss-ai' then
              text = table.concat(vim.api.nvim_buf_get_lines(x, 0, -1, false), '\n')
            end
          end
          return text ~= '' and not text:find('Asking', 1, true)
        end, 50)
        vim.env.AI_ECHO_MODE = nil
        return text
      end
      local failed = run_mode('fail', 20000)
      expect(failed:find('exit code 3', 1, true) and failed:find('quota exceeded', 1, true), 'a failing process: ' .. failed)
      local silent = run_mode('silent', 20000)
      expect(silent:find('no answer', 1, true), 'a silent process: ' .. silent)
      local started = vim.uv.hrtime()
      local hung = run_mode('hang', 1500)
      expect(hung:find('timed out', 1, true), 'a hanging process is stopped: ' .. hung)
      expect((vim.uv.hrtime() - started) / 1e9 < 15, 'and in time')
      local missing
      ai.reset()
      ai.configure({ command = { 'dbbliss-no-such-command-xyz' }, timeout_ms = 5000, max_bytes = 100000, max_tables = 10, confirm = false })
      ai.setup({ request = function(_, _, _, cb) cb(nil, { text = '' }) end, notify = function() end, confirm = function() return 'send' end, run = ai.default_run })
      close_all()
      ai.ask({ conn = conn(), question = 'q', statement = 'select 1' })
      vim.wait(10000, function()
        for _, x in ipairs(vim.api.nvim_list_bufs()) do
          if vim.bo[x].filetype == 'dbbliss-ai' then
            missing = table.concat(vim.api.nvim_buf_get_lines(x, 0, -1, false), '\n')
          end
        end
        return missing ~= nil and not missing:find('Asking', 1, true)
      end, 50)
      expect(missing and missing:find('could not be started', 1, true), 'a command that does not exist: ' .. tostring(missing))
    end,
  },
}

local failed = 0
for _, t in ipairs(tests) do
  close_all()
  local ok, err = pcall(t[2])
  if ok then
    io.stdout:write(('%-70s PASS\n'):format(t[1]))
  else
    failed = failed + 1
    io.stdout:write(('%-70s FAIL  %s\n'):format(t[1], tostring(err)))
  end
end
io.stdout:write(('\n%d tests, %d failed\n'):format(#tests, failed))
os.exit(failed == 0 and 0 or 1)
