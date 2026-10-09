-- The object name under the cursor: schema-qualified if it is written that way. Pure string work, so
-- it is tested without a buffer (tests/nvim/catalog_test.lua).
local M = {}

--- One part of a name at byte `i` (1-based): "quoted ""x""", [bracketed], or a plain word.
--- Returns the index of its last byte, or nil.
local function part_end(s, i)
  local c = s:sub(i, i)
  if c == '"' then
    local j = i + 1
    while j <= #s do
      if s:sub(j, j) == '"' then
        if s:sub(j + 1, j + 1) == '"' then
          j = j + 2
        else
          return j
        end
      else
        j = j + 1
      end
    end
    return nil
  elseif c == '[' then
    local j = i + 1
    while j <= #s do
      if s:sub(j, j) == ']' then
        if s:sub(j + 1, j + 1) == ']' then
          j = j + 2
        else
          return j
        end
      else
        j = j + 1
      end
    end
    return nil
  end
  -- Bytes >= 128 belong to UTF-8 letters.
  local _, e = s:find('^[%w_$#@\128-\255]+', i)
  return e
end

--- The qualified name (a.b.c, any part quoted) that covers `col`, or that ends right before it.
---@param line string
---@param col integer  0-based byte column of the cursor
---@return string? name
function M.at_cursor(line, col)
  local pos = col + 1
  local i = 1
  while i <= #line do
    local e = part_end(line, i)
    if not e then
      i = i + 1
    else
      local start = i
      -- Follow `.part` as long as it continues.
      while line:sub(e + 1, e + 1) == '.' do
        local e2 = part_end(line, e + 2)
        if not e2 then
          break
        end
        e = e2
      end
      -- On the name, or on the character right after it (the cursor sits after the last letter in
      -- insert mode and at the end of a line).
      if pos >= start and pos <= e + 1 then
        return line:sub(start, e)
      end
      i = e + 1
    end
  end
  return nil
end

return M
