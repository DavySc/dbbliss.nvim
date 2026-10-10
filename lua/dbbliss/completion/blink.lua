-- blink.cmp source for dbbliss. Register it in blink.cmp's configuration:
--   sources = { default = { 'dbbliss', ... }, providers = { dbbliss = { name = 'dbbliss', module = 'dbbliss.completion.blink' } } }
-- Thin on purpose: everything is in dbbliss.completion, and this file needs blink.cmp only when blink calls it.
local completion = require('dbbliss.completion')
local shared = require('dbbliss.completion.shared')

local source = {}

function source.new(opts)
  return setmetatable({ opts = opts or {} }, { __index = source })
end

function source:enabled()
  return shared.is_sql(vim.bo.filetype)
end

function source:get_trigger_characters()
  return { '.' }
end

---@param ctx { bufnr: integer, cursor: { [1]: integer, [2]: integer } }  row 1-based, column 0-based bytes
function source:get_completions(ctx, callback)
  local empty = { items = {}, is_incomplete_backward = false, is_incomplete_forward = false }
  if not shared.is_sql(vim.bo[ctx.bufnr].filetype) then
    callback(empty)
    return function() end
  end
  local row, col = ctx.cursor[1], ctx.cursor[2]
  completion.for_buffer(ctx.bufnr, row, col, function(result)
    callback({
      items = shared.lsp_items(result, row, col),
      is_incomplete_backward = false,
      is_incomplete_forward = false,
    })
  end)
  return function() end
end

return source
