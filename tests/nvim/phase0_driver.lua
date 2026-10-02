-- Driven by the Phase 0 cancel suite (backend/tests/Dbbliss.CancelTests). Runs inside
-- `nvim --headless --clean`: connects through the real plugin, starts a long query, reports the
-- server session id and backend pid to DBBLISS_TEST_OUT, then quits with :qa! when
-- DBBLISS_TEST_QUIT_FILE appears (or is killed by the harness).
local env = vim.env
local out_path = assert(env.DBBLISS_TEST_OUT, 'DBBLISS_TEST_OUT not set')

local function report(line)
  local f = assert(io.open(out_path, 'a'))
  f:write(line, '\n')
  f:close()
end

local dbbliss = require('dbbliss')
dbbliss.setup({
  backend = { cmd = { assert(env.DBBLISS_BACKEND, 'DBBLISS_BACKEND not set') } },
  connections = {
    test = {
      engine = env.DBBLISS_TEST_ENGINE,
      connection_string = env.DBBLISS_TEST_CS,
      password = env.DBBLISS_TEST_PW_ENV and { env = env.DBBLISS_TEST_PW_ENV } or nil,
    },
  },
})

dbbliss.connect('test', function(err, conn)
  if err then
    report('error=' .. err.message)
    return
  end
  report('backend_pid=' .. dbbliss._state.backend.pid)
  report('session=' .. conn.server_session_id)
  dbbliss.execute(env.DBBLISS_TEST_SQL, {
    on_done = function(p)
      report('done=' .. p.status)
    end,
  })
end)

-- vim.env is not available inside a luv callback (fast event context): read it here.
local quit_file = env.DBBLISS_TEST_QUIT_FILE
local timer = assert((vim.uv or vim.loop).new_timer())
timer:start(100, 100, function()
  if quit_file and (vim.uv or vim.loop).fs_stat(quit_file) then
    timer:stop()
    vim.schedule(function()
      vim.cmd('qa!')
    end)
  end
end)
