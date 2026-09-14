import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("verify_sagas", ROOT / "eng/verify-sagas.py")
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class SagaVerifierTests(unittest.TestCase):
    def test_real_configuration(self):
        MODULE.validate_config()

    def test_sensitive_scan_rejects_marker(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "diagnostic.txt"
            path.write_text("prefix tcj-saga-secret-payload suffix", encoding="utf-8")
            findings = MODULE.scan_sensitive([Path(temp)], ["tcj-saga-secret-payload"])
            self.assertEqual(1, len(findings))

    def test_transport_neutral_source_validation(self):
        MODULE.validate_transport_neutral_sources()

    def test_explicit_registration_rejects_reflection_scanning_contract(self):
        MODULE.validate_no_reflection_registration()

    def test_contract_has_no_overstated_distributed_guarantees(self):
        contract = MODULE.read_json(MODULE.CONTRACT)
        self.assertFalse(contract["claims"]["distributedAcid"])
        self.assertFalse(contract["claims"]["globalExactlyOnce"])
        self.assertFalse(contract["claims"]["automaticRemoteRollback"])
        self.assertFalse(contract["claims"]["generalPurposeScheduler"])
        self.assertFalse(contract["claims"]["eventSourcingReplay"])

    def test_correlation_contract_does_not_persist_raw_value(self):
        contract = MODULE.read_json(MODULE.CONTRACT)
        self.assertFalse(contract["correlation"]["persistRawValue"])
        self.assertEqual("OrdinalCaseSensitive", contract["correlation"]["stringComparison"])


if __name__ == "__main__":
    unittest.main()
