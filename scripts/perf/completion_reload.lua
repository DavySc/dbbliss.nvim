-- Measures what a completion reload costs on a database with many tables (decision 71): the round trip of catalog/names, the
-- size of its JSON, the main-thread work, and a cached completion. Not run by CI. Make the tables first, e.g. 20 000 with
-- `create table tN (id int primary key, a text, b int, c timestamp)`, then:
--   PERF_ENGINE=postgres PERF_CS="Host=localhost;Port=55432;Username=dbbliss;Database=perfdb" PERF_PW=DBBLISS_TEST_PG_PASSWORD \
--     nvim --headless --clean -l scripts/perf/completion_reload.lua
vim.opt.rtp:prepend('.')
vim.fs.joinpath = vim.fs.joinpath or function(...) return table.concat({...}, '/') end
local engine = os.getenv('PERF_ENGINE')
local dbbliss = require('dbbliss')
vim.notify = function() end
dbbliss.setup({ connections = { db = { engine = engine, connection_string = os.getenv('PERF_CS'), password = { env = os.getenv('PERF_PW') }, env = 'dev' } } })
local done = false
dbbliss.connect('db')
vim.wait(20000, function() return dbbliss._state.connections.db ~= nil end, 20)
local id = dbbliss._state.connections.db.id
local function call(method, params)
  local t0 = vim.uv.hrtime()
  local res
  dbbliss._catalog_request(id, method, params, function(err, r) res = { err = err, r = r } end)
  vim.wait(120000, function() return res ~= nil end, 5)
  local ms = (vim.uv.hrtime() - t0) / 1e6
  return ms, res
end
call('catalog/names', {}) -- warm: opens the catalog session
local samples = {}
local bytes, count, truncated
for i = 1, 7 do
  local ms, res = call('catalog/names', {})
  samples[#samples + 1] = ms
  if i == 1 then
    bytes = #vim.json.encode(res.r)
    count = #res.r.names
    truncated = res.r.truncated
  end
end
table.sort(samples)
local cms = call('catalog/columns', { name = 't42' })
local ms_cols = select(1, call('catalog/columns', { name = 't42' }))
print(('%s: %d names, %d bytes JSON, truncated=%s; names reload median %.0f ms (min %.0f, max %.0f); one table columns %.0f ms'):format(engine, count, bytes, tostring(truncated), samples[4], samples[1], samples[7], ms_cols))
local _, res = call('catalog/names', {})
local enc = vim.json.encode(res.r)
local t0 = vim.uv.hrtime()
for _ = 1, 20 do vim.json.decode(enc) end
local decode_ms = (vim.uv.hrtime() - t0) / 1e6 / 20
local completion = require('dbbliss.completion')
completion.setup({ request = function(cid, m, p, cb) cb(nil, res.r) end, connection_for = function() return { id = 'x', engine = engine } end })
local t1 = vim.uv.hrtime()
for _ = 1, 20 do completion.reset(); completion.complete({ conn = { id = 'x', engine = engine }, before = 'select * from t1', statement = 'select * from t1' }, function() end) end
local complete_ms = (vim.uv.hrtime() - t1) / 1e6 / 20
local t2 = vim.uv.hrtime()
for _ = 1, 20 do completion.complete({ conn = { id = 'x', engine = engine }, before = 'select * from t1', statement = 'select * from t1' }, function() end) end
local cached_ms = (vim.uv.hrtime() - t2) / 1e6 / 20
print(('  main-thread: JSON decode %.1f ms; first complete incl. dedupe+filter %.1f ms; cached complete (prefix t1) %.1f ms'):format(decode_ms, complete_ms, cached_ms))
pcall(dbbliss.on_exit)
os.exit(0)
