-- Process management and line-delimited JSON-RPC client for dbbliss-backend.
local uv = vim.uv or vim.loop

---@class dbbliss.Backend
---@field cmd string[]
---@field proc vim.SystemObj?
---@field pid integer?
---@field exited boolean
---@field private next_id integer
---@field private pending table<integer, fun(err: table?, result: any)>
---@field private handlers table<string, fun(params: table)>
---@field private partial string
---@field private on_exit_cb fun(code: integer, signal: integer)?
local Backend = {}
Backend.__index = Backend

local M = {}

local function log_path()
  return vim.fs.joinpath(vim.fn.stdpath('log'), 'dbbliss-backend.log')
end

---@param cmd string[]
---@return dbbliss.Backend
function M.new(cmd)
  return setmetatable({
    cmd = cmd,
    exited = false,
    next_id = 0,
    pending = {},
    handlers = {},
    partial = '',
  }, Backend)
end

--- Registers the handler for a notification method (query/rows, query/done, ...).
function Backend:on(method, handler)
  self.handlers[method] = handler
end

--- Called once the process has exited, after all pending requests failed.
function Backend:on_exit(cb)
  self.on_exit_cb = cb
end

function Backend:start()
  local exe = self.cmd[1]
  if vim.fn.executable(exe) ~= 1 then
    error(
      ('dbbliss: backend not found or not executable: %s\nBuild it with scripts/build.sh (Linux) or scripts\\build.ps1 (Windows).'):format(
        exe
      ),
      0
    )
  end
  local log = io.open(log_path(), 'a')
  self.proc = vim.system(self.cmd, {
    -- Windows: libuv puts every non-detached child in a job object that kills it the moment
    -- Neovim exits, so a killed Neovim took the backend down hard, before it could cancel its
    -- queries (nvim_killed). Detached, it sees stdin close and shuts down cleanly, as on Linux.
    detach = vim.fn.has('win32') == 1,
    stdin = true,
    text = false,
    stdout = function(err, data)
      if err then
        vim.schedule(function()
          vim.notify('dbbliss: backend stdout error: ' .. err, vim.log.levels.ERROR)
        end)
      elseif data then
        self:_on_stdout(data)
      end
    end,
    stderr = function(_, data)
      if data and log then
        log:write(data)
        log:flush()
      end
    end,
  }, function(result)
    if log then
      log:close()
      log = nil
    end
    vim.schedule(function()
      self:_on_process_exit(result.code, result.signal)
    end)
  end)
  self.pid = self.proc.pid
end

function Backend:_on_stdout(data)
  local buf = self.partial .. data
  local start = 1
  while true do
    local nl = buf:find('\n', start, true)
    if not nl then
      break
    end
    local line = buf:sub(start, nl - 1):gsub('\r$', '')
    start = nl + 1
    if line ~= '' then
      local ok, msg = pcall(vim.json.decode, line)
      vim.schedule(function()
        if ok then
          self:_dispatch(msg)
        else
          vim.notify('dbbliss: invalid message from backend: ' .. line:sub(1, 200), vim.log.levels.ERROR)
        end
      end)
    end
  end
  self.partial = buf:sub(start)
end

function Backend:_dispatch(msg)
  if msg.id ~= nil and msg.id ~= vim.NIL and (msg.result ~= nil or msg.error ~= nil) then
    local cb = self.pending[msg.id]
    self.pending[msg.id] = nil
    if cb then
      local err = msg.error ~= vim.NIL and msg.error or nil
      cb(err, msg.result)
    end
    return
  end
  if msg.id == vim.NIL and msg.error then
    vim.notify('dbbliss: backend error: ' .. tostring(msg.error.message), vim.log.levels.ERROR)
    return
  end
  local handler = msg.method and self.handlers[msg.method]
  if handler then
    handler(msg.params or {})
  elseif msg.method then
    vim.notify('dbbliss: unhandled backend notification ' .. msg.method, vim.log.levels.WARN)
  end
end

function Backend:_on_process_exit(code, signal)
  self.exited = true
  local pending = self.pending
  self.pending = {}
  local reason = { code = -1, message = ('backend exited (code %d, signal %d)'):format(code, signal) }
  for _, cb in pairs(pending) do
    cb(reason, nil)
  end
  if self.on_exit_cb then
    self.on_exit_cb(code, signal)
  end
end

---@param method string
---@param params table?
---@param cb fun(err: table?, result: any)?
function Backend:request(method, params, cb)
  if self.exited or not self.proc then
    if cb then
      cb({ code = -1, message = 'backend is not running' }, nil)
    end
    return
  end
  self.next_id = self.next_id + 1
  local id = self.next_id
  self.pending[id] = cb or function(err)
    if err then
      vim.notify(('dbbliss: %s failed: %s'):format(method, err.message), vim.log.levels.ERROR)
    end
  end
  local payload = vim.json.encode({ jsonrpc = '2.0', id = id, method = method, params = params or vim.empty_dict() })
  self.proc:write(payload .. '\n')
end

--- Blocks until the backend has cancelled running queries and closed its connections (or the
--- timeout passes). Used from VimLeavePre: on Windows the process would otherwise be killed hard.
---@param timeout_ms integer
---@return boolean exited
function Backend:shutdown_sync(timeout_ms)
  if self.exited or not self.proc then
    return true
  end
  self:request('shutdown', nil, function() end)
  pcall(function()
    self.proc:write(nil) -- close stdin: the backend also shuts down on EOF
  end)
  local done = vim.wait(timeout_ms, function()
    return self.exited or self.proc:is_closing()
  end, 20)
  return done
end

M.uv = uv
return M
