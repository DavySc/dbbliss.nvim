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
  desc = 'dbbliss: connect | exec | exec_all | export | fetch | info | tree | script | cancel | begin | commit | rollback | disconnect | status',
})

vim.api.nvim_create_autocmd('VimLeavePre', {
  group = vim.api.nvim_create_augroup('dbbliss', { clear = true }),
  callback = function()
    if package.loaded['dbbliss'] then
      require('dbbliss').on_exit()
    end
  end,
})

local group = vim.api.nvim_create_augroup('dbbliss_guards', { clear = true })

-- Quitting rolls back open transactions: ask first (see dbbliss.on_exit_pre).
vim.api.nvim_create_autocmd('ExitPre', {
  group = group,
  callback = function()
    if package.loaded['dbbliss'] then
      require('dbbliss').on_exit_pre()
    end
  end,
})

-- Closing a buffer that ran statements on a connection with an open transaction.
vim.api.nvim_create_autocmd({ 'BufDelete', 'BufWipeout' }, {
  group = group,
  callback = function(args)
    if package.loaded['dbbliss'] then
      require('dbbliss').on_buf_close(args.buf)
    end
  end,
})

-- Statusline highlights exist from the start, so 'statusline' can use them before the first query.
vim.api.nvim_set_hl(0, 'DbblissProd', { fg = '#ffffff', bg = '#c0392b', bold = true, default = true })
vim.api.nvim_set_hl(0, 'DbblissTx', { link = 'WarningMsg', default = true })
