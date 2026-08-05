from __future__ import annotations

import copy
import importlib.util
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

ENG = Path(__file__).resolve().parents[1]
if str(ENG) not in sys.path:
    sys.path.insert(0, str(ENG))

from sbom_common import build_sbom, nuget_purl, parse_nuspec_xml, write_json  # noqa: E402


def load_script(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, ENG / filename)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


VERIFY = load_script("verify_sbom_module", "verify-sbom.py")
INTEGRITY = load_script("release_integrity_module", "release-integrity.py")

PACKAGES = [
    "TCJ.Core",
    "TCJ.DependencyInjection",
    "TCJ.EntityFrameworkCore",
    "TCJ.EntityFrameworkCore.SqlServer",
    "TCJ.AspNetCore",
]
VERSION = "1.2.3-preview.1"
COMMIT = "0123456789abcdef0123456789abcdef01234567"


class NuspecParsingTests(unittest.TestCase):
    def test_target_specific_dependency_ranges_are_supported(self):
        metadata = parse_nuspec_xml(
            '''<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>Microsoft.Data.SqlClient</id>
    <version>6.1.1</version>
    <dependencies>
      <group targetFramework=".NETFramework4.6.2">
        <dependency id="Microsoft.Bcl.Cryptography" version="[8.0.0, )" />
      </group>
      <group targetFramework=".NETStandard2.0">
        <dependency id="Microsoft.Bcl.Cryptography" version="[9.0.4, )" />
      </group>
      <group targetFramework="net8.0">
        <dependency id="Microsoft.Bcl.Cryptography" version="[8.0.0, )" />
      </group>
    </dependencies>
  </metadata>
</package>
''',
            "Microsoft.Data.SqlClient.6.1.1.nuspec",
        )
        self.assertEqual(("Microsoft.Bcl.Cryptography",), metadata.dependencies)

    def test_conflicting_ranges_inside_same_dependency_group_fail(self):
        with self.assertRaisesRegex(ValueError, "inside dependency group"):
            parse_nuspec_xml(
                '''<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>Broken.Package</id>
    <version>1.0.0</version>
    <dependencies>
      <group targetFramework="net10.0">
        <dependency id="Example" version="[1.0.0]" />
        <dependency id="example" version="[2.0.0]" />
      </group>
    </dependencies>
  </metadata>
</package>
''',
                "broken.nuspec",
            )


