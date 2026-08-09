from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-resilience.py"
SPEC = importlib.util.spec_from_file_location("verify_resilience", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class ResilienceVerifierTests(unittest.TestCase):
    def test_current_repository_configuration_passes(self) -> None:
        policy, contract = MODULE.validate_configuration()
        self.assertGreaterEqual(policy["minimumScenarioCount"], 18)
        self.assertEqual(3, contract["defaults"]["maxRetryAttempts"])

    def test_duplicate_required_values_fail(self) -> None:
        with self.assertRaisesRegex(MODULE.ResilienceError, "duplicates"):
            MODULE.require_unique_strings(["Retry", "Retry"], "requiredCategories")

    def test_trx_parser_rejects_failed_test(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "result.trx").write_text(
                '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                '<ResultSummary outcome="Failed"><Counters total="2" executed="2" passed="1" failed="1" />'
                '</ResultSummary></TestRun>', encoding="utf-8")
            with self.assertRaisesRegex(MODULE.ResilienceError, "failed/error"):
                MODULE.parse_trx_results(root)

    def test_trace_scan_rejects_sensitive_marker(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "trace.json").write_text(json.dumps({"value": "TCJ_TEST_TOKEN_MARKER"}), encoding="utf-8")
            with self.assertRaisesRegex(MODULE.ResilienceError, "Sensitive"):
                MODULE.scan_traces(root)

    def test_trace_scan_rejects_policy_sensitive_pattern(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "trace.json").write_text(json.dumps({"raw_sql": "select 1"}), encoding="utf-8")
            with self.assertRaisesRegex(MODULE.ResilienceError, "Sensitive"):
                MODULE.scan_traces(root, ("raw_sql",))

    def test_trace_scan_requires_valid_json(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "trace.json").write_text("not-json", encoding="utf-8")
            with self.assertRaisesRegex(MODULE.ResilienceError, "Malformed"):
                MODULE.scan_traces(root)


if __name__ == "__main__":
    unittest.main()
