"""Synthetic evidence tests exercise rejection paths; fixtures are never release evidence."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("verify_messaging_compatibility", ROOT / "eng/verify-messaging-compatibility.py")
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)


class MessagingCompatibilityVerifierTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.results = self.root / "results"
        self.output = self.root / "output"
        self.policy = VERIFIER.read_json(ROOT / VERIFIER.POLICY)
        self.contract = VERIFIER.read_json(ROOT / VERIFIER.CONTRACT)
        self.context = {"sourceSha": "a" * 40, "sourceDigest": "b" * 64, "runId": "123", "runAttempt": "1"}
        self.config_patch = patch.object(VERIFIER, "validate_config", return_value=(self.policy, self.contract))
        self.config_patch.start()
        self.addCleanup(self.config_patch.stop)
        self.bundle("neutral", self.contract["requiredNeutralTests"] + ["In_memory_transport_dead_letter_metadata_is_sanitized", "In_memory_transport_preserves_message_id_on_redelivery", "Permanent_failure_abandons_when_dead_letter_not_supported"])
        self.bundle("conformance", self.contract["requiredConformanceTests"])
        for name, spec in self.contract["transports"].items():
            self.bundle(name, [name])
            descriptor = {"name": spec["runtimeName"], "version": "1.0.0", "capabilities": {k[0].lower() + k[1:]: v for k, v in spec["descriptorCapabilities"].items()}}
            report = {"schemaVersion": 1, "transport": name, "descriptor": descriptor, "context": self.context, "overall": "pass", "scenarios": [{"scenario": s, "outcome": "Passed", "actualStatus": "Supported", "durationMs": 1} for s in self.policy["requiredScenarios"]]}
            self.write(self.results / name / (name + ".json"), report)
            if name != "InMemory":
                tests = list(spec["requiredAdapterTests"])
                for entry in list(spec["capabilities"].values()) + list(spec["settlements"].values()):
                    tests += [p.split(":")[1] for p in entry["evidence"] if p.startswith("adapter:")]
                minimum = VERIFIER.read_json(ROOT / spec["adapterContract"].replace("-contract.json", "-policy.json"))["minimumIntegrationTestCount"]
                tests += [f"Additional_executed_adapter_test_{i}" for i in range(minimum)]
                self.bundle(name + "-adapter", sorted(set(tests)))
            self.write(self.results / "packages" / (name + ".json"), {"context": self.context, "overall": "pass", "packageOnly": True, "descriptor": descriptor, "version": VERIFIER.read_json(ROOT / "eng/release-manifest.json")["version"]})

    def write(self, path, value):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value), encoding="utf-8")

    def bundle(self, name, tests):
        folder = self.results / name
        self.write(folder / "context.json", self.context)
        root = ET.Element("TestRun")
        results = ET.SubElement(root, "Results")
        for test in tests:
            ET.SubElement(results, "UnitTestResult", testName="Synthetic." + test, outcome="Passed")
        ET.ElementTree(root).write(folder / "tests.trx", encoding="utf-8")

    def mutate(self, relative, action):
        path = self.results / relative
        obj = VERIFIER.read_json(path)
        action(obj)
        self.write(path, obj)

    def verify(self):
        return VERIFIER.verify(self.results, self.output, self.context)

    def test_complete_evidence_generates_both_reports(self):
        matrix = self.verify()
        self.assertEqual("pass", matrix["overall"])
        self.assertEqual(4, len(matrix["transports"]))
        self.assertTrue((self.output / "MESSAGING_COMPATIBILITY_SUMMARY.md").is_file())

    def test_missing_transport_is_rejected(self):
        (self.results / "Kafka/Kafka.json").unlink()
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_stale_commit_is_rejected(self):
        self.mutate("Kafka/Kafka.json", lambda r: r["context"].update(sourceSha="c" * 40))
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_same_commit_changed_source_is_rejected(self):
        self.mutate("Kafka/Kafka.json", lambda r: r["context"].update(sourceDigest="d" * 64))
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_different_run_attempt_is_rejected(self):
        self.mutate("RabbitMQ-adapter/context.json", lambda r: r.update(runAttempt="2"))
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_descriptor_drift_is_rejected(self):
        self.mutate("Kafka/Kafka.json", lambda r: r["descriptor"]["capabilities"].update(supportsScheduling=True))
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_blocked_supported_scenario_is_rejected(self):
        self.mutate("Kafka/Kafka.json", lambda r: r["scenarios"][0].update(actualStatus="Blocked"))
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_missing_inbox_evidence_is_rejected(self):
        self.mutate("InMemory/InMemory.json", lambda r: r.update(scenarios=[s for s in r["scenarios"] if s["scenario"] != "inbox-order"]))
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_duplicate_scenario_is_rejected(self):
        self.mutate("Kafka/Kafka.json", lambda r: r["scenarios"].append(r["scenarios"][0]))
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_failed_adapter_results_are_rejected(self):
        path = self.results / "Kafka-adapter/tests.trx"
        path.write_text(path.read_text().replace('outcome="Passed"', 'outcome="Failed"', 1))
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_skipped_adapter_results_are_rejected(self):
        path = self.results / "Kafka-adapter/tests.trx"
        path.write_text(path.read_text().replace('outcome="Passed"', 'outcome="NotExecuted"', 1))
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_missing_capability_named_test_is_rejected(self):
        path = self.results / "AzureServiceBus-adapter/tests.trx"
        path.write_text(path.read_text().replace("Scheduled_delivery_is_not_available_early_and_preserves_stable_id", "Unrelated_test"))
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_empty_trx_is_not_evidence(self):
        (self.results / "Kafka-adapter/tests.trx").write_text("<TestRun />")
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_malformed_policy_json_is_rejected(self):
        path = self.root / "malformed.json"
        path.write_text("[]")
        with self.assertRaises(VERIFIER.VerificationError): VERIFIER.read_json(path)

    def test_sensitive_marker_in_any_uploaded_file_fails(self):
        (self.results / "diagnostic.bin").write_bytes(b"tcj-compat-secret-payload")
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_credential_in_trx_fails_scan(self):
        (self.results / "diagnostic.trx").write_text("SharedAccessKey=not-a-real-credential;")
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_missing_package_evidence_is_rejected(self):
        (self.results / "packages/Kafka.json").unlink()
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_package_descriptor_mismatch_is_rejected(self):
        self.mutate("packages/Kafka.json", lambda r: r["descriptor"].update(version="old"))
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_wrong_candidate_package_version_is_rejected(self):
        self.mutate("packages/Kafka.json", lambda r: r.update(version="0.0.0-old"))
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_named_proofs_do_not_replace_minimum_adapter_execution(self):
        path = self.results / "RabbitMQ-adapter/tests.trx"
        tree = ET.parse(path)
        results = tree.getroot().find("Results")
        for node in list(results):
            if "Additional_executed_adapter_test_" in node.get("testName", ""):
                results.remove(node)
        tree.write(path, encoding="utf-8")
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_generated_schema_drift_is_rejected(self):
        self.mutate("Kafka/Kafka.json", lambda r: r.update(schemaVersion=2))
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_output_scan_rejects_preexisting_sensitive_artifact(self):
        self.output.mkdir()
        (self.output / "unsafe.log").write_text("tcj-compat-secret-output")
        with self.assertRaises(VERIFIER.VerificationError): self.verify()

    def test_matrix_json_without_executed_test_is_rejected(self):
        (self.results / "Kafka/tests.trx").unlink()
        with self.assertRaises(VERIFIER.VerificationError): self.verify()


class MessagingCompatibilityConfigurationTests(unittest.TestCase):
    def check_mutation(self, path, change):
        read = VERIFIER.read_json
        replacement = copy.deepcopy(read(ROOT / path))
        change(replacement)
        def amended(candidate):
            return replacement if Path(candidate) == ROOT / path else read(candidate)
        with patch.object(VERIFIER, "read_json", side_effect=amended):
            with self.assertRaises(VERIFIER.VerificationError): VERIFIER.validate_config(run_adapters=False)

    def test_real_configuration(self):
        VERIFIER.validate_config(run_adapters=False)

    def test_required_flag_cannot_be_disabled(self):
        self.check_mutation(VERIFIER.POLICY, lambda p: p.update(requireInboxScenarios=False))

    def test_missing_transport(self):
        self.check_mutation(VERIFIER.CONTRACT, lambda p: p["transports"].pop("Kafka"))

    def test_adapter_contract_mismatch(self):
        self.check_mutation(VERIFIER.CONTRACT, lambda p: p["transports"]["Kafka"]["descriptorCapabilities"].update(SupportsScheduling=True))

    def test_unsupported_cannot_be_reported_supported(self):
        self.check_mutation(VERIFIER.CONTRACT, lambda p: p["transports"]["Kafka"]["capabilities"]["Scheduling"].update(status="Supported"))

    def test_missing_ordering_evidence(self):
        self.check_mutation(VERIFIER.CONTRACT, lambda p: p["transports"]["Kafka"]["capabilities"]["OrderedDelivery"].update(evidence=[]))

    def test_overstated_ordering_scope(self):
        self.check_mutation(VERIFIER.CONTRACT, lambda p: p["transports"]["RabbitMQ"].update(orderingScope="PerPartition"))

    def test_neutral_failure_category_drift(self):
        self.check_mutation(VERIFIER.CONTRACT, lambda p: p["failureCategories"].remove("PermanentTopology"))


if __name__ == "__main__":
    unittest.main()
