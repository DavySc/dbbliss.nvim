-- A stand-in for the configured AI command in ai_test.lua: reads stdin, then behaves as AI_ECHO_MODE says.
-- Run with the Neovim that runs the tests, so it exists on every platform:  nvim --headless --clean -l tests/nvim/ai_echo.lua
local mode = os.getenv('AI_ECHO_MODE') or 'echo'
local input = io.read('*a') or ''
if mode == 'echo' then
  io.stdout:write('ANSWER<<' .. input .. '>>')
elseif mode == 'fail' then
  io.stderr:write('model unavailable: quota exceeded\n')
  os.exit(3)
elseif mode == 'silent' then
  os.exit(0)
elseif mode == 'hang' then
  vim.wait(120000)
end
os.exit(0)
