# Assurance plan

This plan says how dbbliss.nvim is verified, what evidence exists, and where it falls short of the
DO-178C practices it is modelled on. **It is an analogy, not a certification claim.** DO-178C
certifies airborne software through a regulator; nothing here is reviewed by one, and the project
has none of the organisational independence the standard assumes. What the plan does is borrow the
discipline: requirements that can be traced to tests, structural coverage that shows what the tests
never ran, and a written account of every gap.

Target: **Level B-like** (chosen by the owner). In DO-178C, Level B is "hazardous" software:
verification includes decision coverage and independence for most objectives.

## Why this software gets a level at all

dbbliss.nvim sits between a person and a production database. The failure conditions that matter,
in the order of the priorities in `CLAUDE.md`:

| ID | Failure condition | Main requirements |
|---|---|---|
| FC-1 | A transaction is committed or rolled back that the user did not decide | HLR-TX-1..6, HLR-CANCEL-7 |
| FC-2 | A running statement cannot be stopped, or a stop is reported that did not happen | HLR-CANCEL-1..7 |
| FC-3 | Result data is lost, altered or shown wrongly without a report | HLR-DATA-1, HLR-PAGE-*, HLR-EXPORT-1, HLR-CANCEL-3 |
| FC-4 | A server session is left behind holding locks, or a live one is ended | HLR-LIFE-1, HLR-LIFE-2, LLR-LIFE-3 |
| FC-5 | A write reaches a production database without the confirmation the user asked for | HLR-PROD-1, LLR-CLASS-1 |
| FC-6 | A credential is stored in a configuration, logged or shown | HLR-CRED-1, LLR-CRED-2 |
| FC-7 | The catalog browser reports or scripts the wrong object | HLR-CAT-* |

None of these endangers life. The level is a statement of how much evidence the owner wants before
trusting the tool with a production database, not a hazard analysis.

## Documents and where the evidence lives

| DO-178C document | Here |
|---|---|
| Plan for Software Aspects of Certification, development, verification, configuration and QA plans | this file, `CLAUDE.md`, `reviews/review-checklist.md` |
| Software requirements data (high and low level) | `requirements.md` (HLR-*, LLR-*; each with source) |
| Design description | `docs/phase*-decisions.md`, `docs/phase1-plan.md`, formal models in `spec/` |
| Source code, build | `backend/`, `lua/`, `.editorconfig` (build rules), CI |
| Test cases and procedures | `backend/tests/*`, `tests/nvim/*`; each carries its requirement tags |
| Test results | CI runs (`.github/workflows/ci.yml`), job summaries |
| Traceability data | `traceability.md`, generated and checked by `scripts/assurance/trace.py` |
| Structural coverage analysis | `scripts/assurance/coverage_report.py`, `coverage-policy.json`, `coverage-justifications.json`, `coverage-baseline.json` |
| Problem reports | `problem-reports.md` |
| Configuration index, baselines | git history, one tag per phase (see "Configuration management") |
| Reviews | PR checklist (`.github/pull_request_template.md`), `reviews/review-checklist.md` |

## Objectives and their status

Status: **met**, **partly** (evidence exists, with a gap named below), **not met**, **n/a**.
Table numbers refer to DO-178C Annex A.

| Objective (abridged) | Status | Evidence and gap |
|---|---|---|
| A-1 Planning | partly | this plan; one document for all plans |
| A-2 Requirements, design, code, integration are produced | met | requirements, decisions + models, code |
| A-3 Requirements are accurate, consistent, verifiable, trace to system-level needs | partly | each requirement cites its source (priority or decision); no independent review of the catalog |
| A-4 Design verifies against requirements | partly | `spec/` models cover cancel, paging, transactions, quit; not every requirement has a model |
| A-5 Code complies with requirements and standards, is traceable | partly | build rules, warnings as errors, review checklist. **Code-to-requirement traceability is by tests, not by tags in the code** |
| A-6 Tests are requirements-based and robust | met for the catalog, partly overall | every test is tagged to a requirement; normal-range and robustness cases exist (`FailureCases`, `RobustnessCases`, `UnitCases`); see "Known gaps" |
| A-7 Verification of the verification: test procedures correct | partly | new tests are mutation-checked by hand (the code is broken and the test must fail); this is **not automated** |
| A-7 Requirements coverage | met | `trace.py --check` fails CI for an untested requirement or an untagged test |
| A-7 Structural coverage (Level B: decision) | partly | C# statement and branch coverage, merged over suites, gated for critical files; **Lua statement coverage only**; platform-specific code unmeasured |
| A-7 Unreached code is justified or removed | partly | every uncovered item in a critical file needs an entry in `coverage-justifications.json`; non-critical files are only ratcheted |
| A-8 Configuration management | partly | git + CI; **no baseline tags yet**; no access control beyond GitHub |
| A-9 Quality assurance | partly | PR checklist; **no independent QA role** |
| A-10 Certification liaison | n/a | no authority |

## Verification suites

