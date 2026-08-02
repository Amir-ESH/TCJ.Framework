from __future__ import annotations

import importlib.util
import sys
import unittest
from pathlib import Path

SCRIPT_PATH = Path(__file__).resolve().parents[1] / "run-mutation-testing.py"
SPEC = importlib.util.spec_from_file_location("run_mutation_testing", SCRIPT_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Unable to load {SCRIPT_PATH}")
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class MutationRunnerTests(unittest.TestCase):
    def policy(self) -> dict:
        return {
            "projects": [
                {
                    "name": "TCJ.Core",
                    "sourceProject": "src/TCJ.Core/TCJ.Core.csproj",
                    "testProject": "tests/TCJ.Core.Tests/TCJ.Core.Tests.csproj",
                    "mutationTargets": ["Entities/Entity.cs", "Results/Result.cs"],
                }
            ],
            "excludedFilePatterns": ["**/*.g.cs", "tests/**"],
        }

    def config(self) -> dict:
        return {
            "stryker-config": {
                "test-runner": "mtp",
                "coverage-analysis": "off",
                "reporters": ["html", "json"],
            }
        }

    def test_resolve_project_returns_exact_match(self) -> None:
        project = MODULE.resolve_project(self.policy(), "TCJ.Core")
        self.assertEqual("TCJ.Core", project["name"])

    def test_resolve_project_rejects_unknown_name(self) -> None:
        with self.assertRaisesRegex(MODULE.RunnerError, "was not found exactly once"):
            MODULE.resolve_project(self.policy(), "Missing")

    def test_effective_config_sets_project_and_controlled_mutation_scope(self) -> None:
        policy = self.policy()
        project = MODULE.resolve_project(policy, "TCJ.Core")

        effective = MODULE.build_effective_config(self.config(), policy, project)["stryker-config"]

        self.assertEqual("TCJ.Core.csproj", effective["project"])
        self.assertEqual(
            [
                "Entities/Entity.cs",
                "Results/Result.cs",
                "!**/*.g.cs",
                "!tests/**",
            ],
            effective["mutate"],
        )
        self.assertEqual("mtp", effective["test-runner"])
        self.assertEqual("off", effective["coverage-analysis"])

    def test_effective_config_rejects_missing_targets(self) -> None:
        policy = self.policy()
        project = dict(MODULE.resolve_project(policy, "TCJ.Core"))
        project["mutationTargets"] = []

        with self.assertRaisesRegex(MODULE.RunnerError, "must define mutationTargets"):
            MODULE.build_effective_config(self.config(), policy, project)

    def test_effective_config_rejects_missing_base_section(self) -> None:
        policy = self.policy()
        project = MODULE.resolve_project(policy, "TCJ.Core")

        with self.assertRaisesRegex(MODULE.RunnerError, "stryker-config"):
            MODULE.build_effective_config({}, policy, project)


if __name__ == "__main__":
    unittest.main()