class Fixture:
    def __init__(self, root: Path):
        self.root = root
        self.package_directory = root / "artifacts" / "packages"
        self.sbom_directory = root / "artifacts" / "sbom"
        self.cache = root / "nuget-cache"
        self.package_directory.mkdir(parents=True)
        self.sbom_directory.mkdir(parents=True)
        self.cache.mkdir(parents=True)
        self.policy = {
            "schemaVersion": 1,
            "format": "CycloneDX",
            "specVersion": "1.6",
            "fileExtension": ".cdx.json",
            "generatorVersion": 1,
            "repository": "Amir-ESH/TCJ.Framework",
            "requiredPackages": PACKAGES,
            "requireDirectDependencies": True,
            "requireTransitiveDependencies": True,
            "requireHashes": True,
            "hashAlgorithm": "SHA-256",
            "requireLicenses": True,
            "requireRepositoryReference": True,
            "requireCommitSha": True,
            "requireReleaseVersion": True,
        }
        self._create_external_package("Direct.Package", "1.0.0", {"Transitive.Package": "[2.0.0]"})
        self._create_external_package("Transitive.Package", "2.0.0", {})
        self._create_release_packages()
        self._create_assets()
        (self.root / "eng").mkdir(exist_ok=True)
        write_json(
            self.root / "eng" / "release-manifest.json",
            {"version": VERSION, "packages": PACKAGES},
        )
        self.sbom = build_sbom(
            root=root,
            policy=self.policy,
            version=VERSION,
            package_directory=self.package_directory,
            commit_sha=COMMIT,
            release_tag=f"v{VERSION}",
        )
        self.sbom_path = self.sbom_directory / f"TCJ.Framework.{VERSION}.cdx.json"
        write_json(self.sbom_path, self.sbom)

    @staticmethod
    def nuspec(package_id: str, version: str, dependencies: dict[str, str]) -> str:
        dependencies_xml = "".join(
            f'<dependency id="{name}" version="{constraint}" />'
            for name, constraint in dependencies.items()
        )
        return f'''<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>{package_id}</id>
    <version>{version}</version>
    <authors>TCJ Contributors</authors>
    <license type="expression">MIT</license>
    <projectUrl>https://example.test/{package_id}</projectUrl>
    <repository type="git" url="https://github.com/Amir-ESH/TCJ.Framework.git" commit="{COMMIT}" />
    <dependencies><group targetFramework="net10.0">{dependencies_xml}</group></dependencies>
  </metadata>
</package>
'''

    def _zip_package(self, path: Path, package_id: str, version: str, dependencies: dict[str, str]) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr(f"{package_id}.nuspec", self.nuspec(package_id, version, dependencies))
            archive.writestr("lib/net10.0/placeholder.dll", b"fixture")

    def _create_external_package(self, package_id: str, version: str, dependencies: dict[str, str]) -> None:
        package_root = self.cache / package_id.casefold() / version
        package_root.mkdir(parents=True)
        self._zip_package(
            package_root / f"{package_id.casefold()}.{version}.nupkg",
            package_id,
            version,
            dependencies,
        )
        (package_root / f"{package_id.casefold()}.nuspec").write_text(
            self.nuspec(package_id, version, dependencies),
            encoding="utf-8",
        )

    def _create_release_packages(self) -> None:
        dependencies = {
            "TCJ.Core": {},
            "TCJ.DependencyInjection": {
                "TCJ.Core": f"[{VERSION}]",
                "Direct.Package": "[1.0.0]",
            },
            "TCJ.EntityFrameworkCore": {
                "TCJ.Core": f"[{VERSION}]",
                "Direct.Package": "[1.0.0]",
            },
            "TCJ.EntityFrameworkCore.SqlServer": {
                "TCJ.Core": f"[{VERSION}]",
                "TCJ.EntityFrameworkCore": f"[{VERSION}]",
                "Direct.Package": "[1.0.0]",
            },
            "TCJ.AspNetCore": {
                "TCJ.Core": f"[{VERSION}]",
                "Direct.Package": "[1.0.0]",
            },
        }
        for package_id in PACKAGES:
            self._zip_package(
                self.package_directory / f"{package_id}.{VERSION}.nupkg",
                package_id,
                VERSION,
                dependencies[package_id],
            )
            (self.package_directory / f"{package_id}.{VERSION}.snupkg").write_bytes(
                f"symbols:{package_id}".encode()
            )

    def _create_assets(self) -> None:
        target = {
            "Direct.Package/1.0.0": {
                "type": "package",
                "dependencies": {"Transitive.Package": "2.0.0"},
            },
            "Transitive.Package/2.0.0": {
                "type": "package",
            },
        }
        libraries = {
            "Direct.Package/1.0.0": {
                "type": "package",
                "path": "direct.package/1.0.0",
            },
            "Transitive.Package/2.0.0": {
                "type": "package",
                "path": "transitive.package/2.0.0",
            },
        }
        for package_id in PACKAGES:
            path = self.root / "src" / package_id / "obj" / "project.assets.json"
            path.parent.mkdir(parents=True)
            declared = {} if package_id == "TCJ.Core" else {
                "Direct.Package": {"target": "Package", "version": "[1.0.0, )"}
            }
            path.write_text(
                json.dumps(
                    {
                        "version": 3,
                        "targets": {"net10.0": target},
                        "libraries": libraries,
                        "packageFolders": {str(self.cache) + "/": {}},
                        "project": {
                            "frameworks": {
                                "net10.0": {"dependencies": declared}
                            }
                        },
                    }
                ),
                encoding="utf-8",
            )

    def verify(self, sbom: dict | None = None):
        value = sbom or self.sbom
        write_json(self.sbom_path, value)
        return VERIFY.verify_document(
            root=self.root,
            policy=self.policy,
            version=VERSION,
            package_directory=self.package_directory,
            sbom_path=self.sbom_path,
        )


class VerifySbomTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.fixture = Fixture(Path(self.temp.name))

    def tearDown(self):
        self.temp.cleanup()

    def test_valid_sbom_passes(self):
        summary = self.fixture.verify()
        self.assertEqual("PASS", summary["status"])
        self.assertEqual(5, summary["tcjPackageCount"])
        self.assertGreater(summary["transitiveDependencyCount"], 0)

    def test_missing_tcj_package_fails(self):
        sbom = copy.deepcopy(self.fixture.sbom)
        sbom["components"] = [
            item for item in sbom["components"] if item.get("name") != "TCJ.AspNetCore"
        ]
        with self.assertRaisesRegex(ValueError, "missing required TCJ packages"):
            self.fixture.verify(sbom)

    def test_wrong_package_version_fails(self):
        sbom = copy.deepcopy(self.fixture.sbom)
        component = next(item for item in sbom["components"] if item.get("name") == "TCJ.Core")
        component["version"] = "9.9.9"
        with self.assertRaisesRegex(ValueError, "has version"):
            self.fixture.verify(sbom)

    def test_duplicate_component_fails(self):
        sbom = copy.deepcopy(self.fixture.sbom)
        component = next(item for item in sbom["components"] if item.get("name") == "TCJ.Core")
        sbom["components"].append(copy.deepcopy(component))
        with self.assertRaisesRegex(ValueError, "Duplicate SBOM component"):
            self.fixture.verify(sbom)

    def test_broken_dependency_reference_fails(self):
        sbom = copy.deepcopy(self.fixture.sbom)
        sbom["dependencies"][0]["dependsOn"].append("pkg:nuget/Missing@1.0.0")
        with self.assertRaisesRegex(ValueError, "missing components"):
            self.fixture.verify(sbom)

    def test_missing_hash_fails(self):
        sbom = copy.deepcopy(self.fixture.sbom)
        component = next(item for item in sbom["components"] if item.get("name") == "TCJ.Core")
        component["hashes"] = []
        with self.assertRaisesRegex(ValueError, "missing a SHA-256 hash"):
            self.fixture.verify(sbom)

    def test_missing_license_fails(self):
        sbom = copy.deepcopy(self.fixture.sbom)
        component = next(item for item in sbom["components"] if item.get("name") == "Direct.Package")
        component["licenses"] = []
        with self.assertRaisesRegex(ValueError, "missing required license metadata"):
            self.fixture.verify(sbom)

    def test_missing_repository_metadata_fails(self):
        sbom = copy.deepcopy(self.fixture.sbom)
        properties = sbom["metadata"]["properties"]
        next(item for item in properties if item["name"] == "tcj:repository")["value"] = "other/repo"
        with self.assertRaisesRegex(ValueError, "repository metadata mismatch"):
            self.fixture.verify(sbom)

    def test_missing_commit_sha_fails(self):
        sbom = copy.deepcopy(self.fixture.sbom)
        sbom["metadata"]["properties"] = [
            item for item in sbom["metadata"]["properties"] if item["name"] != "tcj:commitSha"
        ]
        with self.assertRaisesRegex(ValueError, "missing tcj:commitSha"):
            self.fixture.verify(sbom)

    def test_package_not_represented_fails(self):
        sbom = copy.deepcopy(self.fixture.sbom)
        filename = f"TCJ.Core.{VERSION}.snupkg"
        sbom["components"] = [
            item
            for item in sbom["components"]
            if not any(
                prop.get("name") == "tcj:artifactFile" and prop.get("value") == filename
                for prop in item.get("properties", [])
            )
        ]
        with self.assertRaisesRegex(ValueError, "not represented"):
            self.fixture.verify(sbom)

    def test_missing_sbom_file_fails(self):
        self.fixture.sbom_path.unlink()
        with self.assertRaisesRegex(ValueError, "does not exist"):
            VERIFY.verify_document(
                root=self.fixture.root,
                policy=self.fixture.policy,
                version=VERSION,
                package_directory=self.fixture.package_directory,
                sbom_path=self.fixture.sbom_path,
            )

    def test_missing_transitive_dependency_component_fails(self):
        sbom = copy.deepcopy(self.fixture.sbom)
        ref = nuget_purl("Transitive.Package", "2.0.0")
        sbom["components"] = [item for item in sbom["components"] if item.get("bom-ref") != ref]
        with self.assertRaisesRegex(ValueError, "missing components"):
            self.fixture.verify(sbom)

    def test_tampered_sbom_fails_release_integrity(self):
        checksums = self.fixture.root / "artifacts" / "release" / "SHA256SUMS"
        INTEGRITY.write_checksums(
            self.fixture.root,
            self.fixture.package_directory,
            checksums,
            VERSION,
            self.fixture.sbom_path,
        )
        self.fixture.sbom_path.write_text("{}\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Checksum verification failed"):
            INTEGRITY.verify_checksums(
                self.fixture.root,
                self.fixture.package_directory,
                checksums,
                VERSION,
                self.fixture.sbom_path,
            )


