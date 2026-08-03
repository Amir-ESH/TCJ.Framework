from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-performance-results.py"
SPEC = importlib.util.spec_from_file_location("verify_performance_results", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class PerformanceVerifierTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.policy_path = self.root / "eng/performance-policy.json"
        self.reports = self.root / "artifacts/performance/reports"
        self.policy_path.parent.mkdir(parents=True)
        self.reports.mkdir(parents=True)
        self.write_policy()

    def tearDown(self) -> None:
        self.temp.cleanup()

    def write_policy(self, **overrides: object) -> None:
        policy = {
            "schemaVersion": 1,
            "minimumBenchmarkCount": 8,
            "maximumRelativeMeanRatio": 1.20,
            "maximumRelativeAllocationRatio": 1.15,
            "maximumUnexplainedAllocatedBytes": 1024,
            "requiredBenchmarkCategories": ["TCJ.Core", "TCJ.DependencyInjection"],
        }
        policy.update(overrides)
        self.policy_path.write_text(json.dumps(policy), encoding="utf-8")

    def manifest_entries(self) -> list[dict]:
        return [
            {
                "type": "CoreBenchmarks",
                "method": "Baseline",
                "categories": ["TCJ.Core"],
                "comparisonGroup": "CoreComparison",
                "baseline": True,
            },
            {
                "type": "CoreBenchmarks",
                "method": "Candidate",
                "categories": ["TCJ.Core"],
                "comparisonGroup": "CoreComparison",
                "baseline": False,
            },
            *[
                {
                    "type": "CoreBenchmarks",
                    "method": f"Standalone{index}",
                    "categories": ["TCJ.Core"],
                    "comparisonGroup": None,
                    "baseline": index == 1,
                }
                for index in range(1, 4)
            ],
            *[
                {
                    "type": "DependencyBenchmarks",
                    "method": f"Operation{index}",
                    "categories": ["TCJ.DependencyInjection"],
                    "comparisonGroup": None,
                    "baseline": index == 1,
                }
                for index in range(1, 4)
            ],
        ]

    def write_manifest(self, entries: list[dict] | None = None) -> None:
        payload = {
            "schemaVersion": 1,
            "benchmarks": entries if entries is not None else self.manifest_entries(),
        }
        (self.reports / "benchmark-manifest.json").write_text(
            json.dumps(payload),
            encoding="utf-8",
        )

    def write_report(
        self,
        *,
        candidate_mean: float = 110.0,
        candidate_allocated: float = 110.0,
        entries: list[dict] | None = None,
    ) -> None:
        definitions = entries if entries is not None else self.manifest_entries()
        benchmarks = []
        for item in definitions:
            mean = 100.0
            allocated = 100.0
            if item["method"] == "Candidate":
                mean = candidate_mean
                allocated = candidate_allocated
            benchmarks.append(
                {
                    "Type": item["type"],
                    "Method": item["method"],
                    "Success": True,
                    "Statistics": {
                        "Mean": mean,
                        "StandardError": 1.0,
                        "StandardDeviation": 2.0,
                    },
                    "Memory": {"BytesAllocatedPerOperation": allocated},
                }
            )
        payload = {
            "Title": "Synthetic performance report",
            "HostEnvironmentInfo": {
                "BenchmarkDotNetCaption": "BenchmarkDotNet v0.15.8",
                "RuntimeVersion": ".NET 10.0",
                "DotNetSdkVersion": "10.0.302",
                "OsVersion": "Linux",
                "Architecture": "X64",
            },
            "Benchmarks": benchmarks,
        }
        (self.reports / "synthetic-report-full.json").write_text(
            json.dumps(payload),
            encoding="utf-8",
        )

    def verify(self):
        policy = MODULE.load_policy(self.policy_path)
        return MODULE.verify_reports(policy, self.reports)

    def test_successful_report_passes(self) -> None:
        self.write_manifest()
        self.write_report()
        evaluated, failures, _, _ = self.verify()
        self.assertEqual(8, len(evaluated))
        self.assertEqual([], failures)

    def test_runtime_regression_fails(self) -> None:
        self.write_manifest()
        self.write_report(candidate_mean=121.0)
        _, failures, _, _ = self.verify()
        self.assertTrue(any("mean ratio" in failure for failure in failures))

    def test_allocation_regression_fails(self) -> None:
        self.write_manifest()
        self.write_report(candidate_allocated=116.0)
        _, failures, _, _ = self.verify()
        self.assertTrue(any("allocation ratio" in failure for failure in failures))

    def test_missing_baseline_fails(self) -> None:
        entries = self.manifest_entries()
        for item in entries:
            if item["comparisonGroup"] == "CoreComparison":
                item["baseline"] = False
        self.write_manifest(entries)
        with self.assertRaisesRegex(MODULE.PerformanceError, "exactly one baseline"):
            MODULE.load_manifest(self.reports / "benchmark-manifest.json")

    def test_missing_category_fails(self) -> None:
        entries = [
            item for item in self.manifest_entries()
            if "TCJ.DependencyInjection" not in item["categories"]
        ]
        self.write_manifest(entries)
        self.write_report(entries=entries)
        _, failures, _, _ = self.verify()
        self.assertTrue(any("Missing required benchmark categories" in failure for failure in failures))

    def test_insufficient_benchmark_count_fails(self) -> None:
        entries = self.manifest_entries()[:7]
        self.write_manifest(entries)
        self.write_report(entries=entries)
        _, failures, _, _ = self.verify()
        self.assertTrue(any("at least 8" in failure for failure in failures))

    def test_missing_report_fails(self) -> None:
        self.write_manifest()
        policy = MODULE.load_policy(self.policy_path)
        with self.assertRaisesRegex(MODULE.PerformanceError, "No BenchmarkDotNet JSON reports"):
            MODULE.verify_reports(policy, self.reports)

    def test_missing_policy_fails(self) -> None:
        self.policy_path.unlink()
        with self.assertRaisesRegex(MODULE.PerformanceError, "missing"):
            MODULE.load_policy(self.policy_path)

    def test_malformed_policy_fails(self) -> None:
        self.policy_path.write_text("{not-json", encoding="utf-8")
        with self.assertRaisesRegex(MODULE.PerformanceError, "Invalid JSON"):
            MODULE.load_policy(self.policy_path)

    def test_ignored_policy_fails(self) -> None:
        subprocess.run(["git", "init", "--quiet"], cwd=self.root, check=True)
        (self.root / ".gitignore").write_text(
            "eng/performance-policy.json\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(MODULE.PerformanceError, "ignored by Git"):
            MODULE.ensure_policy_tracked(self.root, self.policy_path)


    def test_successful_report_writes_summary_and_json(self) -> None:
        self.write_manifest()
        self.write_report()
        policy = MODULE.load_policy(self.policy_path)
        evaluated, failures, warnings, environment = MODULE.verify_reports(
            policy,
            self.reports,
        )
        summary_path = self.root / "artifacts/performance/PERFORMANCE_SUMMARY.md"
        json_path = self.root / "artifacts/performance/performance-summary.json"
        MODULE.write_outputs(
            policy,
            evaluated,
            failures,
            warnings,
            environment,
            summary_path,
            json_path,
        )
        self.assertIn("**Overall status:** PASS", summary_path.read_text(encoding="utf-8"))
        payload = json.loads(json_path.read_text(encoding="utf-8"))
        self.assertEqual("pass", payload["status"])
        self.assertEqual(8, payload["benchmarkCount"])

    def test_unexpected_report_benchmark_fails(self) -> None:
        self.write_manifest()
        self.write_report()
        report_path = self.reports / "synthetic-report-full.json"
        report = json.loads(report_path.read_text(encoding="utf-8"))
        report["Benchmarks"].append(
            {
                "Type": "UnexpectedBenchmarks",
                "Method": "UnexpectedOperation",
                "Success": True,
                "Statistics": {
                    "Mean": 100.0,
                    "StandardError": 1.0,
                    "StandardDeviation": 2.0,
                },
                "Memory": {"BytesAllocatedPerOperation": 0.0},
            }
        )
        report_path.write_text(json.dumps(report), encoding="utf-8")
        _, failures, _, _ = self.verify()
        self.assertTrue(any("Unexpected benchmark results" in failure for failure in failures))

    def test_missing_allocation_measurement_fails(self) -> None:
        self.write_manifest()
        self.write_report()
        report_path = self.reports / "synthetic-report-full.json"
        report = json.loads(report_path.read_text(encoding="utf-8"))
        report["Benchmarks"][0].pop("Memory")
        report_path.write_text(json.dumps(report), encoding="utf-8")
        with self.assertRaisesRegex(MODULE.PerformanceError, "memory-allocation"):
            self.verify()

    def test_non_finite_result_fails(self) -> None:
        self.write_manifest()
        self.write_report()
        report_path = self.reports / "synthetic-report-full.json"
        report = json.loads(report_path.read_text(encoding="utf-8"))
        report["Benchmarks"][0]["Statistics"]["Mean"] = float("inf")
        report_path.write_text(json.dumps(report), encoding="utf-8")
        with self.assertRaisesRegex(MODULE.PerformanceError, "finite"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
