# Installation decisions

For the user's review. Numbered in the one sequence of decisions; 77 and 78 are used on another branch, so this one is 79.

**79. Installing with Neovim's packages and vim.pack.** The request: dbbliss must work well with `pack`, Neovim's built-in package manager.
It already loaded from a `start` or `opt` directory (the backend path is found from the plugin file itself), so the gaps were around it:
a package manager only copies files, and `bin/` is not in git, so a fresh install had **no backend** and the only hint was a path.
Done: `:Dbbliss build` and `require('dbbliss.build').run()` (asynchronous, needs `dotnet`, success only if the executable exists
afterwards); a missing backend now says `:Dbbliss build`; `:checkhealth dbbliss`; `doc/dbbliss.txt` with committed `doc/tags` (a package
needs no `:helptags`; a test fails when the tags are stale); README install section with the `PackChanged` handler for `vim.pack`.
Decisions: *(a)* no prebuilt binary download: there is no release process, and fetching an executable from the network at install time
is a trust decision for the user, not a default; building from source is the reliable path (a release job could add it later).
*(b)* No `vim.pack` API in the plugin: the minimum stays 0.10; `vim.pack` support is documentation plus a tested handler.
*(c)* The build is never started automatically by the plugin (no surprise 1-minute compile on `:packadd`); the handler is opt-in.
*(d)* Tested in child Neovims with their own `packpath` (start, opt, a path with spaces, a symlink) on 0.10, and with real `vim.pack`
(a git remote, install, `PackChanged`) on 0.12.2 in CI job `lua-vim-pack`. Not tested: lazy-loading plugin managers, Windows paths
with `vim.pack` (the layout tests run on Windows in CI, the `vim.pack` one only on Linux), and a real `dotnet publish` from a
`vim.pack` install (the build step is replaced by a fake in the test; run once by hand, see the commit message).
