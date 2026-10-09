# Review checklist

Used for every change to `main`, whoever wrote it. The reviewer ticks each line in the pull request;
a line that does not apply says why. The author and the reviewer should differ; where only one
person (or one AI session) worked on a change, say so in the PR: it is a recorded deviation from
independence (see `plan.md`, "Independence").

## Requirements and traceability
- [ ] Behaviour that is new or changed has a requirement in `docs/assurance/requirements.md` (new ID, never a reused one).
- [ ] Every new or changed test carries a `Verifies:` tag; `python3 scripts/assurance/trace.py` is clean and `traceability.md` is regenerated.
- [ ] A defect fix has an entry in `problem-reports.md` and a test that failed before the fix.

## Verification
- [ ] New code is covered by a test or, in a critical file, each uncovered item is justified in `coverage-justifications.json` with a reason a reviewer can check.
- [ ] A new test was shown able to fail (a mutation of the code, or the test run against the old code).
- [ ] Nothing was skipped, disabled or loosened to make CI pass.

## Safety rules (CLAUDE.md)
- [ ] No driver transaction objects; transaction state comes from the server.
- [ ] The backend never commits or rolls back on its own.
- [ ] No row is dropped silently; truncation is reported.
- [ ] Cancel is protocol-level; no code path closes a connection to cancel.
- [ ] The lease is released under the output lock, immediately before the report.
- [ ] A PostgreSQL session is swept only if provably from a dead instance on this machine.
- [ ] New concurrency or transaction design was checked against `spec/` (and `spec/check.sh` passes).
- [ ] No new dependency without the owner's approval.

## Code
- [ ] Culture-sensitive string operations name their comparison or culture (build rule).
- [ ] Windows and Linux paths both considered; platform-only code is marked as unmeasured by coverage.
- [ ] Errors tell the user what to do, and do not include secrets (credential tool output never appears).

## Records
- [ ] `docs/phase*-decisions.md` updated for a decision the owner should review.
- [ ] CI is green on Linux and Windows, including the `assurance` job.

## Review log

Reviews are recorded in the PR; phase-end reviews by the owner are recorded here.

| Date | Scope | Reviewer | Result |
|---|---|---|---|
| 2026-10-09 | Phase 2 (merged to main at 0fb9636) | owner | merge requested by the owner; no review comments recorded (whether `phase2-decisions.md` was read is not on record) |
