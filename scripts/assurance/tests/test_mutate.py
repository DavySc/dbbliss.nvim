#!/usr/bin/env python3
"""Self-tests of scripts/assurance/mutate.py: it must report a surviving mutant, a stale `find`, a
failing baseline and an unbuildable mutant, and always restore the file.
Run: python3 scripts/assurance/tests/test_mutate.py
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "mutate.py"
# The "test" passes when src.txt says `value=1`; the "build" accepts everything but `value=BROKEN`.
TEST = [sys.executable, "-c", "import sys; sys.exit(0 if 'value=1' in open('src.txt').read() else 1)", "{tests}"]
BUILD = [sys.executable, "-c", "import sys; sys.exit(1 if 'BROKEN' in open('src.txt').read() else 0)"]


class MutateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        (self.root / "src.txt").write_text("value=1\n")
        (self.root / "docs/assurance").mkdir(parents=True)

    def run_with(self, *mutations):
        reg = {"build": BUILD, "test": TEST, "mutations": [dict(m, file="src.txt", why="w", tests=["t"]) for m in mutations]}
        (self.root / "docs/assurance/mutations.json").write_text(json.dumps(reg))
        r = subprocess.run([sys.executable, str(SCRIPT)], capture_output=True, text=True, env=dict(os.environ, DBBLISS_ROOT=str(self.root)))
        self.assertEqual((self.root / "src.txt").read_text(), "value=1\n", "the file must be restored")
        return r.returncode, r.stdout + r.stderr

    def test_a_killed_mutant_passes(self):
        code, out = self.run_with({"id": "M1", "find": "value=1", "replace": "value=2"})
        self.assertEqual(code, 0, out)
        self.assertIn("1 of 1 mutants killed", out)

    def test_a_surviving_mutant_fails(self):
        code, out = self.run_with({"id": "M1", "find": "value=1", "replace": "value=1 # still"})
        self.assertEqual(code, 1)
        self.assertIn("SURVIVED", out)

    def test_a_find_that_no_longer_matches_fails(self):
        code, out = self.run_with({"id": "M1", "find": "value=7", "replace": "value=2"})
        self.assertEqual(code, 1)
        self.assertIn("occurs 0 times", out)

    def test_an_unbuildable_mutant_proves_nothing(self):
        code, out = self.run_with({"id": "M1", "find": "value=1", "replace": "BROKEN"})
        self.assertEqual(code, 1)
        self.assertIn("does not build", out)

    def test_a_failing_baseline_is_reported(self):
        (self.root / "src.txt").write_text("value=0\n")
        reg = {"build": BUILD, "test": TEST, "mutations": [{"id": "M1", "file": "src.txt", "why": "w", "tests": ["t"], "find": "value=0", "replace": "value=2"}]}
        (self.root / "docs/assurance/mutations.json").write_text(json.dumps(reg))
        r = subprocess.run([sys.executable, str(SCRIPT)], capture_output=True, text=True, env=dict(os.environ, DBBLISS_ROOT=str(self.root)))
        self.assertEqual(r.returncode, 1)
        self.assertIn("fail on the unmutated code", r.stderr)


if __name__ == "__main__":
    unittest.main()
