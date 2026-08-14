from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-aspnetcore-integration.py"
SPEC = importlib.util.spec_from_file_location("verify_aspnetcore_integration", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class AspNetCoreIntegrationVerifierTests(unittest.TestCase):
    def test_missing_policy_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaises(MODULE.AspNetCoreIntegrationError):
                MODULE.load_policy(Path(temporary) / "missing.json")

    def test_malformed_policy_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "policy.json"
            path.write_text("{bad-json", encoding="utf-8")
            with self.assertRaises(MODULE.AspNetCoreIntegrationError):
                MODULE.load_policy(path)

    def test_policy_rejects_minimum_below_fifteen(self) -> None:
        data = {
            "schemaVersion": 1,
            "testProject": "tests/project.csproj",
            "minimumTestCount": 14,
            "requiredCategories": ["Integration"],
            "requireLinux": True,
            "requireWindows": True,
            "requireAuthenticatedRequestTests": True,
            "requireAnonymousRequestTests": True,
            "requireProductionEnvironmentTests": True,
            "requireDevelopmentEnvironmentTests": True,
            "collectHostDiagnosticsOnFailure": True,
            "scanUploadedDiagnosticsForSecrets": True,
        }
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "policy.json"
            path.write_text(json.dumps(data), encoding="utf-8")
            with self.assertRaises(MODULE.AspNetCoreIntegrationError):
                MODULE.load_policy(path)

    def test_secret_scan_detects_authorization_bearer_cookie_and_password(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "host.log"
            path.write_text(
                "Authorization: abc\nBearer ey.secret.token\nCookie: auth=abc\nPassword=Secret123\n",
                encoding="utf-8",
            )
            leaks = MODULE.scan_for_secrets([path])
            self.assertGreaterEqual(len(leaks), 4)

    def test_redacted_values_are_allowed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "host.log"
            path.write_text(
                "Authorization: <redacted>\nBearer <redacted>\nCookie: <redacted>\nPassword=<redacted>\n",
                encoding="utf-8",
            )
            self.assertEqual([], MODULE.scan_for_secrets([path]))

    def test_sanitizer_redacts_generated_diagnostics(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "host.log"
            path.write_text("Authorization: abc\nCookie: auth=abc\nPassword=Secret\n", encoding="utf-8")
            MODULE.sanitize_generated_file(path)
            text = path.read_text(encoding="utf-8")
            self.assertNotIn("auth=abc", text)
            self.assertNotIn("Secret", text)
            self.assertIn("<redacted>", text)

    def test_native_aot_smoke_configuration_is_valid(self) -> None:
        MODULE.validate_native_aot_smoke()

    def test_parse_trx_rejects_missing_results(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaises(MODULE.AspNetCoreIntegrationError):
                MODULE.parse_trx_files(Path(temporary))

    def test_verify_platforms_requires_linux_and_windows(self) -> None:
        policy = MODULE.Policy(
            path=Path("policy.json"), test_project="tests/project.csproj", minimum_test_count=15,
            required_categories=("Integration",), require_linux=True, require_windows=True,
            require_authenticated_request_tests=True, require_anonymous_request_tests=True,
            require_production_environment_tests=True, require_development_environment_tests=True,
            collect_host_diagnostics_on_failure=True, scan_uploaded_diagnostics_for_secrets=True,
        )
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "linux").mkdir()
            (root / "linux/aspnetcore-integration-summary.json").write_text(
                json.dumps({"sourceCommitSha": "abc", "operatingSystem": "Linux", "overallStatus": "PASS"}),
                encoding="utf-8",
            )
            with mock.patch.object(MODULE, "validate_config", return_value=policy):
                with self.assertRaisesRegex(MODULE.AspNetCoreIntegrationError, "Windows"):
                    MODULE.verify_platforms(root, root / "out")


if __name__ == "__main__":
    unittest.main()
