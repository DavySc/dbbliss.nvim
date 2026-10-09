-- Runs one Lua test file under luacov (statement coverage of lua/dbbliss/*.lua).
--
--   LUACOV_SRC=<luacov>/src LUACOV_STATS=<file> nvim --headless --clean --cmd 'set rtp^=.' \
--     -l tests/nvim/cover.lua tests/nvim/client_test.lua
--
-- luacov is a development tool fetched by scripts/assurance/lua-coverage.sh (pinned tag); it is
-- not part of the plugin. Stats from several runs are merged by luacov itself (same stats file).
local src = assert(os.getenv('LUACOV_SRC'), 'LUACOV_SRC not set')
package.path = src .. '/?.lua;' .. src .. '/?/init.lua;' .. package.path
local runner = require('luacov.runner')
runner.init({
  statsfile = assert(os.getenv('LUACOV_STATS'), 'LUACOV_STATS not set'),
  include = { 'lua/dbbliss/.*$' },
  exclude = {},
  savestepsize = 1000,
})
local test = assert(arg[1], 'usage: cover.lua <test file>')
-- The test files end in os.exit(): save the stats first.
local exit = os.exit
os.exit = function(code, ...)
  runner.save_stats()
  exit(code, ...)
end
dofile(test)
runner.save_stats()
