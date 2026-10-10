-- Completion tests (Phase 5): what the text before the cursor asks for, the cache that answers without
-- waiting, quoting, and the thin blink.cmp / nvim-cmp adapters, with the backend replaced by a function.
-- No database, and neither completion plugin is installed.
--
--   nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/completion_test.lua
vim.fs.joinpath = vim.fs.joinpath or function(...)
  return table.concat({ ... }, '/')
end
vim.notify = function() end

local completion = require('dbbliss.completion')

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

local NAMES = {
  { schema = 'public', name = 'orders', kind = 'table' },
  { schema = 'public', name = 'order_lines', kind = 'table' },
  { schema = 'public', name = 'customers', kind = 'table' },
  { schema = 'public', name = 'Mixed Case', kind = 'table' },
  { schema = 'public', name = 'user', kind = 'view' },
  { schema = 'sales', name = 'invoices', kind = 'table' },
  { schema = 'sales', name = 'fn_total', kind = 'function' },
}

local COLUMNS = {
  orders = { schema = 'public', name = 'orders', columns = { { name = 'id', type = 'integer' }, { name = 'customer_id', type = 'integer' }, { name = 'total', type = 'numeric(10,2)' } } },
  customers = { schema = 'public', name = 'customers', columns = { { name = 'id', type = 'integer' }, { name = 'name', type = 'text' } } },
}

