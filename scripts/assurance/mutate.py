#!/usr/bin/env python3
"""Replays the recorded mutation checks (docs/assurance/mutations.json).

A mutation check is: break the code in one stated way and watch the named tests fail. It proves the
test can fail. Until now they were done by hand and only described in commit messages; this replays
them, so a test that stops being able to fail (or code that moves out from under its mutation) is
found by a run, not by a reader.

For each mutation: (1) the named tests must pass on the unmutated code (their line says PASS);
(2) the file is changed (`find` must occur exactly once); (3) the project must still build (an
unbuildable mutant proves nothing); (4) at least one named test must report FAIL (a mutant that only
crashes the runner is not a kill). A mutant that survives, a find that no longer matches, or an
unbuildable mutant is a problem. The file is always restored.

The registry names its runners ("runners": {name: {build, test}}) and each mutation its runner
(default "protocol"). `{tests}` in a command is the comma-joined test names, `{nvim}` is $NVIM or `nvim`.

Usage: mutate.py [--id M1,M2] [--list]
Exit 1 on any problem. Only the standard library is used.
"""
import json
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(os.environ.get("DBBLISS_ROOT") or Path(__file__).resolve().parents[2])
REGISTRY = ROOT / "docs/assurance/mutations.json"


def run(cmd, tests=None):
    cmd = [c.replace("{tests}", ",".join(tests or [])).replace("{nvim}", os.environ.get("NVIM", "nvim")) for c in cmd]
    r = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True)
    return r.returncode, r.stdout + r.stderr


def status(out, name):
    """'PASS', 'FAIL' or None: what the runner printed for one test."""
    m = re.search(rf"^{re.escape(name)}\s+(PASS|FAIL)\b", out, re.M)
    return m.group(1) if m else None


def main():
    reg = json.loads(REGISTRY.read_text())
    wanted = None
    if "--id" in sys.argv:
        wanted = set(sys.argv[sys.argv.index("--id") + 1].split(","))
    problems, killed = [], 0
    seen = set()
    for m in reg["mutations"]:
        if m["id"] in seen:
            problems.append(f"{m['id']}: duplicate id")
        seen.add(m["id"])
    if "--list" in sys.argv:
        for m in reg["mutations"]:
            print(f"{m['id']:5} {m['file']}: {m['why']}")
        return 0
    # The tests run from the build output: build every runner that is used once, so a clean checkout works.
    for name in sorted({m.get("runner", "protocol") for m in reg["mutations"] if not wanted or m["id"] in wanted}):
        code, out = run(reg["runners"][name]["build"])
        if code != 0:
            print(f"ERROR: the initial build of runner {name} failed\n{out[-800:]}", file=sys.stderr)
            return 1
    for m in reg["mutations"]:
        if wanted and m["id"] not in wanted:
            continue
        path = ROOT / m["file"]
        original = path.read_text()
        label = f"{m['id']} ({m['file'].rsplit('/', 1)[-1]}: {m['why']})"
        n = original.count(m["find"])
        if n != 1:
            problems.append(f"{label}: `find` occurs {n} times, expected 1 (the code moved: update the registry)")
            continue
        r = reg["runners"][m.get("runner", "protocol")]
        code, out = run(r["test"], m["tests"])
        if any(status(out, t) != "PASS" for t in m["tests"]):
            problems.append(f"{label}: the tests do not all pass on the unmutated code, so a failure would prove nothing\n{out[-600:]}")
            continue
        try:
            path.write_text(original.replace(m["find"], m["replace"]))
            code, out = run(r["build"])
            if code != 0:
                problems.append(f"{label}: the mutant does not build (an invalid mutant proves nothing)\n{out[-600:]}")
                continue
            code, out = run(r["test"], m["tests"])
        finally:
            path.write_text(original)
            run(r["build"])  # the next baseline run uses the build output: it must match the restored source
        if not any(status(out, t) == "FAIL" for t in m["tests"]):
            problems.append(f"{label}: SURVIVED, none of {', '.join(m['tests'])} reported FAIL")
            print(f"SURVIVED {label}")
        else:
            killed += 1
            print(f"killed   {label}")
    total = len([m for m in reg["mutations"] if not wanted or m["id"] in wanted])
    print(f"{killed} of {total} mutants killed")
    for p in problems:
        print("ERROR:", p, file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
