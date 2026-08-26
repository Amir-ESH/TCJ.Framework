from __future__ import annotations

import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ENG = Path(__file__).resolve().parents[1]
if str(ENG) not in sys.path:
    sys.path.insert(0, str(ENG))

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-aot.py"
SPEC = importlib.util.spec_from_file_location("verify_aot", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)

RUNNER_PATH = ENG / "run-native-aot-smoke.py"
RUNNER_SPEC = importlib.util.spec_from_file_location("run_native_aot_smoke", RUNNER_PATH)
assert RUNNER_SPEC and RUNNER_SPEC.loader
RUNNER = importlib.util.module_from_spec(RUNNER_SPEC)
sys.modules[RUNNER_SPEC.name] = RUNNER
RUNNER_SPEC.loader.exec_module(RUNNER)


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

    def test_ef_nativeaot_fixture_requires_compiled_model_and_query_precompile_prerequisites(self) -> None:
        fixture = self.root / MODULE.EF_NATIVEAOT_FIXTURE
        text = fixture.read_text(encoding="utf-8")
        text = text.replace("<EFOptimizeContext>true</EFOptimizeContext>", "")
        text = text.replace(";Microsoft.EntityFrameworkCore.GeneratedInterceptors", "")
        text = text.replace("<PackageReference Include=\"Microsoft.EntityFrameworkCore.Tasks\">", "<PackageReference Include=\"Microsoft.EntityFrameworkCore.Tasks.Missing\">")
        fixture.write_text(text, encoding="utf-8")

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        findings = [item for item in payload["findings"] if item["rule"] == "AOT007"]
        self.assertTrue(any(item["property"] == "EFOptimizeContext" for item in findings))
        self.assertTrue(any(item["property"] == "InterceptorsNamespaces" for item in findings))
        self.assertTrue(any(item["property"] == "Microsoft.EntityFrameworkCore.Tasks" for item in findings))
        self.assertTrue(any("compiled model" in item["message"].lower() for item in findings))

    def test_ef_nativeaot_fixture_requires_generated_strong_id_static_registration_path(self) -> None:
        fixture = self.root / MODULE.EF_NATIVEAOT_FIXTURE
        fixture.write_text(
            fixture.read_text(encoding="utf-8").replace(
                r'    <ProjectReference Include="..\..\src\TCJ.Generators\TCJ.Generators.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" GlobalPropertiesToRemove="TreatWarningsAsErrors;WarningsNotAsErrors" />' + "\n",
                '',
            ),
            encoding="utf-8",
        )
        program = self.root / MODULE.EF_NATIVEAOT_PROGRAM
        program.write_text(
            program.read_text(encoding="utf-8").replace("        modelBuilder.ApplyStrongIdConversions(strongIds);\n", ""),
            encoding="utf-8",
        )

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        findings = [item for item in payload["findings"] if item["rule"] == "AOT007"]
        self.assertTrue(any(item["property"] == "ProjectReference" for item in findings))
        self.assertTrue(any(item["property"] == "Program.cs" and item["value"] == "ApplyStrongIdConversions(" for item in findings))

    def test_ef_nativeaot_fixture_requires_generator_to_remain_analyzer_only(self) -> None:
        fixture = self.root / MODULE.EF_NATIVEAOT_FIXTURE
        fixture.write_text(
            fixture.read_text(encoding="utf-8").replace(' OutputItemType="Analyzer" ReferenceOutputAssembly="false"', ''),
            encoding="utf-8",
        )

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        findings = [item for item in payload["findings"] if item["rule"] == "AOT007"]
        self.assertTrue(any(item["property"] == "TCJ.Generators.OutputItemType" for item in findings))
        self.assertTrue(any(item["property"] == "TCJ.Generators.ReferenceOutputAssembly" for item in findings))

    def test_ef_nativeaot_fixture_rejects_restricted_runtime_discovery_paths(self) -> None:
        program = self.root / MODULE.EF_NATIVEAOT_PROGRAM
        program.write_text(
            program.read_text(encoding="utf-8") + "\n// RegisterAllEntities<Fake>();\n",
            encoding="utf-8",
        )

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        finding = next(
            item for item in payload["findings"]
            if item["rule"] == "AOT007" and item["value"] == "RegisterAllEntities<"
        )
        self.assertIn("documented static path", finding["message"])

    def test_ef_nativeaot_fixture_rejects_dbcontext_method_parameter_query_root(self) -> None:
        program = self.root / MODULE.EF_NATIVEAOT_PROGRAM
        text = program.read_text(encoding="utf-8")
        text = text.replace(
            'public static async Task Main(string[] args)',
            'private static Task LoadNamesAsync(ExperimentalNativeAotDbContext dbContext)'
        )
        program.write_text(text + "\n// LoadNamesAsync(ExperimentalNativeAotDbContext dbContext)\n", encoding="utf-8")

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        finding = next(
            item for item in payload["findings"]
            if item["rule"] == "AOT007" and item["value"] == "DbContext method parameter query root"
        )
        self.assertIn("local startup DbContext", finding["message"])

    def test_ef_nativeaot_fixture_rejects_compiled_model_unsupported_soft_delete_filter(self) -> None:
        program = self.root / MODULE.EF_NATIVEAOT_PROGRAM
        program.write_text(
            program.read_text(encoding="utf-8") + "\n// ApplySoftDeleteQueryFilters();\n",
            encoding="utf-8",
        )

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        finding = next(
            item for item in payload["findings"]
            if item["rule"] == "AOT007" and item["value"] == "ApplySoftDeleteQueryFilters("
        )
        self.assertIn("documented static path", finding["message"])

    def test_ef_nativeaot_fixture_rejects_static_query_lambda_modifiers(self) -> None:
        self._write_valid_repository()
        program = self.root / "tests/TCJ.EntityFrameworkCore.NativeAotExperimental/Program.cs"
        text = program.read_text(encoding="utf-8")
        text = text.replace(".Where(record =>", ".Where(static record =>", 1)
        program.write_text(text, encoding="utf-8")

        findings = MODULE._validate_ef_nativeaot_fixture(self.root)
        self.assertTrue(
            any(f.rule == "AOT007" and f.property == "Program.cs" and f.value == "static query lambda modifier" for f in findings),
            findings,
        )

    def test_packed_native_aot_smoke_requires_central_package_management_disabled(self) -> None:
        fixture = self.root / MODULE.PACKED_AOT_FIXTURE
        text = fixture.read_text(encoding="utf-8").replace(
            "    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n",
            "",
            1,
        )
        fixture.write_text(text, encoding="utf-8")

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        finding = next(
            item for item in payload["findings"]
            if item["rule"] == "AOT008"
            and item["property"] == "ManagePackageVersionsCentrally"
        )
        self.assertEqual("<missing>", finding["value"])
        self.assertIn("false", finding["message"])

    def test_packed_native_aot_smoke_requires_real_aspnetcore_assembly_witness(self) -> None:
        program = self.root / MODULE.PACKED_AOT_PROGRAM
        text = program.read_text(encoding="utf-8").replace(
            "typeof(TCJ.AspNetCore.Extensions.AspNetCoreServiceCollectionExtensions).Assembly",
            "typeof(TCJ.AspNetCore.Extensions.ServiceCollectionExtensions).Assembly",
            1,
        )
        program.write_text(text, encoding="utf-8")

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        finding = next(
            item for item in payload["findings"]
            if item["rule"] == "AOT008"
            and item["project"] == MODULE.PACKED_AOT_PROGRAM
            and item["value"] == "typeof(TCJ.AspNetCore.Extensions.AspNetCoreServiceCollectionExtensions).Assembly"
        )
        self.assertIn("required behavior", finding["message"])

    def test_packed_native_aot_workflows_require_ubuntu_native_toolchain(self) -> None:
        workflow = self.root / ".github/workflows/ci.yml"
        text = workflow.read_text(encoding="utf-8").replace(
            "          sudo apt-get install -y clang zlib1g-dev\n",
            "",
            1,
        )
        workflow.write_text(text, encoding="utf-8")

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        finding = next(
            item for item in payload["findings"]
            if item["rule"] == "AOT009"
            and item["project"] == ".github/workflows/ci.yml"
            and item["value"] == "sudo apt-get install -y clang zlib1g-dev"
        )
        self.assertIn("blocking packed Native AOT", finding["message"])

    def test_packed_native_aot_smoke_rejects_repository_project_references(self) -> None:
        fixture = self.root / MODULE.PACKED_AOT_FIXTURE
        text = fixture.read_text(encoding="utf-8").replace(
            "</Project>",
            '<ItemGroup><ProjectReference Include="../../src/TCJ.Core/TCJ.Core.csproj" /></ItemGroup></Project>',
        )
        fixture.write_text(text, encoding="utf-8")

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        finding = next(
            item for item in payload["findings"]
            if item["rule"] == "AOT008" and item["property"] == "ProjectReference"
        )
        self.assertIn("TCJ.Core.csproj", finding["value"])

    def test_full_support_tier_cannot_drift_from_packed_execution_evidence(self) -> None:
        policy = self._read_policy()
        core = next(item for item in policy["packages"] if item["packageId"] == "TCJ.Core")
        ef = next(item for item in policy["packages"] if item["packageId"] == "TCJ.EntityFrameworkCore")
        ef["tier"] = "Full"
        ef["fullSupportEvidence"] = [dict(core["fullSupportEvidence"][0])]
        self._write_policy(policy)

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        self.assertTrue(
            any(
                item["rule"] == "AOT008" and item["property"] == "PackageReference"
                for item in payload["findings"]
            )
        )
        self.assertTrue(
            any(
                item["rule"] == "AOT009" and item["property"] == "TCJ.EntityFrameworkCore"
                for item in payload["findings"]
            )
        )

    def test_current_repository_wires_blocking_packed_aot_gates_into_ci_and_release(self) -> None:
        repository_root = MODULE.ROOT
        commands = (
            "python3 eng/verify-aot.py verify",
            "python3 eng/run-native-aot-smoke.py",
            "python3 eng/verify-aot.py verify-result",
            "sudo apt-get install -y clang zlib1g-dev",
            "linux-x64",
        )
        for name in ("ci.yml", "release-preflight.yml", "release.yml"):
            workflow = repository_root / ".github/workflows" / name
            text = workflow.read_text(encoding="utf-8")
            for command in commands:
                self.assertIn(command, text, f"{name} must block on the Important 8 Native AOT gate")
            self.assertIn("id: native-aot-smoke", text)
            self.assertIn("steps.native-aot-smoke.outcome != 'skipped'", text)

    def test_native_aot_smoke_uses_normalized_runtime_packages_and_excludes_tooling(self) -> None:
        manifest = json.loads((MODULE.ROOT / "eng/release-manifest.json").read_text(encoding="utf-8"))
        runtime_packages = set(RUNNER.runtime_package_ids())
        tooling_packages = {item["id"] for item in manifest["releasePackages"]["tooling"]}
        smoke_packages = set(RUNNER.smoke_package_ids())

        self.assertTrue(tooling_packages)
        self.assertTrue(smoke_packages.issubset(runtime_packages))
        self.assertTrue(smoke_packages.isdisjoint(tooling_packages))
        policy = json.loads((MODULE.ROOT / "eng/aot-policy.json").read_text(encoding="utf-8"))
        full_packages = {item["packageId"] for item in policy["packages"] if item["tier"] == "Full"}
        self.assertEqual(full_packages, smoke_packages)

    def test_packed_native_aot_smoke_rejects_tooling_package_reference(self) -> None:
        manifest = json.loads((self.root / "eng/release-manifest.json").read_text(encoding="utf-8"))
        tooling_package = manifest["releasePackages"]["tooling"][0]["id"]
        fixture = self.root / MODULE.PACKED_AOT_FIXTURE
        text = fixture.read_text(encoding="utf-8").replace(
            "</Project>",
            f'<ItemGroup><PackageReference Include="{tooling_package}" Version="$(TCJNativeAotPackageVersion)" /></ItemGroup></Project>',
        )
        fixture.write_text(text, encoding="utf-8")

        payload, success = MODULE.verify_repository(self.root, output_path=self.output)

        self.assertFalse(success)
        finding = next(
            item for item in payload["findings"]
            if item["rule"] == "AOT008" and item["property"] == "RuntimePackageReference"
        )
        self.assertEqual(tooling_package, finding["value"])

    def test_runtime_result_empty_version_reports_version_flow_error_without_fake_package_names(self) -> None:
        result_path = self.root / MODULE.PACKED_AOT_RESULT
        package_directory = self.root / "artifacts/packages"
        package_directory.mkdir(parents=True, exist_ok=True)

        payload, success = MODULE.verify_runtime_result(
            root=self.root,
            expected_version="",
            result_path=result_path,
            package_directory=package_directory,
            output_path=self.root / "artifacts/aot/runtime-result.json",
        )

        self.assertFalse(success)
        properties = [item["property"] for item in payload["findings"]]
        self.assertIn("packageVersion", properties)
        self.assertIn("result", properties)
        self.assertNotIn("package", properties)

    def test_smoke_runner_writes_result_for_empty_version(self) -> None:
        output = self.root / "artifacts/aot/runner-empty-version"
        completed = subprocess.run(
            [
                sys.executable,
                str(RUNNER_PATH),
                "--version",
                "",
                "--output",
                str(output),
            ],
            cwd=MODULE.ROOT,
            capture_output=True,
            text=True,
            check=False,
        )

        self.assertNotEqual(0, completed.returncode)
        result_path = output / "native-aot-result.json"
        self.assertTrue(result_path.is_file())
        result = json.loads(result_path.read_text(encoding="utf-8"))
        self.assertEqual("", result["packageVersion"])
        self.assertEqual("failed", result["status"])
        self.assertIn("Package version must be non-empty", result["failure"])

    def test_runtime_result_accepts_exact_full_package_execution_evidence(self) -> None:
        version = "9.9.9-test"
        result_path, package_directory = self._write_runtime_result_fixture(version=version)

        payload, success = MODULE.verify_runtime_result(
            root=self.root,
            expected_version=version,
            result_path=result_path,
            package_directory=package_directory,
            output_path=self.root / "artifacts/aot/runtime-result.json",
        )

        self.assertTrue(success)
        self.assertEqual("passed", payload["status"])
        self.assertEqual(self._full_package_ids(), payload["fullPackages"])
        self.assertEqual([], payload["findings"])

    def test_runtime_result_rejects_intentionally_broken_loaded_package_version(self) -> None:
        version = "9.9.9-test"
        result_path, package_directory = self._write_runtime_result_fixture(version=version)
        result = json.loads(result_path.read_text(encoding="utf-8"))
        result["loadedPackageVersions"]["TCJ.AspNetCore"] = "9.9.8-broken"
        result_path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")

        payload, success = MODULE.verify_runtime_result(
            root=self.root,
            expected_version=version,
            result_path=result_path,
            package_directory=package_directory,
            output_path=self.root / "artifacts/aot/runtime-result.json",
        )

        self.assertFalse(success)
        self.assertEqual("failed", payload["status"])
        finding = next(item for item in payload["findings"] if item["property"] == "loadedPackageVersions")
        self.assertEqual("AOT100", finding["rule"])
        self.assertIn(version, finding["message"])

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
            "runtimeIdentifier": "linux-x64",
            "resultArtifact": "artifacts/aot/native-aot-smoke/native-aot-result.json",
            "consumerSource": "PackedNuGet",
            "usesProjectReference": False,
            "publishAot": True,
            "publishSucceeded": True,
            "publishedBinaryExecuted": True,
            "tcjTrimWarningCount": 0,
            "tcjAotWarningCount": 0,
        }

    def _full_package_ids(self) -> list[str]:
        policy = self._read_policy()
        return sorted(
            item["packageId"] for item in policy["packages"] if item["tier"] == "Full"
        )

    def _write_runtime_result_fixture(self, *, version: str) -> tuple[Path, Path]:
        package_directory = self.root / "artifacts/packages"
        package_directory.mkdir(parents=True, exist_ok=True)
        packages = self._full_package_ids()
        for package_id in packages:
            (package_directory / f"{package_id}.{version}.nupkg").write_bytes(b"fixture")

        result_path = self.root / MODULE.PACKED_AOT_RESULT
        result_path.parent.mkdir(parents=True, exist_ok=True)
        result = {
            "schemaVersion": 1,
            "status": "passed",
            "packageVersion": version,
            "runtimeIdentifier": "linux-x64",
            "consumerProject": MODULE.PACKED_AOT_FIXTURE,
            "consumerSource": "PackedNuGet",
            "usesProjectReference": False,
            "publishAot": True,
            "packageSourceStatus": "pass",
            "restoreStatus": "pass",
            "publishStatus": "pass",
            "executionStatus": "pass",
            "expectedPackages": packages,
            "resolvedPackages": {package_id: version for package_id in packages},
            "loadedPackageVersions": {package_id: version for package_id in packages},
            "trimWarnings": [],
            "aotWarnings": [],
            "tcjWarnings": [],
            "upstreamWarnings": [],
            "unexpectedAotWarningCount": 0,
            "warningCount": 0,
            "failure": None,
        }
        result_path.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        return result_path, package_directory

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
            ".github/workflows/ci.yml",
            ".github/workflows/release-preflight.yml",
            ".github/workflows/release.yml",
            "eng/run-native-aot-smoke.py",
            "smoke/NuGet.Config",
            "smoke/TCJ.NativeAot.SmokeTest/TCJ.NativeAot.SmokeTest.csproj",
            "smoke/TCJ.NativeAot.SmokeTest/Program.cs",
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
            "tests/TCJ.EntityFrameworkCore.NativeAotExperimental/TCJ.EntityFrameworkCore.NativeAotExperimental.csproj",
            "tests/TCJ.EntityFrameworkCore.NativeAotExperimental/Program.cs",
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
