-- SQL keywords for completion, per dialect. `start` can open a statement; `inside` are the clause and
-- expression keywords that can follow in the middle of one. Lower case here; the case written is chosen
-- by the completion core. A keyword of two words is one entry.
local M = {}

local function list(words)
  local out = {}
  for w in words:gmatch('[^,]+') do
    out[#out + 1] = vim.trim(w)
  end
  return out
end

-- Clause and expression keywords both dialects share.
local COMMON = [[select, distinct, all, as, from, where, group by, order by, having, union, union all, intersect, except,
  join, inner join, left join, left outer join, right join, right outer join, full join, full outer join, cross join, on, using,
  and, or, not, in, not in, exists, between, like, not like, is null, is not null, null, true, false,
  case, when, then, else, end, asc, desc, values, set, into, default, over, partition by, with]]

M.postgres = {
  start = list([[select, with, insert into, update, delete from, merge into, values, table, create, alter, drop, truncate, begin, commit,
    rollback, savepoint, release, explain, explain analyze, analyze, vacuum, copy, grant, revoke, set, reset, show, do, call, listen,
    notify, comment on, lock, prepare, execute, deallocate, start transaction]]),
  inside = list(COMMON .. [[, ilike, not ilike, similar to, limit, offset, fetch first, for update, returning, on conflict, do nothing,
    do update set, lateral, filter, window, rows between, current_date, current_timestamp, cast, interval, array, only, natural join,
    distinct on, nulls first, nulls last]]),
}

M.sqlserver = {
  start = list([[select, with, insert into, update, delete from, merge, create, alter, drop, truncate table, begin, begin transaction,
    commit, rollback, save transaction, exec, execute, declare, set, use, print, if, while, return, grant, revoke, deny, backup, restore,
    waitfor, throw, raiserror, go]]),
  inside = list(COMMON .. [[, top, output, offset, fetch next, rows only, apply, cross apply, outer apply, for xml, for json,
    option, nolock, readuncommitted, rowlock, updlock, natural, try_convert, getdate, isnull, current_timestamp, cast, convert]]),
}

return M