--- A completion service whose backend is a function that holds its answers until the test lets them go.
local function fake(opts)
  opts = opts or {}
  local f = { calls = {}, pending = {}, notes = {}, engine = opts.engine or 'postgres' }
  completion.reset()
  completion.setup({
    request = function(conn_id, method, params, cb)
      f.calls[#f.calls + 1] = { conn_id = conn_id, method = method, params = params }
      f.pending[#f.pending + 1] = { method = method, params = params, cb = cb }
    end,
    notify = function(msg, level)
      f.notes[#f.notes + 1] = { msg = msg, level = level }
    end,
    connection_for = function(bufnr)
      if opts.no_connection or not vim.api.nvim_buf_is_valid(bufnr) then
        return nil
      end
      return { id = 'c1', engine = f.engine }
    end,
  })
  --- Answers the oldest request for `method` the way the backend would.
  function f.answer(method, over)
    for i, p in ipairs(f.pending) do
      if p.method == method then
        table.remove(f.pending, i)
        if over and over.err then
          return p.cb(over.err, nil)
        end
        if method == 'catalog/names' then
          return p.cb(nil, vim.tbl_extend('force', { names = opts.names or NAMES, truncated = false, limit = 20000 }, over or {}))
        end
        local key = (p.params.name or ''):lower():gsub('^.*%.', '')
        local doc = COLUMNS[key]
        if not doc then
          return p.cb({ code = 1007, message = 'Nothing named ' .. tostring(p.params.name) }, nil)
        end
        return p.cb(nil, doc)
      end
    end
    error('no pending ' .. method, 0)
  end
  function f.count(method)
    local n = 0
    for _, c in ipairs(f.calls) do
      n = n + (c.method == method and 1 or 0)
    end
    return n
  end
  return f
end

local function conn(engine)
  return { id = 'c1', engine = engine or 'postgres' }
end

--- Completes and returns what was offered (answering the loads on the way), as sorted labels.
local function labels(f, before, statement, engine)
  local got
  completion.complete({ conn = conn(engine or f.engine), before = before, statement = statement or before }, function(result)
    got = result
  end)
  for _ = 1, 4 do
    if got then
      break
    end
    local p = f.pending[1]
    if p then
      f.answer(p.method)
    end
  end
  expect(got, 'no answer for: ' .. before)
  local out = {}
  for _, item in ipairs(got.items) do
    out[#out + 1] = item.label
  end
  table.sort(out)
  return out, got
end

local tests = {
  {
    -- Verifies: LLR-COMP-3
    'completion_object_names_follow_from_join_update_into_table_truncate',
    function()
      for _, text in ipairs({
        'select * from ord',
        'select * from a join ord',
        'update ord',
        'insert into ord',
        'truncate table ord',
        'truncate ord',
        'delete from ord',
        'SELECT *\n  FROM\n  ord',
        'select * from a left outer join ord',
        'select * from a, ord',
        'alter table ord',
        'drop table if exists ord',
      }) do
        local ctx = completion.analyze(text, text, 'postgres')
        eq({ ctx.kind, ctx.partial }, { 'objects', 'ord' }, 'context of: ' .. text)
      end
      eq(completion.analyze('select * from ', 'select * from ', 'postgres').partial, '', 'nothing typed yet')
    end,
  },
  {
    -- Verifies: LLR-COMP-3
    'completion_columns_elsewhere_and_after_a_table_with_its_alias',
    function()
      for _, text in ipairs({
        'select ',
        'select o.id, ',
        'select * from orders o where ',
        'select * from orders o where o.id = 1 and ',
        'select * from orders o join customers c on ',
        'select * from orders o order by ',
        'insert into orders (',
        'update orders set ',
        'select * from orders o ',
      }) do
        eq(completion.analyze(text, text, 'postgres').kind, 'columns', 'context of: ' .. text)
      end
    end,
  },
  {
    -- Verifies: LLR-COMP-3
    'completion_reads_the_tables_and_aliases_of_the_statement',
    function()
      local sql = 'select o. from public.orders as o join customers c on c.id = o.customer_id join "Mixed Case" m on 1 = 1'
      local ctx = completion.analyze('select o.', sql, 'postgres')
      eq(ctx.kind, 'qualified', 'kind')
      eq(ctx.qualifier, 'o', 'qualifier')
      eq(ctx.tables, {
        { name = 'public.orders', alias = 'o' },
        { name = 'customers', alias = 'c' },
        { name = '"Mixed Case"', alias = 'm' },
      }, 'tables and aliases (the alias defined after the cursor is found)')
      local update = completion.analyze('update orders set ', 'update orders set total = 1 where id = 2', 'postgres')
      eq(update.tables, { { name = 'orders' } }, 'update')
      local insert = completion.analyze('insert into orders (', 'insert into orders (id) values (1)', 'postgres')
      eq(insert.tables, { { name = 'orders' } }, 'insert')
      local none = completion.analyze('select 1 where ', 'select 1 where x in (select 2 from) ', 'postgres')
      eq(none.tables, {}, 'no tables')
    end,
  },
  {
    -- Verifies: LLR-COMP-3
    'completion_dot_after_a_schema_lists_its_objects_after_a_table_its_columns',
    function()
      local f = fake()
      eq(labels(f, 'select * from sales.'), { 'fn_total', 'invoices' }, 'objects of a schema')
      eq(labels(f, 'select * from sales.inv'), { 'invoices' }, 'filtered by what is typed')
      eq(labels(f, 'select orders.', 'select orders. from orders'), { 'customer_id', 'id', 'total' }, 'columns of a table')
      eq(labels(f, 'select o.', 'select o. from public.orders o'), { 'customer_id', 'id', 'total' }, 'columns through an alias')
      eq(labels(f, 'select public.orders.', 'select public.orders. from public.orders'), { 'customer_id', 'id', 'total' }, 'columns of a qualified table')
      eq(labels(f, 'select x.', 'select x. from orders o'), {}, 'an unknown qualifier offers nothing')
    end,
  },
  {
    -- Verifies: LLR-COMP-3
    'completion_columns_without_a_dot_come_from_the_statements_tables',
    function()
      local f = fake()
      local out = labels(f, 'select ', 'select  from orders o join customers c on c.id = o.customer_id')
      eq(out, { 'customer_id', 'id', 'id', 'name', 'total' }, 'the columns of both tables')
      local alone = labels(f, 'select ', 'select ')
      eq(alone, {}, 'a statement that names no table offers no columns')
      local filtered = labels(f, 'select * from orders where to', 'select * from orders where to')
      eq(filtered, { 'total' }, 'filtered by the prefix')
    end,
  },
  {
    -- Verifies: LLR-COMP-3
    'completion_filters_by_prefix_ignoring_case_and_sets_where_the_word_starts',
    function()
      local f = fake()
      local out, got = labels(f, 'select * from ORD')
      eq(out, { 'order_lines', 'orders' }, 'case-insensitive prefix')
      eq(got.start, #'select * from ', 'the replaced text starts at the word')
      local _, quoted = labels(f, 'select * from "Mix')
      eq(quoted.start, #'select * from ', 'a typed opening quote is replaced too')
      eq(labels(f, 'select * from "Mix'), { 'Mixed Case' }, 'matching ignores the quote')
      local _, after_dot = labels(f, 'select o.to', 'select o.to from orders o')
      eq(after_dot.start, #'select o.', 'after a dot the word starts after the dot')
    end,
  },
  {
    -- Verifies: LLR-COMP-3
    'completion_inserts_quoted_what_needs_quotes_for_the_engine',
    function()
      eq(completion.quote('orders', 'postgres'), 'orders', 'plain')
      eq(completion.quote('Orders', 'postgres'), '"Orders"', 'PostgreSQL folds to lower case: mixed case needs quotes')
      eq(completion.quote('Order Line', 'postgres'), '"Order Line"', 'a space')
      eq(completion.quote('user', 'postgres'), '"user"', 'a reserved word')
      eq(completion.quote('say "hi"', 'postgres'), '"say ""hi"""', 'quotes are doubled')
      eq(completion.quote('9lives', 'postgres'), '"9lives"', 'starts with a digit')
      eq(completion.quote('Orders', 'sqlserver'), 'Orders', 'SQL Server does not fold: mixed case is fine')
      eq(completion.quote('Order Line', 'sqlserver'), '[Order Line]', 'brackets')
      eq(completion.quote('select', 'sqlserver'), '[select]', 'a reserved word')
      eq(completion.quote('a]b', 'sqlserver'), '[a]]b]', 'a closing bracket is doubled')
      local f = fake()
      local _, got = labels(f, 'select * from ')
      local by_label = {}
      for _, item in ipairs(got.items) do
        by_label[item.label] = item
      end
      eq(by_label['Mixed Case'].insert, '"Mixed Case"', 'the inserted text is quoted, the label is not')
      eq(by_label['user'].insert, '"user"', 'a reserved word is quoted when inserted')
      eq(by_label['orders'].insert, 'orders', 'a plain name is not')
      eq(by_label['invoices'].insert, 'sales.invoices', 'a name outside the default schema is qualified')
      expect(by_label['sales'] and by_label['sales'].kind == 'schema', 'schemas are offered too')
    end,
  },
  {
    -- Verifies: LLR-COMP-4
    'completion_answers_from_the_cache_and_never_waits',
    function()
      local f = fake()
      local answered
      completion.complete({ conn = conn(), before = 'select * from ', statement = 'select * from ' }, function(result)
        answered = result
      end)
      eq(f.count('catalog/names'), 1, 'the first request starts one load')
      expect(answered == nil, 'nothing is offered until the load ends')
      f.answer('catalog/names')
      expect(answered and #answered.items > 0, 'the items are offered when the load ends')
      local second
      completion.complete({ conn = conn(), before = 'select * from ord', statement = 'select * from ord' }, function(result)
        second = result
      end)
      expect(second ~= nil, 'a cached answer is given at once, inside the call')
      eq(f.count('catalog/names'), 1, 'no new load')
    end,
  },
  {
    -- Verifies: LLR-COMP-4
    'completion_requests_that_arrive_during_a_load_share_it',
    function()
      local f = fake()
      local n = 0
      for _ = 1, 3 do
        completion.complete({ conn = conn(), before = 'select * from ', statement = 'select * from ' }, function()
          n = n + 1
        end)
      end
      eq(f.count('catalog/names'), 1, 'one load for three requests')
      f.answer('catalog/names')
      eq(n, 3, 'all three are answered when it ends')
    end,
  },
  {
    -- Verifies: LLR-COMP-4
    'completion_loads_columns_lazily_once_per_table',
    function()
      local f = fake()
      labels(f, 'select * from ') -- names loaded
      local before = f.count('catalog/columns')
      labels(f, 'select o.', 'select o. from orders o')
      eq(f.count('catalog/columns'), before + 1, 'columns of orders requested once')
      eq(f.calls[#f.calls].params, { name = 'orders' }, 'by the name as written')
      labels(f, 'select o.', 'select o. from orders o')
      labels(f, 'select orders.', 'select orders. from orders')
      eq(f.count('catalog/columns'), before + 1, 'cached')
    end,
  },
  {
    -- Verifies: LLR-COMP-4
    'completion_a_failed_load_offers_nothing_and_is_retried_later',
    function()
      local f = fake()
      local got
      completion.complete({ conn = conn(), before = 'select * from ', statement = 'select * from ' }, function(result)
        got = result
      end)
      f.answer('catalog/names', { err = { code = 1005, message = 'connection lost' } })
      expect(got and #got.items == 0, 'a failed load offers nothing')
      completion.complete({ conn = conn(), before = 'select * from ', statement = 'select * from ' }, function() end)
      eq(f.count('catalog/names'), 2, 'the next request tries again')
      -- A table the server does not know: no columns, and no new request every keystroke is not promised, but no error shown.
      eq(#f.notes, 0, 'no notification for a failed completion load')
    end,
  },
  {
    -- Verifies: LLR-COMP-4
    'completion_cache_is_dropped_on_invalidate_and_forget',
    function()
      local f = fake()
      labels(f, 'select * from ')
      completion.invalidate('c1')
      completion.complete({ conn = conn(), before = 'select * from ', statement = 'select * from ' }, function() end)
      eq(f.count('catalog/names'), 2, 'a refresh of the schema tree drops the cache')
      f.answer('catalog/names')
      completion.forget('c1')
      expect(completion._state.caches['c1'] == nil, 'a disconnect forgets the connection')
      -- An answer that arrives after the cache was dropped must not bring it back.
      completion.complete({ conn = conn(), before = 'select * from ', statement = 'select * from ' }, function() end)
      completion.invalidate('c1')
      f.answer('catalog/names')
      expect(completion._state.caches['c1'] == nil or completion._state.caches['c1'].names == nil, 'a late answer is not kept')
    end,
  },
  {
    -- Verifies: HLR-COMP-1, LLR-COMP-4
    'completion_says_once_when_the_list_was_cut_at_the_limit',
    function()
      local f = fake()
      local got
      completion.complete({ conn = conn(), before = 'select * from ', statement = 'select * from ' }, function(result)
        got = result
      end)
      f.answer('catalog/names', { truncated = true, limit = 20000 })
      expect(got and #got.items > 0, 'the names that were loaded are still offered')
      eq(#f.notes, 1, 'the user is told')
      expect(f.notes[1].msg:find('20000', 1, true), 'with the limit: ' .. f.notes[1].msg)
      completion.complete({ conn = conn(), before = 'select * from ', statement = 'select * from ' }, function() end)
      eq(#f.notes, 1, 'once')
    end,
  },
  {
    -- Verifies: LLR-COMP-3
    'completion_offers_nothing_inside_strings_and_comments',
    function()
      for _, text in ipairs({ "select * from t where name = 'abc", 'select * from t -- note ', 'select /* a note ' }) do
        eq(completion.analyze(text, text, 'postgres').kind, 'none', 'context of: ' .. text)
      end
      eq(completion.analyze("select 'it''s' || ", "select 'it''s' || ", 'postgres').kind, 'columns', 'a closed string is just text')
      eq(completion.analyze('select * from [dbo].[Order Line] where ', 'select * from [dbo].[Order Line] where ', 'sqlserver').kind, 'columns', 'bracket quotes on SQL Server')
      eq(completion.analyze('select * from [dbo].[Order Line] where ', 'select * from [dbo].[Order Line] where ', 'sqlserver').tables, { { name = '[dbo].[Order Line]' } }, 'a bracketed name is one name')
    end,
  },
  {
    -- Verifies: LLR-COMP-3
    'completion_finds_the_statement_around_the_cursor',
    function()
      local lines = { 'select 1;', '', 'select o. from orders o;', 'select 2;' }
      local before, statement = completion.statement_at(lines, 3, #'select o.')
      eq(before, 'select o.', 'text of the statement up to the cursor')
      eq(statement, 'select o. from orders o', 'the whole statement')
      local b2, s2 = completion.statement_at({ 'select *', 'from or', 'where x = 1' }, 2, #'from or')
      eq(b2, 'select *\nfrom or', 'across lines')
      eq(s2, 'select *\nfrom or\nwhere x = 1', 'to the end of the statement')
      local b4, s4 = completion.statement_at({ 'select 1', '', 'select o.', '', 'select 3' }, 3, #'select o.')
      eq({ b4, s4 }, { 'select o.', 'select o.' }, 'a blank line ends a statement that has no semicolon')
      local b3, s3 = completion.statement_at({ 'select 1;', 'select ' }, 2, #'select ')
      eq({ b3, s3 }, { 'select ', 'select ' }, 'after a semicolon the statement starts again')
    end,
  },
  {
    -- Verifies: LLR-COMP-5
    'completion_blink_adapter_is_thin_and_needs_no_plugin',
    function()
      expect(package.loaded['blink.cmp'] == nil and package.loaded['cmp'] == nil, 'the test must run without either plugin')
      local f = fake()
      local source = require('dbbliss.completion.blink').new({})
      eq(source:get_trigger_characters(), { '.' }, 'a dot triggers')
      local buf = vim.api.nvim_create_buf(false, true)
      vim.bo[buf].filetype = 'sql'
      vim.api.nvim_buf_set_lines(buf, 0, -1, false, { 'select * from or' })
      vim.api.nvim_set_current_buf(buf)
      expect(source:enabled(), 'enabled in an SQL buffer')
      local got
      source:get_completions({ bufnr = buf, cursor = { 1, #'select * from or' }, line = 'select * from or' }, function(r)
        got = r
      end)
      f.answer('catalog/names')
      expect(got and got.items, 'items are given')
      local labels_got = {}
      for _, item in ipairs(got.items) do
        labels_got[#labels_got + 1] = item.label
        expect(type(item.kind) == 'number', 'kinds are LSP completion item kinds')
        expect(item.textEdit and item.textEdit.newText, 'each item carries the text to insert')
      end
      table.sort(labels_got)
      eq(labels_got, { 'order_lines', 'orders' }, 'filtered by what was typed')
      expect(got.is_incomplete_backward == false and got.is_incomplete_forward == false, 'the list is complete')
      -- A buffer that is not SQL, and an SQL buffer without a connection, get nothing.
      vim.bo[buf].filetype = 'lua'
      expect(not source:enabled(), 'not enabled outside an SQL buffer')
      local other
      source:get_completions({ bufnr = buf, cursor = { 1, 3 }, line = 'sel' }, function(r)
        other = r
      end)
      eq(other.items, {}, 'nothing outside an SQL buffer')
      vim.bo[buf].filetype = 'sql'
      local g = fake({ no_connection = true })
      local none
      require('dbbliss.completion.blink').new({}):get_completions({ bufnr = buf, cursor = { 1, #'select * from or' }, line = 'select * from or' }, function(r)
        none = r
      end)
      eq(none and none.items, {}, 'nothing without a connection, at once')
      eq(#g.calls, 0, 'and nothing asked of the backend')
    end,
  },
  {
    -- Verifies: LLR-COMP-5
    'completion_cmp_adapter_is_thin_and_needs_no_plugin',
    function()
      local f = fake()
      local source = require('dbbliss.completion.cmp').new()
      vim.api.nvim_set_current_buf(vim.api.nvim_create_buf(false, true))
      expect(source:is_available() == false, 'not available outside an SQL buffer')
      local buf = vim.api.nvim_create_buf(false, true)
      vim.bo[buf].filetype = 'sql'
      vim.api.nvim_buf_set_lines(buf, 0, -1, false, { 'select o. from orders o' })
      vim.api.nvim_set_current_buf(buf)
      expect(source:is_available(), 'available in an SQL buffer')
      eq(source:get_trigger_characters(), { '.' }, 'a dot triggers')
      local got
      source:complete({ context = { bufnr = buf, cursor = { row = 1, col = #'select o.' + 1 }, cursor_before_line = 'select o.' } }, function(r)
        got = r
      end)
      f.answer('catalog/columns')
      expect(got and got.items and #got.items == 3, 'the columns of orders: ' .. vim.inspect(got))
      for _, item in ipairs(got.items) do
        expect(type(item.kind) == 'number' and item.insertText and item.label, 'cmp items have a label, a kind and the text to insert')
      end
      vim.bo[buf].filetype = 'markdown'
      local other
      source:complete({ context = { bufnr = buf, cursor = { row = 1, col = 2 }, cursor_before_line = 's' } }, function(r)
        other = r
      end)
      eq(other, { items = {} }, 'nothing outside an SQL buffer')
    end,
  },
}

local failed = 0
for _, t in ipairs(tests) do
  local ok, err = pcall(t[2])
  if ok then
    io.stdout:write(('%-62s PASS\n'):format(t[1]))
  else
    failed = failed + 1
    io.stdout:write(('%-62s FAIL  %s\n'):format(t[1], tostring(err)))
  end
end
io.stdout:write(('\n%d tests, %d failed\n'):format(#tests, failed))
os.exit(failed == 0 and 0 or 1)
