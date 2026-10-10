-- The schema browser: database → schema → folder (tables, views, functions, procedures) → object,
-- loaded one level at a time when a node is opened, and kept per connection until refreshed.
-- Which databases, schemas and folders exist is the backend's answer (catalog/children); nothing
-- here is engine-specific.
local M = {}

local ns = vim.api.nvim_create_namespace('dbbliss_tree')

---@class dbbliss.TreeNode
---@field name string
---@field kind string  database | schema | folder | table | view | function | procedure
---@field folder string?
---@field identity string?
---@field system boolean
---@field browsable boolean
---@field expandable boolean
---@field detail string?
---@field path { database: string?, schema: string?, folder: string? }
---@field children dbbliss.TreeNode[]?  nil until loaded
---@field expanded boolean
---@field loading boolean
---@field depth integer

local deps = {
  --- request(conn_id, method, params, callback(err, result))
  request = nil, ---@type fun(conn_id: string, method: string, params: table, cb: fun(err: table?, result: table?))?
  --- called with (conn_id, conn_name, node) for the object under the cursor
  info = nil, ---@type fun(conn_id: string, conn_name: string, node: dbbliss.TreeNode)?
  script = nil, ---@type fun(conn_id: string, conn_name: string, node: dbbliss.TreeNode)?
  --- called with (conn_id) when the user asks for a refresh: what was loaded about the schema may be out of date
  refreshed = nil, ---@type fun(conn_id: string)?
  --- called with (conn_id, conn_name, node) to drop the object or database under the cursor
  drop = nil, ---@type fun(conn_id: string, conn_name: string, node: dbbliss.TreeNode)?
  notify = function(msg, level)
    vim.notify('dbbliss: ' .. msg, level)
  end,
  show_system = false,
}

local S = {
  buf = nil, ---@type integer?
  --- connection id → { name, roots: dbbliss.TreeNode[]?, loading: boolean }
  trees = {},
  current = nil, ---@type string?
  --- buffer line (1-based) → node, for the lines drawn last
  line_nodes = {},
}
M._state = S

---@param opts table  see `deps`
function M.setup(opts)
  for k, v in pairs(opts) do
    deps[k] = v
  end
end

local OBJECT_KINDS = { table = true, view = true, ['function'] = true, procedure = true }
local DROP_KINDS = vim.tbl_extend('force', OBJECT_KINDS, { database = true })

local function valid(buf)
  return buf ~= nil and vim.api.nvim_buf_is_valid(buf)
end

-- Data ------------------------------------------------------------------------------------------

---@return dbbliss.TreeNode
local function make_node(raw, parent)
  local path
  local depth = parent and parent.depth + 1 or 0
  local pp = parent and parent.path or {}
  if raw.kind == 'database' then
    path = { database = raw.name }
  elseif raw.kind == 'schema' then
    path = { database = pp.database, schema = raw.name }
  elseif raw.kind == 'folder' then
    path = { database = pp.database, schema = pp.schema, folder = raw.folder }
  else
    path = { database = pp.database, schema = pp.schema, folder = pp.folder }
  end
  local nil_if = function(v)
    return (v ~= vim.NIL and v ~= '') and v or nil
  end
  return {
    name = raw.name,
    kind = raw.kind,
    folder = nil_if(raw.folder),
    identity = nil_if(raw.identity),
    system = raw.system == true,
    browsable = raw.browsable ~= false,
    expandable = raw.expandable == true and raw.browsable ~= false,
    detail = nil_if(raw.detail),
    path = path,
    expanded = false,
    loading = false,
    depth = depth,
  }
end

local function fail(err)
  deps.notify(err.message or tostring(err), vim.log.levels.ERROR)
end

