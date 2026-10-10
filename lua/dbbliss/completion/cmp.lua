-- nvim-cmp source for dbbliss. Register it:
--   require('cmp').register_source('dbbliss', require('dbbliss.completion.cmp').new())
-- and add { name = 'dbbliss' } to the sources. Thin on purpose: everything is in dbbliss.completion.
local completion = require('dbbliss.completion')
local shared = require('dbbliss.completion.shared')

local source = {}

function source.new()
  return setmetatable({}, { __index = source })
end

function source:is_available()
  return shared.is_sql(vim.bo.filetype)
end

function source:get_trigger_characters()
  return { '.' }
end

function source:get_debug_name()
  return 'dbbliss'
end

---@param params { context: { bufnr: integer, cursor: { row: integer, col: integer }, cursor_before_line: string } }
function source:complete(params, callback)
  local context = params.context
  if not shared.is_sql(vim.bo[context.bufnr].filetype) then
    return callback({ items = {} })
  end
  -- cmp's column is 1-based; the text before the cursor says how many bytes there are.
  local row, col = context.cursor.row, #context.cursor_before_line
  completion.for_buffer(context.bufnr, row, col, function(result)
    callback({ items = shared.lsp_items(result, row, col) })
  end)
end

return source
