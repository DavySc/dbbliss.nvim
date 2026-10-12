-- Where the backend lives inside the plugin directory, and how to build it. Nothing here knows how the plugin was installed:
-- a pack/*/start or pack/*/opt directory, vim.pack (Neovim 0.12+) or a checkout on 'runtimepath' all look the same from inside.
local M = {}

local uv = vim.uv or vim.loop

local function join(...)
  return (vim.fs.joinpath or function(...)
    return table.concat({ ... }, '/')
  end)(...)
end

--- The plugin directory, found from this file (so it is right wherever the package sits).
function M.root()
  local src = debug.getinfo(1, 'S').source:sub(2)
  return vim.fn.fnamemodify(src, ':p:h:h:h')
end

---@class dbbliss.Platform
---@field os 'windows'|'linux'
---@field arch 'x64'|'arm64'

---@return dbbliss.Platform
local function platform()
  local uname = uv.os_uname()
  return {
    os = vim.fn.has('win32') == 1 and 'windows' or 'linux',
    arch = (uname.machine == 'aarch64' or uname.machine == 'arm64') and 'arm64' or 'x64',
  }
end

--- The .NET runtime identifier the backend is published for.
---@param p dbbliss.Platform?
function M.rid(p)
  p = p or platform()
  return (p.os == 'windows' and 'win-' or 'linux-') .. p.arch
end

--- Where the plugin looks for the backend executable.
---@param p dbbliss.Platform?
function M.backend_path(p)
  p = p or platform()
  return join(M.root(), 'bin', M.rid(p), p.os == 'windows' and 'dbbliss-backend.exe' or 'dbbliss-backend')
end

--- The command that publishes the backend into bin/<rid>/.
---@param p dbbliss.Platform?
function M.script_cmd(p)
  p = p or platform()
  if p.os == 'windows' then
    return { 'powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', join(M.root(), 'scripts', 'build.ps1'), '-Rid', M.rid(p) }
  end
  return { 'bash', join(M.root(), 'scripts', 'build.sh'), M.rid(p) }
end

local function tail(text, n)
  local lines = vim.split(vim.trim(text or ''), '\n', { plain = true })
  return table.concat(vim.list_slice(lines, math.max(1, #lines - n + 1), #lines), '\n')
end

--- Builds the backend without blocking Neovim. `on_done(ok, message)` is called once, always from the main loop.
--- Needs the .NET 10 SDK. Success means the executable exists afterwards, not only that the script exited 0.
---@param opts { on_done: fun(ok: boolean, message: string)?, cmd: string[]?, exe: string?, executable: (fun(name: string): boolean)? }?
function M.run(opts)
  opts = opts or {}
  local executable = opts.executable or function(name)
    return vim.fn.executable(name) == 1
  end
  local function done(ok, message)
    if opts.on_done then
      opts.on_done(ok, message)
    else
      vim.notify('dbbliss: ' .. message, ok and vim.log.levels.INFO or vim.log.levels.ERROR)
    end
  end
  if not executable('dotnet') then
    return done(false, 'building the backend needs the .NET 10 SDK (https://dot.net), and `dotnet` is not on PATH. Install it, then run :Dbbliss build again.')
  end
  local cmd = opts.cmd or M.script_cmd()
  local exe = opts.exe or M.backend_path()
  local ok, proc = pcall(vim.system, cmd, { cwd = M.root(), text = true }, function(res)
    vim.schedule(function()
      if res.code ~= 0 then
        return done(false, ('the backend build failed (exit code %d):\n%s'):format(res.code, tail((res.stderr or '') .. '\n' .. (res.stdout or ''), 15)))
      end
      if not uv.fs_stat(exe) then
        return done(false, ('the build script finished but %s does not exist.'):format(exe))
      end
      done(true, 'backend built: ' .. exe)
    end)
  end)
  if not ok then
    return done(false, 'could not start the build: ' .. tostring(proc))
  end
  if not opts.on_done then
    vim.notify('dbbliss: building the backend (this takes a minute) ...', vim.log.levels.INFO)
  end
end

return M