| Suite | Runs | Needs | Covers |
|---|---|---|---|
| protocol tests | `dotnet run --project backend/tests/Dbbliss.ProtocolTests` | nothing | the backend in-process against a fake engine: protocol, lease, transactions, paging, catalog, failure paths, pure units |
| Lua client, results, catalog tests | `nvim --headless --clean --cmd 'set rtp^=.' -l tests/nvim/<name>_test.lua` | Neovim 0.10+ | the plugin against a stub backend |
| cancel suite | `dotnet run --project backend/tests/Dbbliss.CancelTests` | PostgreSQL, SQL Server, Neovim | the published backend against real servers, both engines, Linux and Windows |
| end-to-end | `tests/nvim/e2e_test.lua` | databases, Neovim | the real plugin, backend and server |
| formal models | `spec/check.sh` | Java, Quint, TLC | design-level safety and liveness |
| traceability | `python3 scripts/assurance/trace.py --check` | Python 3 | requirement ↔ test, both directions |
| coverage | `scripts/assurance/dotnet-coverage.sh`, `lua-coverage.sh`, then `coverage_report.py --check` | the above, `dotnet-coverage`, luacov | structural coverage |

All run in CI on every push (job names: `cancel-suite`, `lua-min-version`, `spec-check`,
`assurance`). Coverage is measured on Linux only: the tools instrument the backend as a child
process, and the cancel suite starts it from its build output (`scripts/assurance/backend-dll.sh`).

## Coverage policy

1. **Critical files** (`coverage-policy.json`): the backend files that carry FC-1 to FC-6. Every
   statement not executed and every decision with a branch not taken must either be removed, be
   covered by a new requirement-based test, or have an entry in `coverage-justifications.json`
   naming the file, the line range, the kind (`line`, `branch` or `both`) and the reason. The gate
   fails on an uncovered item without an entry, and on an entry that no longer matches anything.
2. **All other backend files and the Lua files**: a ratchet. Coverage may not fall below
   `coverage-baseline.json`, which is updated only upward (`coverage_report.py --update-baseline`)
   in the commit that raised it.
3. **Accepted reasons** for a justification: platform-specific code that CI runs on the other OS
   (named); a defensive branch that the type system or a caller makes unreachable (say how); an
   empty handler for an error that cannot be provoked without privileges (say which). "Hard to
   test" is not a reason.
4. Branch coverage here means the branch points of the compiled code (cobertura via
   `dotnet-coverage`). It counts compiler-generated branches of `async` methods, `?.` and `??`, so
   it is stricter in some places and different in others than DO-178C's decision coverage. MC/DC is
   not required at Level B and is not measured.
5. Merging suites takes the larger covered count per line over all suites: a lower bound of the
   union. The report can understate coverage; it cannot overstate it.

## Independence

DO-178C Level B expects verification to be done by someone other than the author for most
objectives. Here the code, the tests and this catalog were produced by the same author (a
developer working with an AI assistant) and then checked by a human owner who decides merges. That
is **not** independence. Compensations, none of which equal it:

- tests are proved able to fail by breaking the code (mutation by hand), recorded in the commit;
- CI re-runs everything on two operating systems with two database engines;
- the formal models in `spec/` were written separately from the code and found four defects after
  the code existed;
- the owner decides every merge and is given the decisions documents at each phase end (`reviews/review-checklist.md` records what is known of those reviews).

## Tools

The standard asks for tool qualification (DO-330). Nothing here is qualified. What is used, and how
far it is trusted:

| Tool | Used for | Trust |
|---|---|---|
| `dotnet-coverage` 18.12.0 | C# coverage | cross-checked once: the lines of the cancel re-send loop were reported uncovered, became covered after the test for them was added, and fell back to uncovered when that code was mutated away |
| luacov 0.15.0 | Lua statement coverage | its totals matched the per-line status the report script reads (323 missed lines both ways); fetched at a pinned tag, not vendored |
| `trace.py`, `coverage_report.py` | traceability and gate | small, standard-library Python; their checks are exercised by CI failing when they should |
| `TreatWarningsAsErrors`, `.editorconfig` rules | code standard | the compiler is the oracle |

None of the tools ships with the plugin.

## Configuration management

- Source: git; `main` is the baseline; work happens on a branch and merges after CI is green and
  the owner agrees.
- Phase ends should be tagged (`phase-N`, the commit the owner reviewed). **None are tagged yet**;
  until they are, the baseline of a phase is the merge commit named in `reviews/review-checklist.md`.
- Every CI run keeps its logs and the coverage summary as an artifact.
- Problem reports: `problem-reports.md`, append-only.
- Changing a requirement, a justification or the baseline is a commit like any other and is
  reviewed with the change that needs it.

## Known gaps

These are open on purpose and listed so that nobody mistakes the evidence for more than it is.

1. **No independence** (see above).
2. **Lua decision coverage is not measured.** luacov reports statements only. The Lua code is
   presentation and orchestration; the logic behind FC-1 to FC-4 is in the backend, and the Lua
   guards (quit, buffer close, prod confirmation) are tested by requirement but not by branch.
3. **Windows-only code is not measured.** Coverage runs on Linux; Windows Credential Manager and
   Windows-specific process handling run in the Windows CI job without instrumentation.
4. **SQL Server and PostgreSQL engine code is measured only through the databases in CI**, so a
   change there cannot be checked for coverage on a laptop without them.
5. **Mutation checks are manual.** Nothing re-runs them.
6. **Requirements are written after much of the code.** The catalog was derived from the decisions
   documents and the tests, then used to find gaps; it did not drive the original design. The
   formal models are the exception.
7. **DB2 for i is out of scope** (deferred, decision 16): its engine is excluded from coverage and
   has no requirements.
8. **Real-world use is untested** on a Windows desktop and with Docker/Rancher Desktop.

## Maintaining this

A change that adds behaviour adds a requirement and a tagged test in the same commit, keeps
`trace.py --check` clean, and either keeps critical-file coverage justified or adds the test.
`docs/assurance/reviews/review-checklist.md` is the checklist for that review.
