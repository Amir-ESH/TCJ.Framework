from __future__ import annotations

import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

ENG = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "verify_reproducible_build_module",
    ENG / "verify-reproducible-build.py",
)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)

PACKAGES = [
    "TCJ.Core",
    "TCJ.DependencyInjection",
    "TCJ.EntityFrameworkCore",
    "TCJ.EntityFrameworkCore.SqlServer",
    "TCJ.AspNetCore",
]
VERSION = "1.2.3-preview.4"
COMMIT = "0123456789abcdef0123456789abcdef01234567"


class Fixture:
    def __init__(self, root: Path):
        self.root = root
        self.build_a = root / "artifacts/reproducibility/build-a/packages"
        self.build_b = root / "artifacts/reproducibility/build-b/packages"
        self.output = root / "artifacts/reproducibility/report"
        self.build_a.mkdir(parents=True)
        self.build_b.mkdir(parents=True)
        (root / "global.json").write_text(
            json.dumps({"sdk": {"version": "10.0.100"}}), encoding="utf-8"
        )
        self.policy = MODULE.load_policy(ENG / "reproducibility-policy.json")

    @staticmethod
    def nuspec(package_id: str, *, commit: str = COMMIT, dependency: str = "[10.0.10]") -> bytes:
        return f'''<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>{package_id}</id>
    <version>{VERSION}</version>
    <authors>TCJ Contributors</authors>
    <repository type="git" url="https://github.com/Amir-ESH/TCJ.Framework.git" commit="{commit}" />
    <dependencies><group targetFramework="net10.0"><dependency id="Example" version="{dependency}" /></group></dependencies>
  </metadata>
</package>
'''.encode()

    @staticmethod
    def relationships(core_name: str, relationship_id: str = "R123") -> bytes:
        return f'''<?xml version="1.0" encoding="utf-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="/package/services/metadata/core-properties/{core_name}" Id="{relationship_id}" />
</Relationships>
'''.encode()

    @staticmethod
    def content_types(core_name: str) -> bytes:
        return f'''<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
  <Override PartName="/package/services/metadata/core-properties/{core_name}" ContentType="application/vnd.openxmlformats-package.core-properties+xml" />
</Types>
'''.encode()

    @staticmethod
    def core_properties(created: str | None) -> bytes:
        created_element = (
            f'<dcterms:created xsi:type="dcterms:W3CDTF">{created}</dcterms:created>'
            if created is not None
            else ""
        )
        return f'''<?xml version="1.0" encoding="utf-8"?>
<coreProperties xmlns="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  {created_element}
</coreProperties>
'''.encode()

    @staticmethod
    def pdb(package_id: str, source_url: str | None = None, extra: bytes = b"") -> bytes:
        url = source_url or f"https://raw.githubusercontent.com/Amir-ESH/TCJ.Framework/{COMMIT}/*"
        source_link = json.dumps(
            {"documents": {"/_/*": url}}, sort_keys=True, separators=(",", ":")
        ).encode()
        return b"BSJB\x01\x00portable-pdb\x00" + source_link + extra

    @staticmethod
    def zip_info(name: str, timestamp: tuple[int, int, int, int, int, int]) -> zipfile.ZipInfo:
        info = zipfile.ZipInfo(name, timestamp)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o100644 << 16
        return info

    def create_package(
        self,
        directory: Path,
        package_id: str,
        package_type: str,
        *,
        zip_timestamp: tuple[int, int, int, int, int, int] = (2026, 1, 1, 0, 0, 0),
        created: str | None = "2026-01-01T00:00:00Z",
        core_name: str = "11111111-1111-1111-1111-111111111111.psmdcp",
        relationship_id: str = "R123",
        dll: bytes = b"deterministic-assembly",
        xml_documentation: bytes = b"<doc><assembly /></doc>",
        pdb: bytes | None = None,
        nuspec: bytes | None = None,
        unsafe_entry: str | None = None,
    ) -> Path:
        suffix = ".snupkg" if package_type == "snupkg" else ".nupkg"
        path = directory / f"{package_id}.{VERSION}{suffix}"
        entries: list[tuple[str, bytes]] = [
            ("_rels/.rels", self.relationships(core_name, relationship_id)),
            ("[Content_Types].xml", self.content_types(core_name)),
            (f"{package_id}.nuspec", nuspec or self.nuspec(package_id)),
            (
                f"package/services/metadata/core-properties/{core_name}",
                self.core_properties(created),
            ),
        ]
        if package_type == "nupkg":
            entries.extend(
                [
                    (f"lib/net10.0/{package_id}.dll", dll),
                    (f"lib/net10.0/{package_id}.xml", xml_documentation),
                ]
            )
        else:
            entries.extend(
                [
                    (f"lib/net10.0/{package_id}.pdb", pdb or self.pdb(package_id)),
                    (f"src/{package_id}/Example.cs", b"namespace Example; public sealed class Value {}"),
                ]
            )
        if unsafe_entry:
            entries.append((unsafe_entry, b"unsafe"))
        with zipfile.ZipFile(path, "w") as archive:
            for name, data in entries:
                archive.writestr(self.zip_info(name, zip_timestamp), data)
        return path

    def create_set(self, directory: Path, **kwargs) -> None:
        for package_id in PACKAGES:
            self.create_package(directory, package_id, "nupkg", **kwargs)
            self.create_package(directory, package_id, "snupkg", **kwargs)

    def compare(self):
        return MODULE.compare_package_sets(
            self.root,
            self.policy,
            VERSION,
            self.build_a,
            self.build_b,
            self.output,
        )


class ReproducibleBuildTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.fixture = Fixture(self.root)

    def tearDown(self) -> None:
        self.temp.cleanup()

    def test_fully_reproducible_package_set_passes(self) -> None:
        self.fixture.create_set(self.fixture.build_a)
        self.fixture.create_set(self.fixture.build_b)
        summary = self.fixture.compare()
        self.assertEqual("PASS", summary.status)
        self.assertTrue(summary.archiveByteEquality)
        self.assertEqual(5, summary.comparedNupkgCount)
        self.assertEqual(5, summary.comparedSnupkgCount)

    def test_core_properties_without_created_timestamp_are_supported(self) -> None:
        self.fixture.create_set(self.fixture.build_a, created=None)
        self.fixture.create_set(self.fixture.build_b, created=None)
        summary = self.fixture.compare()
        self.assertEqual("PASS", summary.status)
        self.assertFalse(any(
            item["rule"] == "nuget-core-properties-created"
            for item in summary.normalizedContainerDifferences
        ))

    def test_created_timestamp_presence_mismatch_is_blocking(self) -> None:
        self.fixture.create_set(self.fixture.build_a, created=None)
        self.fixture.create_set(self.fixture.build_b, created="2026-01-01T00:00:00Z")
        with self.assertRaisesRegex(MODULE.ReproducibilityError, "Blocking package differences"):
            self.fixture.compare()

    def test_equivalent_contents_with_different_zip_metadata_passes_with_warning(self) -> None:
        self.fixture.create_set(
            self.fixture.build_a,
            zip_timestamp=(2026, 1, 1, 0, 0, 0),
            created="2026-01-01T00:00:00Z",
            core_name="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.psmdcp",
            relationship_id="RA",
        )
        self.fixture.create_set(
            self.fixture.build_b,
            zip_timestamp=(2026, 1, 2, 0, 0, 0),
            created="2026-01-02T00:00:00Z",
            core_name="bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb.psmdcp",
            relationship_id="RB",
        )
        summary = self.fixture.compare()
        self.assertEqual("PASS_WITH_WARNINGS", summary.status)
        self.assertTrue(summary.packageContentEquality)
        self.assertFalse(summary.archiveByteEquality)
        self.assertTrue(summary.normalizedContainerDifferences)
        archive_warnings = [item for item in summary.differences if item["category"] == "archive-container"]
        self.assertTrue(archive_warnings)
        self.assertIn("ZIP entry timestamps differ", archive_warnings[0]["structural_difference"])
        self.assertTrue(any(
            "[Content_Types].xml" in item["path"]
            for item in summary.normalizedContainerDifferences
        ))

    def test_assembly_difference_fails(self) -> None:
        self.fixture.create_set(self.fixture.build_a)
        self.fixture.create_set(self.fixture.build_b)
        target = self.fixture.build_b / f"TCJ.Core.{VERSION}.nupkg"
        target.unlink()
        self.fixture.create_package(self.fixture.build_b, "TCJ.Core", "nupkg", dll=b"different")
        with self.assertRaisesRegex(MODULE.ReproducibilityError, "Blocking package differences"):
            self.fixture.compare()
        summary = json.loads((self.fixture.output / MODULE.JSON_NAME).read_text())
        self.assertFalse(summary["assemblyEquality"])

    def test_xml_documentation_difference_fails(self) -> None:
        self.fixture.create_set(self.fixture.build_a)
        self.fixture.create_set(self.fixture.build_b)
        target = self.fixture.build_b / f"TCJ.Core.{VERSION}.nupkg"
        target.unlink()
        self.fixture.create_package(
            self.fixture.build_b,
            "TCJ.Core",
            "nupkg",
            xml_documentation=b"<doc><assembly><name>changed</name></assembly></doc>",
        )
        with self.assertRaises(MODULE.ReproducibilityError):
            self.fixture.compare()
        summary = json.loads((self.fixture.output / MODULE.JSON_NAME).read_text())
        self.assertFalse(summary["xmlDocumentationEquality"])

    def test_missing_source_link_metadata_fails(self) -> None:
        self.fixture.create_set(self.fixture.build_a)
        self.fixture.create_set(self.fixture.build_b)
        target = self.fixture.build_b / f"TCJ.Core.{VERSION}.snupkg"
        target.unlink()
        self.fixture.create_package(
            self.fixture.build_b,
            "TCJ.Core",
            "snupkg",
            pdb=b"BSJB portable pdb without source link",
        )
        with self.assertRaisesRegex(MODULE.ReproducibilityError, "does not contain Source Link"):
            self.fixture.compare()

    def test_portable_pdb_difference_fails_even_when_source_link_matches(self) -> None:
        self.fixture.create_set(self.fixture.build_a)
        self.fixture.create_set(self.fixture.build_b)
        target = self.fixture.build_b / f"TCJ.Core.{VERSION}.snupkg"
        target.unlink()
        self.fixture.create_package(
            self.fixture.build_b,
            "TCJ.Core",
            "snupkg",
            pdb=self.fixture.pdb("TCJ.Core", extra=b"different-checksum"),
        )
        with self.assertRaises(MODULE.ReproducibilityError):
            self.fixture.compare()
        summary = json.loads((self.fixture.output / MODULE.JSON_NAME).read_text())
        self.assertFalse(summary["portablePdbEquality"])
        self.assertTrue(summary["sourceLinkEquality"])

    def test_source_link_difference_fails(self) -> None:
        self.fixture.create_set(self.fixture.build_a)
        self.fixture.create_set(self.fixture.build_b)
        target = self.fixture.build_b / f"TCJ.Core.{VERSION}.snupkg"
        target.unlink()
        self.fixture.create_package(
            self.fixture.build_b,
            "TCJ.Core",
            "snupkg",
            pdb=self.fixture.pdb("TCJ.Core", source_url="https://example.test/wrong/*"),
        )
        with self.assertRaises(MODULE.ReproducibilityError):
            self.fixture.compare()
        summary = json.loads((self.fixture.output / MODULE.JSON_NAME).read_text())
        self.assertFalse(summary["sourceLinkEquality"])

    def test_nuspec_metadata_difference_fails(self) -> None:
        self.fixture.create_set(self.fixture.build_a)
        self.fixture.create_set(self.fixture.build_b)
        target = self.fixture.build_b / f"TCJ.Core.{VERSION}.nupkg"
        target.unlink()
        self.fixture.create_package(
            self.fixture.build_b,
            "TCJ.Core",
            "nupkg",
            nuspec=self.fixture.nuspec("TCJ.Core", dependency="[11.0.0]"),
        )
        with self.assertRaises(MODULE.ReproducibilityError):
            self.fixture.compare()
        summary = json.loads((self.fixture.output / MODULE.JSON_NAME).read_text())
        self.assertFalse(summary["nuspecEquality"])

    def test_missing_package_fails(self) -> None:
        self.fixture.create_set(self.fixture.build_a)
        self.fixture.create_set(self.fixture.build_b)
        (self.fixture.build_b / f"TCJ.AspNetCore.{VERSION}.snupkg").unlink()
        with self.assertRaisesRegex(MODULE.ReproducibilityError, "Missing expected packages"):
            self.fixture.compare()

    def test_unexpected_package_fails(self) -> None:
        self.fixture.create_set(self.fixture.build_a)
        self.fixture.create_set(self.fixture.build_b)
        self.fixture.create_package(self.fixture.build_b, "TCJ.Extra", "nupkg")
        with self.assertRaisesRegex(MODULE.ReproducibilityError, "Unexpected TCJ package"):
            self.fixture.compare()

    def test_version_mismatch_fails(self) -> None:
        self.fixture.create_set(self.fixture.build_a)
        self.fixture.create_set(self.fixture.build_b)
        target = self.fixture.build_b / f"TCJ.Core.{VERSION}.nupkg"
        with zipfile.ZipFile(target, "a") as archive:
            pass
        wrong = self.fixture.nuspec("TCJ.Core").replace(VERSION.encode(), b"9.9.9")
        target.unlink()
        self.fixture.create_package(self.fixture.build_b, "TCJ.Core", "nupkg", nuspec=wrong)
        with self.assertRaisesRegex(MODULE.ReproducibilityError, "does not match expected version"):
            self.fixture.compare()

    def test_invalid_archive_path_fails(self) -> None:
        self.fixture.create_set(self.fixture.build_a)
        self.fixture.create_set(self.fixture.build_b)
        target = self.fixture.build_b / f"TCJ.Core.{VERSION}.nupkg"
        target.unlink()
        self.fixture.create_package(
            self.fixture.build_b,
            "TCJ.Core",
            "nupkg",
            unsafe_entry="../escape.txt",
        )
        with self.assertRaisesRegex(MODULE.ReproducibilityError, "unsafe ZIP path"):
            self.fixture.compare()

    def test_missing_package_directory_fails(self) -> None:
        with self.assertRaisesRegex(MODULE.ReproducibilityError, "Package directory does not exist"):
            MODULE.discover_packages(self.root / "missing", self.fixture.policy, VERSION)

    def test_malformed_policy_fails(self) -> None:
        path = self.root / "malformed.json"
        path.write_text("{not-json", encoding="utf-8")
        with self.assertRaisesRegex(MODULE.ReproducibilityError, "Invalid JSON"):
            MODULE.load_policy(path)

    def test_missing_policy_fails(self) -> None:
        with self.assertRaisesRegex(MODULE.ReproducibilityError, "Required JSON file is missing"):
            MODULE.load_policy(self.root / "missing-policy.json")

    def test_duplicate_package_identity_fails(self) -> None:
        self.fixture.create_set(self.fixture.build_a)
        original = self.fixture.build_a / f"TCJ.Core.{VERSION}.nupkg"
        shutil.copy2(original, self.fixture.build_a / f"duplicate-{VERSION}.nupkg")
        with self.assertRaisesRegex(MODULE.ReproducibilityError, "Duplicate package identity"):
            MODULE.discover_packages(self.fixture.build_a, self.fixture.policy, VERSION)

    def test_policy_ignored_by_git_fails(self) -> None:
        repo = self.root / "repo"
        (repo / "eng").mkdir(parents=True)
        policy = repo / "eng/reproducibility-policy.json"
        verifier = repo / "eng/verify-reproducible-build.py"
        policy.write_text((ENG / "reproducibility-policy.json").read_text(), encoding="utf-8")
        verifier.write_text("# verifier\n", encoding="utf-8")
        (repo / ".gitignore").write_text(
            "artifacts/reproducibility/\n"
            "!eng/reproducibility-policy.json\n"
            "!eng/verify-reproducible-build.py\n"
            "eng/reproducibility-policy.json\n",
            encoding="utf-8",
        )
        subprocess.run(["git", "init", "-q"], cwd=repo, check=True)
        subprocess.run(["git", "add", "-f", "eng/reproducibility-policy.json", "eng/verify-reproducible-build.py", ".gitignore"], cwd=repo, check=True)
        with self.assertRaisesRegex(MODULE.ReproducibilityError, "ignored by Git"):
            MODULE.ensure_git_tracking(repo, policy, verifier, check_git=True)


if __name__ == "__main__":
    unittest.main()
