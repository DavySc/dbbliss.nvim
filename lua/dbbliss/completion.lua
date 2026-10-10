-- SQL completion from the catalog. The core works out from the text before the cursor what is wanted
-- (object names, the objects of a schema, columns), answers from a per-connection cache, and starts a
-- load when the cache has no answer yet: it never waits for the server. The blink.cmp and nvim-cmp
-- adapters (dbbliss.completion.blink / .cmp) are thin shells around `for_buffer`.
--
-- The text analysis is a tokenizer plus a few rules, not a parser: it reads the statement the cursor is
-- in (found by `;` and blank lines), which is enough for FROM/JOIN lists, aliases and `alias.` columns.
-- Subqueries and CTEs are not resolved: a column of a derived table is not offered.
local M = {}

local deps = {
  --- request(conn_id, method, params, callback(err, result))
  request = nil, ---@type fun(conn_id: string, method: string, params: table, cb: fun(err: table?, result: table?))?
  notify = function(msg, level)
    vim.notify('dbbliss: ' .. msg, level)
  end,
  --- The connection a buffer's completion reads from: { id, engine } or nil.
  connection_for = function()
    return nil
  end,
}

local S = {
  caches = {}, ---@type table<string, table>  connection id -> { names = , columns = {} }
}
M._state = S

---@param opts table  see `deps`
function M.setup(opts)
  for k, v in pairs(opts) do
    deps[k] = v
  end
end

--- Drops every cache (tests, and a backend that went away).
function M.reset()
  S.caches = {}
end

--- The connection's schema changed (the tree was refreshed): what was loaded may be out of date.
---@param conn_id string
function M.invalidate(conn_id)
  S.caches[conn_id] = nil
end

--- The connection is gone.
---@param conn_id string
function M.forget(conn_id)
  S.caches[conn_id] = nil
end

-- Quoting ---------------------------------------------------------------------------------------

local function set(words)
  local t = {}
  for w in words:gmatch('%S+') do
    t[w] = true
  end
  return t
end

local RESERVED = {
  postgres = set([[all analyse analyze and any array as asc asymmetric authorization between binary both case cast check collate column
    constraint create cross current_catalog current_date current_role current_time current_timestamp current_user default deferrable desc
    distinct do else end except false fetch for foreign freeze from full grant group having in initially inner intersect into is isnull
    join lateral leading left like limit localtime localtimestamp natural not notnull null offset on only or order outer overlaps placing
    primary references returning right select session_user similar some symmetric table tablesample then to trailing true union unique
    user using variadic verbose when where window with]]),
  sqlserver = set([[add all alter and any as asc authorization backup begin between break browse bulk by cascade case check checkpoint
    close clustered coalesce collate column commit compute constraint contains continue convert create cross current current_date
    current_time current_timestamp current_user cursor database dbcc deallocate declare default delete deny desc disk distinct distributed
    double drop dump else end errlvl escape except exec execute exists exit external fetch file fillfactor for foreign freetext from full
    function goto grant group having holdlock identity identity_insert identitycol if in index inner insert intersect into is join key
    kill left like lineno load merge national nocheck nonclustered not null nullif of off offsets on open opendatasource openquery
    openrowset openxml option or order outer over percent pivot plan precision primary print proc procedure public raiserror read
    readtext reconfigure references replication restore restrict return revert revoke right rollback rowcount rowguidcol rule save
    schema securityaudit select semantickeyphrasetable semanticsimilaritydetailstable semanticsimilaritytable session_user set setuser
    shutdown some statistics system_user table tablesample textsize then to top tran transaction trigger truncate try_convert tsequal
    union unique unpivot update updatetext use user values varying view waitfor when where while with within writetext]]),
}

--- The identifier as it must be written to mean this name on this engine.
---@param name string
---@param engine string?
---@return string
function M.quote(name, engine)
  if engine == 'sqlserver' then
    if name:match('^[%a_@#][%w_@#$]*$') and not RESERVED.sqlserver[name:lower()] then
      return name
    end
    return '[' .. name:gsub('%]', ']]') .. ']'
  end
  if name:match('^[a-z_][a-z0-9_$]*$') and not RESERVED.postgres[name] then
    return name
  end
  return '"' .. name:gsub('"', '""') .. '"'
end

local function unquote(ident)
  local q = ident:match('^"(.*)"$')
  if q then
    return (q:gsub('""', '"'))
  end
  q = ident:match('^%[(.*)%]$')
  if q then
    return (q:gsub('%]%]', ']'))
  end
  return ident
