from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-sqlserver-integration.py"
SPEC = importlib.util.spec_from_file_location("verify_sqlserver_integration", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class SqlServerIntegrationVerifierTests(unittest.TestCase):
    def test_floating_image_tags_are_rejected(self) -> None:
        self.assertTrue(MODULE.is_floating_image("mcr.microsoft.com/mssql/server:latest"))
        self.assertTrue(MODULE.is_floating_image("mcr.microsoft.com/mssql/server"))
        self.assertFalse(MODULE.is_floating_image("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04"))
        self.assertFalse(MODULE.is_floating_image("example/sql@sha256:" + "a" * 64))

    def test_missing_policy_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            missing = Path(temporary) / "missing.json"
            with self.assertRaises(MODULE.SqlServerIntegrationError):
                MODULE.load_policy(missing)

    def test_malformed_policy_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            malformed = Path(temporary) / "policy.json"
            malformed.write_text("{not-json", encoding="utf-8")
            with self.assertRaises(MODULE.SqlServerIntegrationError):
                MODULE.load_policy(malformed)

    def test_policy_requires_supported_schema_and_minimum_test_count(self) -> None:
        policy = {
            "schemaVersion": 1,
            "testProject": "tests/project.csproj",
            "containerImage": "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04",
            "minimumTestCount": 11,
            "startupTimeoutSeconds": 120,
            "commandTimeoutSeconds": 30,
            "collectContainerLogsOnFailure": True,
            "requirePinnedImage": True,
            "requireDockerHealthCheck": True,
            "allowExternalDatabase": False,
            "databaseIsolation": "database-per-test",
            "requiredCategories": ["Integration"],
        }
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "policy.json"
            path.write_text(json.dumps(policy), encoding="utf-8")
            with self.assertRaises(MODULE.SqlServerIntegrationError):
                MODULE.load_policy(path)

    def test_credential_scan_detects_passwords_and_allows_redaction(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            clean = root / "clean.log"
            leaked = root / "leaked.log"
            clean.write_text("Password=<redacted>\n", encoding="utf-8")
            leaked.write_text("Password=Secret123!\n", encoding="utf-8")

            leaks = MODULE.scan_for_credentials([clean, leaked])

            self.assertEqual(1, len(leaks))
            self.assertIn("unredacted Password/Pwd value", leaks[0])

    def test_generated_runtime_password_pattern_is_detected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "result.trx"
            path.write_text("Tcj!aA1_0123456789ABCDEF0123456789ABCDEF", encoding="utf-8")

            leaks = MODULE.scan_for_credentials([path])

            self.assertEqual(1, len(leaks))
            self.assertIn("generated SQL Server password pattern", leaks[0])

    def test_sanitize_generated_file_redacts_before_artifact_upload(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "result.trx"
            path.write_text(
                "Server=db;Password=Secret123!;Value=Tcj!aA1_0123456789ABCDEF0123456789ABCDEF",
                encoding="utf-8",
            )

            MODULE.sanitize_generated_file(path)

            sanitized = path.read_text(encoding="utf-8")
            self.assertNotIn("Secret123!", sanitized)
            self.assertNotIn("Tcj!aA1_", sanitized)
            self.assertIn("Password=<redacted>", sanitized)


if __name__ == "__main__":
    unittest.main()
