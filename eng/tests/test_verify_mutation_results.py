from __future__ import annotations

import hashlib
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
        self.baseline_path = self.root / "eng/mutation-baseline.json"
        self.summary_path = self.root / "artifacts/mutation/MUTATION_SUMMARY.md"
        self.json_path = self.root / "artifacts/mutation/mutation-summary.json"
        self.candidate_path = self.root / "artifacts/mutation/mutation-baseline-candidate.json"
        self.write_policy()
        self.write_baseline(recorded=True)

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def project_data(self, project: str, minimum_tested: int = 5) -> dict:
        return {
            "name": project,
            "sourceProject": f"src/{project}/{project}.csproj",
            "testProject": f"tests/{project}.Tests/{project}.Tests.csproj",
            "minimumTestedMutants": minimum_tested,
            "mutationTargets": ["Example.cs"],
            "reportPath": f"artifacts/mutation/reports/{project}/reports/mutation-report.json",
            "htmlReportPath": f"artifacts/mutation/reports/{project}/reports/mutation-report.html",
            "runMetadataPath": f"artifacts/mutation/reports/{project}/run-metadata.json",
            "consoleLogPath": f"artifacts/mutation/reports/{project}/stryker-console.log",
        }

    def policy_data(self, minimum_score: float = 50.0, minimum_tested: int = 10) -> dict:
        return {
            "schemaVersion": 2,
            "strykerVersion": "4.16.0",
            "testRunner": "mtp",
            "coverageAnalysis": "off",
            "baselinePath": "eng/mutation-baseline.json",
            "requireRecordedBaseline": True,
            "minimumMutationScore": minimum_score,
            "allowedBaselineScoreRegression": 0.0,
            "minimumTestedMutants": minimum_tested,
            "minimumKilledMutants": 2,
            "minimumKilledMutantsPerProject": 1,
            "maximumCompileErrorPercentage": 10.0,
            "maximumRuntimeErrorMutants": 0,
            "projects": [self.project_data(project) for project in self.PROJECTS],
            "excludedFilePatterns": ["**/*.g.cs", "tests/**"],
            "scopeNotes": ["Controlled test scope."],
            "forbiddenRunnerLogMarkers": ["test coverage capture failed", "no tests were found"],
            "ignoredMutationTypes": [],
            "ignoredMutationJustifications": {},
            "reportPaths": {
                "reportsDirectory": "artifacts/mutation/reports",
                "summaryJson": "artifacts/mutation/mutation-summary.json",
                "summaryMarkdown": "artifacts/mutation/MUTATION_SUMMARY.md",
                "baselineCandidate": "artifacts/mutation/mutation-baseline-candidate.json",
            },
        }

    def write_policy(self, minimum_score: float = 50.0, minimum_tested: int = 10) -> None:
        self.policy_path.write_text(
            json.dumps(self.policy_data(minimum_score, minimum_tested), indent=2) + "\n",
            encoding="utf-8",
        )

    def write_baseline(self, *, recorded: bool, score: float = 50.0) -> None:
        if not recorded:
            payload = {"schemaVersion": 1, "status": "pending", "reason": "No accepted run."}
        else:
            project_killed = int(score / 10)
            project_survived = 10 - project_killed
            payload = {
                "schemaVersion": 1,
                "status": "recorded",
                "recordedAtUtc": "2026-08-02T00:00:00Z",
                "reviewedAtUtc": "2026-08-02T00:00:00Z",
                "reviewedBy": "reviewer",
                "reviewNotes": "Reviewed both HTML reports and accepted the meaningful survivors.",
                "sourceRevision": "abc123",
                "strykerVersion": "4.16.0",
                "testRunner": "mtp",
                "coverageAnalysis": "off",
                "mutationScore": score,
                "totalMutants": 20,
                "testedMutants": 20,
                "killedMutants": project_killed * 2,
                "survivedMutants": project_survived * 2,
                "timeoutMutants": 0,
                "noCoverageMutants": 0,
                "ignoredMutants": 0,
                "compileErrorMutants": 0,
                "compileErrorPercentage": 0.0,
                "runtimeErrorMutants": 0,
                "pendingMutants": 0,
                "notRunMutants": 0,
                "reportSetSha256": "a" * 64,
                "projects": [
                    {
                        "name": project,
                        "mutationScore": score,
                        "totalMutants": 10,
                        "testedMutants": 10,
                        "killedMutants": project_killed,
                        "survivedMutants": project_survived,
                        "timeoutMutants": 0,
                        "noCoverageMutants": 0,
                        "ignoredMutants": 0,
                        "compileErrorMutants": 0,
                        "compileErrorPercentage": 0.0,
                        "runtimeErrorMutants": 0,
                        "pendingMutants": 0,
                        "notRunMutants": 0,
                        "reportSha256": "b" * 64,
                    }
                    for project in self.PROJECTS
                ],
                "reviewRequired": False,
                "survivedMutantsReviewed": True,
            }
        self.baseline_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")

    def report_path(self, project: str) -> Path:
        return self.root / f"artifacts/mutation/reports/{project}/reports/mutation-report.json"

    def metadata_path(self, project: str) -> Path:
        return self.root / f"artifacts/mutation/reports/{project}/run-metadata.json"

    def write_report(
        self,
        project: str,
        statuses: list[str],
        *,
        reported_project: str | None = None,
        include_tests: bool = True,
        schema_version: int | str = 2,
        metadata_status: str = "success",
        exit_code: int = 0,
    ) -> None:
        path = self.report_path(project)
        path.parent.mkdir(parents=True, exist_ok=True)
        actual_project = reported_project or project
        payload = {
            "schemaVersion": schema_version,
            "projectRoot": str(self.root / "src" / actual_project),
            "testFiles": {
                str(self.root / "tests" / f"{project}.Tests" / "Tests.cs"): {
                    "language": "cs",
                    "source": "public sealed class Tests { }",
                    "tests": ([{"id": "test-1", "name": "Tests.Works"}] if include_tests else []),
                }
            },
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
        html = path.with_suffix(".html")
        html.write_text("<html></html>", encoding="utf-8")
        console_log_path = self.root / f"artifacts/mutation/reports/{project}/stryker-console.log"
        console_log_path.write_text("Stryker run completed successfully.\n", encoding="utf-8")
        metadata_path = self.metadata_path(project)
        metadata_path.parent.mkdir(parents=True, exist_ok=True)
        metadata = {
            "schemaVersion": 1,
            "project": project,
            "sourceRevision": "abc123",
            "strykerVersion": "4.16.0",
            "testRunner": "mtp",
            "coverageAnalysis": "off",
            "status": metadata_status,
            "exitCode": exit_code,
            "reportSha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "policySha256": hashlib.sha256(self.policy_path.read_bytes()).hexdigest(),
            "consoleLogPath": f"artifacts/mutation/reports/{project}/stryker-console.log",
            "consoleLogSha256": hashlib.sha256(console_log_path.read_bytes()).hexdigest(),
        }
        metadata_path.write_text(json.dumps(metadata), encoding="utf-8")

    def write_passing_reports(self) -> None:
        for project in self.PROJECTS:
            self.write_report(project, ["Killed"] * 6 + ["Survived"] * 4)

    def load(self):
        policy = MODULE.load_policy(self.policy_path)
        baseline = MODULE.load_baseline(self.baseline_path, policy)
        return policy, baseline

    def test_valid_recorded_baseline_passes_and_writes_summaries(self) -> None:
        self.write_passing_reports()
        policy, baseline = self.load()
        result = MODULE.execute_gate(
            policy, baseline, self.root, self.summary_path, self.json_path, "verify"
        )
        self.assertTrue(result.passed)
        self.assertEqual(20, result.totals.tested)
        self.assertEqual(12, result.totals.killed)
        self.assertAlmostEqual(60.0, result.totals.mutation_score)
        self.assertIn("**Overall status:** PASS", self.summary_path.read_text(encoding="utf-8"))
        self.assertEqual("pass", json.loads(self.json_path.read_text(encoding="utf-8"))["status"])

    def test_pending_baseline_blocks_verify(self) -> None:
        self.write_baseline(recorded=False)
        self.write_passing_reports()
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "baseline is pending"):
            MODULE.execute_gate(
                policy, baseline, self.root, self.summary_path, self.json_path, "verify"
            )

    def test_capture_baseline_accepts_pending_and_writes_candidate(self) -> None:
        self.write_baseline(recorded=False)
        self.write_passing_reports()
        policy, baseline = self.load()
        MODULE.execute_gate(
            policy,
            baseline,
            self.root,
            self.summary_path,
            self.json_path,
            "capture-baseline",
            self.candidate_path,
        )
        candidate = json.loads(self.candidate_path.read_text(encoding="utf-8"))
        self.assertEqual("candidate", candidate["status"])
        self.assertEqual(60.0, candidate["mutationScore"])
        self.assertTrue(candidate["reviewRequired"])
        self.assertFalse(candidate["survivedMutantsReviewed"])

    def test_candidate_requires_explicit_review_before_becoming_baseline(self) -> None:
        self.write_baseline(recorded=False)
        self.write_passing_reports()
        policy, baseline = self.load()
        MODULE.execute_gate(
            policy,
            baseline,
            self.root,
            self.summary_path,
            self.json_path,
            "capture-baseline",
            self.candidate_path,
        )

        accepted_path = self.root / "eng/accepted-baseline.json"
        accepted = MODULE.accept_baseline_candidate(
            self.candidate_path,
            accepted_path,
            policy,
            "reviewer",
            "Reviewed both HTML reports and accepted documented survivors.",
        )

        self.assertEqual("recorded", accepted["status"])
        self.assertFalse(accepted["reviewRequired"])
        self.assertTrue(accepted["survivedMutantsReviewed"])
        MODULE.load_baseline(accepted_path, policy)

    def test_candidate_cannot_be_accepted_without_review_metadata(self) -> None:
        self.write_baseline(recorded=False)
        self.write_passing_reports()
        policy, baseline = self.load()
        MODULE.execute_gate(
            policy,
            baseline,
            self.root,
            self.summary_path,
            self.json_path,
            "capture-baseline",
            self.candidate_path,
        )
        with self.assertRaisesRegex(MODULE.MutationError, "reviewed-by"):
            MODULE.accept_baseline_candidate(
                self.candidate_path, self.root / "baseline.json", policy, "", "reviewed"
            )

    def test_all_survived_result_is_rejected_as_invalid(self) -> None:
        for project in self.PROJECTS:
            self.write_report(project, ["Survived"] * 10)
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "degenerate all-survived"):
            MODULE.execute_gate(
                policy, baseline, self.root, self.summary_path, self.json_path, "verify"
            )

    def test_zero_tests_is_rejected(self) -> None:
        self.write_report("TCJ.Core", ["Killed"] * 6 + ["Survived"] * 4, include_tests=False)
        self.write_report("TCJ.DependencyInjection", ["Killed"] * 6 + ["Survived"] * 4)
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "discovered zero tests"):
            MODULE.execute_gate(
                policy, baseline, self.root, self.summary_path, self.json_path, "verify"
            )

    def test_wrong_report_schema_is_rejected(self) -> None:
        self.write_report("TCJ.Core", ["Killed"] * 6 + ["Survived"] * 4, schema_version=1)
        self.write_report("TCJ.DependencyInjection", ["Killed"] * 6 + ["Survived"] * 4)
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "schemaVersion must be 2"):
            MODULE.execute_gate(
                policy, baseline, self.root, self.summary_path, self.json_path, "verify"
            )

    def test_wrong_project_is_rejected(self) -> None:
        self.write_report("TCJ.Core", ["Killed"] * 6 + ["Survived"] * 4)
        self.write_report(
            "TCJ.DependencyInjection",
            ["Killed"] * 6 + ["Survived"] * 4,
            reported_project="Different.Project",
        )
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "does not identify configured project"):
            MODULE.execute_gate(
                policy, baseline, self.root, self.summary_path, self.json_path, "verify"
            )

    def test_known_runner_failure_marker_is_rejected(self) -> None:
        self.write_passing_reports()
        log_path = self.root / "artifacts/mutation/reports/TCJ.Core/stryker-console.log"
        log_path.write_text("It looks like the test coverage capture failed.\n", encoding="utf-8")
        metadata = json.loads(self.metadata_path("TCJ.Core").read_text(encoding="utf-8"))
        metadata["consoleLogSha256"] = hashlib.sha256(log_path.read_bytes()).hexdigest()
        self.metadata_path("TCJ.Core").write_text(json.dumps(metadata), encoding="utf-8")
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "runner log contains invalid-execution marker"):
            MODULE.execute_gate(
                policy, baseline, self.root, self.summary_path, self.json_path, "verify"
            )

    def test_report_hash_mismatch_is_rejected(self) -> None:
        self.write_passing_reports()
        metadata = json.loads(self.metadata_path("TCJ.Core").read_text(encoding="utf-8"))
        metadata["reportSha256"] = "0" * 64
        self.metadata_path("TCJ.Core").write_text(json.dumps(metadata), encoding="utf-8")
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "report hash mismatch"):
            MODULE.execute_gate(
                policy, baseline, self.root, self.summary_path, self.json_path, "verify"
            )

    def test_runner_failure_is_rejected(self) -> None:
        self.write_report(
            "TCJ.Core", ["Killed"] * 6 + ["Survived"] * 4,
            metadata_status="failure", exit_code=1,
        )
        self.write_report("TCJ.DependencyInjection", ["Killed"] * 6 + ["Survived"] * 4)
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "runner did not complete"):
            MODULE.execute_gate(
                policy, baseline, self.root, self.summary_path, self.json_path, "verify"
            )

    def test_compile_error_rate_is_enforced(self) -> None:
        for project in self.PROJECTS:
            self.write_report(project, ["Killed"] * 6 + ["Survived"] * 3 + ["CompileError"] * 2)
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "compile-error rate"):
            MODULE.execute_gate(
                policy, baseline, self.root, self.summary_path, self.json_path, "verify"
            )

    def test_below_policy_score_is_rejected(self) -> None:
        for project in self.PROJECTS:
            self.write_report(project, ["Killed"] * 4 + ["Survived"] * 6)
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "mutation score 40.00% is below"):
            MODULE.execute_gate(
                policy, baseline, self.root, self.summary_path, self.json_path, "verify"
            )

    def test_baseline_regression_is_rejected(self) -> None:
        self.write_baseline(recorded=True, score=70.0)
        self.write_passing_reports()
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "recorded baseline floor 70.00%"):
            MODULE.execute_gate(
                policy, baseline, self.root, self.summary_path, self.json_path, "verify"
            )

    def test_missing_and_malformed_policy_fail(self) -> None:
        with self.assertRaisesRegex(MODULE.MutationError, "Mutation policy is missing"):
            MODULE.load_policy(self.root / "eng/missing.json")
        self.policy_path.write_text("{broken", encoding="utf-8")
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
