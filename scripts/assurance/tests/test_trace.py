#!/usr/bin/env python3
"""Self-tests of scripts/assurance/trace.py: each check must be able to fail.

Builds a small synthetic repository in a temporary directory (DBBLISS_ROOT), runs the script on it,
and breaks one thing at a time. Run: python3 -m unittest discover -s scripts/assurance/tests -t .
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "trace.py"

REQS = """# Requirements

### HLR-A-1
Does a thing. Source: priorities.

### HLR-A-2
Does another thing. Method: A.
"""
PROTOCOL = """var tests = new (string Name, string Bug, Func<Task> Run)[]
{
    // Verifies: HLR-A-1
    ("first_test", "", First),
    // Verifies: HLR-A-2
    ("second_test", "", Second),
};
"""
LUA = "local names = {\n  'lua_one',\n}\n"
LUA_TAGGED = "local names = {\n  -- Verifies: HLR-A-1\n  'lua_one',\n}\n"
CI = "\n".join(f"nvim -l tests/nvim/{n}_test.lua" for n in ("client", "results", "catalog", "sessions", "management", "plan") for _ in range(2))
CI += "\nnvim -l tests/nvim/e2e_test.lua\nnvim -l tests/nvim/e2e_test.lua\n"
COV = "\n".join(f"run tests/nvim/{n}_test.lua" for n in ("client", "results", "catalog", "sessions", "management", "plan", "e2e")) + "\n"


class Repo:
    def __init__(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.write("docs/assurance/requirements.md", REQS)
        self.write("backend/tests/Dbbliss.ProtocolTests/Program.cs", PROTOCOL)
        self.write("backend/tests/Dbbliss.CancelTests/Scenarios.cs", "")
        for n in ("client", "results", "catalog", "sessions", "management", "plan", "e2e"):
            self.write(f"tests/nvim/{n}_test.lua", "")
        self.write("tests/nvim/client_test.lua", LUA_TAGGED)
        self.write(".github/workflows/ci.yml", CI)
        self.write("scripts/assurance/lua-coverage.sh", COV)
        self.write("docs/assurance/problem-reports.md", "| PR-001 | x | y | HLR-A-1 | `first_test` |\n")

    def write(self, rel, text):
        p = self.root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text)

    def run(self, *args):
        env = dict(os.environ, DBBLISS_ROOT=str(self.root))
        r = subprocess.run([sys.executable, str(SCRIPT), *args], capture_output=True, text=True, env=env)
        return r.returncode, r.stderr

    def close(self):
        self.tmp.cleanup()


class TraceTests(unittest.TestCase):
    def setUp(self):
        self.repo = Repo()
        self.addCleanup(self.repo.close)
        # The matrix is generated; the clean tree starts consistent.
        self.assertEqual(self.repo.run()[0], 0, self.repo.run()[1])

    def fails_with(self, text, *args):
        code, err = self.repo.run(*args or ("--check",))
        self.assertEqual(code, 1, err)
        self.assertIn(text, err)

    def test_clean_tree_passes(self):
        self.assertEqual(self.repo.run("--check")[0], 0)

    def test_a_tag_does_not_cross_into_the_previous_test(self):
        # second_test loses its own tag: it must not inherit first_test's.
        self.repo.write("backend/tests/Dbbliss.ProtocolTests/Program.cs", PROTOCOL.replace("    // Verifies: HLR-A-2\n", ""))
        self.fails_with("second_test has no `Verifies:` tag")
        self.fails_with("HLR-A-2 is not verified")

    def test_unknown_requirement_in_a_tag(self):
        self.repo.write("backend/tests/Dbbliss.ProtocolTests/Program.cs", PROTOCOL.replace("HLR-A-2", "HLR-A-9"))
        self.fails_with("unknown requirement HLR-A-9")

    def test_unverified_requirement(self):
        self.repo.write("docs/assurance/requirements.md", REQS + "\n### HLR-A-3\nNew. Source: x.\n")
        self.fails_with("HLR-A-3 is not verified")

    def test_requirement_without_source(self):
        self.repo.write("docs/assurance/requirements.md", REQS.replace("Source: priorities.", "Because."))
        self.fails_with("HLR-A-1 states no `Source:` or `Method:`")

    def test_unregistered_lua_suite(self):
        self.repo.write("tests/nvim/completion_test.lua", "")
        self.fails_with("completion_test.lua is not registered")

    def test_lua_suite_missing_from_ci(self):
        self.repo.write(".github/workflows/ci.yml", CI.replace("tests/nvim/sessions_test.lua", "x"))
        self.fails_with("sessions_test.lua is run 0 time(s) in ci.yml")

    def test_lua_suite_missing_from_coverage_script(self):
        self.repo.write("scripts/assurance/lua-coverage.sh", COV.replace("management", "other"))
        self.fails_with("management_test.lua is not run by scripts/assurance/lua-coverage.sh")

    def test_protocol_test_method_never_registered(self):
        self.repo.write("backend/tests/Dbbliss.ProtocolTests/MoreTests.cs", "class T {\n    public static async Task Forgotten()\n    {\n    }\n}\n")
        self.fails_with("test method Forgotten is not registered")

    def test_problem_report_names_a_missing_test(self):
        self.repo.write("docs/assurance/problem-reports.md", "| PR-001 | x | y | HLR-A-1 | `no_such_test` |\n")
        self.fails_with("PR-001 names test `no_such_test`")

    def test_mutation_names_a_missing_test_or_moved_code(self):
        self.repo.write("src.txt", "x = 1\n")
        reg = {"mutations": [{"id": "M1", "file": "src.txt", "find": "x = 1", "replace": "x = 2", "tests": ["first_test"], "why": "w"}]}
        self.repo.write("docs/assurance/mutations.json", json.dumps(reg))
        self.assertEqual(self.repo.run("--check")[0], 0)
        reg["mutations"][0]["tests"] = ["no_such_test"]
        self.repo.write("docs/assurance/mutations.json", json.dumps(reg))
        self.fails_with("mutation M1 names test `no_such_test`")
        reg["mutations"][0].update(tests=["first_test"], find="x = 9")
        self.repo.write("docs/assurance/mutations.json", json.dumps(reg))
        self.fails_with("mutation M1: `find` is not in src.txt exactly once")

    def test_stale_matrix(self):
        self.repo.write("docs/assurance/traceability.md", "old\n")
        self.fails_with("traceability.md is out of date")


if __name__ == "__main__":
    unittest.main()
