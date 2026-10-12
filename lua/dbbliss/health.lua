-- :checkhealth dbbliss
local M = {}

---@param deps { executable: (fun(name: string): boolean)?, backend_exists: (fun(path: string): boolean)? }?
function M.check(deps)
  deps = deps or {}
  local build = require('dbbliss.build')
  local executable = deps.executable or function(name)
    return vim.fn.executable(name) == 1
  end
  local backend_exists = deps.backend_exists or function(path)
    return (vim.uv or vim.loop).fs_stat(path) ~= nil
  end
  local health = vim.health

  health.start('dbbliss: Neovim')
  local v = vim.version()
  local version = ('%d.%d.%d'):format(v.major, v.minor, v.patch)
  if vim.fn.has('nvim-0.10') == 1 then
    health.ok(('Neovim %s (0.10 or newer is required)'):format(version))
  else
    health.error(('Neovim %s is too old: dbbliss needs 0.10 or newer'):format(version))
  end
  health.info('plugin directory: ' .. build.root())

  health.start('dbbliss: backend')
  health.info('platform: ' .. build.rid())
  local exe = build.backend_path()
  local have_dotnet = executable('dotnet')
  if backend_exists(exe) then
    health.ok('backend found: ' .. exe)
  else
    health.error('backend not found: ' .. exe, { 'Run :Dbbliss build (needs the .NET 10 SDK).', 'With vim.pack, build it from a PackChanged handler: see :help dbbliss-install.' })
  end
  if have_dotnet then
    health.ok('dotnet found (the backend can be built here)')
  elseif backend_exists(exe) then
    health.warn('dotnet not found: fine to run, but :Dbbliss build cannot rebuild the backend')
  else
    health.error('dotnet not found: install the .NET 10 SDK (https://dot.net) to build the backend')
  end
end

return M
