#!/usr/bin/env python3
"""Bidirectional traceability between docs/assurance/requirements.md and the tests.

A test carries a tag comment `Verifies: ID[, ID...]` on its registry line or within the three lines
above it (`//` in C#, `--` in Lua, `#` in shell and config). `Verifies: none (reason)` marks a test
that traces to no requirement and says why; these are listed, not hidden.

Checks (exit 1 on any):
  1. every requirement is verified by at least one tagged test or other evidence file;
  2. every tag names an existing requirement;
  3. every test is tagged;
  4. with --check: docs/assurance/traceability.md is what this script generates now;
  5. every requirement states its source or method ("Source:" / "Method:" in its text);
  6. every Lua suite in tests/nvim/ is registered here, run by CI (ci.yml, and lua-min-version when
     it needs no database) and measured (lua-coverage.sh), so a new suite cannot silently go unrun;
  7. every public static test method of the protocol suite is registered in Program.cs;
  8. every test a problem report names exists;
  9. every mutation in docs/assurance/mutations.json names tests that exist and its code still matches.
A tag belongs to the registry line below it and never crosses into the previous test.

Tests are enumerated from the registries of the suites:
  protocol   backend/tests/Dbbliss.ProtocolTests/Program.cs   ("name", "bug", Fn)
  cancel     backend/tests/Dbbliss.CancelTests/Scenarios.cs   yield return ("name", ...)
  lua:*      tests/nvim/*_test.lua                            a line holding only 'name',
Other evidence (not subject to check 3): spec/check.sh, .editorconfig.

Only the standard library is used.
"""
import json
import os
import re
import sys
from pathlib import Path

ROOT = Path(os.environ.get("DBBLISS_ROOT") or Path(__file__).resolve().parents[2])
REQS = ROOT / "docs/assurance/requirements.md"
OUT = ROOT / "docs/assurance/traceability.md"

SUITES = [
    ("protocol", "backend/tests/Dbbliss.ProtocolTests/Program.cs", r'^\s*\("([a-z0-9_]+)",\s*"'),
    ("cancel", "backend/tests/Dbbliss.CancelTests/Scenarios.cs", r'yield return \("([a-z0-9_]+)"'),
    ("lua-client", "tests/nvim/client_test.lua", r"^\s*'([a-z0-9_]+)',\s*$"),
    ("lua-results", "tests/nvim/results_test.lua", r"^\s*'([a-z0-9_]+)',\s*$"),
    ("lua-catalog", "tests/nvim/catalog_test.lua", r"^\s*'([a-z0-9_]+)',\s*$"),
    ("lua-sessions", "tests/nvim/sessions_test.lua", r"^\s*'([a-z0-9_]+)',\s*$"),
    ("lua-management", "tests/nvim/management_test.lua", r"^\s*'([a-z0-9_]+)',\s*$"),
    ("lua-plan", "tests/nvim/plan_test.lua", r"^\s*'([a-z0-9_]+)',\s*$"),
    ("lua-completion", "tests/nvim/completion_test.lua", r"^\s*'([a-z0-9_]+)',\s*$"),
    ("e2e", "tests/nvim/e2e_test.lua", r"^\s*'([a-z0-9_]+)',\s*$"),
]
OTHER = ["spec/check.sh", ".editorconfig"]
PROBLEMS = "docs/assurance/problem-reports.md"
# Suites that need a database: not run by lua-min-version.
NEEDS_DATABASE = {"e2e"}
TAG = re.compile(r"(?://|--|#)\s*Verifies:\s*(.+?)\s*$")
ID = re.compile(r"\b((?:HLR|LLR)-[A-Z0-9]+-\d+)\b")


def requirements():
    reqs = {}
    cur = None
    for line in REQS.read_text().splitlines():
        m = re.match(r"^### ((?:HLR|LLR)-[A-Z0-9]+-\d+)\s*$", line)
        if m:
            cur = m.group(1)
            if cur in reqs:
                sys.exit(f"duplicate requirement {cur}")
            reqs[cur] = ""
        elif line.startswith("#"):
            cur = None
        elif cur and line.strip():
            reqs[cur] += (" " if reqs[cur] else "") + line.strip()
    return reqs


