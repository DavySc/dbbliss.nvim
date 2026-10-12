-- Installation with Neovim packages (Phase 7): the plugin in a pack/*/start or pack/*/opt directory (path with spaces),
-- vim.pack (Neovim 0.12+) with a PackChanged build hook, the build command, the help file and :checkhealth.
-- The package layouts are tested in child Neovim processes with their own 'packpath', so what is tested is what a user gets.
--
--   nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/pack_test.lua
-- DBBLISS_REQUIRE_VIM_PACK=1 makes a Neovim without vim.pack a failure instead of a skip (CI sets it on 0.12+).
vim.fs.joinpath = vim.fs.joinpath or function(...)
  return table.concat({ ... }, '/')
end
vim.notify = function() end

local uv = vim.uv or vim.loop
local is_win = vim.fn.has('win32') == 1
local root = vim.fn.fnamemodify(debug.getinfo(1, 'S').source:sub(2), ':p:h:h:h')
local build = require('dbbliss.build')

local function expect(cond, msg)
  if not cond then
    error(msg, 0)
  end
end

local function eq(actual, expected, what)
  if not vim.deep_equal(actual, expected) then
    error(('%s:\n  got      %s\n  expected %s'):format(what, vim.inspect(actual), vim.inspect(expected)), 0)
  end
end

local function tmpdir(name)
  local dir = vim.fn.tempname() .. ' ' .. name -- a space in every path: a package may sit anywhere
  vim.fn.mkdir(dir, 'p')
  return dir
end

local function copy_tree(from, to)
  vim.fn.mkdir(to, 'p')
  for name, kind in vim.fs.dir(from) do
    local src, dst = vim.fs.joinpath(from, name), vim.fs.joinpath(to, name)
    if kind == 'directory' then
      copy_tree(src, dst)
    else
      assert(uv.fs_copyfile(src, dst))
    end
  end
end

--- The plugin as a package copy: only what is committed that matters to a user (no bin/, no backend source).
local function install_copy(dest)
  for _, d in ipairs({ 'lua', 'plugin', 'doc', 'scripts' }) do
    copy_tree(vim.fs.joinpath(root, d), vim.fs.joinpath(dest, d))
  end
end

local function write(path, text)
  vim.fn.mkdir(vim.fn.fnamemodify(path, ':h'), 'p')
  local f = assert(io.open(path, 'w'))
  f:write(text)
  f:close()
end

--- Runs a child Neovim the way a user's does: `init_text` is the init.lua (so vim.pack.add and packadd! behave as in a real
--- init), `main_text` runs on VimEnter, after the start packages are loaded. Own data directories, and own packpath unless nil
--- (vim.pack installs into the default one).
--- Returns the decoded JSON `main_text` wrote to stdout.
local function child(home, packpath, main_text, init_text, env)
  local init = vim.fs.joinpath(home, 'init.lua')
  write(init, (init_text or '') .. '\nvim.api.nvim_create_autocmd("VimEnter", { once = true, callback = function()\n'
    .. main_text .. '\nvim.schedule(function() vim.cmd("qa!") end)\nend })\n')
  local cmd = { vim.v.progpath, '--headless', '-n', '-i', 'NONE' }
  if packpath then
    vim.list_extend(cmd, { '--cmd', 'let &packpath = ' .. vim.fn.string(packpath) })
  end
  vim.list_extend(cmd, { '-u', init })
  local res = vim.system(cmd, {
    text = true,
    env = vim.tbl_extend('force', {
      XDG_DATA_HOME = vim.fs.joinpath(home, 'data'),
      XDG_STATE_HOME = vim.fs.joinpath(home, 'state'),
      XDG_CONFIG_HOME = vim.fs.joinpath(home, 'config'),
    }, env or {}),
  }):wait(60000)
  local text = (res.stdout or '') .. (res.stderr or '')
  local json = text:match('(%b{})')
  expect(json, 'the child printed no result (exit ' .. tostring(res.code) .. '): ' .. text)
  return vim.json.decode(json), text
end

local function wait_for(f, ms)
  local result
  vim.wait(ms or 15000, function()
    result = f()
    return result ~= nil
  end, 20)
  return result
end

