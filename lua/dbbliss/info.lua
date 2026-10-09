-- The object info buffer: what the backend's catalog/describe returned, a list of sections, each a
-- small table (columns, indexes, constraints, ...) or a block of text (a definition). Nothing here
-- knows an engine: the backend decides what the sections are.
local render = require('dbbliss.render')

local M = {}

local ns = vim.api.nvim_create_namespace('dbbliss_info')
local cfg = { max_col_width = 200 }

---@param opts { max_col_width: integer? }
function M.setup(opts)
  cfg.max_col_width = opts.max_col_width or cfg.max_col_width
end

---@class dbbliss.InfoSection
---@field title string
---@field columns string[]
---@field rows any[][]
---@field text boolean

---@class dbbliss.Info
---@field title string
---@field kind string
---@field sections dbbliss.InfoSection[]

--- The buffer's lines and their highlights, for a result of catalog/describe.
---@param info dbbliss.Info
---@return string[] lines, { [1]: integer, [2]: integer, [3]: integer, [4]: string }[] marks  line (0-based), start byte, end byte, group
function M.render(info)
  local lines, marks = {}, {}
  local function add(text, group, from, to)
    lines[#lines + 1] = text
    if group and text ~= '' then
      marks[#marks + 1] = { #lines - 1, from or 0, to or #text, group }
    end
  end
  add(info.title, 'DbblissHeader')
  for _, section in ipairs(info.sections) do
    add('')
    if section.text then
      add(('── %s ──'):format(section.title), 'DbblissRule')
      for _, row in ipairs(section.rows) do
        add('  ' .. tostring(row[1] or ''))
      end
    else
      add(('── %s (%d) ──'):format(section.title, #section.rows), 'DbblissRule')
      if #section.rows == 0 then
        add('  (none)', 'DbblissInfo')
      else
        local columns = vim.tbl_map(function(name)
          return { name = name }
        end, section.columns)
        local widths = render.widths(columns, section.rows, cfg.max_col_width)
        local header, rule = render.format_header(columns, widths)
        add('  ' .. header, 'DbblissHeader')
        add('  ' .. rule, 'DbblissRule')
        for _, row in ipairs(section.rows) do
          local line, nulls = render.format_row(row, widths)
          add('  ' .. line)
          for _, n in ipairs(nulls) do
            marks[#marks + 1] = { #lines - 1, n[1] + 2, n[2] + 2, 'DbblissNull' }
          end
        end
      end
    end
  end
  return lines, marks
end

local function find_buffer(name)
  local nr = vim.fn.bufnr('^' .. vim.fn.escape(name, '[]*.\\') .. '$')
  return nr > 0 and vim.api.nvim_buf_is_valid(nr) and nr or nil
end

--- Shows an object's info in its own buffer (reused for the same object) and focuses it.
---@param info dbbliss.Info
---@param ctx { connection: string, refresh: fun()?, script: fun()? }
---@return integer bufnr
function M.open(info, ctx)
  local name = ('dbbliss://info/%s/%s'):format(ctx.connection, info.title)
  local buf = find_buffer(name)
  if not buf then
    buf = vim.api.nvim_create_buf(false, true)
    vim.api.nvim_buf_set_name(buf, name)
    vim.bo[buf].buftype = 'nofile'
    vim.bo[buf].bufhidden = 'hide'
    vim.bo[buf].swapfile = false
    vim.bo[buf].filetype = 'dbbliss-info'
    local function map(lhs, fn, desc)
      vim.keymap.set('n', lhs, fn, { buffer = buf, silent = true, desc = 'dbbliss: ' .. desc })
    end
    map('q', function()
      for _, win in ipairs(vim.fn.win_findbuf(buf)) do
        pcall(vim.api.nvim_win_close, win, true)
      end
    end, 'close')
    map('r', function()
      local c = M._ctx and M._ctx[buf]
      if c and c.refresh then
        c.refresh()
      end
    end, 'refresh')
    map('s', function()
      local c = M._ctx and M._ctx[buf]
      if c and c.script then
        c.script()
      end
    end, 'script this object')
  end
  local lines, marks = M.render(info)
  vim.bo[buf].modifiable = true
  vim.api.nvim_buf_set_lines(buf, 0, -1, false, lines)
  vim.bo[buf].modifiable = false
  vim.api.nvim_buf_clear_namespace(buf, ns, 0, -1)
  for _, m in ipairs(marks) do
    pcall(vim.api.nvim_buf_set_extmark, buf, ns, m[1], m[2], { end_col = m[3], hl_group = m[4] })
  end
  -- Closures cannot live in a buffer variable: they are kept by buffer number.
  M._ctx = M._ctx or {}
  M._ctx[buf] = ctx
  if #vim.fn.win_findbuf(buf) == 0 then
    vim.cmd('botright 20split')
    vim.api.nvim_win_set_buf(0, buf)
    vim.wo.wrap = false
    vim.wo.number = false
  else
    vim.api.nvim_set_current_win(vim.fn.win_findbuf(buf)[1])
  end
  return buf
end

return M
