#!/usr/bin/env python3
"""Structural coverage report and gate (statement + decision for the backend, statement for Lua).

Inputs (all under coverage/):
  *.cobertura.xml      one per suite run under dotnet-coverage (scripts/assurance/dotnet-coverage.sh)
  luacov.lines.txt     luacov per-line status (scripts/assurance/lua-coverage.sh)

Merging. A source line can appear in several cobertura classes (lambdas, async state machines) and in
several suites. A line is covered when any class in any suite hit it. For a line with branches, the
covered count is the largest seen and the total the largest seen, which is a lower bound of the true
union: the report can only understate coverage, never overstate it.

Gate (docs/assurance/coverage-policy.json):
  critical files  every line not hit and every branch not taken must be justified in
                  docs/assurance/coverage-justifications.json, else the gate fails. An entry names
                  its code by a snippet that must occur on exactly one line of the file ("match"),
                  plus lines before/after it, so edits elsewhere do not move it.
  ratchet         per file, statement and decision percentages may not fall below the baseline.
  A justification that no longer matches an uncovered item is itself an error, so the list cannot rot.

Usage: coverage_report.py [--check] [--update-baseline]
Exit 1 when --check finds a violation. Only the standard library is used.
"""
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
COV = ROOT / "coverage"
POLICY = ROOT / "docs/assurance/coverage-policy.json"
JUSTIFICATIONS = ROOT / "docs/assurance/coverage-justifications.json"
BASELINE = ROOT / "docs/assurance/coverage-baseline.json"
SRC_PREFIX = "backend/src/Dbbliss.Backend/"


def load_policy():
    return json.loads(POLICY.read_text()) if POLICY.exists() else {"critical": [], "exclude": [], "tolerance": 0.1}


def relative(filename):
    p = Path(filename)
    try:
        return p.resolve().relative_to(ROOT).as_posix()
    except ValueError:
        return filename.replace("\\", "/")


def read_cobertura(files, exclude):
    """-> {file: {line: [hits, covered_branches, total_branches]}}"""
    data = {}
    for path in files:
        root = ET.parse(path).getroot()
        for cls in root.iter("class"):
            rel = relative(cls.get("filename"))
            if not rel.startswith(SRC_PREFIX) or any(rel.endswith(e) for e in exclude):
                continue
            lines = data.setdefault(rel, {})
            for ln in cls.iter("line"):
                n = int(ln.get("number"))
                hits = int(ln.get("hits"))
                covered = total = 0
                m = re.search(r"\((\d+)/(\d+)\)", ln.get("condition-coverage") or "")
                if ln.get("branch") == "True" and m:
                    covered, total = int(m.group(1)), int(m.group(2))
                cur = lines.setdefault(n, [0, 0, 0])
                cur[0] = max(cur[0], hits)
                cur[1] = max(cur[1], covered)
                cur[2] = max(cur[2], total)
    return data


def summarize_dotnet(data):
    out = {}
    for f, lines in sorted(data.items()):
        total = len(lines)
        hit = sum(1 for v in lines.values() if v[0] > 0)
        bt = sum(v[2] for v in lines.values())
        bc = sum(min(v[1], v[2]) for v in lines.values())
        miss_lines = sorted(n for n, v in lines.items() if v[0] == 0)
        miss_branches = sorted(n for n, v in lines.items() if v[2] > v[1])
        out[f] = {
            "lines": round(100.0 * hit / total, 2) if total else 100.0,
            "branches": round(100.0 * bc / bt, 2) if bt else 100.0,
            "line_count": total,
            "branch_count": bt,
            "missed_lines": miss_lines,
            "missed_branches": miss_branches,
        }
    return out


def read_luacov(report):
    """luacov.lines.txt: 'F file', then 'H n' (executed) / 'M n' (executable, not executed)."""
    out = {}
    if not report.exists():
        return out
    cur = None
    for raw in report.read_text().splitlines():
        kind, _, rest = raw.partition(" ")
        if kind == "F":
            cur = out.setdefault(rest, {"hit": 0, "missed": []})
        elif kind == "H" and cur is not None:
            cur["hit"] += 1
        elif kind == "M" and cur is not None:
            cur["missed"].append(int(rest))
    return {
        f: {
            "lines": round(100.0 * v["hit"] / (v["hit"] + len(v["missed"])), 2) if v["hit"] + len(v["missed"]) else 100.0,
            "line_count": v["hit"] + len(v["missed"]),
            "missed_lines": sorted(v["missed"]),
        }
        for f, v in sorted(out.items())
    }


def justification_index(problems):
    """-> [(file, from_line, to_line, kind, reason)], with snippets resolved against the current source."""
    if not JUSTIFICATIONS.exists():
        return []
    resolved = []
    for j in json.loads(JUSTIFICATIONS.read_text())["justifications"]:
        path = ROOT / j["file"]
        lines = path.read_text(errors="replace").splitlines() if path.exists() else []
        hits = [i + 1 for i, text in enumerate(lines) if j["match"] in text]
        label = f"{j['file']} `{j['match'][:50]}`"
        if len(hits) != 1:
            problems.append(f"justification {label} matches {len(hits)} lines, expected exactly 1")
            continue
        if not j.get("reason", "").strip():
            problems.append(f"justification {label} has no reason")
            continue
        at = hits[0]
        if j.get("category") not in ("platform", "unreachable", "defensive"):
            problems.append(f"justification {label} needs a category: platform, unreachable or defensive")
            continue
        resolved.append((j["file"], at - j.get("before", 0), at + j.get("after", 0), j["kind"], j["reason"], label, j["category"]))
    return resolved


