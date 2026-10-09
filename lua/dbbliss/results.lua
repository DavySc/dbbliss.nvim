-- The results buffer: aligned tables, cell navigation, yanking, a paused-query hint that fetches
-- more rows, and a messages pane. Only text and highlights (extmarks); no plugin dependencies.
--
-- Everything is appended to the end of the buffer, in the order the backend reports it, so a set's
-- rows are contiguous: the rows of a result set sit on lines [first, first + nrows). The one thing
-- that is not appended for good is the "paused" hint, which is always the last line and goes away
-- before anything else is written.
local render = require('dbbliss.render')

local M = {}

local ns = vim.api.nvim_create_namespace('dbbliss_results')

---@class dbbliss.Set
---@field qid string
---@field index integer
---@field columns { name: string, type: string }[]
---@field widths integer[]?  nil until the first page of rows (or the end) fixes them
---@field starts integer[]?
---@field header_line integer?  0-based
---@field first integer?  0-based buffer line of the first row
---@field rows any[][]
---@field count integer?  the server's row count, once known

local cfg = { window_rows = 1000, max_col_width = 40 }

local S = {
  buf = nil, ---@type integer?
  msg_buf = nil, ---@type integer?
  sets = {}, ---@type dbbliss.Set[]
  --- query id → the set being filled
  open = {},
  --- the query that is paused on this buffer: { qid, line (0-based) }
  paused = nil,
  --- called with (query_id, rows) to ask the backend for more rows
  on_fetch = nil,
}
M._state = S

---@param opts { window_rows: integer?, max_col_width: integer?, on_fetch: fun(query_id: string, rows: integer)? }
function M.setup(opts)
  cfg.window_rows = opts.window_rows or cfg.window_rows
  cfg.max_col_width = opts.max_col_width or cfg.max_col_width
  S.on_fetch = opts.on_fetch
end

function M.window_rows()
  return cfg.window_rows
end

local function define_highlights()
  local links = {
    DbblissHeader = 'Title',
    DbblissRule = 'Comment',
    DbblissNull = 'Comment',
    DbblissInfo = 'Comment',
    DbblissError = 'ErrorMsg',
    DbblissWarn = 'WarningMsg',
    DbblissPaused = 'MoreMsg',
  }
  for name, link in pairs(links) do
    vim.api.nvim_set_hl(0, name, { link = link, default = true })
  end
end

-- Buffers --------------------------------------------------------------------------------------

local function map(buf, lhs, fn, desc)
  vim.keymap.set('n', lhs, fn, { buffer = buf, silent = true, desc = 'dbbliss: ' .. desc })
end

local function make_buffer(name)
  local buf = vim.api.nvim_create_buf(false, true)
  vim.api.nvim_buf_set_name(buf, name)
  vim.bo[buf].buftype = 'nofile'
  vim.bo[buf].bufhidden = 'hide'
  vim.bo[buf].swapfile = false
  vim.bo[buf].modifiable = false
  return buf
end

local function valid(buf)
  return buf ~= nil and vim.api.nvim_buf_is_valid(buf)
end

function M.buffer()
  if valid(S.buf) then
    return S.buf
  end
  define_highlights()
  local buf = make_buffer('dbbliss://results')
  S.buf = buf
  vim.bo[buf].filetype = 'dbbliss-results'
  map(buf, '<Tab>', function()
    M.next_cell(1)
  end, 'next cell')
  map(buf, '<S-Tab>', function()
    M.next_cell(-1)
  end, 'previous cell')
  map(buf, 'yc', function()
    M.yank('cell')
  end, 'yank cell')
  map(buf, 'yr', function()
    M.yank('row')
  end, 'yank row')
  map(buf, 'yC', function()
    M.yank('column')
  end, 'yank column')
  map(buf, 'gm', function()
    M.fetch_more()
  end, 'fetch more rows')
  map(buf, ']]', function()
    M.jump_set(1)
  end, 'next result set')
  map(buf, '[[', function()
    M.jump_set(-1)
  end, 'previous result set')
  map(buf, 'q', function()
    for _, win in ipairs(vim.fn.win_findbuf(buf)) do
      pcall(vim.api.nvim_win_close, win, true)
    end
  end, 'close')
  -- Moving to the end of a paused result asks for the next window of rows.
  vim.api.nvim_create_autocmd('CursorMoved', {
    buffer = buf,
    callback = function()
      M.maybe_fetch()
    end,
  })
  return buf
