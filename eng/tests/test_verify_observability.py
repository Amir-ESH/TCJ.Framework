from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-observability.py"
SPEC = importlib.util.spec_from_file_location("verify_observability", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class ObservabilityVerifierTests(unittest.TestCase):
    def test_current_repository_configuration_passes(self) -> None:
        policy, contract = MODULE.validate_configuration()
        self.assertEqual(1, policy["schemaVersion"])
        self.assertEqual(1, contract["schemaVersion"])

    def test_duplicate_contract_values_fail(self) -> None:
        with self.assertRaisesRegex(MODULE.ObservabilityError, "duplicates"):
            MODULE.require_unique_strings(["tcj.test", "tcj.test"], "test")

    def test_unstable_name_fails(self) -> None:
        with self.assertRaisesRegex(MODULE.ObservabilityError, "Unstable"):
            MODULE.validate_name_stability(["tcj.repository.{id}"], "activity")

    def test_trx_parser_requires_passing_executed_tests(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "result.trx").write_text(
                '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                '<ResultSummary outcome="Completed"><Counters total="3" executed="3" passed="3" failed="0" />'
                '</ResultSummary></TestRun>',
                encoding="utf-8",
            )
            self.assertEqual((3, 0), MODULE.parse_trx_results(root))

    def test_metric_dimensions_outside_policy_fail(self) -> None:
        source = (MODULE.ROOT / "src/TCJ.Core/Diagnostics/TcjDiagnosticNames.cs").read_text(encoding="utf-8")
        with self.assertRaisesRegex(MODULE.ObservabilityError, "outside allowedMetricDimensions"):
            MODULE.validate_metric_dimensions(source, ["tcj.operation.outcome"])

    def test_sensitive_marker_scan_detects_leak(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "telemetry.log").write_text("value=TCJ_TEST_TOKEN_MARKER", encoding="utf-8")
            result = MODULE.scan_sensitive_markers(root, ["TCJ_TEST_TOKEN_MARKER"])
            self.assertEqual("fail", result["status"])
            self.assertEqual(1, len(result["hits"]))


if __name__ == "__main__":
    unittest.main()
