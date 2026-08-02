from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT_PATH = Path(__file__).resolve().parents[1] / "verify-mutation-results.py"
SPEC = importlib.util.spec_from_file_location("verify_mutation_results", SCRIPT_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Unable to load {SCRIPT_PATH}")
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class MutationVerifierTests(unittest.TestCase):
    PROJECTS = ("TCJ.Core", "TCJ.DependencyInjection")

    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary_directory.name)
        (self.root / "eng").mkdir(parents=True)
        self.policy_path = self.root / "eng/mutation-policy.json"
        self.summary_path = self.root / "artifacts/mutation/MUTATION_SUMMARY.md"
        self.json_path = self.root / "artifacts/mutation/mutation-summary.json"
        self.write_policy()

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def policy_data(self, minimum_score: float = 50.0, minimum_tested: int = 20) -> dict:
        return {
            "schemaVersion": 1,
            "minimumMutationScore": minimum_score,
            "minimumTestedMutants": minimum_tested,
            "projects": list(self.PROJECTS),
            "excludedFilePatterns": ["**/*.g.cs", "tests/**"],
            "ignoredMutationTypes": [],
            "ignoredMutationJustifications": {},
            "reportPaths": {
                "reportsDirectory": "artifacts/mutation/reports",
                "summaryJson": "artifacts/mutation/mutation-summary.json",
                "summaryMarkdown": "artifacts/mutation/MUTATION_SUMMARY.md",
                "projectReports": {
                    project: (
                        f"artifacts/mutation/reports/{project}/reports/mutation-report.json"
                    )
                    for project in self.PROJECTS
                },
            },
        }

    def write_policy(self, minimum_score: float = 50.0, minimum_tested: int = 20) -> None:
        self.policy_path.write_text(
            json.dumps(self.policy_data(minimum_score, minimum_tested), indent=2) + "\n",
            encoding="utf-8",
        )

    def report_path(self, project: str) -> Path:
        return (
            self.root
            / "artifacts"
            / "mutation"
            / "reports"
            / project
            / "reports"
            / "mutation-report.json"
        )

    def write_report(self, project: str, statuses: list[str], *, reported_project: str | None = None) -> None:
        path = self.report_path(project)
        path.parent.mkdir(parents=True, exist_ok=True)
        actual_project = reported_project or project
        payload = {
            "schemaVersion": "2",
            "projectRoot": str(self.root / "src" / actual_project),
            "files": {
                str(self.root / "src" / actual_project / "Example.cs"): {
                    "language": "cs",
                    "source": "public sealed class Example { }",
                    "mutants": [
                        {"id": str(index), "status": status}
                        for index, status in enumerate(statuses)
                    ],
                }
            },
        }
        path.write_text(json.dumps(payload), encoding="utf-8")

    def load_policy(self):
        return MODULE.load_policy(self.policy_path)

    def test_successful_result_passes_and_writes_summaries(self) -> None:
        statuses = ["Killed"] * 8 + ["Survived"] * 2 + ["NoCoverage"] * 2
        for project in self.PROJECTS:
            self.write_report(project, statuses)

        result = MODULE.verify(
            self.load_policy(), self.root, self.summary_path, self.json_path
        )

        self.assertTrue(result.passed)
        self.assertEqual(20, result.totals.tested)
        self.assertAlmostEqual(66.6666667, result.totals.mutation_score)
        self.assertIn("**Overall status:** PASS", self.summary_path.read_text(encoding="utf-8"))
        summary_json = json.loads(self.json_path.read_text(encoding="utf-8"))
        self.assertEqual("pass", summary_json["status"])
        self.assertEqual(24, summary_json["totals"]["totalMutants"])

    def test_below_threshold_result_fails_after_writing_summaries(self) -> None:
        statuses = ["Killed"] * 3 + ["Survived"] * 7
        for project in self.PROJECTS:
            self.write_report(project, statuses)

        with self.assertRaisesRegex(MODULE.MutationError, "mutation score 30.00% is below"):
            MODULE.verify(self.load_policy(), self.root, self.summary_path, self.json_path)

        summary_json = json.loads(self.json_path.read_text(encoding="utf-8"))
        self.assertEqual("fail", summary_json["status"])

    def test_build_error_status_is_counted_as_compile_error(self) -> None:
        statuses = ["Killed"] * 10 + ["BuildError"]
        for project in self.PROJECTS:
            self.write_report(project, statuses)

        result = MODULE.verify(
            self.load_policy(), self.root, self.summary_path, self.json_path
        )

        self.assertEqual(2, result.totals.compile_error)
        self.assertEqual(20, result.totals.tested)

    def test_missing_report_fails(self) -> None:
        self.write_report("TCJ.Core", ["Killed"] * 10)

        with self.assertRaisesRegex(MODULE.MutationError, "TCJ.DependencyInjection"):
            MODULE.verify(self.load_policy(), self.root, self.summary_path, self.json_path)

    def test_insufficient_tested_mutants_fails(self) -> None:
        statuses = ["Killed"] * 3 + ["Survived"] * 2
        for project in self.PROJECTS:
            self.write_report(project, statuses)

        with self.assertRaisesRegex(MODULE.MutationError, "tested mutant count 10 is below 20"):
            MODULE.verify(self.load_policy(), self.root, self.summary_path, self.json_path)

    def test_report_for_wrong_project_fails(self) -> None:
        self.write_report("TCJ.Core", ["Killed"] * 10)
        self.write_report(
            "TCJ.DependencyInjection",
            ["Killed"] * 10,
            reported_project="Different.Project",
        )

        with self.assertRaisesRegex(
            MODULE.MutationError,
            "does not identify configured project 'TCJ.DependencyInjection'",
        ):
            MODULE.verify(self.load_policy(), self.root, self.summary_path, self.json_path)

    def test_missing_and_malformed_policy_fail(self) -> None:
        missing = self.root / "eng/missing-policy.json"
        with self.assertRaisesRegex(MODULE.MutationError, "Mutation policy is missing"):
            MODULE.load_policy(missing)

        self.policy_path.write_text("{not-json", encoding="utf-8")
        with self.assertRaisesRegex(MODULE.MutationError, "malformed JSON"):
            MODULE.load_policy(self.policy_path)

    def test_policy_ignored_by_git_fails(self) -> None:
        subprocess.run(["git", "init", "--quiet"], cwd=self.root, check=True)
        (self.root / ".gitignore").write_text("eng/mutation-policy.json\n", encoding="utf-8")
        subprocess.run(["git", "add", ".gitignore"], cwd=self.root, check=True)

        with self.assertRaisesRegex(MODULE.MutationError, "ignored by Git"):
            MODULE.validate_git_tracking(self.root, [self.policy_path])


if __name__ == "__main__":
    unittest.main()
