#!/usr/bin/env python3
"""Self-tests of scripts/assurance/coverage_report.py: each gate must be able to fail.

A synthetic repository with one cobertura file; the script runs with DBBLISS_ROOT pointing at it.
Run: python3 scripts/assurance/tests/test_coverage.py
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "coverage_report.py"
SRC = "backend/src/Dbbliss.Backend/"


def cobertura(root, files):
    classes = ""
    for name, lines in files.items():
        body = "".join(f'<line number="{n}" hits="{h}" branch="False"/>' for n, h in lines.items())
        classes += f'<class filename="{root}/{SRC}{name}"><lines>{body}</lines></class>'
    return f"<coverage><packages><package><classes>{classes}</classes></package></packages></coverage>"


class Repo:
    def __init__(self, critical, files, baseline=None, justifications=None):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name).resolve()
        for name in files:
            self.write(SRC + name, "\n".join(f"code line {i}" for i in range(1, 11)) + "\n")
        self.write("docs/assurance/coverage-policy.json", json.dumps({"tolerance": 0.1, "exclude": [], "critical": [SRC + c for c in critical]}))
        self.write("docs/assurance/coverage-justifications.json", json.dumps({"justifications": justifications or []}))
        if baseline is not None:
            self.write("docs/assurance/coverage-baseline.json", json.dumps(baseline))
        self.write("coverage/x.cobertura.xml", cobertura(self.root, files))

    def write(self, rel, text):
        p = self.root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text)

    def run(self, *args):
        env = dict(os.environ, DBBLISS_ROOT=str(self.root))
        r = subprocess.run([sys.executable, str(SCRIPT), *args], capture_output=True, text=True, env=env)
        return r.returncode, r.stdout + r.stderr


class CoverageTests(unittest.TestCase):
    def repo(self, *a, **k):
        r = Repo(*a, **k)
        self.addCleanup(r.tmp.cleanup)
        return r

    def test_fully_covered_critical_file_passes(self):
        r = self.repo(["A.cs"], {"A.cs": {1: 1, 2: 1}})
        self.assertEqual(r.run("--check")[0], 0)

    def test_uncovered_line_in_a_critical_file_fails(self):
        r = self.repo(["A.cs"], {"A.cs": {1: 1, 2: 0}})
        code, out = r.run("--check")
        self.assertEqual(code, 1)
        self.assertIn("uncovered item(s) without justification", out)

    def test_justified_line_passes_and_stale_justification_fails(self):
        j = [{"file": SRC + "A.cs", "match": "code line 2", "kind": "line", "category": "defensive", "reason": "r"}]
        ok = self.repo(["A.cs"], {"A.cs": {1: 1, 2: 0}}, justifications=j)
        self.assertEqual(ok.run("--check")[0], 0)
        stale = self.repo(["A.cs"], {"A.cs": {1: 1, 2: 1}}, justifications=j)
        code, out = stale.run("--check")
        self.assertEqual(code, 1)
        self.assertIn("stale justification", out)

    def test_justification_for_a_file_that_is_not_critical_fails(self):
        j = [{"file": SRC + "B.cs", "match": "code line 2", "kind": "line", "category": "defensive", "reason": "r"}]
        r = self.repo(["A.cs"], {"A.cs": {1: 1}, "B.cs": {1: 1, 2: 0}}, justifications=j)
        code, out = r.run("--check")
        self.assertEqual(code, 1)
        self.assertIn("not critical", out)

    def test_justification_needs_a_reason_and_a_category(self):
        j = [{"file": SRC + "A.cs", "match": "code line 2", "kind": "line", "category": "hard-to-test", "reason": "r"}]
        r = self.repo(["A.cs"], {"A.cs": {1: 1, 2: 0}}, justifications=j)
        self.assertEqual(r.run("--check")[0], 1)

    def test_ratchet_fails_when_coverage_falls(self):
        r = self.repo([], {"B.cs": {1: 1, 2: 0}}, baseline={SRC + "B.cs": {"lines": 100.0, "branches": 100.0}})
        code, out = r.run("--check")
        self.assertEqual(code, 1)
        self.assertIn("coverage fell", out)

    def test_a_new_file_must_enter_the_baseline(self):
        r = self.repo([], {"B.cs": {1: 1}, "C.cs": {1: 1}}, baseline={SRC + "B.cs": {"lines": 100.0, "branches": 100.0}})
        code, out = r.run("--check")
        self.assertEqual(code, 1)
        self.assertIn("C.cs is measured but not in the baseline", out)

    def test_baseline_update_refuses_to_lower(self):
        r = self.repo([], {"B.cs": {1: 1, 2: 0}}, baseline={SRC + "B.cs": {"lines": 100.0, "branches": 100.0}})
        code, out = r.run("--update-baseline")
        self.assertEqual(code, 1)
        self.assertIn("baseline NOT written", out)
        self.assertEqual(json.loads((r.root / "docs/assurance/coverage-baseline.json").read_text())[SRC + "B.cs"]["lines"], 100.0)
        self.assertEqual(r.run("--update-baseline", "--allow-lower")[0], 0)
        self.assertEqual(json.loads((r.root / "docs/assurance/coverage-baseline.json").read_text())[SRC + "B.cs"]["lines"], 50.0)

    def test_baseline_update_raises_the_floor(self):
        r = self.repo([], {"B.cs": {1: 1, 2: 1}}, baseline={SRC + "B.cs": {"lines": 50.0, "branches": 100.0}})
        self.assertEqual(r.run("--update-baseline")[0], 0)
        self.assertEqual(json.loads((r.root / "docs/assurance/coverage-baseline.json").read_text())[SRC + "B.cs"]["lines"], 100.0)

    def test_critical_file_without_data_fails(self):
        r = self.repo(["A.cs"], {"B.cs": {1: 1}})
        code, out = r.run("--check")
        self.assertEqual(code, 1)
        self.assertIn("has no coverage data", out)


if __name__ == "__main__":
    unittest.main()
