#!/usr/bin/env bash
# Statement coverage of lua/dbbliss/*.lua by the Lua suites, with luacov (development tool, pinned,
# fetched on demand; not part of the plugin). Output: coverage/luacov.lines.txt and
# coverage/luacov.stats.out. Needs Neovim 0.10+ as `nvim` (or $NVIM).
#
# With DBBLISS_E2E_ENGINE and DBBLISS_E2E_CS set, the end-to-end test runs under luacov too.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
nvim="${NVIM:-nvim}"
out="coverage"
mkdir -p "$out"
rm -f "$out/luacov.stats.out" "$out/luacov.lines.txt"

src="${LUACOV_DIR:-$out/luacov-src}"
if [ ! -d "$src/src/luacov" ]; then
  git clone -q --depth 1 --branch v0.15.0 https://github.com/lunarmodules/luacov "$src"
fi
export LUACOV_SRC="$PWD/$src/src" LUACOV_STATS="$PWD/$out/luacov.stats.out"

run() { "$nvim" --headless --clean --cmd 'set rtp^=.' -l tests/nvim/cover.lua "$@"; }
run tests/nvim/client_test.lua
run tests/nvim/results_test.lua
run tests/nvim/catalog_test.lua
if [ -n "${DBBLISS_E2E_ENGINE:-}" ]; then run tests/nvim/e2e_test.lua; fi

cat > "$out/luacov.config.lua" <<LUA
return { statsfile = '$PWD/$out/luacov.stats.out', reportfile = '$PWD/$out/luacov.lines.txt', include = { 'lua/dbbliss/.*\$' } }
LUA
cat > "$out/luacov-report.lua" <<'LUA'
-- Writes one line per source line: "F <file>", then "H <n>" (executed) or "M <n>" (executable, not executed).
package.path = os.getenv('LUACOV_SRC') .. '/?.lua;' .. os.getenv('LUACOV_SRC') .. '/?/init.lua;' .. package.path
require('luacov.runner').load_config(dofile(os.getenv('LUACOV_CONFIG')))
local reporter = require('luacov.reporter')
local R = setmetatable({}, reporter.ReporterBase or getmetatable(reporter.DefaultReporter) or reporter.DefaultReporter)
R.__index = R
local base = reporter.DefaultReporter
local Lines = setmetatable({}, { __index = base })
Lines.__index = Lines
function Lines:on_start() end
function Lines:on_new_file(f) self:write('F ' .. f .. '\n') end
function Lines:on_empty_line() end
function Lines:on_mis_line(_, n) self:write('M ' .. n .. '\n') end
function Lines:on_hit_line(_, n) self:write('H ' .. n .. '\n') end
function Lines:on_end_file() end
function Lines:on_end() end
reporter.report(Lines)
LUA
LUACOV_CONFIG="$PWD/$out/luacov.config.lua" "$nvim" --headless --clean -l "$out/luacov-report.lua"
grep -c '^M ' "$out/luacov.lines.txt" | sed 's/^/lua lines not executed: /'
