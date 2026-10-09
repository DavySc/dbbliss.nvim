-- Results buffer tests: rendering, paging, cell navigation, yanking, messages. No backend.
--
--   nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/results_test.lua
--
-- Exits 1 if any test fails.
vim.fs.joinpath = vim.fs.joinpath or function(...)
  return table.concat({ ... }, '/')
end

vim.notify = function() end
local render = require('dbbliss.render')
local results = require('dbbliss.results')

local fetched

local function reset()
  results.clear()
  results.setup({
    window_rows = 3,
    max_col_width = 8,
    on_fetch = function(qid, rows)
      fetched = { qid = qid, rows = rows }
    end,
  })
  fetched = nil
end

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

local COLS = { { name = 'id', type = 'int4' }, { name = 'name', type = 'text' } }

--- Shows the buffer in the current window and returns it.
local function focus()
  results.show()
  local buf = results.buffer()
  local win = vim.fn.win_findbuf(buf)[1]
  vim.api.nvim_set_current_win(win)
  return buf, win
end

local function table_with(rows)
  results.resultset({ query_id = 'q1', result_set = 0, columns = COLS })
  results.rows({ query_id = 'q1', result_set = 0, rows = rows })
  results.resultset_done({ query_id = 'q1', result_set = 0, rows = #rows })
end

local tests = {
  {
    'render_cells',
    function()
      eq({ render.cell_text(vim.NIL) }, { 'NULL', true }, 'null')
      eq({ render.cell_text(42) }, { '42', false }, 'integer')
      eq({ render.cell_text(1e15) }, { '1000000000000000', false }, 'large integer')
      eq({ render.cell_text(0.1) }, { '0.1', false }, 'float')
      eq({ render.cell_text(true) }, { 'true', false }, 'boolean')
      eq(render.raw_text(vim.NIL), '', 'NULL is empty when yanked')
      eq(render.one_line('a\nb\r\nc\td'), 'a↵b↵c d', 'control characters')
    end,
  },
  {
    'render_fit_pads_and_truncates',
    function()
      eq(render.fit('ab', 5, false), 'ab   ', 'left aligned')
      eq(render.fit('ab', 5, true), '   ab', 'right aligned')
      eq(render.fit('abcdefghij', 5, false), 'abcd…', 'truncated with an ellipsis')
      eq(vim.fn.strdisplaywidth(render.fit('日本語日本語', 7, false)), 7, 'wide characters fit the width')
      eq(vim.fn.strdisplaywidth(render.fit('日本', 6, false)), 6, 'wide characters are padded by display width')
    end,
  },
  {
    'render_widths_are_capped',
    function()
      local w = render.widths(COLS, { { 1, 'a very long name indeed' }, { 100, 'x' } }, 8)
      eq(w, { 3, 8 }, 'widths')
      eq(render.cell_starts(w), { 0, 6 }, 'cell starts')
    end,
  },
  {
    'table_is_aligned',
    function()
      reset()
      table_with({ { 1, 'alice' }, { 22, vim.NIL } })
      eq(results.lines(), {
        'id │ name ',
        '───┼──────',
        ' 1 │ alice',
        '22 │ NULL ',
        '(2 rows)',
        '',
      }, 'buffer')
    end,
  },
  {
    -- Widths come from the first page; a later page with wider values must not cut them short.
    'later_pages_widen_the_table',
    function()
      reset()
      results.resultset({ query_id = 'q1', result_set = 0, columns = COLS })
      results.rows({ query_id = 'q1', result_set = 0, rows = { { 1, 'a' }, { 22, vim.NIL } } })
      results.rows({ query_id = 'q1', result_set = 0, rows = { { 2500, 'bcd' } } })
      results.resultset_done({ query_id = 'q1', result_set = 0, rows = 3 })
      eq(results.lines(), {
        'id   │ name',
        '─────┼─────',
        '   1 │ a   ',
        '  22 │ NULL',
        '2500 │ bcd ',
        '(3 rows)',
        '',
      }, 'buffer')
      local marks = vim.api.nvim_buf_get_extmarks(results.buffer(), vim.api.nvim_create_namespace('dbbliss_results'), 0, -1, { details = true })
      local null_marks = vim.tbl_filter(function(m)
        return m[4].hl_group == 'DbblissNull'
      end, marks)
      eq(#null_marks, 1, 'one NULL highlight, on the right line')
      eq(null_marks[1][2], 3, 'the NULL is on line 4')
    end,
  },
  {
    'pages_are_contiguous_and_fetch_continues_them',
    function()
      reset()
      results.resultset({ query_id = 'q1', result_set = 0, columns = COLS })
      results.rows({ query_id = 'q1', result_set = 0, rows = { { 1, 'a' }, { 2, 'b' }, { 3, 'c' } } })
      results.paused({ query_id = 'q1', rows_sent = 3 })
      local lines = results.lines()
      expect(lines[#lines]:find('gm', 1, true), 'the paused hint is the last line')
      results.fetch_more()
      eq(fetched, { qid = 'q1', rows = 3 }, 'fetch request')
      expect(not results.lines()[#results.lines()]:find('gm', 1, true), 'the hint is gone once rows are asked for')
      results.rows({ query_id = 'q1', result_set = 0, rows = { { 4, 'd' } } })
      results.resultset_done({ query_id = 'q1', result_set = 0, rows = 4 })
      eq(results.lines(), {
        'id │ name',
        '───┼─────',
        ' 1 │ a   ',
        ' 2 │ b   ',
        ' 3 │ c   ',
        ' 4 │ d   ',
        '(4 rows)',
        '',
      }, 'later rows follow the earlier ones directly')
    end,
  },
  {
    'moving_to_the_end_fetches_more',
    function()
      reset()
      local _, win = focus()
      results.resultset({ query_id = 'q1', result_set = 0, columns = COLS })
      results.rows({ query_id = 'q1', result_set = 0, rows = { { 1, 'a' }, { 2, 'b' }, { 3, 'c' } } })
      results.paused({ query_id = 'q1', rows_sent = 3 })
      vim.api.nvim_win_set_cursor(win, { 3, 0 })
      results.maybe_fetch()
      expect(fetched == nil, 'fetched while still near the top')
      vim.api.nvim_win_set_cursor(win, { #results.lines(), 0 })
      results.maybe_fetch()
      eq(fetched, { qid = 'q1', rows = 3 }, 'fetch on reaching the end')
    end,
  },
  {
    'query_end_drops_the_paused_hint',
    function()
      reset()
      results.resultset({ query_id = 'q1', result_set = 0, columns = COLS })
      results.rows({ query_id = 'q1', result_set = 0, rows = { { 1, 'a' } } })
      results.paused({ query_id = 'q1', rows_sent = 1 })
      results.query_ended('q1')
      for _, l in ipairs(results.lines()) do
        expect(not l:find('gm', 1, true), 'the hint outlived the query')
      end
      results.fetch_more()
      expect(fetched == nil, 'fetch asked for a query that ended')
    end,
  },
  {
    'cell_navigation_wraps_over_rows',
    function()
      reset()
      local _, win = focus()
      table_with({ { 1, 'alice' }, { 22, 'bob' } })
      local function at()
        local pos = vim.api.nvim_win_get_cursor(win)
        return { pos[1], vim.fn.virtcol('.') }
      end
      vim.api.nvim_win_set_cursor(win, { 3, 0 }) -- first data row, first cell
      results.next_cell(1)
      eq(at(), { 3, 6 }, 'second cell of the row') -- "id" is 2 wide, the separator 3: the cell starts at column 6
      results.next_cell(1)
      eq(at(), { 4, 1 }, 'wraps to the next row')
      results.next_cell(-1)
      eq(at(), { 3, 6 }, 'wraps back')
      vim.api.nvim_win_set_cursor(win, { 4, 6 })
      results.next_cell(1)
      eq(at(), { 4, 6 }, 'stays on the last cell')
    end,
  },
  {
    'yank_cell_row_column',
    function()
      reset()
      local _, win = focus()
      table_with({ { 1, 'alice' }, { 22, vim.NIL }, { 3, 'carol' } })
      vim.api.nvim_win_set_cursor(win, { 3, 7 }) -- row 1, name
      eq(results.yank_text('cell'), 'alice', 'cell')
      eq(results.yank_text('row'), '1\talice', 'row')
      eq(results.yank_text('column'), 'alice\n\ncarol', 'column (NULL is empty)')
      vim.api.nvim_win_set_cursor(win, { 4, 7 })
      eq(results.yank_text('cell'), '', 'a NULL cell yanks nothing')
      vim.api.nvim_win_set_cursor(win, { 1, 7 })
      eq(results.yank_text('cell'), 'name', 'the header yanks the column name')
      vim.api.nvim_win_set_cursor(win, { #results.lines(), 0 })
      eq(results.yank_text('cell'), nil, 'outside a table')
    end,
  },
  {
    'yank_is_not_shortened',
    function()
      reset()
      local _, win = focus()
      table_with({ { 1, 'a value much longer than the column' } })
      vim.api.nvim_win_set_cursor(win, { 3, 7 })
      eq(results.yank_text('cell'), 'a value much longer than the column', 'full value')
      expect(results.lines()[3]:find('…', 1, true), 'the display is shortened')
    end,
  },
  {
    'several_result_sets',
    function()
      reset()
      local _, win = focus()
      results.resultset({ query_id = 'q1', result_set = 0, columns = COLS })
      results.rows({ query_id = 'q1', result_set = 0, rows = { { 1, 'a' } } })
      results.resultset_done({ query_id = 'q1', result_set = 0, rows = 1 })
      results.resultset({ query_id = 'q1', result_set = 1, columns = { { name = 'x', type = 'int4' } } })
      results.rows({ query_id = 'q1', result_set = 1, rows = { { 9 } } })
      results.resultset_done({ query_id = 'q1', result_set = 1, rows = 1 })
      vim.api.nvim_win_set_cursor(win, { 1, 0 })
      results.jump_set(1)
      eq(vim.api.nvim_win_get_cursor(win)[1], 6, 'second header')
      eq(vim.api.nvim_buf_get_lines(0, 5, 6, false)[1], 'x', 'it is the second set')
      results.jump_set(-1)
      eq(vim.api.nvim_win_get_cursor(win)[1], 1, 'back to the first header')
      vim.api.nvim_win_set_cursor(win, { 8, 0 })
      eq(results.yank_text('cell'), '9', 'the second set maps its own rows')
    end,
  },
  {
    'messages_pane_shows_severity',
    function()
      reset()
      results.message({ severity = 'info', text = 'hello', number = 0 })
      results.message({ severity = 'error', text = 'boom', number = 50000, line = 3 })
      eq(results.message_lines(), { '[info] hello', '[error] [50000] boom (line 3)' }, 'messages')
    end,
  },
  {
    -- SQL Server error texts contain line breaks; nvim_buf_set_lines refuses them.
    'notes_with_line_breaks_become_lines',
    function()
      reset()
      results.note({ '-- error (line 2): Operation cancelled by user.\r\nThe statement has been terminated.', '-- done' }, 'DbblissError')
      eq(results.lines(), { '-- error (line 2): Operation cancelled by user.', 'The statement has been terminated.', '-- done' }, 'buffer')
    end,
  },
  {
    'clear_starts_a_fresh_run',
    function()
      reset()
      table_with({ { 1, 'a' } })
      results.clear()
      eq(results.lines(), { '' }, 'buffer')
      table_with({ { 2, 'b' } })
      eq(results.lines()[3], ' 2 │ b   ', 'new rows land at the top')
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
