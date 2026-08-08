#!/usr/bin/env python3

import importlib.util
import sys
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "detect-mutation-scope.py"
SPEC = importlib.util.spec_from_file_location("tcj_detect_mutation_scope", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class MutationScopeTests(unittest.TestCase):
    @staticmethod
    def manifest(stryker_version: str = "4.16.0", *, include_docfx: bool = False) -> dict:
        tools = {
            "dotnet-stryker": {
                "version": stryker_version,
                "commands": ["dotnet-stryker"],
                "rollForward": False,
            }
        }
        if include_docfx:
            tools["docfx"] = {
                "version": "2.78.5",
                "commands": ["docfx"],
                "rollForward": False,
            }
        return {"version": 1, "isRoot": True, "tools": tools}

    def test_docfx_only_tool_manifest_change_does_not_require_mutation_run(self):
        run, reason = MODULE.requires_mutation_run(
            [MODULE.TOOL_MANIFEST],
            self.manifest(),
            self.manifest(include_docfx=True),
        )
        self.assertFalse(run)
        self.assertIn("dotnet-stryker", reason)

    def test_stryker_tool_change_requires_mutation_run(self):
        run, _ = MODULE.requires_mutation_run(
            [MODULE.TOOL_MANIFEST],
            self.manifest("4.16.0"),
            self.manifest("4.17.0"),
        )
        self.assertTrue(run)

    def test_controlled_source_change_requires_mutation_run(self):
        run, _ = MODULE.requires_mutation_run(["src/TCJ.Core/Results/Result.cs"])
        self.assertTrue(run)

    def test_controlled_test_change_requires_mutation_run(self):
        run, _ = MODULE.requires_mutation_run(["tests/TCJ.DependencyInjection.Tests/RegistrationTests.cs"])
        self.assertTrue(run)

    def test_documentation_and_workflow_only_changes_do_not_require_full_mutation_run(self):
        run, _ = MODULE.requires_mutation_run([
            "docs/index.md",
            ".github/workflows/mutation-testing.yml",
            "eng/detect-mutation-scope.py",
            "eng/tests/test_detect_mutation_scope.py",
        ])
        self.assertFalse(run)

    def test_unreadable_changed_tool_manifest_runs_conservatively(self):
        run, reason = MODULE.requires_mutation_run([MODULE.TOOL_MANIFEST], None, self.manifest())
        self.assertTrue(run)
        self.assertIn("conservatively", reason)


if __name__ == "__main__":
    unittest.main()
