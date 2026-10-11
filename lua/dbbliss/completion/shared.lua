-- What the completion adapters have in common: which buffers are SQL, and the LSP item shape both
-- plugins accept.
local M = {}

local SQL = { sql = true, pgsql = true, tsql = true, mysql = true, plsql = true }

function M.is_sql(filetype)
  return SQL[filetype] == true
end

-- vim.lsp.protocol.CompletionItemKind: Method 2, Function 3, Field 5, Interface 8, Module 9, Keyword 14, Struct 22.
local KIND = { column = 5, table = 22, view = 8, ['function'] = 3, procedure = 2, schema = 9, keyword = 14 }

--- Items in the LSP shape, each replacing the word being typed (from result.start to the cursor).
---@param result { items: table[], start: integer }
---@param row integer  1-based
---@param col integer  0-based byte column of the cursor
function M.lsp_items(result, row, col)
  local range = { start = { line = row - 1, character = result.start }, ['end'] = { line = row - 1, character = col } }
  local items = {}
  for _, item in ipairs(result.items) do
    items[#items + 1] = {
      label = item.label,
      kind = KIND[item.kind] or 1,
      detail = item.detail,
      insertText = item.insert,
      filterText = item.label,
      -- Names and columns before keywords.
      sortText = (item.kind == 'keyword' and '9' or '1') .. item.label,
      textEdit = { newText = item.insert, range = range },
    }
  end
  return items
end

return M
