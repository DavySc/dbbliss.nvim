// -*- mode: Bluespec; -*-
// Variants of client.qnt for quitting (Phase 1 M4). Both prompts are needed: dropping either one lets
// a quit roll back a transaction nobody was asked about (NoSilentRollback). Kept in their own file:
// `quint verify` hands Apalache every module in the file it is given, and Apalache fails on these
// two ("error: error", no detail) while the same model without them verifies in under three minutes.
// They are checked by simulation only (spec/check.sh).

module QuitNoPrompt {
  // Neovim quits without asking.
  import client(ServerTruth = true, LeaseAfterWrite = true, LuaById = true, AskOnQuit = false, AskWhileRunning = false, MaxActions = 6, MaxConnects = 2).* from "./client"
}

module QuitViewOnly {
  // Asks only when Lua's view shows a transaction: a typed BEGIN whose report is still on its way
  // is missed.
  import client(ServerTruth = true, LeaseAfterWrite = true, LuaById = true, AskOnQuit = true, AskWhileRunning = false, MaxActions = 6, MaxConnects = 2).* from "./client"
}
