from __future__ import annotations

import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ENG = Path(__file__).resolve().parents[1]
if str(ENG) not in sys.path:
    sys.path.insert(0, str(ENG))

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-architecture-policy.py"
SPEC = importlib.util.spec_from_file_location("verify_architecture_policy", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class ArchitecturePolicyVerifierTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.policy_path = self.root / "eng/architecture-policy.json"
        self._write_valid_repository()

    def tearDown(self) -> None:
        self.temp.cleanup()

    def test_valid_configuration_passes(self) -> None:
        policy = MODULE.validate_configuration(self.root, self.policy_path, check_git=False)
        self.assertEqual(set(MODULE.REQUIRED_ASSEMBLIES), set(policy.assemblies))

    def test_missing_policy_fails(self) -> None:
        self.policy_path.unlink()
        with self.assertRaisesRegex(MODULE.ArchitecturePolicyError, "Missing architecture policy"):
            MODULE.validate_configuration(self.root, self.policy_path, check_git=False)

    def test_malformed_policy_fails(self) -> None:
        self.policy_path.write_text("{not-json", encoding="utf-8")
        with self.assertRaisesRegex(MODULE.ArchitecturePolicyError, "Invalid JSON"):
            MODULE.validate_configuration(self.root, self.policy_path, check_git=False)

    def test_unknown_assembly_fails(self) -> None:
        policy = self._read_policy()
        policy["assemblies"]["TCJ.Unknown"] = []
        self._write_policy(policy)
        with self.assertRaisesRegex(MODULE.ArchitecturePolicyError, "unknown: TCJ.Unknown"):
            MODULE.validate_configuration(self.root, self.policy_path, check_git=False)

    def test_unknown_allowed_dependency_fails(self) -> None:
        policy = self._read_policy()
        policy["assemblies"]["TCJ.Core"] = ["TCJ.Unknown"]
        self._write_policy(policy)
        with self.assertRaisesRegex(MODULE.ArchitecturePolicyError, "unknown allowed dependencies"):
            MODULE.validate_configuration(self.root, self.policy_path, check_git=False)

    def test_self_dependency_fails(self) -> None:
        policy = self._read_policy()
        policy["assemblies"]["TCJ.Core"] = ["TCJ.Core"]
        self._write_policy(policy)
        with self.assertRaisesRegex(MODULE.ArchitecturePolicyError, "must not depend on itself"):
            MODULE.validate_configuration(self.root, self.policy_path, check_git=False)

    def test_cycle_fails(self) -> None:
        policy = self._read_policy()
        policy["assemblies"]["TCJ.Core"] = ["TCJ.AspNetCore"]
        self._write_policy(policy)
        with self.assertRaisesRegex(MODULE.ArchitecturePolicyError, "contains a cycle"):
            MODULE.validate_configuration(self.root, self.policy_path, check_git=False)

    def test_release_manifest_mismatch_fails(self) -> None:
        manifest = json.loads((self.root / "eng/release-manifest.json").read_text())
        manifest["packages"].pop()
        (self.root / "eng/release-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaisesRegex(MODULE.ArchitecturePolicyError, "must match release-manifest"):
            MODULE.validate_configuration(self.root, self.policy_path, check_git=False)

    def test_missing_forbidden_prefixes_fails(self) -> None:
        policy = self._read_policy()
        policy["forbiddenDependencyPrefixes"]["TCJ.Core"] = []
        self._write_policy(policy)
        with self.assertRaisesRegex(MODULE.ArchitecturePolicyError, "must be a non-empty array"):
            MODULE.validate_configuration(self.root, self.policy_path, check_git=False)

    def test_ignored_policy_fails(self) -> None:
        subprocess.run(["git", "init", "-q"], cwd=self.root, check=True)
        (self.root / ".gitignore").write_text("eng/architecture-policy.json\n", encoding="utf-8")
        with self.assertRaisesRegex(MODULE.ArchitecturePolicyError, "ignored by Git"):
            MODULE.validate_configuration(self.root, self.policy_path, check_git=True)

    def test_missing_approved_extension_containers_fails(self) -> None:
        policy = self._read_policy()
        policy.pop("approvedExtensionContainers")
        self._write_policy(policy)
        with self.assertRaisesRegex(
            MODULE.ArchitecturePolicyError,
            "approvedExtensionContainers must be a non-empty array",
        ):
            MODULE.validate_configuration(self.root, self.policy_path, check_git=False)

    def test_summary_contains_dependency_graph(self) -> None:
        policy = MODULE.validate_configuration(self.root, self.policy_path, check_git=False)
        summary = MODULE.build_summary(policy)
        self.assertIn("Approved dependency graph", summary)
        self.assertIn("TCJ.EntityFrameworkCore.SqlServer", summary)
        self.assertIn("architecture policy validation passed", summary)

    def _read_policy(self) -> dict:
        return json.loads(self.policy_path.read_text(encoding="utf-8"))

    def _write_policy(self, policy: dict) -> None:
        self.policy_path.write_text(json.dumps(policy, indent=2), encoding="utf-8")

    def _write_valid_repository(self) -> None:
        for path in (
            "eng",
            "docs",
            "tests/TCJ.Architecture.Tests",
            ".github/workflows",
            ".github",
        ):
            (self.root / path).mkdir(parents=True, exist_ok=True)

        assemblies = {
            "TCJ.Core": [],
            "TCJ.DependencyInjection": ["TCJ.Core"],
            "TCJ.EntityFrameworkCore": ["TCJ.Core", "TCJ.DependencyInjection"],
            "TCJ.EntityFrameworkCore.SqlServer": [
                "TCJ.Core",
                "TCJ.DependencyInjection",
                "TCJ.EntityFrameworkCore",
            ],
            "TCJ.AspNetCore": ["TCJ.Core", "TCJ.DependencyInjection"],
            "TCJ.Messaging": ["TCJ.Core"],
        }
        project_paths = {
            assembly: f"src/{assembly}/{assembly}.csproj"
            for assembly in MODULE.REQUIRED_ASSEMBLIES
        }
        policy = {
            "schemaVersion": 1,
            "documentation": "docs/architecture-tests.md",
            "assemblies": assemblies,
            "projectPaths": project_paths,
            "namespaceRoots": {assembly: assembly for assembly in MODULE.REQUIRED_ASSEMBLIES},
            "forbiddenDependencyPrefixes": {
                assembly: ["Forbidden.Infrastructure"]
                for assembly in MODULE.REQUIRED_ASSEMBLIES
            },
            "forbiddenPublicApiTypePrefixes": {
                assembly: ["Forbidden.PublicApi"]
                for assembly in MODULE.REQUIRED_ASSEMBLIES
            },
            "approvedExtensionContainers": ["TCJ.Core.Guards.Check"],
            "approvedPublicOptionTypes": ["TCJ.AspNetCore.Options.TcjAspNetCoreOptions"],
        }
        self._write_policy(policy)
        (self.root / "eng/release-manifest.json").write_text(
            json.dumps({"packages": list(MODULE.REQUIRED_ASSEMBLIES)}),
            encoding="utf-8",
        )
        (self.root / "docs/architecture-tests.md").write_text(
            "# Architecture\n\nApproved dependency graph. Policy: `eng/architecture-policy.json`.\n",
            encoding="utf-8",
        )

        for assembly, project_path in project_paths.items():
            path = self.root / project_path
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(
                f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><PackageId>{assembly}</PackageId></PropertyGroup></Project>',
                encoding="utf-8",
            )

        references = "".join(
            f'<ProjectReference Include="../../src/{assembly}/{assembly}.csproj" />'
            for assembly in MODULE.REQUIRED_ASSEMBLIES
        )
        (self.root / "tests/TCJ.Architecture.Tests/TCJ.Architecture.Tests.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>'
            f"<ItemGroup>{references}</ItemGroup></Project>",
            encoding="utf-8",
        )

        test_content = '[Trait("Category", "Architecture")]\n// ArchitectureFailure.Format\n'
        for test_file in MODULE.REQUIRED_TEST_FILES:
            path = self.root / test_file
            path.write_text(test_content, encoding="utf-8")

        (self.root / "TCJ.slnx").write_text(
            '<Solution><Folder Name="/tests/"><Project Path="tests/TCJ.Architecture.Tests/TCJ.Architecture.Tests.csproj" /></Folder></Solution>',
            encoding="utf-8",
        )
        workflow_content = (
            "python3 eng/verify-architecture-policy.py validate-config\n"
            "dotnet test TCJ.slnx\n"
        )
        for workflow in MODULE.REQUIRED_WORKFLOWS:
            (self.root / workflow).write_text(workflow_content, encoding="utf-8")

        (self.root / ".github/PULL_REQUEST_TEMPLATE.md").write_text(
            "Architecture tests pass; architecture-policy changes are justified.",
            encoding="utf-8",
        )
        (self.root / "tests/README.md").write_text(
            "TCJ.Architecture.Tests: --filter Category=Architecture",
            encoding="utf-8",
        )
        (self.root / "docs/README.md").write_text(
            "architecture-tests.md",
            encoding="utf-8",
        )


if __name__ == "__main__":
    unittest.main()