def main():
    check = "--check" in sys.argv
    update = "--update-baseline" in sys.argv
    policy = load_policy()
    files = sorted(COV.glob("*.cobertura.xml"))
    dotnet = summarize_dotnet(read_cobertura(files, policy.get("exclude", []))) if files else {}
    lua = read_luacov(COV / "luacov.lines.txt")
    problems = []

    # --- critical files: every miss justified ------------------------------------------------
    js = justification_index(problems)
    artifact_patterns = [re.compile(a["pattern"]) for a in policy.get("ignore_artifacts", [])]
    ignored_artifacts = 0
    used = set()
    unjustified = {}
    for f in policy.get("critical", []):
        info = dotnet.get(f) or lua.get(f)
        if info is None:
            problems.append(f"critical file {f} has no coverage data")
            continue
        source = (ROOT / f).read_text(errors="replace").splitlines() if (ROOT / f).exists() else []
        for kind, key in (("line", "missed_lines"), ("branch", "missed_branches")):
            for n in info.get(key, []):
                text = source[n - 1] if 0 < n <= len(source) else ""
                if any(rx.search(text) for rx in artifact_patterns):
                    ignored_artifacts += 1
                    continue
                hit = next(
                    (i for i, j in enumerate(js)
                     if j[0] == f and j[1] <= n <= j[2] and j[3] in (kind, "both")),
                    None,
                )
                if hit is None:
                    unjustified.setdefault(f, []).append((kind, n))
                else:
                    used.add(hit)
    for f, items in unjustified.items():
        shown = ", ".join(f"{k} {n}" for k, n in items[:25]) + (" ..." if len(items) > 25 else "")
        problems.append(f"{f}: {len(items)} uncovered item(s) without justification: {shown}")
    for i, j in enumerate(js):
        if i not in used and j[0] in policy.get("critical", []):
            problems.append(f"stale justification (nothing uncovered there any more): {j[5]} ({j[3]})")

    # --- ratchet ----------------------------------------------------------------------------
    baseline = json.loads(BASELINE.read_text()) if BASELINE.exists() else {}
    tol = policy.get("tolerance", 0.1)
    current = {f: {"lines": v["lines"], "branches": v["branches"]} for f, v in dotnet.items()}
    current.update({f: {"lines": v["lines"]} for f, v in lua.items()})
    for f, cur in current.items():
        base = baseline.get(f)
        if not base:
            continue
        for k in ("lines", "branches"):
            if k in cur and k in base and cur[k] + tol < base[k]:
                problems.append(f"{f}: {k} coverage fell from {base[k]}% to {cur[k]}%")
    for f in baseline:
        if f not in current and (dotnet or lua):
            problems.append(f"{f} is in the baseline but has no coverage data now")

    # --- report -----------------------------------------------------------------------------
    md = ["# Structural coverage", "", "Generated by `scripts/assurance/coverage_report.py`. Lower bound of the union of all suites.", ""]
    md += ["## Backend (C#): statement and decision", "", "| File | Lines | Branches | Missed lines | Missed branch lines |", "|---|---|---|---|---|"]
    for f, v in dotnet.items():
        crit = " (critical)" if f in policy.get("critical", []) else ""
        md.append(f"| `{f.replace(SRC_PREFIX, '')}`{crit} | {v['lines']}% | {v['branches']}% | {len(v['missed_lines'])} | {len(v['missed_branches'])} |")
    md += ["", "## Lua: statement (luacov)", "", "| File | Lines | Missed |", "|---|---|---|"]
    for f, v in lua.items():
        md.append(f"| `{f}` | {v['lines']}% | {len(v['missed_lines'])} |")
    md += ["", "## Uncovered code in critical files", "",
           "`L` statement not executed, `B` decision with a branch not taken. Each needs a test or an entry in `coverage-justifications.json`.", ""]
    for f in policy.get("critical", []):
        info = dotnet.get(f) or lua.get(f)
        if not info:
            continue
        path = ROOT / f
        src = path.read_text(errors="replace").splitlines() if path.exists() else []
        items = sorted({(n, "L") for n in info.get("missed_lines", [])} | {(n, "B") for n in info.get("missed_branches", [])})
        if not items:
            continue
        md += [f"### `{f.replace(SRC_PREFIX, '')}`", "", "```"]
        for n, k in items:
            text = src[n - 1].strip() if 0 < n <= len(src) else ""
            md.append(f"{n:5d} {k} {text[:110]}")
        md += ["```", ""]
    by_category = {}
    for j in js:
        by_category[j[6]] = by_category.get(j[6], 0) + 1
    md += ["", "## Justifications in use", "",
           "`platform`: runs on the other OS in CI, not measured. `unreachable`: the code cannot take that branch. "
           "`defensive`: a handler for a failure that cannot be provoked; it is not tested. "
           + ", ".join(f"{n} {c}" for c, n in sorted(by_category.items()))
           + f". {ignored_artifacts} uncovered brace-only or rethrow lines ignored as compiler artifacts (coverage-policy.json)."]
    md += ["", "## Problems", ""] + ([f"- {p}" for p in problems] or ["None."])
    COV.mkdir(exist_ok=True)
    (COV / "summary.md").write_text("\n".join(md) + "\n")
    (COV / "summary.json").write_text(json.dumps({"backend": dotnet, "lua": lua}, indent=1))
    print("\n".join(md))

    if update:
        BASELINE.write_text(json.dumps(current, indent=1, sort_keys=True) + "\n")
        print(f"\nbaseline written: {BASELINE.relative_to(ROOT)}")
        return 0
    if check and problems:
        print(f"\n{len(problems)} coverage problem(s)", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