--- Loads a node's children (or the roots, for node == nil), then calls `done`.
local function load_children(conn_id, node, done)
  local tree = S.trees[conn_id]
  if not tree then
    return
  end
  local params = node and vim.deepcopy(node.path) or {}
  if node then
    node.loading = true
  else
    tree.loading = true
  end
  deps.request(conn_id, 'catalog/children', params, function(err, result)
    if node then
      node.loading = false
    else
      tree.loading = false
    end
    if err then
      -- A catalog failure is shown, never swallowed; the node stays closed so it can be tried again.
      if node then
        node.expanded = false
      end
      fail(err)
    else
      local list = {}
      for _, raw in ipairs(result.nodes) do
        list[#list + 1] = make_node(raw, node)
      end
      if node then
        node.children = list
      else
        tree.roots = list
      end
    end
    if done then
      done(not err)
    end
    M.draw()
  end)
end

-- Drawing ---------------------------------------------------------------------------------------

local function visible(list)
  return vim.tbl_filter(function(n)
    return deps.show_system or not n.system
  end, list)
end

--- The lines and highlights for the current connection's tree.
function M.lines()
  local tree = S.current and S.trees[S.current]
  if not tree then
    return { 'no connection' }, {}, {}
  end
  local lines, marks, nodes = {}, {}, {}
  lines[1] = ('%s: schema browser   <CR> open  i info  s script  r refresh  S system objects  q close'):format(tree.name)
  marks[#marks + 1] = { 0, 0, #lines[1], 'DbblissHeader' }
  local function add(list)
    for _, n in ipairs(visible(list)) do
      local icon = n.expandable and (n.expanded and '▾' or '▸') or ' '
      local text = ('%s%s %s'):format(('  '):rep(n.depth), icon, n.name)
      local from = #text
      if n.loading then
        text = text .. '  …'
      elseif n.detail and n.kind ~= 'function' and n.kind ~= 'procedure' then
        text = text .. '  ' .. n.detail
      elseif n.detail and n.detail ~= '' then
        text = text .. '(' .. n.detail .. ')'
      end
      lines[#lines + 1] = text
      nodes[#lines] = n
      if #text > from then
        marks[#marks + 1] = { #lines - 1, from, #text, 'DbblissInfo' }
      end
      if n.system or not n.browsable then
        marks[#marks + 1] = { #lines - 1, 0, from, 'DbblissInfo' }
      end
      if n.expanded and n.children then
        add(n.children)
      end
    end
  end
  if tree.roots then
    add(tree.roots)
  elseif tree.loading then
    lines[#lines + 1] = '  loading …'
  end
  return lines, marks, nodes
end

function M.draw()
  if not valid(S.buf) then
    return
  end
  local lines, marks, nodes = M.lines()
  -- Keep the cursor on the node it was on.
  local keep
  local wins = vim.fn.win_findbuf(S.buf)
  if wins[1] then
    keep = S.line_nodes[vim.api.nvim_win_get_cursor(wins[1])[1]]
  end
  S.line_nodes = nodes
  vim.bo[S.buf].modifiable = true
  vim.api.nvim_buf_set_lines(S.buf, 0, -1, false, lines)
  vim.bo[S.buf].modifiable = false
  vim.api.nvim_buf_clear_namespace(S.buf, ns, 0, -1)
  for _, m in ipairs(marks) do
    pcall(vim.api.nvim_buf_set_extmark, S.buf, ns, m[1], m[2], { end_col = m[3], hl_group = m[4] })
  end
  if keep and wins[1] then
    for l, n in pairs(nodes) do
      if n == keep then
        pcall(vim.api.nvim_win_set_cursor, wins[1], { l, 0 })
      end
    end
  end
end

local function buffer()
  if valid(S.buf) then
    return S.buf
  end
  local buf = vim.api.nvim_create_buf(false, true)
  vim.api.nvim_buf_set_name(buf, 'dbbliss://schema')
  vim.bo[buf].buftype = 'nofile'
  vim.bo[buf].bufhidden = 'hide'
  vim.bo[buf].swapfile = false
  vim.bo[buf].modifiable = false
  vim.bo[buf].filetype = 'dbbliss-tree'
  S.buf = buf
  local function map(lhs, fn, desc)
    vim.keymap.set('n', lhs, fn, { buffer = buf, silent = true, desc = 'dbbliss: ' .. desc })
  end
  map('<CR>', M.activate, 'open or close; info on an object')
  map('o', M.activate, 'open or close; info on an object')
  map('i', M.info, 'object info')
  map('s', M.script, 'script as CREATE')
  map('r', M.refresh, 'refresh this node')
  map('R', M.refresh_all, 'refresh everything')
  map('S', M.toggle_system, 'show or hide system databases and schemas')
  map('D', M.drop, 'drop the object or database (asks for its name)')
  map('q', function()
    for _, win in ipairs(vim.fn.win_findbuf(buf)) do
      pcall(vim.api.nvim_win_close, win, true)
    end
  end, 'close')
  return buf
end

-- Actions ---------------------------------------------------------------------------------------

---@return dbbliss.TreeNode?
local function node_at_cursor()
  local win = vim.fn.win_findbuf(S.buf or -1)[1]
  if not win then
    return nil
  end
  return S.line_nodes[vim.api.nvim_win_get_cursor(win)[1]]
end

--- Opens the tree for a connection (reusing what it loaded before) and shows it.
---@param conn_id string
---@param conn_name string
function M.open(conn_id, conn_name)
  local fresh = S.trees[conn_id] == nil
  if fresh then
    S.trees[conn_id] = { name = conn_name, roots = nil, loading = false }
  end
  S.current = conn_id
  local buf = buffer()
  if #vim.fn.win_findbuf(buf) == 0 then
    vim.cmd('topleft 40vsplit')
    vim.api.nvim_win_set_buf(0, buf)
    vim.wo.wrap = false
    vim.wo.number = false
    vim.wo.cursorline = true
    vim.wo.winfixwidth = true
  else
    vim.api.nvim_set_current_win(vim.fn.win_findbuf(buf)[1])
  end
  M.draw()
  if fresh then
    load_children(conn_id, nil, function(ok)
      -- A server with one database you can browse (PostgreSQL always): open it.
      local tree = S.trees[conn_id]
      if ok and tree and tree.roots then
        local open = vim.tbl_filter(function(n)
          return n.expandable and not n.system
        end, tree.roots)
        if #open == 1 then
          M.expand(open[1])
        end
      end
    end)
  end
end

---@param node dbbliss.TreeNode
function M.expand(node)
  if not node.expandable or not S.current then
    return
  end
  node.expanded = true
  if node.children == nil and not node.loading then
    load_children(S.current, node)
  end
  M.draw()
end

function M.activate()
  local node = node_at_cursor()
  if not node then
    return
  end
  if node.expandable then
    if node.expanded then
      node.expanded = false
      M.draw()
    else
      M.expand(node)
    end
  elseif OBJECT_KINDS[node.kind] then
    M.info()
  end
end

local function object_action(fn, what)
  local node = node_at_cursor()
  if not node or not OBJECT_KINDS[node.kind] then
    deps.notify(what .. ': put the cursor on a table, view, function or procedure', vim.log.levels.INFO)
    return
  end
  local tree = S.trees[S.current]
  if fn then
    fn(S.current, tree.name, node)
  end
end

function M.info()
  object_action(deps.info, 'info')
end

function M.script()
  object_action(deps.script, 'script')
end

function M.drop()
  local node = node_at_cursor()
  if not node or not DROP_KINDS[node.kind] then
    deps.notify('drop: put the cursor on a database, table, view, function or procedure', vim.log.levels.INFO)
    return
  end
  if deps.drop then
    deps.drop(S.current, S.trees[S.current].name, node)
  end
end

local function key_of(n)
  return table.concat({ n.kind, n.path.database or '', n.path.schema or '', n.path.folder or '', n.name, n.identity or '' }, '\0')
end

--- The keys of every open node under `list`, so a refresh can open them again.
local function open_keys(list, set)
  set = set or {}
  for _, n in ipairs(list or {}) do
    if n.expanded then
      set[key_of(n)] = true
    end
    open_keys(n.children, set)
  end
  return set
end

--- Opens again, level by level as each loads, the nodes that were open before a refresh.
local function reopen(list, set)
  for _, n in ipairs(list or {}) do
    if set[key_of(n)] and n.expandable then
      n.expanded = true
      load_children(S.current, n, function(ok)
        if ok then
          reopen(n.children, set)
        end
      end)
    end
  end
end

--- Forgets what a node loaded and loads it again if it is open. Its open descendants stay open.
function M.refresh()
  local node = node_at_cursor()
  if not node then
    return M.refresh_all()
  end
  if deps.refreshed and S.current then
    deps.refreshed(S.current)
  end
  local set = open_keys(node.children)
  node.children = nil
  if node.expanded and node.expandable then
    load_children(S.current, node, function(ok)
      if ok then
        reopen(node.children, set)
      end
    end)
  end
  M.draw()
end

--- Loads everything again, from the databases down; what was open stays open.
function M.refresh_all()
  local tree = S.current and S.trees[S.current]
  if not tree then
    return
  end
  if deps.refreshed then
    deps.refreshed(S.current)
  end
  local set = open_keys(tree.roots)
  tree.roots = nil
  load_children(S.current, nil, function(ok)
    if ok then
      reopen(tree.roots, set)
      deps.notify('schema browser refreshed')
    end
  end)
end

function M.toggle_system()
  deps.show_system = not deps.show_system
  M.draw()
end

--- A connection went away: its tree is no longer true.
---@param conn_id string
function M.forget(conn_id)
  S.trees[conn_id] = nil
  if S.current == conn_id then
    S.current = nil
    M.draw()
  end
end

return M
