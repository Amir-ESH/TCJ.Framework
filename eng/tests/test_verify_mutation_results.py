from __future__ import annotations

import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "verify-mutation-results.py"
SPEC = importlib.util.spec_from_file_location("verify_mutation_results", SCRIPT)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Unable to load {SCRIPT}")
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class MutationVerifierTests(unittest.TestCase):
    PROJECTS = ("TCJ.Core", "TCJ.DependencyInjection")

    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        for directory in ("eng", ".config", ".github/workflows", "tests", "src"):
            (self.root / directory).mkdir(parents=True, exist_ok=True)
        self.policy_path = self.root / "eng/mutation-policy.json"
        self.baseline_path = self.root / "eng/mutation-baseline.json"
        self.config_path = self.root / "stryker-config.json"
        self.manifest_path = self.root / ".config/dotnet-tools.json"
        self.workflow_path = self.root / ".github/workflows/mutation-testing.yml"
        self.required_gate_path = self.root / ".github/workflows/required-pr-gate.yml"
        self.test_props_path = self.root / "tests/TestProject.props"
        self.summary_path = self.root / "artifacts/mutation/MUTATION_SUMMARY.md"
        self.json_path = self.root / "artifacts/mutation/mutation-summary.json"
        self.candidate_path = self.root / "artifacts/mutation/mutation-baseline-candidate.json"
        self.write_repository_files()

    def tearDown(self) -> None:
        self.temp.cleanup()

    def project(self, name: str) -> dict:
        return {
            "name": name,
            "sourceProject": f"src/{name}/{name}.csproj",
            "testProject": f"tests/{name}.Tests/{name}.Tests.csproj",
            "minimumTestedMutants": 4,
            "mutationTargets": ["Example.cs"],
            "reportPath": f"artifacts/mutation/reports/{name}/reports/mutation-report.json",
            "htmlReportPath": f"artifacts/mutation/reports/{name}/reports/mutation-report.html",
            "runMetadataPath": f"artifacts/mutation/reports/{name}/run-metadata.json",
            "consoleLogPath": f"artifacts/mutation/reports/{name}/stryker-console.log",
        }

    def policy(self, score: float = 50.0) -> dict:
        return {
            "schemaVersion": 2,
            "strykerVersion": "4.16.0",
            "testRunner": "mtp",
            "coverageAnalysis": "off",
            "baselinePath": "eng/mutation-baseline.json",
            "minimumMutationScore": score,
            "allowedBaselineScoreRegression": 0.0,
            "minimumTestedMutants": 8,
            "minimumKilledMutants": 2,
            "minimumKilledMutantsPerProject": 1,
            "maximumCompileErrorPercentage": 10.0,
            "maximumRuntimeErrorMutants": 0,
            "projects": [self.project(name) for name in self.PROJECTS],
            "excludedFilePatterns": ["**/*.g.cs", "tests/**"],
            "ignoredMutationTypes": [],
            "ignoredMutationJustifications": {},
            "forbiddenRunnerLogMarkers": ["test coverage capture failed", "no tests were found"],
            "sourceLevelExclusions": [],
            "reportPaths": {
                "reportsDirectory": "artifacts/mutation/reports",
                "summaryJson": "artifacts/mutation/mutation-summary.json",
                "summaryMarkdown": "artifacts/mutation/MUTATION_SUMMARY.md",
                "baselineCandidate": "artifacts/mutation/mutation-baseline-candidate.json",
            },
        }

    def write_repository_files(self) -> None:
        self.policy_path.write_text(json.dumps(self.policy(), indent=2), encoding="utf-8")
        self.write_baseline(recorded=False)
        self.config_path.write_text(
            json.dumps({
                "stryker-config": {
                    "reporters": ["html", "json"],
                    "configuration": "Release",
                    "test-runner": "mtp",
                    "coverage-analysis": "off",
                    "concurrency": 1,
                    "disable-mix-mutants": True,
                    "thresholds": {"high": 80, "low": 50.0, "break": 0},
                    "ignore-mutations": [],
                }
            }),
            encoding="utf-8",
        )
        self.manifest_path.write_text(
            json.dumps({"version": 1, "isRoot": True, "tools": {"dotnet-stryker": {"version": "4.16.0"}}}),
            encoding="utf-8",
        )
        self.workflow_path.write_text(
            """name: Mutation testing
on:
  workflow_call:
  workflow_dispatch:
  schedule:
  push:
jobs:
  gate:
    name: Run mutation tests
    steps:
      - name: Run TCJ.Core mutation tests
      - name: Run TCJ.DependencyInjection mutation tests
      - name: Upload mutation reports
        with:
          path: artifacts/mutation/mutation-baseline-candidate.json
""",
            encoding="utf-8",
        )
        self.required_gate_path.write_text(
            """name: Required PR orchestration
on:
  pull_request:
jobs:
  mutation:
    uses: ./.github/workflows/mutation-testing.yml
  required:
    name: Required PR Gate
""",
            encoding="utf-8",
        )
        self.test_props_path.write_text(
            "<Project><PropertyGroup><OutputType>Exe</OutputType>"
            "<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>"
            "</PropertyGroup></Project>",
            encoding="utf-8",
        )
        (self.root / "eng/run-mutation-testing.py").write_text("# runner\n", encoding="utf-8")
        for name in self.PROJECTS:
            source = self.root / f"src/{name}/{name}.csproj"
            test = self.root / f"tests/{name}.Tests/{name}.Tests.csproj"
            source.parent.mkdir(parents=True, exist_ok=True)
            test.parent.mkdir(parents=True, exist_ok=True)
            source.write_text("<Project />", encoding="utf-8")
            test.write_text("<Project />", encoding="utf-8")

    def write_baseline(self, *, recorded: bool, score: float = 60.0) -> None:
        if not recorded:
            data = {"schemaVersion": 1, "status": "pending", "reason": "Needs a real run."}
        else:
            data = {
                "schemaVersion": 1,
                "status": "recorded",
                "recordedAtUtc": "2026-08-03T00:00:00Z",
                "reviewedAtUtc": "2026-08-03T00:00:00Z",
                "reviewedBy": "reviewer",
                "reviewNotes": "Reviewed both HTML reports.",
                "sourceRevision": "abc123",
                "strykerVersion": "4.16.0",
                "testRunner": "mtp",
                "coverageAnalysis": "off",
                "reportSetSha256": "a" * 64,
                "mutationScore": score,
                "survivedMutantsReviewed": True,
            }
        self.baseline_path.write_text(json.dumps(data, indent=2), encoding="utf-8")

    def write_report(self, name: str, statuses: list[str], *, log: str = "Stryker completed.\n") -> None:
        report = self.root / f"artifacts/mutation/reports/{name}/reports/mutation-report.json"
        report.parent.mkdir(parents=True, exist_ok=True)
        report.write_text(
            json.dumps({
                "schemaVersion": "2",
                "projectRoot": str(self.root / "src" / name),
                "files": {
                    str(self.root / "src" / name / "Example.cs"): {
                        "language": "cs",
                        "source": "public class Example {}",
                        "mutants": [{"id": str(i), "status": status} for i, status in enumerate(statuses)],
                    }
                },
            }),
            encoding="utf-8",
        )
        report.with_suffix(".html").write_text("<html></html>", encoding="utf-8")
        log_path = self.root / f"artifacts/mutation/reports/{name}/stryker-console.log"
        log_path.write_text(log, encoding="utf-8")
        metadata_path = self.root / f"artifacts/mutation/reports/{name}/run-metadata.json"
        metadata_path.write_text(
            json.dumps({
                "schemaVersion": 1,
                "project": name,
                "sourceRevision": "abc123",
                "strykerVersion": "4.16.0",
                "testRunner": "mtp",
                "coverageAnalysis": "off",
                "status": "success",
                "exitCode": 0,
                "reportSha256": hashlib.sha256(report.read_bytes()).hexdigest(),
                "policySha256": hashlib.sha256(self.policy_path.read_bytes()).hexdigest(),
                "consoleLogPath": f"artifacts/mutation/reports/{name}/stryker-console.log",
                "consoleLogSha256": hashlib.sha256(log_path.read_bytes()).hexdigest(),
            }),
            encoding="utf-8",
        )

    def load(self):
        policy = MODULE.load_policy(self.policy_path)
        baseline = MODULE.load_baseline(self.baseline_path, policy)
        return policy, baseline

    def write_passing_reports(self) -> None:
        for name in self.PROJECTS:
            self.write_report(name, ["Killed"] * 3 + ["Survived"] * 2)

    def test_pending_baseline_does_not_block_configuration_validation(self) -> None:
        policy, baseline = MODULE.validate_configuration(
            self.root, self.policy_path, self.baseline_path, self.config_path,
            self.manifest_path, self.workflow_path, self.test_props_path, check_git=False,
        )
        self.assertEqual("pending", baseline.status)
        self.assertEqual(2, len(policy.projects))

    def test_workflow_precheck_that_blocks_stryker_is_rejected(self) -> None:
        self.workflow_path.write_text(
            self.workflow_path.read_text(encoding="utf-8") + "\n# Require a recorded baseline\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(MODULE.MutationError, "must not stop before Stryker"):
            MODULE.validate_configuration(
                self.root, self.policy_path, self.baseline_path, self.config_path,
                self.manifest_path, self.workflow_path, self.test_props_path, check_git=False,
            )

    def test_missing_source_level_exclusion_marker_is_rejected(self) -> None:
        data = json.loads(self.policy_path.read_text(encoding="utf-8"))
        source_file = self.root / "src/TCJ.DependencyInjection/StaticState.cs"
        source_file.write_text("private static readonly object State = new();\n", encoding="utf-8")
        data["sourceLevelExclusions"] = [{
            "file": "src/TCJ.DependencyInjection/StaticState.cs",
            "declarationContains": "private static readonly object State",
            "comment": "// Stryker disable once all: MTP process reuse.",
            "reason": "MTP process reuse can contaminate later mutant sessions.",
        }]
        self.policy_path.write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaisesRegex(MODULE.MutationError, "exclusion is missing"):
            MODULE.validate_configuration(
                self.root, self.policy_path, self.baseline_path, self.config_path,
                self.manifest_path, self.workflow_path, self.test_props_path, check_git=False,
            )

    def test_vstest_configuration_is_rejected(self) -> None:
        data = json.loads(self.config_path.read_text(encoding="utf-8"))
        data["stryker-config"]["test-runner"] = "vstest"
        self.config_path.write_text(json.dumps(data), encoding="utf-8")
        with self.assertRaisesRegex(MODULE.MutationError, "must use the MTP runner"):
            MODULE.validate_configuration(
                self.root, self.policy_path, self.baseline_path, self.config_path,
                self.manifest_path, self.workflow_path, self.test_props_path, check_git=False,
            )

    def test_verify_pending_runs_reports_generates_candidate_then_fails(self) -> None:
        self.write_passing_reports()
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "valid candidate was generated"):
            MODULE.execute_gate(
                self.root, policy, baseline, "verify", self.summary_path, self.json_path, self.candidate_path
            )
        self.assertTrue(self.candidate_path.is_file())
        self.assertEqual("candidate", json.loads(self.candidate_path.read_text())["status"])

    def test_capture_baseline_passes_with_pending_baseline(self) -> None:
        self.write_passing_reports()
        policy, baseline = self.load()
        result = MODULE.execute_gate(
            self.root, policy, baseline, "capture-baseline", self.summary_path, self.json_path, self.candidate_path
        )
        self.assertEqual(6, result.totals.killed)
        self.assertTrue(self.candidate_path.is_file())

    def test_accept_baseline_requires_review_metadata(self) -> None:
        self.write_passing_reports()
        policy, baseline = self.load()
        MODULE.execute_gate(
            self.root, policy, baseline, "capture-baseline", self.summary_path, self.json_path, self.candidate_path
        )
        with self.assertRaisesRegex(MODULE.MutationError, "reviewed-by"):
            MODULE.accept_candidate(self.candidate_path, self.baseline_path, policy, "", "reviewed")

    def test_recorded_baseline_allows_verify(self) -> None:
        self.write_baseline(recorded=True, score=60.0)
        self.write_passing_reports()
        policy, baseline = self.load()
        result = MODULE.execute_gate(
            self.root, policy, baseline, "verify", self.summary_path, self.json_path, self.candidate_path
        )
        self.assertAlmostEqual(60.0, result.totals.score)

    def test_recorded_baseline_uses_persisted_score_precision(self) -> None:
        self.write_baseline(recorded=True, score=72.73)
        self.write_report("TCJ.Core", ["Killed"] * 4 + ["Survived"] * 2)
        self.write_report("TCJ.DependencyInjection", ["Killed"] * 4 + ["Survived"])
        policy, baseline = self.load()

        result = MODULE.execute_gate(
            self.root, policy, baseline, "verify", self.summary_path, self.json_path, self.candidate_path
        )

        self.assertAlmostEqual(72.72727272727273, result.totals.score)
        self.assertTrue(result.baseline_score_passed)

    def test_all_survived_result_is_rejected_and_no_candidate_is_created(self) -> None:
        for name in self.PROJECTS:
            self.write_report(name, ["Survived"] * 5)
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "degenerate all-survived"):
            MODULE.execute_gate(
                self.root, policy, baseline, "capture-baseline", self.summary_path, self.json_path, self.candidate_path
            )
        self.assertFalse(self.candidate_path.exists())

    def test_forbidden_runner_marker_is_rejected(self) -> None:
        self.write_report("TCJ.Core", ["Killed"] * 3 + ["Survived"] * 2, log="test coverage capture failed\n")
        self.write_report("TCJ.DependencyInjection", ["Killed"] * 3 + ["Survived"] * 2)
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "invalid-execution marker"):
            MODULE.execute_gate(
                self.root, policy, baseline, "capture-baseline", self.summary_path, self.json_path, self.candidate_path
            )

    def test_below_threshold_result_fails(self) -> None:
        for name in self.PROJECTS:
            self.write_report(name, ["Killed"] * 2 + ["Survived"] * 3)
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "mutation score 40.00% is below"):
            MODULE.execute_gate(
                self.root, policy, baseline, "capture-baseline", self.summary_path, self.json_path, self.candidate_path
            )

    def test_missing_report_fails(self) -> None:
        self.write_report("TCJ.Core", ["Killed"] * 3 + ["Survived"] * 2)
        policy, baseline = self.load()
        with self.assertRaisesRegex(MODULE.MutationError, "Stryker report for TCJ.DependencyInjection is missing"):
            MODULE.execute_gate(
                self.root, policy, baseline, "capture-baseline", self.summary_path, self.json_path, self.candidate_path
            )


if __name__ == "__main__":
    unittest.main()
