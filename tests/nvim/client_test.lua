-- Lua client tests: the client side of spec/quint/client.qnt, with a stub in place of the backend
-- process. The stub records requests; each test answers them in the order it wants.
--
--   nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/client_test.lua
--
-- Exits 1 if any test fails.

local stub = { requests = {} }

package.loaded['dbbliss.backend'] = {
  new = function()
    local b = { exited = false, pid = 0 }
    function b:on() end
    function b:on_exit() end
    function b:start() end
    function b:shutdown_sync()
      return true
    end
    function b:request(method, params, cb)
      if method ~= 'initialize' then
        table.insert(stub.requests, { method = method, params = params, cb = cb or function() end })
      end
    end
    return b
  end,
}

local dbbliss = require('dbbliss')
vim.notify = function() end
-- Every rollback prompt is answered "Rollback".
vim.fn.confirm = function()
  stub.confirms = stub.confirms + 1
  return 1
end

local next_connection = 0

--- Answers the oldest unanswered request for `method` the way the backend would.
local function answer(method)
  for i, r in ipairs(stub.requests) do
    if r.method == method then
      table.remove(stub.requests, i)
      if method == 'connect' then
        next_connection = next_connection + 1
        local id = 'c' .. next_connection
        stub.open[id] = true
        r.cb(nil, { connection_id = id, engine = 'postgres', server_session_id = tostring(100 + next_connection) })
      elseif method == 'disconnect' then
        local id = r.params.connection_id
        if stub.server_tx[id] and not r.params.rollback then
          r.cb({ code = 1003, message = 'The connection has an open transaction (active).' }, nil)
        else
          stub.open[id] = nil
          stub.server_tx[id] = nil
          r.cb(nil, { rolled_back = r.params.rollback })
        end
      end
      return true
    end
  end
  return false
end

local function answer_all()
  while answer('connect') or answer('disconnect') do
  end
end

local function reset()
  dbbliss.setup({ connections = { a = { engine = 'postgres', connection_string = 'Host=x' } } })
  dbbliss._state.connections = {}
  dbbliss._state.current = nil
  dbbliss._state.backend = nil
  stub.requests = {}
  stub.open = {}
  stub.server_tx = {}
  stub.confirms = 0
  next_connection = 0
end

--- NoOrphanSession: every connection the backend has open is one the client knows about.
local function check_no_orphan()
  local known = {}
  for _, c in pairs(dbbliss._state.connections) do
    known[c.id] = true
  end
  for id in pairs(stub.open) do
    if not known[id] then
      error(('backend connection %s is open but the client no longer knows it (orphaned session)'):format(id), 0)
    end
  end
end

local tests = {
  {
    'connect_disconnect',
    '',
    function()
      dbbliss.connect('a')
      answer_all()
      dbbliss.disconnect()
      answer_all()
      check_no_orphan()
    end,
  },
  {
    -- The client heard nothing of the transaction (typed BEGIN still running, say); the backend
    -- refuses the plain disconnect and the user is asked, not left with an error.
    'disconnect_refused_asks_user',
    '',
    function()
      dbbliss.connect('a')
      answer_all()
      stub.server_tx.c1 = true
      dbbliss.disconnect()
      answer_all()
      if stub.confirms ~= 1 then
        error(('expected one rollback prompt, got %d'):format(stub.confirms), 0)
      end
      if stub.open.c1 or dbbliss._state.connections.a then
        error('the connection should be closed after the user chose rollback', 0)
      end
      check_no_orphan()
    end,
  },
  {
    'connect_twice_while_connected',
    '3: NoOrphanSession',
    function()
      dbbliss.connect('a')
      answer_all()
      dbbliss.connect('a')
      answer_all()
      check_no_orphan()
    end,
  },
  {
    'connect_twice_while_connecting',
    '3: NoOrphanSession',
    function()
      dbbliss.connect('a')
      dbbliss.connect('a')
      answer_all()
      check_no_orphan()
    end,
  },
}

local failed = 0
for _, t in ipairs(tests) do
  local name, bug, run = t[1], t[2], t[3]
  reset()
  local ok, err = pcall(run)
  if ok then
    io.stdout:write(('%-34s PASS\n'):format(name))
  else
    failed = failed + 1
    io.stdout:write(('%-34s FAIL  %s%s\n'):format(name, bug ~= '' and ('[bug ' .. bug .. '] ') or '', tostring(err)))
  end
end
io.stdout:write(('\n%d tests, %d failed\n'):format(#tests, failed))
os.exit(failed == 0 and 0 or 1)