class ValidateSbomConfigurationTests(unittest.TestCase):
    def make_root(self) -> Path:
        root = Path(self.temp.name)
        (root / "eng" / "tests").mkdir(parents=True)
        (root / "docs").mkdir()
        (root / ".github" / "workflows").mkdir(parents=True)
        policy = Fixture.__new__(Fixture)
        policy_value = {
            "schemaVersion": 1,
            "format": "CycloneDX",
            "specVersion": "1.6",
            "fileExtension": ".cdx.json",
            "repository": "Amir-ESH/TCJ.Framework",
            "requiredPackages": PACKAGES,
            "requireDirectDependencies": True,
            "requireTransitiveDependencies": True,
            "requireHashes": True,
            "requireLicenses": True,
            "requireRepositoryReference": True,
            "requireCommitSha": True,
            "requireReleaseVersion": True,
        }
        write_json(root / "eng" / "sbom-policy.json", policy_value)
        write_json(
            root / "eng" / "release-manifest.json",
            {"repository": "Amir-ESH/TCJ.Framework", "packages": PACKAGES},
        )
        for relative in (
            "eng/generate-sbom.py",
            "eng/sbom_common.py",
            "eng/verify-sbom.py",
            "eng/tests/test_verify_sbom.py",
            "docs/software-bill-of-materials.md",
        ):
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("fixture\n", encoding="utf-8")
        (root / ".gitignore").write_text(
            "artifacts/sbom/\n*.cdx.json\n!eng/sbom-policy.json\n",
            encoding="utf-8",
        )
        common = "\n".join(
            (
                "python3 eng/verify-sbom.py validate-config",
                "python3 eng/generate-sbom.py",
                "python3 eng/verify-sbom.py verify",
                "artifacts/sbom/SBOM_SUMMARY.md",
                "artifacts/sbom/sbom-summary.json",
            )
        )
        (root / ".github/workflows/ci.yml").write_text(common, encoding="utf-8")
        (root / ".github/workflows/release-preflight.yml").write_text(common, encoding="utf-8")
        (root / ".github/workflows/release.yml").write_text(
            common + "\nartifacts/sbom/*.cdx.json\nuses: actions/attest@v4\n",
            encoding="utf-8",
        )
        (root / "eng/release-integrity.py").write_text(
            ".cdx.json\n--sbom\nSBOM\n",
            encoding="utf-8",
        )
        subprocess = __import__("subprocess")
        subprocess.run(["git", "init", "-q"], cwd=root, check=True)
        return root

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.temp.cleanup()

    def test_missing_policy_fails(self):
        root = self.make_root()
        (root / "eng/sbom-policy.json").unlink()
        with self.assertRaisesRegex(ValueError, "does not exist"):
            VERIFY.validate_configuration(root)

    def test_malformed_policy_fails(self):
        root = self.make_root()
        (root / "eng/sbom-policy.json").write_text("{not-json", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Malformed JSON"):
            VERIFY.validate_configuration(root)

    def test_ignored_policy_fails(self):
        root = self.make_root()
        (root / ".gitignore").write_text("eng/sbom-policy.json\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "ignored by Git"):
            VERIFY.validate_configuration(root)


if __name__ == "__main__":
    unittest.main()