local tests = {
  {
    -- Verifies: HLR-PACK-1
    'pack_start_directory_loads_the_plugin_and_finds_its_backend_inside',
    function()
      local home = tmpdir('start')
      local pkg = vim.fs.joinpath(home, 'pack', 'mine', 'start', 'dbbliss.nvim')
      install_copy(pkg)
      local out = child(home, home, [[
        local b = require('dbbliss.build')
        io.stdout:write(vim.json.encode({
          command = vim.fn.exists(':Dbbliss'),
          loaded = vim.g.loaded_dbbliss,
          backend = b.backend_path(),
          root = b.root(),
        }))
      ]])
      eq(out.command, 2, 'the :Dbbliss command exists with no change to runtimepath')
      eq(out.loaded, true, 'plugin/dbbliss.lua was sourced')
      eq(vim.fs.normalize(out.root), vim.fs.normalize(pkg), 'the plugin root is the package directory')
      expect(vim.startswith(vim.fs.normalize(out.backend), vim.fs.normalize(pkg)), 'the backend is looked for inside the package: ' .. out.backend)
    end,
  },
  {
    -- Verifies: HLR-PACK-1
    'pack_opt_directory_loads_only_on_packadd_and_setup_works_after_it',
    function()
      local home = tmpdir('opt')
      install_copy(vim.fs.joinpath(home, 'pack', 'mine', 'opt', 'dbbliss.nvim'))
      local out = child(home, home, [[
        local out = { before = vim.fn.exists(':Dbbliss'), required_before = (pcall(require, 'dbbliss')) }
        vim.cmd('packadd dbbliss.nvim')
        out.after = vim.fn.exists(':Dbbliss')
        out.required_after = (pcall(require, 'dbbliss'))
        out.setup = (pcall(function() require('dbbliss').setup({ connections = {} }) end))
        out.status = (pcall(vim.cmd, 'Dbbliss status'))
        io.stdout:write(vim.json.encode(out))
      ]])
      eq(out.before, 0, 'an opt package is not loaded at startup')
      eq(out.required_before, false, 'and not on the runtimepath')
      eq(out.after, 2, 'packadd loads plugin/dbbliss.lua')
      eq(out.required_after, true, 'packadd puts lua/ on the runtimepath')
      eq(out.setup, true, 'setup works after packadd')
      eq(out.status, true, ':Dbbliss status runs')
    end,
  },
  {
    -- Verifies: HLR-PACK-1
    'pack_symlinked_directory_works',
    function()
      if is_win then
        return -- creating a symlink needs a privilege on Windows
      end
      local home = tmpdir('link')
      local real = vim.fs.joinpath(home, 'checkout')
      install_copy(real)
      local dir = vim.fs.joinpath(home, 'pack', 'mine', 'start')
      vim.fn.mkdir(dir, 'p')
      assert(uv.fs_symlink(real, vim.fs.joinpath(dir, 'dbbliss.nvim')))
      local out = child(home, home, [[
        io.stdout:write(vim.json.encode({ command = vim.fn.exists(':Dbbliss'), backend = require('dbbliss.build').backend_path() }))
      ]])
      eq(out.command, 2, 'the command exists through a symlink')
      expect(out.backend:find('bin', 1, true), 'the backend path is built: ' .. out.backend)
    end,
  },
  {
    -- Verifies: LLR-PACK-3
    'pack_missing_backend_names_the_path_and_the_fix',
    function()
      local home = tmpdir('missing')
      install_copy(vim.fs.joinpath(home, 'pack', 'mine', 'start', 'dbbliss.nvim'))
      local out = child(home, home, [[
        local b = require('dbbliss.build')
        local backend = require('dbbliss.backend').new({ b.backend_path() })
        local ok, err = pcall(function() backend:start() end)
        io.stdout:write(vim.json.encode({ ok = ok, err = tostring(err), path = b.backend_path() }))
      ]])
      eq(out.ok, false, 'starting a backend that is not built fails')
      expect(out.err:find(out.path, 1, true), 'the message names the path that was looked for: ' .. out.err)
      expect(out.err:find(':Dbbliss build', 1, true), 'and the fix: ' .. out.err)
    end,
  },
  {
    -- Verifies: LLR-PACK-4
    'build_command_is_the_platform_script_with_the_runtime_identifier',
    function()
      eq(build.rid({ os = 'linux', arch = 'x64' }), 'linux-x64', 'linux x64')
      eq(build.rid({ os = 'linux', arch = 'arm64' }), 'linux-arm64', 'linux arm64')
      eq(build.rid({ os = 'windows', arch = 'x64' }), 'win-x64', 'windows x64')
      eq(build.rid({ os = 'windows', arch = 'arm64' }), 'win-arm64', 'windows arm64')
      local linux = build.script_cmd({ os = 'linux', arch = 'x64' })
      eq(linux, { 'bash', vim.fs.joinpath(build.root(), 'scripts', 'build.sh'), 'linux-x64' }, 'linux command')
      local win = build.script_cmd({ os = 'windows', arch = 'x64' })
      eq(win, { 'powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', vim.fs.joinpath(build.root(), 'scripts', 'build.ps1'), '-Rid', 'win-x64' }, 'windows command')
      expect(build.backend_path({ os = 'windows', arch = 'x64' }):find('dbbliss-backend.exe', 1, true), 'the Windows executable has .exe')
      expect(not build.backend_path({ os = 'linux', arch = 'x64' }):find('.exe', 1, true), 'the Linux one does not')
    end,
  },
  {
    -- Verifies: LLR-PACK-4
    'dbbliss_build_subcommand_runs_the_build_and_is_completed',
    function()
      local dbbliss = require('dbbliss')
      local real = build.run
      local called = 0
      build.run = function()
        called = called + 1
      end
      local ok, err = pcall(dbbliss.command, { args = 'build', fargs = { 'build' }, range = 0, line1 = 1, line2 = 1 })
      build.run = real
      expect(ok, tostring(err))
      eq(called, 1, ':Dbbliss build runs the build once')
      expect(vim.tbl_contains(dbbliss.complete('bu', 'Dbbliss bu'), 'build'), 'build is completed')
    end,
  },
  {
    -- Verifies: LLR-PACK-4
    'build_refuses_without_dotnet_and_says_what_to_install',
    function()
      local done
      build.run({
        executable = function(name)
          return name ~= 'dotnet'
        end,
        on_done = function(ok, msg)
          done = { ok = ok, msg = msg }
        end,
      })
      expect(done, 'the refusal is reported at once')
      eq(done.ok, false, 'no build without dotnet')
      expect(done.msg:find('.NET 10 SDK', 1, true), 'the message names what to install: ' .. done.msg)
    end,
  },
  {
    -- Verifies: LLR-PACK-4
    'build_reports_the_exit_code_and_output_tail_of_a_failed_build',
    function()
      local home = tmpdir('failbuild')
      write(vim.fs.joinpath(home, 'fail.lua'), "io.stdout:write('line one\\n') io.stderr:write('compiler said no\\n') os.exit(3)")
      local done
      build.run({
        executable = function()
          return true
        end,
        cmd = { vim.v.progpath, '--headless', '--clean', '-l', vim.fs.joinpath(home, 'fail.lua') },
        on_done = function(ok, msg)
          done = { ok = ok, msg = msg }
        end,
      })
      expect(wait_for(function()
        return done
      end), 'the build did not end')
      eq(done.ok, false, 'a failing build is a failure')
      expect(done.msg:find('3', 1, true) and done.msg:find('compiler said no', 1, true), 'exit code and output tail: ' .. done.msg)
    end,
  },
  {
    -- Verifies: LLR-PACK-4
    'build_succeeds_only_if_the_backend_exists_afterwards',
    function()
      local home = tmpdir('okbuild')
      local exe = vim.fs.joinpath(home, 'bin', 'dbbliss-backend')
      local function run(script)
        write(vim.fs.joinpath(home, 'ok.lua'), script)
        local done
        build.run({
          executable = function()
            return true
          end,
          cmd = { vim.v.progpath, '--headless', '--clean', '-l', vim.fs.joinpath(home, 'ok.lua') },
          exe = exe,
          on_done = function(ok, msg)
            done = { ok = ok, msg = msg }
          end,
        })
        expect(wait_for(function()
          return done
        end), 'the build did not end')
        return done
      end
      local empty = run('os.exit(0)')
      eq(empty.ok, false, 'exit 0 without an executable is not a success')
      expect(empty.msg:find(exe, 1, true), 'the missing file is named: ' .. empty.msg)
      local full = run(("vim.fn.mkdir(%q, 'p') io.open(%q, 'w'):close() os.exit(0)"):format(vim.fs.joinpath(home, 'bin'), exe))
      eq(full.ok, true, 'exit 0 and the executable exists')
    end,
  },
  {
    -- Verifies: LLR-PACK-5
    'help_file_is_shipped_with_current_tags',
    function()
      local doc = vim.fs.joinpath(root, 'doc')
      expect(vim.uv.fs_stat(vim.fs.joinpath(doc, 'dbbliss.txt')), 'doc/dbbliss.txt exists')
      expect(vim.uv.fs_stat(vim.fs.joinpath(doc, 'tags')), 'doc/tags is committed (a package needs no :helptags)')
      local scratch = tmpdir('helptags')
      copy_tree(doc, scratch)
      os.remove(vim.fs.joinpath(scratch, 'tags'))
      vim.cmd('helptags ' .. vim.fn.fnameescape(scratch))
      local function tags(dir)
        local lines = vim.fn.readfile(vim.fs.joinpath(dir, 'tags'))
        table.sort(lines)
        return lines
      end
      eq(tags(doc), tags(scratch), 'doc/tags is what :helptags makes of doc/dbbliss.txt (run :helptags doc in the repository)')
      local home = tmpdir('help')
      install_copy(vim.fs.joinpath(home, 'pack', 'mine', 'start', 'dbbliss.nvim'))
      local out = child(home, home, [[
        local ok = pcall(vim.cmd, 'help dbbliss')
        io.stdout:write(vim.json.encode({ ok = ok, file = vim.fn.expand('%:t'), bt = vim.bo.buftype }))
      ]])
      eq(out, { ok = true, file = 'dbbliss.txt', bt = 'help' }, ':help dbbliss opens the plugin help after a start package loads')
    end,
  },
  {
    -- Verifies: LLR-PACK-6
    'health_check_reports_version_backend_dotnet_and_platform',
    function()
      local lines = {}
      local real = vim.health
      local function add(prefix)
        return function(msg, advice)
          lines[#lines + 1] = prefix .. ' ' .. tostring(msg) .. (advice and (' ' .. table.concat(type(advice) == 'table' and advice or { advice }, ' ')) or '')
        end
      end
      vim.health = { start = add('#'), ok = add('ok'), warn = add('warn'), error = add('error'), info = add('info') }
      local ok, err = pcall(function()
        require('dbbliss.health').check({
          executable = function()
            return false
          end,
          backend_exists = function()
            return false
          end,
        })
      end)
      vim.health = real
      expect(ok, tostring(err))
      local text = table.concat(lines, '\n')
      expect(text:find('Neovim', 1, true) and text:find('0.10', 1, true), 'the Neovim version against the minimum:\n' .. text)
      expect(text:find('backend', 1, true) and text:find(':Dbbliss build', 1, true), 'a missing backend and how to build it:\n' .. text)
      expect(text:find('error backend not found', 1, true), 'a missing backend is an error:\n' .. text)
      expect(text:find('error dotnet not found', 1, true), 'dotnet missing is an error when the backend is missing too:\n' .. text)
      expect(text:find(build.rid(), 1, true), 'the platform identifier:\n' .. text)

      local good = {}
      vim.health = { start = function() end, ok = function(m) good[#good + 1] = 'ok ' .. m end, warn = function(m) good[#good + 1] = 'warn ' .. m end, error = function(m) good[#good + 1] = 'error ' .. m end, info = function() end }
      pcall(function()
        require('dbbliss.health').check({
          executable = function()
            return true
          end,
          backend_exists = function()
            return true
          end,
        })
      end)
      vim.health = real
      for _, l in ipairs(good) do
        expect(not l:find('^error'), 'nothing is an error when all is present: ' .. l)
      end
    end,
  },
  {
    -- Verifies: LLR-PACK-6
    'checkhealth_dbbliss_runs_from_a_package',
    function()
      local home = tmpdir('health')
      install_copy(vim.fs.joinpath(home, 'pack', 'mine', 'start', 'dbbliss.nvim'))
      local out = child(home, home, [[
        local ok, err = pcall(vim.cmd, 'checkhealth dbbliss')
        io.stdout:write(vim.json.encode({ ok = ok, text = table.concat(vim.api.nvim_buf_get_lines(0, 0, -1, false), '\n') }))
      ]])
      eq(out.ok, true, ':checkhealth dbbliss runs')
      expect(out.text:find('dbbliss', 1, true) and out.text:find('backend', 1, true), 'its report is shown: ' .. out.text:sub(1, 400))
    end,
  },
  {
    -- Verifies: LLR-PACK-2
    'vim_pack_installs_and_one_packchanged_handler_builds_the_backend',
    function()
      if vim.pack == nil then
        expect(vim.env.DBBLISS_REQUIRE_VIM_PACK ~= '1', 'vim.pack is required here (Neovim 0.12+) but this Neovim has none')
        return
      end
      expect(vim.fn.executable('git') == 1, 'git is needed for vim.pack')
      local home = tmpdir('vimpack')
      -- The "remote": a git repository holding the plugin as committed.
      local remote = vim.fs.joinpath(home, 'remote repo')
      install_copy(remote)
      local function git(...)
        local res = vim.system(vim.list_extend({ 'git', '-C', remote, '-c', 'user.name=t', '-c', 'user.email=t@example.com' }, { ... }), { text = true }):wait(30000)
        expect(res.code == 0, 'git ' .. table.concat({ ... }, ' ') .. ': ' .. (res.stderr or ''))
      end
      git('init', '-q', '-b', 'main')
      git('add', '-A')
      git('commit', '-q', '-m', 'plugin')
      -- A fake build that makes the executable the real one would (no dotnet needed).
      write(vim.fs.joinpath(home, 'fakebuild.lua'), [[
        local exe = os.getenv('DBBLISS_FAKE_EXE')
        vim.fn.mkdir(vim.fn.fnamemodify(exe, ':h'), 'p')
        io.open(exe, 'w'):close()
      ]])
      local fake = vim.fs.joinpath(home, 'fakebuild.lua')
      local init_text = ([[
        _G.seen, _G.done = {}, nil
        vim.api.nvim_create_autocmd('PackChanged', {
          callback = function(ev)
            local d = ev.data
            seen[#seen + 1] = d.kind .. ':' .. d.spec.name
            if d.spec.name == 'dbbliss.nvim' and (d.kind == 'install' or d.kind == 'update') then
              if not d.active then vim.cmd.packadd('dbbliss.nvim') end
              local b = require('dbbliss.build')
              vim.env.DBBLISS_FAKE_EXE = b.backend_path()
              b.run({
                executable = function() return true end,
                cmd = { vim.v.progpath, '--headless', '--clean', '-l', %s },
                on_done = function(ok, msg) _G.done = { ok = ok, msg = msg } end,
              })
            end
          end,
        })
        vim.pack.add({ { src = %s, name = 'dbbliss.nvim' } }, { confirm = false })
      ]]):format(vim.fn.string(fake), vim.fn.string(remote))
      local out = child(home, nil, [[
        vim.wait(30000, function() return _G.done ~= nil end, 20)
        local b = require('dbbliss.build')
        io.stdout:write(vim.json.encode({
          command = vim.fn.exists(':Dbbliss'),
          seen = _G.seen,
          done = _G.done,
          exe = vim.uv.fs_stat(b.backend_path()) ~= nil,
          root = b.root(),
        }))
      ]], init_text)
      eq(out.command, 2, 'vim.pack.add loads the plugin: :Dbbliss exists')
      expect(vim.tbl_contains(out.seen, 'install:dbbliss.nvim'), 'PackChanged fired for the install: ' .. vim.inspect(out.seen))
      expect(out.done and out.done.ok, 'the build run from the handler succeeded: ' .. vim.inspect(out.done))
      eq(out.exe, true, 'the backend executable is where the plugin looks for it')
      expect(out.root:find('pack', 1, true), 'the plugin runs from vim.pack\'s directory: ' .. out.root)
    end,
  },
}

local failed = 0
for _, t in ipairs(tests) do
  local ok, err = pcall(t[2])
  if ok then
    io.stdout:write(('%-80s PASS\n'):format(t[1]))
  else
    failed = failed + 1
    io.stdout:write(('%-80s FAIL  %s\n'):format(t[1], tostring(err)))
  end
end
io.stdout:write(('\n%d tests, %d failed\n'):format(#tests, failed))
os.exit(failed == 0 and 0 or 1)
