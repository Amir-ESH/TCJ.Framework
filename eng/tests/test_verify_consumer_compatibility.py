from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-consumer-compatibility.py"
SPEC = importlib.util.spec_from_file_location("verify_consumer_compatibility", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)

REAL_ROOT = Path(__file__).resolve().parents[2]
REAL_POLICY = json.loads((REAL_ROOT / "eng/compatibility-policy.json").read_text(encoding="utf-8"))
VERSION = "0.1.0-preview.2"
COMMIT = "a" * 40


def write_package(path: Path, package_id: str, symbol: bool = False, include_xml: bool = True, source_link_commit: str = COMMIT) -> None:
    nuspec = f'''<?xml version="1.0"?>
<package><metadata><id>{package_id}</id><version>{VERSION}</version><authors>TCJ</authors><description>x</description>
<repository type="git" url="https://github.com/Amir-ESH/TCJ.Framework.git" commit="{COMMIT}" />
<dependencies><group targetFramework="net10.0"></group></dependencies></metadata></package>'''
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr(f"{package_id}.nuspec", nuspec)
        if symbol:
            source_link = json.dumps({"documents": {"/_/*": f"https://raw.githubusercontent.com/Amir-ESH/TCJ.Framework/{source_link_commit}/*"}}).encode()
            archive.writestr(f"lib/net10.0/{package_id}.pdb", b"BSJB" + b"\0" * 16 + source_link)
        else:
            archive.writestr(f"lib/net10.0/{package_id}.dll", b"MZ")
            if include_xml:
                archive.writestr(f"lib/net10.0/{package_id}.xml", b"<doc />")
            archive.writestr("README.md", b"readme")
            archive.writestr("LICENSE.txt", b"license")


class ConsumerCompatibilityVerifierTests(unittest.TestCase):
    def test_real_policy_loads(self) -> None:
        policy = MODULE.load_policy(REAL_ROOT)
        self.assertEqual(7, len(policy["consumers"]))
        self.assertEqual(["ubuntu-latest", "windows-latest", "macos-latest"], policy["requiredOperatingSystems"])
        self.assertEqual({"ubuntu-latest": "x64", "windows-latest": "x64", "macos-latest": "arm64"}, policy["requiredArchitectureByOperatingSystem"])

    def test_missing_policy_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaises(MODULE.VerificationError):
                MODULE.load_policy(Path(temporary))

    def test_malformed_policy_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); (root / "eng").mkdir(); (root / "eng/compatibility-policy.json").write_text("{bad", encoding="utf-8")
            with self.assertRaises(MODULE.VerificationError):
                MODULE.load_policy(root)

    def test_project_reference_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "consumer.csproj"
            path.write_text('<Project><ItemGroup><ProjectReference Include="../../src/TCJ.Core/TCJ.Core.csproj" /></ItemGroup></Project>', encoding="utf-8")
            with self.assertRaises(MODULE.VerificationError):
                MODULE.parse_project(path)

    def test_tcj_version_property_is_required(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "consumer.csproj"
            path.write_text('<Project><ItemGroup><PackageReference Include="TCJ.Core" Version="1.0.0" /></ItemGroup></Project>', encoding="utf-8")
            packages, version_ok = MODULE.parse_project(path)
            self.assertEqual({"TCJ.Core"}, packages)
            self.assertFalse(version_ok)

    def test_local_nuget_mapping_is_valid(self) -> None:
        MODULE.validate_nuget_config(REAL_ROOT, published=False)

    def test_published_nuget_mapping_is_valid(self) -> None:
        MODULE.validate_nuget_config(REAL_ROOT, published=True)

    def test_source_link_json_is_extracted(self) -> None:
        payload = b"BSJB" + json.dumps({"documents": {"/_/*": "https://example/*"}}).encode()
        self.assertEqual({"documents": {"/_/*": "https://example/*"}}, MODULE.extract_json_object(payload))

    def test_primary_package_requires_xml_documentation(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / f"TCJ.Core.{VERSION}.nupkg"
            write_package(path, "TCJ.Core", include_xml=False)
            with self.assertRaisesRegex(MODULE.VerificationError, "XML documentation"):
                MODULE.validate_primary_package(path, "TCJ.Core", VERSION, REAL_POLICY, COMMIT)

    def test_symbol_package_requires_portable_pdb(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / f"TCJ.Core.{VERSION}.snupkg"
            nuspec = f'<package><metadata><id>TCJ.Core</id><version>{VERSION}</version></metadata></package>'
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("TCJ.Core.nuspec", nuspec)
            with self.assertRaisesRegex(MODULE.VerificationError, "portable PDB"):
                MODULE.validate_symbol_package(path, "TCJ.Core", VERSION, REAL_POLICY, COMMIT)

    def test_symbol_package_requires_matching_source_link_commit(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / f"TCJ.Core.{VERSION}.snupkg"
            write_package(path, "TCJ.Core", symbol=True, source_link_commit="b" * 40)
            with self.assertRaisesRegex(MODULE.VerificationError, "does not reference commit"):
                MODULE.validate_symbol_package(path, "TCJ.Core", VERSION, REAL_POLICY, COMMIT)

    def test_platform_result_rejects_wrong_version(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "platform-result.json"
            path.write_text(json.dumps({"schemaVersion": 1, "platform": "ubuntu-latest", "packageVersion": "9.9.9"}), encoding="utf-8")
            with self.assertRaisesRegex(MODULE.VerificationError, "version mismatch"):
                MODULE.validate_platform_result(path, REAL_POLICY, VERSION, "ubuntu-latest", "local")

    def test_platform_result_rejects_wrong_architecture_for_macos(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "platform-result.json"
            consumers = []
            for consumer in REAL_POLICY["consumers"]:
                consumers.append({
                    "name": consumer["name"],
                    "restoreStatus": "pass",
                    "buildStatus": "pass",
                    "runtimeStatus": "pass",
                    "packageVersionStatus": "pass",
                    "packageSourceStatus": "pass",
                    "warningCount": 0,
                })
            payload = {
                "schemaVersion": 1, "platform": "macos-latest", "packageVersion": VERSION,
                "configuration": "Release", "targetFramework": "net10.0", "architecture": "x64",
                "dotnetSdkVersion": "10.0.302", "sourceMode": "local", "consumers": consumers,
                "consumerCount": len(consumers), "restoreSuccessCount": len(consumers),
                "buildSuccessCount": len(consumers), "runtimeSuccessCount": len(consumers),
                "warningCount": 0, "overall": "pass",
            }
            path.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaisesRegex(MODULE.VerificationError, "Architecture mismatch"):
                MODULE.validate_platform_result(path, REAL_POLICY, VERSION, "macos-latest", "local")

    def test_find_platform_result_rejects_missing_os(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(MODULE.VerificationError, "exactly one result"):
                MODULE.find_platform_result(Path(temporary), "macos-latest")

    def test_gitignore_rejects_compatibility_tree_rule(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); (root / ".gitignore").write_text("compatibility/**\n", encoding="utf-8")
            with self.assertRaisesRegex(MODULE.VerificationError, "hides compatibility"):
                MODULE.check_gitignore(root, [])

    def test_machine_path_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "bad.nupkg"
            with self.assertRaisesRegex(MODULE.VerificationError, "absolute machine path"):
                MODULE.validate_no_machine_paths({"lib/net10.0/x.pdb": b"/home/runner/work/repo/file.cs"}, path)


if __name__ == "__main__":
    unittest.main()
