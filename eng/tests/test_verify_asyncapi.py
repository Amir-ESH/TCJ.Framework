from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

ENG = Path(__file__).resolve().parents[1]
if str(ENG) not in sys.path:
    sys.path.insert(0, str(ENG))

MODULE_PATH = ENG / "verify-asyncapi.py"
SPEC = importlib.util.spec_from_file_location("verify_asyncapi", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class AsyncApiFoundationVerifierTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / "eng").mkdir(parents=True)
        (self.root / "src/TCJ.Messaging.AsyncApi").mkdir(parents=True)
        (self.root / "eng/TCJ.AsyncApi.Tool").mkdir(parents=True)
        (self.root / "eng/tests").mkdir(parents=True, exist_ok=True)
        for relative in (
            "src/TCJ.Messaging.AsyncApi/TCJ.Messaging.AsyncApi.csproj",
            "eng/TCJ.AsyncApi.Tool/TCJ.AsyncApi.Tool.csproj",
            "eng/tests/test_verify_asyncapi.py",
        ):
            (self.root / relative).write_text("placeholder", encoding="utf-8")
        self.policy_path = self.root / "eng/asyncapi-policy.json"
        self.governance_path = self.root / "eng/asyncapi-governance-contract.json"
        self.catalog_schema_path = self.root / "eng/messaging-catalog-input.schema.json"
        self.policy_path.write_text((ENG / "asyncapi-policy.json").read_text(encoding="utf-8"), encoding="utf-8")
        self.governance_path.write_text((ENG / "asyncapi-governance-contract.json").read_text(encoding="utf-8"), encoding="utf-8")
        self.catalog_schema_path.write_text((ENG / "messaging-catalog-input.schema.json").read_text(encoding="utf-8"), encoding="utf-8")

    def tearDown(self) -> None:
        self.temp.cleanup()

    def test_valid_foundation_configuration_passes(self) -> None:
        policy, governance = MODULE.validate_configuration(self.root, self.policy_path, self.governance_path)
        self.assertEqual("3.1.0", policy["asyncApiSpecificationVersion"])
        self.assertEqual("1.0", governance["governanceVersion"])

    def test_asyncapi_version_must_be_pinned(self) -> None:
        policy = self._read(self.policy_path)
        policy["asyncApiSpecificationVersion"] = "3.0.0"
        self._write(self.policy_path, policy)
        with self.assertRaisesRegex(MODULE.AsyncApiPolicyError, "exactly 3.1.0"):
            MODULE.validate_configuration(self.root, self.policy_path, self.governance_path)

    def test_canonical_format_must_be_json(self) -> None:
        policy = self._read(self.policy_path)
        policy["canonicalOutput"]["format"] = "YAML"
        self._write(self.policy_path, policy)
        with self.assertRaisesRegex(MODULE.AsyncApiPolicyError, "must be JSON"):
            MODULE.validate_configuration(self.root, self.policy_path, self.governance_path)

    def test_remote_references_must_remain_disallowed(self) -> None:
        policy = self._read(self.policy_path)
        policy["remoteReferences"]["allowed"] = True
        self._write(self.policy_path, policy)
        with self.assertRaisesRegex(MODULE.AsyncApiPolicyError, "Remote references"):
            MODULE.validate_configuration(self.root, self.policy_path, self.governance_path)

    def test_determinism_must_be_required(self) -> None:
        policy = self._read(self.policy_path)
        policy["deterministicOutputRequired"] = False
        self._write(self.policy_path, policy)
        with self.assertRaisesRegex(MODULE.AsyncApiPolicyError, "Deterministic output"):
            MODULE.validate_configuration(self.root, self.policy_path, self.governance_path)

    def test_step52_local_references_must_remain_root_constrained(self) -> None:
        policy = self._read(self.policy_path)
        policy["step52ContractArtifacts"]["localReferences"]["parentTraversalAllowed"] = True
        self._write(self.policy_path, policy)
        with self.assertRaisesRegex(MODULE.AsyncApiPolicyError, "parent traversal"):
            MODULE.validate_configuration(self.root, self.policy_path, self.governance_path)

    def test_step52_fingerprint_validation_must_reuse_governed_implementation(self) -> None:
        policy = self._read(self.policy_path)
        policy["step52ContractArtifacts"]["fingerprintValidation"] = "custom"
        self._write(self.policy_path, policy)
        with self.assertRaisesRegex(MODULE.AsyncApiPolicyError, "MessageContractSchemaGenerator"):
            MODULE.validate_configuration(self.root, self.policy_path, self.governance_path)

    def test_policy_and_governance_versions_must_agree(self) -> None:
        governance = self._read(self.governance_path)
        governance["asyncApiSpecificationVersion"] = "3.0.0"
        self._write(self.governance_path, governance)
        with self.assertRaisesRegex(MODULE.AsyncApiPolicyError, "must agree"):
            MODULE.validate_configuration(self.root, self.policy_path, self.governance_path)

    def test_governance_changes_must_be_compatibility_sensitive(self) -> None:
        governance = self._read(self.governance_path)
        governance["compatibilitySensitive"] = False
        self._write(self.governance_path, governance)
        with self.assertRaisesRegex(MODULE.AsyncApiPolicyError, "compatibility-sensitive"):
            MODULE.validate_configuration(self.root, self.policy_path, self.governance_path)

    def test_step52_contract_identity_must_remain_logical_type_and_version(self) -> None:
        governance = self._read(self.governance_path)
        governance["step52ContractIntegration"]["contractIdentity"] = "ClrType"
        self._write(self.governance_path, governance)
        with self.assertRaisesRegex(MODULE.AsyncApiPolicyError, "governed-contract integration semantics"):
            MODULE.validate_configuration(self.root, self.policy_path, self.governance_path)

    def test_exactly_once_delivery_semantics_is_rejected(self) -> None:
        governance = self._read(self.governance_path)
        governance["deliverySemantics"].append("ExactlyOnce")
        self._write(self.governance_path, governance)
        with self.assertRaisesRegex(MODULE.AsyncApiPolicyError, "deliverySemantics must be exactly"):
            MODULE.validate_configuration(self.root, self.policy_path, self.governance_path)


    def test_messaging_catalog_policy_and_governance_versions_must_be_one(self) -> None:
        policy = self._read(self.policy_path)
        policy["versions"]["messagingCatalogInputSchema"] = 2
        self._write(self.policy_path, policy)
        with self.assertRaisesRegex(MODULE.AsyncApiPolicyError, "messagingCatalogInputSchema must be 1"):
            MODULE.validate_configuration(self.root, self.policy_path, self.governance_path, self.catalog_schema_path)

    def test_messaging_catalog_schema_version_is_pinned(self) -> None:
        schema = self._read(self.catalog_schema_path)
        schema["properties"]["schemaVersion"] = {"const": 2}
        self._write(self.catalog_schema_path, schema)
        with self.assertRaisesRegex(MODULE.AsyncApiPolicyError, "schema version must be exactly 1"):
            MODULE.validate_configuration(self.root, self.policy_path, self.governance_path, self.catalog_schema_path)

    def test_messaging_catalog_schema_root_must_be_closed(self) -> None:
        schema = self._read(self.catalog_schema_path)
        schema["additionalProperties"] = True
        self._write(self.catalog_schema_path, schema)
        with self.assertRaisesRegex(MODULE.AsyncApiPolicyError, "root must be a closed object"):
            MODULE.validate_configuration(self.root, self.policy_path, self.governance_path, self.catalog_schema_path)

    def test_configured_paths_must_exist(self) -> None:
        (self.root / "eng/TCJ.AsyncApi.Tool/TCJ.AsyncApi.Tool.csproj").unlink()
        with self.assertRaisesRegex(MODULE.AsyncApiPolicyError, "Configured path does not exist"):
            MODULE.validate_configuration(self.root, self.policy_path, self.governance_path)

    @staticmethod
    def _read(path: Path) -> dict:
        return json.loads(path.read_text(encoding="utf-8"))

    @staticmethod
    def _write(path: Path, value: dict) -> None:
        path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