end

-- Tokens ------------------------------------------------------------------------------------------

local function is_ident_byte(c)
  return c:match('[%w_$#@]') ~= nil or c:byte() >= 128
end

--- Splits SQL text into tokens { type = word|quoted|punct|string|number, text, s, e } (1-based, inclusive).
--- `open` names what the text ends inside of: 'string', 'comment' or 'quote' (an identifier quote).
---@return table[] tokens
---@return string? open
---@return integer? open_at  where the open quote started
local function lex(text, engine)
  local tokens, i, n = {}, 1, #text
  local function add(type, s, e)
    tokens[#tokens + 1] = { type = type, text = text:sub(s, e), s = s, e = e }
  end
  while i <= n do
    local c = text:sub(i, i)
    if c:match('%s') then
      i = i + 1
    elseif text:sub(i, i + 1) == '--' then
      local e = text:find('\n', i, true)
      if not e then
        return tokens, 'comment'
      end
      i = e + 1
    elseif text:sub(i, i + 1) == '/*' then
      local _, e = text:find('*/', i + 2, true)
      if not e then
        return tokens, 'comment'
      end
      i = e + 1
    elseif c == "'" then
      local j = i + 1
      while true do
        local e = text:find("'", j, true)
        if not e then
          return tokens, 'string'
        end
        if text:sub(e + 1, e + 1) == "'" then
          j = e + 2
        else
          add('string', i, e)
          i = e + 1
          break
        end
      end
    elseif c == '"' or (c == '[' and engine == 'sqlserver') then
      local close = c == '"' and '"' or ']'
      local j = i + 1
      while true do
        local e = text:find(close, j, true)
        if not e then
          return tokens, 'quote', i
        end
        if text:sub(e + 1, e + 1) == close then
          j = e + 2
        else
          add('quoted', i, e)
          i = e + 1
          break
        end
      end
    elseif is_ident_byte(c) then
      local j = i
      while j < n and is_ident_byte(text:sub(j + 1, j + 1)) do
        j = j + 1
      end
      add(c:match('%d') and 'number' or 'word', i, j)
      i = j + 1
    else
      add('punct', i, i)
      i = i + 1
    end
  end
  return tokens, nil
end

local OBJECT_KW = set('FROM JOIN UPDATE INTO TABLE TRUNCATE VIEW')
local CLAUSE_KW = set([[FROM JOIN UPDATE INTO TABLE TRUNCATE VIEW SELECT WHERE ON SET GROUP ORDER BY HAVING VALUES AND OR LIMIT OFFSET USING
  RETURNING WHEN THEN ELSE CASE TOP DISTINCT UNION INTERSECT EXCEPT]])
local OBJECT_TAIL = set('IF EXISTS ONLY TABLE TEMP TEMPORARY')
local ALIAS_STOP = set([[WHERE ON JOIN INNER LEFT RIGHT FULL OUTER CROSS NATURAL GROUP ORDER HAVING LIMIT OFFSET UNION INTERSECT EXCEPT SET
  VALUES USING RETURNING SELECT FETCH FOR WINDOW WITH TABLESAMPLE LATERAL]])

local function upper_word(t)
  return t and t.type == 'word' and t.text:upper() or nil
end

--- The tables a statement names after FROM, JOIN, UPDATE and INTO, with their aliases.
---@return { name: string, alias: string? }[]
local function tables_of(statement, engine)
  local toks = lex(statement, engine)
  local out = {}
  local i = 1
  local function name_chain(j)
    local t = toks[j]
    if not t or (t.type ~= 'word' and t.type ~= 'quoted') then
      return nil
    end
    if t.type == 'word' and (CLAUSE_KW[t.text:upper()] or ALIAS_STOP[t.text:upper()]) then
      return nil
    end
    local first, last = t.s, t.e
    j = j + 1
    while toks[j] and toks[j].text == '.' and toks[j].s == last + 1 and toks[j + 1] and (toks[j + 1].type == 'word' or toks[j + 1].type == 'quoted') do
      last = toks[j + 1].e
      j = j + 2
    end
    if toks[j] and toks[j].text == '.' and toks[j].s == last + 1 then
      return nil -- `schema.` with nothing after the dot yet: the name is being typed
    end
    return statement:sub(first, last), j
  end
  local function ref(j, allow_paren)
    local u = upper_word(toks[j])
    if u == 'ONLY' or u == 'LATERAL' then
      j = j + 1
    end
    local name, after = name_chain(j)
    if not name then
      return nil
    end
    if toks[after] and toks[after].text == '(' and not allow_paren then
      return nil -- a function call, not a table
    end
    local entry = { name = name }
    local a = toks[after]
    if upper_word(a) == 'AS' and toks[after + 1] and (toks[after + 1].type == 'word' or toks[after + 1].type == 'quoted') then
      entry.alias = toks[after + 1].text
      after = after + 2
    elseif a and (a.type == 'quoted' or (a.type == 'word' and not ALIAS_STOP[a.text:upper()] and not CLAUSE_KW[a.text:upper()] and a.text:upper() ~= 'AS')) then
      entry.alias = a.text
      after = after + 1
    end
    out[#out + 1] = entry
    return after
  end
  while i <= #toks do
    local u = upper_word(toks[i])
    if u == 'FROM' or u == 'JOIN' or u == 'UPDATE' or u == 'INTO' then
      local j = ref(i + 1, u == 'INTO')
      while j and u == 'FROM' and toks[j] and toks[j].text == ',' do
        j = ref(j + 1, false)
      end
    end
    i = i + 1
  end
  return out
end

--- Reads the text before the cursor. kind is 'objects' (a table, view, function or procedure is wanted), 'qualified'
--- (after `qualifier.`), 'columns' (an expression: columns of the statement's tables) or 'none'.
---@param before string  the statement up to the cursor
---@param statement string  the whole statement
---@param engine string?
---@return { kind: string, partial: string, start: integer, qualifier: string?, tables: table[] }
function M.analyze(before, statement, engine)
  local toks, open, open_at = lex(before, engine)
  if open == 'string' or open == 'comment' then
    return { kind = 'none', partial = '', start = #before, tables = {} }
  end
  local partial, start
  if open == 'quote' then
    partial, start = before:sub(open_at + 1), open_at - 1
    toks = lex(before:sub(1, open_at - 1), engine)
  else
    local word = before:match('[%w_$#@\128-\255]*$')
    partial, start = word, #before - #word
    toks = lex(before:sub(1, start), engine)
  end
  local ctx = { partial = partial, start = start, tables = tables_of(statement, engine) }
  local n = #toks
  local last = toks[n]
  if last and last.text == '.' and last.type == 'punct' and last.e == start then
    -- qualifier . partial  (the qualifier is one or more name parts joined by dots, as written)
    local j, first = n - 1, nil
    local stop = last.s - 1
    while toks[j] and (toks[j].type == 'word' or toks[j].type == 'quoted') and toks[j].e == stop do
      first, stop = toks[j].s, toks[j].s - 1
      if toks[j - 1] and toks[j - 1].text == '.' and toks[j - 1].e == stop then
        stop = toks[j - 1].s - 1
        j = j - 2
      else
        break
      end
    end
    if first then
      ctx.kind, ctx.qualifier = 'qualified', before:sub(first, last.s - 1)
      return ctx
    end
    ctx.kind = 'none'
    return ctx
  end
  -- The nearest clause keyword at this nesting level decides.
  local depth, kw, at = 0, nil, nil
  for j = n, 1, -1 do
    local t = toks[j]
    if t.text == ')' and t.type == 'punct' then
      depth = depth + 1
    elseif t.text == '(' and t.type == 'punct' then
      if depth == 0 then
        break -- inside a parenthesis that is still open: a column list or a call
      end
      depth = depth - 1
    elseif depth == 0 and t.type == 'word' and CLAUSE_KW[t.text:upper()] then
      kw, at = t.text:upper(), j
      break
    end
  end
  ctx.kind = 'columns'
  if kw and OBJECT_KW[kw] then
    local tail = {}
    for j = at + 1, n do
      tail[#tail + 1] = toks[j]
    end
    local only_filler = true
    for _, t in ipairs(tail) do
      if not (t.type == 'word' and OBJECT_TAIL[t.text:upper()]) then
        only_filler = false
      end
    end
    if #tail == 0 or only_filler or (kw == 'FROM' and tail[#tail].text == ',') then
      ctx.kind = 'objects'
    end
  end
  return ctx
end

--- The statement the cursor is in, found by `;` and blank lines: its text up to the cursor and all of it.
---@param lines string[]
---@param row integer  1-based
---@param col integer  0-based byte column
---@return string before, string statement
function M.statement_at(lines, row, col)
  local text = table.concat(lines, '\n')
  local offset = 0
  for r = 1, row - 1 do
    offset = offset + #lines[r] + 1
  end
  offset = offset + math.min(col, #(lines[row] or ''))
  -- Backwards to the previous ';' or blank line.
  local from = 1
  for i = offset, 1, -1 do
    local c = text:sub(i, i)
    if c == ';' then
      from = i + 1
      break
    end
    if c == '\n' then
      local rest = text:sub(1, i - 1)
      local prev = rest:match('([^\n]*)$')
      if prev:match('^%s*$') then
        from = i + 1
        break
      end
    end
  end
  -- Forwards to the next ';' or blank line.
  local to = #text
  for i = offset + 1, #text do
    local c = text:sub(i, i)
    if c == ';' then
      to = i - 1
      break
    end
    if c == '\n' then
      local nxt = text:match('^([^\n]*)', i + 1)
      if nxt:match('^%s*$') and i + 1 <= #text + 1 and text:find('\n', i + 1, true) then
        to = i - 1
        break
      end
    end
  end
  local lead = text:sub(from):match('^%s*')
  from = from + #lead
  local before = text:sub(from, offset)
  local statement = text:sub(from, math.max(to, offset))
  if #statement > #before then
    statement = before .. statement:sub(#before + 1):gsub('%s+$', '')
  end
  return before, statement
end

-- Cache ---------------------------------------------------------------------------------------------

local function cache_of(conn)
  local c = S.caches[conn.id]
  if not c then
    c = { names = nil, columns = {} }
    S.caches[conn.id] = c
  end
  return c
end

--- Calls back with the connection's names ({ items } or nil when the load failed). One load serves every request
--- made while it runs; a failed load is forgotten so the next request tries again.
local function with_names(conn, cb)
  local cache = cache_of(conn)
  local entry = cache.names
  if entry and entry.state == 'ready' then
    return cb(entry)
  end
  if entry and entry.state == 'loading' then
    entry.waiters[#entry.waiters + 1] = cb
    return
  end
  entry = { state = 'loading', waiters = { cb } }
  cache.names = entry
  deps.request(conn.id, 'catalog/names', {}, function(err, result)
    local waiters = entry.waiters
    entry.waiters = {}
    if err or not result then
      if cache.names == entry then
        cache.names = nil
      end
      for _, w in ipairs(waiters) do
        w(nil)
      end
      return
    end
    local items, seen = {}, {}
    for _, n in ipairs(result.names or {}) do
      local key = n.schema .. '\0' .. n.name .. '\0' .. n.kind
      if not seen[key] then
        seen[key] = true
        items[#items + 1] = n
      end
    end
    entry.state, entry.items = 'ready', items
    if result.truncated and not cache.notified then
      cache.notified = true
      deps.notify(
        ('completion: the database has more than %d names; only the first %d are offered'):format(result.limit or #items, result.limit or #items),
        vim.log.levels.INFO
      )
    end
    for _, w in ipairs(waiters) do
      w(entry)
    end
  end)
end

local function with_columns(conn, name, cb)
  local cache = cache_of(conn)
  local key = name:lower()
  local entry = cache.columns[key]
  if entry and entry.state == 'ready' then
    return cb(entry)
  end
  if entry and entry.state == 'loading' then
    entry.waiters[#entry.waiters + 1] = cb
    return
  end
  entry = { state = 'loading', waiters = { cb } }
  cache.columns[key] = entry
  deps.request(conn.id, 'catalog/columns', { name = name }, function(err, result)
    local waiters = entry.waiters
    entry.waiters = {}
    if err or not result then
      if cache.columns[key] == entry then
        cache.columns[key] = nil
      end
      for _, w in ipairs(waiters) do
        w(nil)
      end
      return
    end
    entry.state, entry.doc = 'ready', result
    for _, w in ipairs(waiters) do
      w(entry)
    end
  end)
end

-- Answers -------------------------------------------------------------------------------------------

local DEFAULT_SCHEMA = { postgres = 'public', sqlserver = 'dbo' }

local function matches(label, partial)
  local p = unquote(partial):lower()
  return p == '' or label:lower():sub(1, #p) == p
end

local function object_items(names, engine, partial, schema)
  local items, seen = {}, {}
  for _, n in ipairs(names) do
    if (schema == nil or n.schema:lower() == schema:lower()) and matches(n.name, partial) then
      local insert = M.quote(n.name, engine)
      if schema == nil and n.schema ~= DEFAULT_SCHEMA[engine] then
        insert = M.quote(n.schema, engine) .. '.' .. insert
      end
      items[#items + 1] = { label = n.name, insert = insert, kind = n.kind, detail = n.schema .. ' · ' .. n.kind }
    end
    if schema == nil and not seen[n.schema] and matches(n.schema, partial) then
      seen[n.schema] = true
    end
  end
  if schema == nil then
    local schemas = {}
    for s in pairs(seen) do
      schemas[#schemas + 1] = s
    end
    table.sort(schemas)
    for _, s in ipairs(schemas) do
      items[#items + 1] = { label = s, insert = M.quote(s, engine), kind = 'schema', detail = 'schema' }
    end
  end
  return items
end

local function column_items(doc, engine, partial, from)
  local items = {}
  for _, c in ipairs(doc.columns or {}) do
    if matches(c.name, partial) then
      items[#items + 1] = { label = c.name, insert = M.quote(c.name, engine), kind = 'column', detail = from and (c.type .. ' · ' .. from) or c.type }
    end
  end
  return items
end

local function same_name(a, b)
  return unquote(a):lower() == unquote(b):lower()
end

--- The statement table a qualifier refers to: an alias, or the table's own name (with or without schema).
local function table_for(ctx, qualifier)
  for _, t in ipairs(ctx.tables) do
    if t.alias and same_name(t.alias, qualifier) then
      return t
    end
  end
  for _, t in ipairs(ctx.tables) do
    if same_name(t.name, qualifier) or same_name(t.name:gsub('^.*%.', ''), qualifier) then
      return t
    end
  end
end

--- Works out what to offer and calls `cb({ items, start, kind })` when it is known: at once when the cache has it,
--- otherwise when the load that this call starts ends. Never blocks.
---@param args { conn: { id: string, engine: string? }, before: string, statement: string? }
---@param cb fun(result: { items: table[], start: integer, kind: string })
function M.complete(args, cb)
  local conn, engine = args.conn, args.conn.engine
  local ctx = M.analyze(args.before, args.statement or args.before, engine)
  local function done(items)
    cb({ items = items or {}, start = ctx.start, kind = ctx.kind })
  end
  if ctx.kind == 'none' then
    return done()
  end
  if ctx.kind == 'objects' then
    return with_names(conn, function(names)
      done(names and object_items(names.items, engine, ctx.partial, nil))
    end)
  end
  if ctx.kind == 'qualified' then
    local t = table_for(ctx, ctx.qualifier)
    if t or ctx.qualifier:find('.', 1, true) then
      local name = t and t.name or ctx.qualifier
      return with_columns(conn, name, function(cols)
        done(cols and column_items(cols.doc, engine, ctx.partial))
      end)
    end
    return with_names(conn, function(names)
      if not names then
        return done()
      end
      for _, n in ipairs(names.items) do
        if same_name(n.schema, ctx.qualifier) then
          return done(object_items(names.items, engine, ctx.partial, unquote(ctx.qualifier)))
        end
      end
      for _, n in ipairs(names.items) do
        if (n.kind == 'table' or n.kind == 'view') and same_name(n.name, ctx.qualifier) then
          return with_columns(conn, ctx.qualifier, function(cols)
            done(cols and column_items(cols.doc, engine, ctx.partial))
          end)
        end
      end
      done()
    end)
  end
  -- columns of the statement's tables
  local tables = ctx.tables
  if #tables == 0 then
    return done()
  end
  local items, pending = {}, #tables
  local per = {}
  for i, t in ipairs(tables) do
    with_columns(conn, t.name, function(cols)
      per[i] = cols and column_items(cols.doc, engine, ctx.partial, t.alias or t.name:gsub('^.*%.', '')) or {}
      pending = pending - 1
      if pending == 0 then
        for _, list in ipairs(per) do
          vim.list_extend(items, list)
        end
        done(items)
      end
    end)
  end
end

--- Completion for a buffer position (what the adapters call). `cb` gets { items, start, kind } with `start` the
--- 0-based column in the line where the replaced word begins, and nothing for a buffer without a connection.
---@param bufnr integer
---@param row integer  1-based
---@param col integer  0-based byte column
---@param cb fun(result: { items: table[], start: integer, kind: string })
function M.for_buffer(bufnr, row, col, cb)
  local conn = deps.connection_for(bufnr)
  if not conn then
    return cb({ items = {}, start = col, kind = 'none' })
  end
  local lines = vim.api.nvim_buf_get_lines(bufnr, 0, -1, false)
  local before, statement = M.statement_at(lines, row, col)
  M.complete({ conn = conn, before = before, statement = statement }, function(result)
    result.start = col - (#before - result.start)
    cb(result)
  end)
end

return M
