-- Choosing what to run from the statements the backend's script/split returned. Splitting itself is
-- engine logic and stays in the backend; this only picks among its answer. Lines and columns are
-- 0-based, columns in UTF-16 code units, as the backend reports them.
local M = {}

---@class dbbliss.Statement
---@field text string
---@field start { line: integer, col: integer }
---@field end { line: integer, col: integer }  exclusive
---@field repeat integer  GO count, 1 otherwise

--- The statement under the cursor: the last one that starts at or before it, so a cursor on a blank
--- line or a trailing comment picks the statement above, and one above the first picks the first.
---@param statements dbbliss.Statement[]
---@param line integer
---@param col integer
---@return dbbliss.Statement?
function M.pick(statements, line, col)
  local picked
  for _, s in ipairs(statements) do
    if s.start.line < line or (s.start.line == line and s.start.col <= col) then
      picked = s
    else
      break
    end
  end
  return picked or statements[1]
end

---@class dbbliss.Unit
---@field text string
---@field line_offset integer  0-based buffer line the text starts on
---@field index integer  position of its statement among those chosen
---@field count integer  how many statements were chosen

--- The queue to run, in order, with each statement repeated by its GO count.
---@param statements dbbliss.Statement[]
---@param base_line integer  0-based buffer line of the split text's first line
---@return dbbliss.Unit[]
function M.expand(statements, base_line)
  local queue = {}
  for i, s in ipairs(statements) do
    for _ = 1, s['repeat'] or 1 do
      queue[#queue + 1] = { text = s.text, line_offset = base_line + s.start.line, index = i, count = #statements }
    end
  end
  return queue
end

return M
