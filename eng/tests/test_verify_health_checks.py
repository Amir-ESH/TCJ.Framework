from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-health-checks.py"
SPEC = importlib.util.spec_from_file_location("verify_health_checks", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class HealthCheckVerifierTests(unittest.TestCase):
    def test_current_repository_configuration_passes(self) -> None:
        policy, contract = MODULE.validate_config()
        self.assertGreaterEqual(policy["minimumIntegrationTestCount"], 15)
        self.assertEqual("existing-packages", contract["packageStrategy"])

    def test_duplicate_required_values_fail(self) -> None:
        with self.assertRaisesRegex(MODULE.HealthCheckError, "duplicate"):
            MODULE.strings(["ready", "ready"], "requiredTags")

    def test_trx_parser_detects_failed_test(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "health.trx"
            path.write_text(
                '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                '<Results><UnitTestResult testName="HealthFailure" outcome="Failed" /></Results>'
                '<ResultSummary><Counters total="1" executed="1" passed="0" failed="1" /></ResultSummary>'
                '</TestRun>', encoding="utf-8")
            total, failed, names = MODULE.parse_results(Path(directory))
            self.assertEqual(1, total)
            self.assertEqual(1, failed)
            self.assertEqual(["HealthFailure"], names)

    def test_sensitive_scan_rejects_marker(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "response.json").write_text('{"detail":"TCJ_TEST_SECRET"}', encoding="utf-8")
            findings = MODULE.scan_sensitive([root], ["TCJ_TEST_SECRET"])
            self.assertEqual(1, len(findings))

    def test_sensitive_scan_ignores_clean_evidence(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "response.json").write_text('{"status":"Healthy"}', encoding="utf-8")
            self.assertEqual([], MODULE.scan_sensitive([root], ["TCJ_TEST_SECRET"]))


if __name__ == "__main__":
    unittest.main()