end

--- Shows the results window if it is not visible (it does not take focus).
function M.show()
  local buf = M.buffer()
  if #vim.fn.win_findbuf(buf) == 0 then
    local current = vim.api.nvim_get_current_win()
    vim.cmd('botright 14split')
    vim.api.nvim_win_set_buf(0, buf)
    vim.wo.wrap = false
    vim.wo.number = false
    vim.wo.cursorline = true
    if vim.api.nvim_win_is_valid(current) then
      vim.api.nvim_set_current_win(current)
    end
  end
end

local function line_count(buf)
  return vim.api.nvim_buf_line_count(buf)
end

local function is_blank(buf)
  return line_count(buf) == 1 and vim.api.nvim_buf_get_lines(buf, 0, 1, false)[1] == ''
end

---@param buf integer
---@param fn fun()
local function writable(buf, fn)
  vim.bo[buf].modifiable = true
  local ok, err = pcall(fn)
  vim.bo[buf].modifiable = false
  if not ok then
    error(err, 0)
  end
end

--- Removes the "paused" hint, which is only ever the last line.
local function drop_paused_line(buf)
  if S.paused and S.paused.line ~= nil then
    local at = S.paused.line
    if at == line_count(buf) - 1 then
      writable(buf, function()
        vim.api.nvim_buf_set_lines(buf, at, at + 1, false, {})
      end)
    end
    S.paused.line = nil
  end
end

---@param lines string[]
---@param marks { [1]: integer, [2]: integer, [3]: integer, [4]: string }[]?  line offset in `lines` (0-based), start byte, end byte, group
---@return integer first  0-based buffer line of lines[1]
local function append(lines, marks)
  local buf = M.buffer()
  drop_paused_line(buf)
  -- Follow the end of the output only if the cursor is already there.
  local following = {}
  for _, win in ipairs(vim.fn.win_findbuf(buf)) do
    following[win] = vim.api.nvim_win_get_cursor(win)[1] >= line_count(buf)
  end
  local first
  writable(buf, function()
    if is_blank(buf) then
      first = 0
      vim.api.nvim_buf_set_lines(buf, 0, 1, false, lines)
    else
      first = line_count(buf)
      vim.api.nvim_buf_set_lines(buf, first, first, false, lines)
    end
  end)
  for _, m in ipairs(marks or {}) do
    local line = first + m[1]
    pcall(vim.api.nvim_buf_set_extmark, buf, ns, line, m[2], { end_col = m[3], hl_group = m[4] })
  end
  for win, follow in pairs(following) do
    if follow and vim.api.nvim_win_is_valid(win) then
      vim.api.nvim_win_set_cursor(win, { line_count(buf), 0 })
    end
  end
  return first
end