def tags_near(lines, i, registry):
    """Tags that belong to the registry line i: those after the previous registry line (or three
    lines above, whichever is nearer) up to line i. A tag never crosses into the previous test,
    so a test cannot borrow its neighbour's requirements. -> (ids, none_reason or None)"""
    start = max(0, i - 3)
    for j in range(i - 1, start - 1, -1):
        if registry.search(lines[j]):
            start = j + 1
            break
    ids, none = [], None
    for j in range(start, i + 1):
        m = TAG.search(lines[j])
        if not m:
            continue
        body = m.group(1)
        if body.lower().startswith("none"):
            none = body
        else:
            ids += ID.findall(body)
    return ids, none


def collect():
    tests = []  # (suite, name, file, line, ids, none)
    for suite, rel, pattern in SUITES:
        lines = (ROOT / rel).read_text().splitlines()
        rx = re.compile(pattern)
        for i, line in enumerate(lines):
            m = rx.search(line)
            if m:
                ids, none = tags_near(lines, i, rx)
                tests.append((suite, m.group(1), rel, i + 1, ids, none))
    others = []
    for rel in OTHER:
        p = ROOT / rel
        if not p.exists():
            continue
        for i, line in enumerate(p.read_text().splitlines()):
            m = TAG.search(line)
            if m:
                others.append((rel, i + 1, ID.findall(m.group(1))))
    return tests, others


def lint_suites(tests, problems):
    """Checks 6 to 8: nothing that looks like a test is left out of the registries and the CI."""
    registered = {Path(rel).name for suite, rel, _ in SUITES if suite.startswith(("lua", "e2e"))}
    nvim = ROOT / "tests/nvim"
    ci = (ROOT / ".github/workflows/ci.yml").read_text() if (ROOT / ".github/workflows/ci.yml").exists() else None
    cov = (ROOT / "scripts/assurance/lua-coverage.sh").read_text() if (ROOT / "scripts/assurance/lua-coverage.sh").exists() else None
    if nvim.is_dir():
        for f in sorted(nvim.glob("*_test.lua")):
            if f.name not in registered:
                problems.append(f"tests/nvim/{f.name} is not registered in trace.py SUITES (its tests would go untraced)")
    for suite, rel, _ in SUITES:
        if not suite.startswith(("lua", "e2e")):
            continue
        name = Path(rel).name
        if ci is not None:
            runs = ci.count(f"tests/nvim/{name}")
            want = 1 if suite in NEEDS_DATABASE else 2
            if suite == "e2e":
                want = 2  # one run per engine
            if runs < want:
                problems.append(f"{rel} is run {runs} time(s) in ci.yml, expected at least {want}")
        if cov is not None and f"tests/nvim/{name}" not in cov:
            problems.append(f"{rel} is not run by scripts/assurance/lua-coverage.sh")

    program = ROOT / "backend/tests/Dbbliss.ProtocolTests/Program.cs"
    if program.exists():
        text = program.read_text()
        method = re.compile(r"^\s*public static (?:async )?Task(?:<[^>]+>)? ([A-Za-z0-9_]+)\(\)", re.M)
        for f in sorted(program.parent.glob("*.cs")):
            if f.name == "Program.cs":
                continue
            for m in method.finditer(f.read_text()):
                if not re.search(rf"\b{m.group(1)}\b", text):
                    problems.append(f"{f.name}: test method {m.group(1)} is not registered in Program.cs (it never runs)")

    pr = ROOT / PROBLEMS
    if pr.exists():
        known = {t[1] for t in tests}
        for line in pr.read_text().splitlines():
            if not line.startswith("| PR-"):
                continue
            cells = [c.strip() for c in line.strip("|").split("|")]
            for name in re.findall(r"`([a-z0-9]+(?:_[a-z0-9]+)+)`", cells[-1]):
                if name not in known:
                    problems.append(f"{cells[0]} names test `{name}`, which is in no registry")


