if vim.g.loaded_dbbliss then
  return
end
vim.g.loaded_dbbliss = true

vim.api.nvim_create_user_command('Dbbliss', function(opts)
  require('dbbliss').command(opts)
end, {
  nargs = '+',
  range = true,
  complete = function(arglead, cmdline)
    return require('dbbliss').complete(arglead, cmdline)
  end,
  desc = 'dbbliss: connect | exec | exec_all | export | fetch | cancel | begin | commit | rollback | disconnect | status',
})

vim.api.nvim_create_autocmd('VimLeavePre', {
  group = vim.api.nvim_create_augroup('dbbliss', { clear = true }),
  callback = function()
    if package.loaded['dbbliss'] then
      require('dbbliss').on_exit()
    end
  end,
})