local function whole_line(offset, text, group)
  return { offset, 0, #text, group }
end

--- Plain output lines: notes, status, errors.
---@param lines string[]
---@param group string?  highlight group for every line
function M.note(lines, group)
  local marks = {}
  for i, text in ipairs(lines) do
    if group and text ~= '' then
      marks[#marks + 1] = whole_line(i - 1, text, group)
    end
  end
  append(lines, marks)
end

--- Starts a fresh run: the buffers are emptied.
function M.clear()
  S.sets = {}
  S.open = {}
  S.paused = nil
  if valid(S.buf) then
    writable(S.buf, function()
      vim.api.nvim_buf_set_lines(S.buf, 0, -1, false, {})
    end)
    vim.api.nvim_buf_clear_namespace(S.buf, ns, 0, -1)
  end
  if valid(S.msg_buf) then
    writable(S.msg_buf, function()
      vim.api.nvim_buf_set_lines(S.msg_buf, 0, -1, false, {})
    end)
    vim.api.nvim_buf_clear_namespace(S.msg_buf, ns, 0, -1)
  end
end

-- Result sets ----------------------------------------------------------------------------------

---@param p { query_id: string, result_set: integer, columns: { name: string, type: string }[] }
function M.resultset(p)
  local set = { qid = p.query_id, index = p.result_set, columns = p.columns, rows = {} }
  S.sets[#S.sets + 1] = set
  S.open[p.query_id] = set
end

--- The lines of a table block (header, rule, rows) and the highlights to put on them.
---@param set dbbliss.Set
---@param rows any[][]
local function block(set, rows, with_header)
  local lines, marks = {}, {}
  if with_header then
    local header, rule = render.format_header(set.columns, set.widths)
    lines[1], lines[2] = header, rule
    marks[1] = whole_line(0, header, 'DbblissHeader')
    marks[2] = whole_line(1, rule, 'DbblissRule')
  end
  local base = #lines
  for i, row in ipairs(rows) do
    local line, nulls = render.format_row(row, set.widths)
    lines[base + i] = line
    for _, n in ipairs(nulls) do
      marks[#marks + 1] = { base + i - 1, n[1], n[2], 'DbblissNull' }
    end
  end
  return lines, marks
end

--- Fixes the widths and writes the header. Called with the first page, or at the end of an empty set.
---@param set dbbliss.Set
---@param first_page any[][]
local function start_table(set, first_page)
  set.widths = render.widths(set.columns, first_page, cfg.max_col_width)
  set.starts = render.cell_starts(set.widths)
  local lines, marks = block(set, {}, true)
  local at = append(lines, marks)
  set.header_line = at
  set.first = at + 2
end

--- A later page can hold wider values than the first. Widths only ever grow (up to the cap), and
--- the table, which is the last thing in the buffer while its rows arrive, is written again with
--- them: a number cut short to "24…" would be a wrong number on screen.
---@param set dbbliss.Set
---@param page any[][]
---@return boolean grew
local function widen(set, page)
  local wanted = render.widths(set.columns, page, cfg.max_col_width)
  local grew = false
  for i, w in ipairs(wanted) do
    if w > set.widths[i] then
      set.widths[i] = w
      grew = true
    end
  end
  if not grew then
    return false
  end
  set.starts = render.cell_starts(set.widths)
  local buf = M.buffer()
  drop_paused_line(buf)
  local all = vim.list_extend(vim.list_slice(set.rows), page)
  local lines, marks = block(set, all, true)
  writable(buf, function()
    vim.api.nvim_buf_set_lines(buf, set.header_line, set.first + #set.rows, false, lines)
  end)
  vim.api.nvim_buf_clear_namespace(buf, ns, set.header_line, set.header_line + #lines)
  for _, m in ipairs(marks) do
    pcall(vim.api.nvim_buf_set_extmark, buf, ns, set.header_line + m[1], m[2], { end_col = m[3], hl_group = m[4] })
  end
  for _, row in ipairs(page) do
    set.rows[#set.rows + 1] = row
  end
  return true
end

---@param p { query_id: string, result_set: integer, rows: any[][] }
function M.rows(p)
  local set = S.open[p.query_id]
  if not set or #p.rows == 0 then
    return
  end
  if not set.widths then
    start_table(set, p.rows)
  elseif widen(set, p.rows) then
    return
  end
  local lines, marks = block(set, p.rows, false)
  for _, row in ipairs(p.rows) do
    set.rows[#set.rows + 1] = row
  end
  append(lines, marks)
end

---@param p { query_id: string, rows: integer }
function M.resultset_done(p)
  local set = S.open[p.query_id]
  if not set then
    return
  end
  if not set.widths then
    start_table(set, {})
  end
  set.count = p.rows
  S.open[p.query_id] = nil
  local text = ('(%d row%s)'):format(p.rows, p.rows == 1 and '' or 's')
  append({ text, '' }, { whole_line(0, text, 'DbblissInfo') })
end

-- Paging ----------------------------------------------------------------------------------------

--- The backend paused the query after `p.rows_sent` rows.
---@param p { query_id: string, rows_sent: integer }
function M.paused(p)
  local set = S.open[p.query_id]
  local text = ('-- %d rows shown; more are available. Move to the end or press gm to fetch %d more.'):format(
    p.rows_sent,
    cfg.window_rows
  )
  local at = append({ text }, { whole_line(0, text, 'DbblissPaused') })
  S.paused = { qid = p.query_id, line = at, set = set }
end

--- Asks for the next window of rows of the paused query.
function M.fetch_more()
  local paused = S.paused
  if not paused then
    vim.notify('dbbliss: no paused result to fetch more of', vim.log.levels.INFO)
    return
  end
  drop_paused_line(M.buffer())
  S.paused = nil
  if S.on_fetch then
    S.on_fetch(paused.qid, cfg.window_rows)
  end
end

function M.maybe_fetch()
  local paused = S.paused
  if not paused or paused.line == nil or not valid(S.buf) then
    return
  end
  local row = vim.api.nvim_win_get_cursor(0)[1] - 1
  if row >= paused.line - 1 then
    M.fetch_more()
  end
end

--- The query ended; whatever paused it no longer applies.
function M.query_ended(query_id)
  S.open[query_id] = nil
  if S.paused and S.paused.qid == query_id then
    drop_paused_line(M.buffer())
    S.paused = nil
  end
end

-- Cursor, cells and yanking --------------------------------------------------------------------

---@return dbbliss.Set? set, integer? row  row: 1-based data row; 0 for the header; nil outside the rows
local function locate(lnum0)
  for _, set in ipairs(S.sets) do
    if set.first then
      if lnum0 == set.header_line then
        return set, 0
      end
      if lnum0 >= set.first and lnum0 < set.first + #set.rows then
        return set, lnum0 - set.first + 1
      end
    end
  end
end

local function cell_at(set, vcol0)
  local k = 1
  for i, start in ipairs(set.starts) do
    if start <= vcol0 then
      k = i
    end
  end
  return k
end

local function cursor_cell()
  local pos = vim.api.nvim_win_get_cursor(0)
  local set, row = locate(pos[1] - 1)
  if not set then
    return nil
  end
  local vcol0 = vim.fn.virtcol('.') - 1
  return set, row, cell_at(set, vcol0)
end

local function goto_cell(set, row, k)
  local lnum = (row == 0 and set.header_line or (set.first + row - 1)) + 1
  local bytecol = vim.fn.virtcol2col(0, lnum, set.starts[k] + 1)
  vim.api.nvim_win_set_cursor(0, { lnum, math.max(0, bytecol - 1) })
end

--- Moves to the next (dir = 1) or previous cell, wrapping to the next or previous row. Outside a
--- table it goes to the first cell of the nearest table in that direction.
function M.next_cell(dir)
  local set, row, k = cursor_cell()
  if not set then
    local lnum0 = vim.api.nvim_win_get_cursor(0)[1] - 1
    local target
    for _, s in ipairs(S.sets) do
      if s.first and ((dir > 0 and s.header_line > lnum0) or (dir < 0 and s.first + #s.rows <= lnum0)) then
        if dir > 0 then
          target = target or s
        else
          target = s
        end
      end
    end
    if target then
      goto_cell(target, dir > 0 and 0 or #target.rows, dir > 0 and 1 or #target.columns)
    end
    return
  end
  k = k + dir
  if k > #set.columns then
    if row >= #set.rows then
      return
    end
    row, k = row + 1, 1
  elseif k < 1 then
    if row <= 0 then
      return
    end
    row, k = row - 1, #set.columns
  end
  goto_cell(set, row, k)
end

--- The next or previous result set's header.
function M.jump_set(dir)
  local lnum0 = vim.api.nvim_win_get_cursor(0)[1] - 1
  local target
  for _, s in ipairs(S.sets) do
    if s.header_line then
      if dir > 0 and s.header_line > lnum0 then
        target = target or s
      elseif dir < 0 and s.header_line < lnum0 then
        target = s
      end
    end
  end
  if target then
    vim.api.nvim_win_set_cursor(0, { target.header_line + 1, 0 })
  end
end

--- The text a yank of `kind` would copy, or nil when the cursor is not on a table.
---@param kind 'cell'|'row'|'column'
function M.yank_text(kind)
  local set, row, k = cursor_cell()
  if not set then
    return nil
  end
  if kind == 'cell' then
    return row == 0 and set.columns[k].name or render.raw_text(set.rows[row][k])
  elseif kind == 'row' then
    local parts = {}
    for i = 1, #set.columns do
      parts[i] = row == 0 and set.columns[i].name or render.raw_text(set.rows[row][i])
    end
    return table.concat(parts, '\t')
  end
  local values = {}
  for i, r in ipairs(set.rows) do
    values[i] = render.raw_text(r[k])
  end
  return table.concat(values, '\n')
end

---@param kind 'cell'|'row'|'column'
function M.yank(kind)
  local text = M.yank_text(kind)
  if not text then
    vim.notify('dbbliss: not on a result table', vim.log.levels.INFO)
    return
  end
  vim.fn.setreg('"', text)
  pcall(vim.fn.setreg, '+', text)
  vim.notify(('dbbliss: yanked %s (%d characters)'):format(kind, vim.fn.strchars(text)))
end

-- Messages -------------------------------------------------------------------------------------

local severity_group = { error = 'DbblissError', warning = 'DbblissWarn' }

--- A server message (NOTICE, PRINT, RAISERROR): shown in its own pane, with its severity.
---@param p { severity: string, text: string, number: integer?, line: integer? }
function M.message(p)
  if not valid(S.msg_buf) then
    define_highlights()
    S.msg_buf = make_buffer('dbbliss://messages')
    vim.keymap.set('n', 'q', '<Cmd>close<CR>', { buffer = S.msg_buf, silent = true })
  end
  local buf = S.msg_buf
  local where = (p.line and p.line ~= vim.NIL) and (' (line %s)'):format(p.line) or ''
  local code = (p.number and p.number ~= vim.NIL and p.number ~= 0) and (' [%s]'):format(p.number) or ''
  local text = ('[%s]%s %s%s'):format(p.severity, code, p.text, where)
  local lines = vim.split(text, '\n', { plain = true })
  writable(buf, function()
    local blank = line_count(buf) == 1 and vim.api.nvim_buf_get_lines(buf, 0, 1, false)[1] == ''
    local at = blank and 0 or line_count(buf)
    vim.api.nvim_buf_set_lines(buf, at, blank and 1 or at, false, lines)
    local group = severity_group[p.severity] or 'DbblissInfo'
    for i, l in ipairs(lines) do
      pcall(vim.api.nvim_buf_set_extmark, buf, ns, at + i - 1, 0, { end_col = #l, hl_group = group })
    end
  end)
  if #vim.fn.win_findbuf(buf) == 0 then
    local current = vim.api.nvim_get_current_win()
    vim.cmd('botright 6split')
    vim.api.nvim_win_set_buf(0, buf)
    vim.wo.wrap = true
    if vim.api.nvim_win_is_valid(current) then
      vim.api.nvim_set_current_win(current)
    end
  end
  for _, win in ipairs(vim.fn.win_findbuf(buf)) do
    vim.api.nvim_win_set_cursor(win, { line_count(buf), 0 })
  end
end

--- All lines of the results buffer, for tests and for "copy everything".
function M.lines()
  return valid(S.buf) and vim.api.nvim_buf_get_lines(S.buf, 0, -1, false) or {}
end

function M.message_lines()
  return valid(S.msg_buf) and vim.api.nvim_buf_get_lines(S.msg_buf, 0, -1, false) or {}
end

return M