def lint_mutations(tests, problems):
    reg = ROOT / "docs/assurance/mutations.json"
    if not reg.exists():
        return
    known = {t[1] for t in tests}
    for m in json.loads(reg.read_text())["mutations"]:
        for name in m["tests"]:
            if name not in known:
                problems.append(f"mutation {m['id']} names test `{name}`, which is in no registry")
        path = ROOT / m["file"]
        if not path.exists() or path.read_text().count(m["find"]) != 1:
            problems.append(f"mutation {m['id']}: `find` is not in {m['file']} exactly once (update docs/assurance/mutations.json)")


def lint_requirements(reqs, problems):
    for rid, text in reqs.items():
        if not re.search(r"\b(?:Source|Method)s?:", text):
            problems.append(f"{rid} states no `Source:` or `Method:`")


def main():
    reqs = requirements()
    tests, others = collect()
    problems = []

    by_req = {r: [] for r in reqs}
    for suite, name, rel, line, ids, none in tests:
        for rid in ids:
            if rid not in reqs:
                problems.append(f"{rel}:{line} {suite}:{name} verifies unknown requirement {rid}")
            else:
                by_req[rid].append(f"{suite}:{name}")
        if not ids and not none:
            problems.append(f"{rel}:{line} {suite}:{name} has no `Verifies:` tag")
    other_by_req = {r: [] for r in reqs}
    for rel, line, ids in others:
        for rid in ids:
            if rid not in reqs:
                problems.append(f"{rel}:{line} verifies unknown requirement {rid}")
            else:
                other_by_req[rid].append(f"{rel}:{line}")
    for rid in reqs:
        if not by_req[rid] and not other_by_req[rid]:
            problems.append(f"{rid} is not verified by any test or evidence file")

    lint_suites(tests, problems)
    lint_requirements(reqs, problems)
    lint_mutations(tests, problems)
    thin = sorted(r for r in reqs if len(set(by_req[r])) + len(set(other_by_req[r])) == 1)

    md = ["# Traceability matrix", "",
          "Generated by `scripts/assurance/trace.py`; do not edit. Requirements: `requirements.md`.", "",
          f"{len(reqs)} requirements, {len(tests)} tests ({sum(1 for t in tests if t[4])} traced, "
          f"{sum(1 for t in tests if not t[4] and t[5])} marked `none`).", "",
          "## Requirement → verification", "", "| Requirement | Verified by |", "|---|---|"]
    for rid in sorted(reqs):
        ev = sorted(set(by_req[rid])) + [f"`{o}`" for o in sorted(set(other_by_req[rid]))]
        md.append(f"| {rid} | {', '.join(ev) if ev else '**NONE**'} |")
    md += ["", "## Verified by a single test or model", "",
           "A requirement here has one piece of evidence; a defect in that test leaves it unverified. Not a failure; a list to read.", ""]
    md += [f"- {r}: {(sorted(set(by_req[r])) + sorted(set(other_by_req[r])))[0]}" for r in thin] or ["None."]
    md += ["", "## Test → requirements", "", "| Test | Requirements |", "|---|---|"]
    for suite, name, rel, line, ids, none in sorted(tests):
        md.append(f"| {suite}:{name} | {', '.join(sorted(set(ids))) if ids else (none or '**UNTAGGED**')} |")
    text = "\n".join(md) + "\n"

    if "--check" in sys.argv:
        if not OUT.exists() or OUT.read_text() != text:
            problems.append("docs/assurance/traceability.md is out of date: run scripts/assurance/trace.py")
    else:
        OUT.write_text(text)
    for p in problems:
        print("ERROR:", p, file=sys.stderr)
    print(f"{len(reqs)} requirements, {len(tests)} tests, {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
