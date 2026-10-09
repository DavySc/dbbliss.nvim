-- Formatting a result set as aligned text. Pure functions, no buffers: tested in
-- tests/nvim/results_test.lua.
local M = {}

M.SEP = ' │ '
M.SEP_WIDTH = 3
M.ELLIPSIS = '…'

--- A JSON value from the backend as display text.
---@return string text, boolean is_null
function M.cell_text(v)
  if v == nil or v == vim.NIL then
    return 'NULL', true
  end
  local t = type(v)
  if t == 'string' then
    return v, false
  elseif t == 'boolean' then
    return tostring(v), false
  elseif t == 'number' then
    if v == math.floor(v) and math.abs(v) < 2 ^ 53 then
      return ('%d'):format(v), false
    end
    local s = ('%.15g'):format(v)
    if tonumber(s) ~= v then
      s = ('%.17g'):format(v)
    end
    return s, false
  end
  return vim.json.encode(v), false
end

--- The cell's value as the text to yank: NULL is empty, nothing is shortened.
function M.raw_text(v)
  local text, is_null = M.cell_text(v)
  return is_null and '' or text
end

--- One line: control characters would break the table.
function M.one_line(text)
  text = text:gsub('\r\n', '↵'):gsub('[\r\n]', '↵'):gsub('\t', ' ')
  return (text:gsub('[%z\1-\8\11\12\14-\31\127]', '·'))
end

--- Pads or shortens `text` to exactly `width` display cells.
function M.fit(text, width, align_right)
  text = M.one_line(text)
  local w = vim.fn.strdisplaywidth(text)
  if w > width then
    -- At most `width` characters can matter; then cut until it fits with the ellipsis.
    text = vim.fn.strcharpart(text, 0, width)
    while text ~= '' and vim.fn.strdisplaywidth(text) > width - 1 do
      text = vim.fn.strcharpart(text, 0, vim.fn.strchars(text) - 1)
    end
    text = text .. M.ELLIPSIS
    w = vim.fn.strdisplaywidth(text)
  end
  local pad = (' '):rep(math.max(0, width - w))
  return align_right and (pad .. text) or (text .. pad)
end

--- Column widths from the header and the rows given, at most `cap` each.
---@param columns { name: string }[]
---@param rows any[][]
---@param cap integer
function M.widths(columns, rows, cap)
  local widths = {}
  for i, c in ipairs(columns) do
    widths[i] = math.max(1, vim.fn.strdisplaywidth(M.one_line(c.name)))
  end
  for _, row in ipairs(rows) do
    for i = 1, #columns do
      local text = M.cell_text(row[i])
      -- Long text is capped anyway; do not measure all of a megabyte.
      local w = #text > cap * 4 and cap or vim.fn.strdisplaywidth(M.one_line(text))
      if w > widths[i] then
        widths[i] = w
      end
    end
  end
  for i = 1, #widths do
    widths[i] = math.min(widths[i], cap)
  end
  return widths
end

--- Display column (0-based) where each cell starts.
function M.cell_starts(widths)
  local starts, at = {}, 0
  for i, w in ipairs(widths) do
    starts[i] = at
    at = at + w + M.SEP_WIDTH
  end
  return starts
end

--- A data row: the line, and the byte ranges of its NULL cells (for highlighting).
---@return string line, { [1]: integer, [2]: integer }[] nulls
function M.format_row(row, widths)
  local parts, nulls, byte = {}, {}, 0
  for i, w in ipairs(widths) do
    local v = row[i]
    local text, is_null = M.cell_text(v)
    local cell = M.fit(text, w, type(v) == 'number')
    if is_null then
      nulls[#nulls + 1] = { byte, byte + #cell }
    end
    parts[i] = cell
    byte = byte + #cell + #M.SEP
  end
  return table.concat(parts, M.SEP), nulls
end

function M.format_header(columns, widths)
  local names = {}
  for i, c in ipairs(columns) do
    names[i] = c.name
  end
  local cells, rule = {}, {}
  for i, w in ipairs(widths) do
    cells[i] = M.fit(names[i], w, false)
    rule[i] = ('─'):rep(w)
  end
  return table.concat(cells, M.SEP), table.concat(rule, '─┼─')
end

return M
