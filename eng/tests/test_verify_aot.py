from __future__ import annotations

import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-aot.py"
SPEC = importlib.util.spec_from_file_location("verify_aot", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class AotVerifierTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.output = self.root / "artifacts/aot/aot-verification.json"
        self._write_valid_repository()

    def tearDown(self) -> None:
        self.temp.cleanup()

    def test_success_writes_deterministic_machine_readable_baseline(self) -> None:
        first, first_success = MODULE.verify_repository(self.root, output_path=self.output)
        first_bytes = self.output.read_bytes()
        second, second_success = MODULE.verify_repository(self.root, output_path=self.output)
        second_bytes = self.output.read_bytes()

        self.assertTrue(first_success)
        self.assertTrue(second_success)
        self.assertEqual(first, second)
        self.assertEqual(first_bytes, second_bytes)
        self.assertEqual("passed", first["status"])
        self.assertEqual([], first["findings"])
        self.assertEqual(5, len(first["packages"]))
        self.assertEqual(
            sorted(package["packageId"] for package in first["packages"]),
            [package["packageId"] for package in first["packages"]],
        )

    def test_cli_returns_zero_for_success_and_nonzero_for_violation(self) -> None:
        passed = subprocess.run(
            [sys.executable, str(MODULE_PATH), "verify", "--root", str(self.root)],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(0, passed.returncode, passed.stderr)

        policy = self._read_policy()
        policy["packages"][0]["tier"] = "Mostly"
        self._write_policy(policy)
        failed = subprocess.run(
            [sys.executable, str(MODULE_PATH), "verify", "--root", str(self.root)],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertNotEqual(0, failed.returncode)
        self.assertIn("AOT001", failed.stderr)

    def test_missing_policy_fails_closed_and_writes_report(self) -> None:
        (self.root / "eng/aot-policy.json").unlink()

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        self.assertEqual("failed", payload["status"])
        self.assertEqual("AOT001", payload["findings"][0]["rule"])
        self.assertIn("Missing AOT policy", payload["findings"][0]["message"])
        self.assertTrue(self.output.is_file())

    def test_duplicate_package_fails_closed(self) -> None:
        policy = self._read_policy()
        policy["packages"].append(dict(policy["packages"][0]))
        self._write_policy(policy)

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        self.assertIn("appears more than once", payload["findings"][0]["message"])

    def test_unknown_support_tier_fails_closed(self) -> None:
        policy = self._read_policy()
        policy["packages"][0]["tier"] = "Mostly"
        self._write_policy(policy)

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        self.assertIn("invalid support tier 'Mostly'", payload["findings"][0]["message"])

    def test_full_package_explicitly_disabling_is_aot_compatible_fails(self) -> None:
        policy = self._read_policy()
        package = next(item for item in policy["packages"] if item["packageId"] == "TCJ.Core")
        package["tier"] = "Full"
        package["fullSupportEvidence"] = [self._full_evidence()]
        self._write_policy(policy)
        self._write_evidence_files()
        core_project = self.root / "src/TCJ.Core/TCJ.Core.csproj"
        core_project.write_text(
            core_project.read_text(encoding="utf-8").replace(
                "<IsAotCompatible>true</IsAotCompatible>",
                "<IsAotCompatible>false</IsAotCompatible>",
            ),
            encoding="utf-8",
        )

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        finding = next(item for item in payload["findings"] if item["rule"] == "AOT003")
        self.assertEqual("TCJ.Core", finding["package"])
        self.assertEqual("src/TCJ.Core/TCJ.Core.csproj", finding["project"])
        self.assertEqual("IsAotCompatible", finding["property"])
        self.assertEqual("false", finding["value"])

    def test_broad_trim_warning_suppression_fails_for_each_affected_package(self) -> None:
        path = self.root / "Directory.Build.props"
        path.write_text(
            '<Project><PropertyGroup><NoWarn>$(NoWarn);IL2*</NoWarn></PropertyGroup></Project>',
            encoding="utf-8",
        )

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        findings = [item for item in payload["findings"] if item["rule"] == "AOT004"]
        self.assertEqual(5, len(findings))
        self.assertTrue(all(item["project"] == "Directory.Build.props" for item in findings))
        self.assertTrue(all(item["property"] == "NoWarn" for item in findings))

    def test_exact_suppression_must_be_explicitly_allowed(self) -> None:
        core_project = self.root / "src/TCJ.Core/TCJ.Core.csproj"
        text = core_project.read_text(encoding="utf-8").replace(
            "</PropertyGroup>", "<NoWarn>$(NoWarn);IL2026</NoWarn></PropertyGroup>"
        )
        core_project.write_text(text, encoding="utf-8")

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)
        self.assertFalse(success)
        self.assertTrue(any(item["rule"] == "AOT005" for item in payload["findings"]))

        policy = self._read_policy()
        policy["warningPolicy"]["suppressions"]["allowed"] = [
            {
                "packageId": "TCJ.Core",
                "project": "src/TCJ.Core/TCJ.Core.csproj",
                "property": "NoWarn",
                "diagnostic": "IL2026",
                "reason": "Narrow test fixture suppression with an explicit reviewed reason.",
            }
        ]
        self._write_policy(policy)

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)
        self.assertTrue(success)
        self.assertEqual([], payload["findings"])

    def test_core_analyzer_fixture_must_remain_package_only_and_aot_analyzed(self) -> None:
        fixture = self.root / "compatibility/Consumers/Core.Console/Core.Console.csproj"
        fixture.write_text(
            """<Project Sdk=\"Microsoft.NET.Sdk\">
  <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>
  <ItemGroup><ProjectReference Include=\"../../../src/TCJ.Core/TCJ.Core.csproj\" /></ItemGroup>
</Project>
""",
            encoding="utf-8",
        )

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        findings = [item for item in payload["findings"] if item["rule"] == "AOT006"]
        self.assertTrue(any(item["property"] == "IsAotCompatible" for item in findings))
        self.assertTrue(any(item["property"] == "PackageReference" for item in findings))
        self.assertTrue(any(item["property"] == "ProjectReference" for item in findings))

    def test_core_analyzer_fixture_must_stay_compile_only(self) -> None:
        fixture = self.root / "compatibility/Consumers/Core.Console/Core.Console.csproj"
        text = fixture.read_text(encoding="utf-8").replace(
            "<IsAotCompatible>true</IsAotCompatible>",
            "<IsAotCompatible>true</IsAotCompatible><PublishAot>true</PublishAot>",
        )
        fixture.write_text(text, encoding="utf-8")

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        finding = next(item for item in payload["findings"] if item["property"] == "PublishAot")
        self.assertEqual("AOT006", finding["rule"])

    def test_dependency_injection_analyzer_fixture_requires_package_aot_contract_and_expected_package_closure(self) -> None:
        project = self.root / "src/TCJ.DependencyInjection/TCJ.DependencyInjection.csproj"
        project.write_text(
            project.read_text(encoding="utf-8").replace(
                "<IsAotCompatible>true</IsAotCompatible>",
                "<IsAotCompatible>false</IsAotCompatible>",
            ),
            encoding="utf-8",
        )
        fixture = self.root / "compatibility/Consumers/DependencyInjection.AotSafe.Console/DependencyInjection.AotSafe.Console.csproj"
        fixture.write_text(
            fixture.read_text(encoding="utf-8").replace(
                '<PackageReference Include="TCJ.Core" Version="$(TCJCompatibilityVersion)" />',
                '',
            ),
            encoding="utf-8",
        )

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        findings = [
            item for item in payload["findings"]
            if item["rule"] == "AOT006" and item["package"] == "TCJ.DependencyInjection"
        ]
        self.assertTrue(any(item["project"] == "src/TCJ.DependencyInjection/TCJ.DependencyInjection.csproj" and item["property"] == "IsAotCompatible" for item in findings))
        self.assertTrue(any(item["property"] == "PackageReference" for item in findings))


    def test_aspnetcore_analyzer_fixture_requires_package_aot_contract_and_expected_package_closure(self) -> None:
        project = self.root / "src/TCJ.AspNetCore/TCJ.AspNetCore.csproj"
        project.write_text(
            project.read_text(encoding="utf-8").replace(
                "<IsAotCompatible>true</IsAotCompatible>",
                "<IsAotCompatible>false</IsAotCompatible>",
            ),
            encoding="utf-8",
        )
        fixture = self.root / "compatibility/Consumers/AspNetCore.MinimalApi/AspNetCore.MinimalApi.csproj"
        fixture.write_text(
            fixture.read_text(encoding="utf-8").replace(
                '<PackageReference Include="TCJ.AspNetCore" Version="$(TCJCompatibilityVersion)" />',
                '',
            ),
            encoding="utf-8",
        )

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        findings = [
            item for item in payload["findings"]
            if item["rule"] == "AOT006" and item["package"] == "TCJ.AspNetCore"
        ]
        self.assertTrue(any(item["project"] == "src/TCJ.AspNetCore/TCJ.AspNetCore.csproj" and item["property"] == "IsAotCompatible" for item in findings))
        self.assertTrue(any(item["property"] == "PackageReference" for item in findings))

    def test_current_repository_does_not_wire_aot_verification_into_blocking_workflows(self) -> None:
        repository_root = MODULE.ROOT
        commands = ("eng/verify-aot.py", "eng/verify-aot-policy.py")
        for name in ("ci.yml", "release-preflight.yml", "release.yml"):
            workflow = repository_root / ".github/workflows" / name
            text = workflow.read_text(encoding="utf-8")
            for command in commands:
                self.assertNotIn(command, text, f"{name} must stay non-blocking until Important 8")

    def _read_policy(self) -> dict:
        return json.loads((self.root / "eng/aot-policy.json").read_text(encoding="utf-8"))

    def _write_policy(self, policy: dict) -> None:
        (self.root / "eng/aot-policy.json").write_text(
            json.dumps(policy, indent=2) + "\n", encoding="utf-8"
        )

    @staticmethod
    def _full_evidence() -> dict:
        return {
            "scenario": "fixture",
            "consumerProject": "compatibility/Consumer/Consumer.csproj",
            "workflow": ".github/workflows/aot-fixture.yml",
            "consumerSource": "PackedNuGet",
            "usesProjectReference": False,
            "publishAot": True,
            "publishSucceeded": True,
            "publishedBinaryExecuted": True,
            "tcjTrimWarningCount": 0,
            "tcjAotWarningCount": 0,
        }

    def _write_evidence_files(self) -> None:
        consumer = self.root / "compatibility/Consumer/Consumer.csproj"
        consumer.parent.mkdir(parents=True, exist_ok=True)
        consumer.write_text("<Project />", encoding="utf-8")
        workflow = self.root / ".github/workflows/aot-fixture.yml"
        workflow.parent.mkdir(parents=True, exist_ok=True)
        workflow.write_text("name: fixture\n", encoding="utf-8")

    def _write_valid_repository(self) -> None:
        source_root = MODULE.ROOT
        for relative in (
            "eng/aot-policy.json",
            "eng/release-manifest.json",
            ".github/PULL_REQUEST_TEMPLATE.md",
            "docs/guides/native-aot-and-trimming.md",
            "docs/toc.yml",
            "docs/README.md",
            "Directory.Build.props",
            "eng/DependencySecurity.props",
            "eng/Packaging.props",
            "eng/PackageValidation.props",
            "compatibility/Consumers/Core.Console/Core.Console.csproj",
            "compatibility/Consumers/DependencyInjection.AotSafe.Console/DependencyInjection.AotSafe.Console.csproj",
            "compatibility/Consumers/AspNetCore.MinimalApi/AspNetCore.MinimalApi.csproj",
        ):
            source = source_root / relative
            target = self.root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)

        for project in sorted((source_root / "src").glob("*/*.csproj")):
            target = self.root / project.relative_to(source_root)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(project, target)


if __name__ == "__main__":
    unittest.main()
