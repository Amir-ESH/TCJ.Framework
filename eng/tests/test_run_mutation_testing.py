from __future__ import annotations

import importlib.util
import sys
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "run-mutation-testing.py"
SPEC = importlib.util.spec_from_file_location("run_mutation_testing", SCRIPT)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Unable to load {SCRIPT}")
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
                "concurrency": 1,
                "disable-mix-mutants": True,
                "reporters": ["html", "json"],
            }
        }

    def test_resolve_project_returns_exact_match(self) -> None:
        project = MODULE.resolve_project(self.policy(), "TCJ.Core")
        self.assertEqual("TCJ.Core", project["name"])

    def test_resolve_project_rejects_unknown_project(self) -> None:
        with self.assertRaisesRegex(MODULE.RunnerError, "not found exactly once"):
            MODULE.resolve_project(self.policy(), "Missing")

    def test_effective_config_is_scoped_to_policy_targets(self) -> None:
        project = MODULE.resolve_project(self.policy(), "TCJ.Core")
        config = MODULE.build_effective_config(self.config(), self.policy(), project)["stryker-config"]
        self.assertEqual("TCJ.Core.csproj", config["project"])
        self.assertEqual(
            ["Entities/Entity.cs", "Results/Result.cs", "!**/*.g.cs", "!tests/**"],
            config["mutate"],
        )
        self.assertEqual("mtp", config["test-runner"])
        self.assertEqual("off", config["coverage-analysis"])

    def test_effective_config_rejects_empty_targets(self) -> None:
        policy = self.policy()
        project = dict(policy["projects"][0])
        project["mutationTargets"] = []
        with self.assertRaisesRegex(MODULE.RunnerError, "must define mutationTargets"):
            MODULE.build_effective_config(self.config(), policy, project)


if __name__ == "__main__":
    unittest.main()
