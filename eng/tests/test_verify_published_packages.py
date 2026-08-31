from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
from unittest import mock
import zipfile
from pathlib import Path

ENG = Path(__file__).resolve().parents[1]
if str(ENG) not in sys.path:
    sys.path.insert(0, str(ENG))


SCRIPT = Path(__file__).resolve().parents[1] / "verify-published-packages.py"
SPEC = importlib.util.spec_from_file_location("verify_published_packages", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class PublishedPackageLicenseResolutionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.published = {
            "version": "0.1.0-preview.1",
            "licenseExpression": "MIT",
        }

    def write_release_manifest(self, directory: str) -> Path:
        path = Path(directory) / "release-manifest.json"
        path.write_text(
            json.dumps(
                {
                    "version": "0.1.0-preview.2",
                    "licenseExpression": "LGPL-3.0-only",
                }
            ),
            encoding="utf-8",
        )
        return path

    def test_published_version_uses_immutable_published_license(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            release_manifest = self.write_release_manifest(directory)
            actual = MODULE.resolve_expected_license_expression(
                "0.1.0-preview.1",
                self.published,
                release_manifest,
            )
            self.assertEqual(actual, "MIT")

    def test_current_candidate_uses_release_manifest_license(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            release_manifest = self.write_release_manifest(directory)
            actual = MODULE.resolve_expected_license_expression(
                "0.1.0-preview.2",
                self.published,
                release_manifest,
            )
            self.assertEqual(actual, "LGPL-3.0-only")

    def test_unknown_historical_version_requires_explicit_license(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            release_manifest = self.write_release_manifest(directory)
            with self.assertRaisesRegex(RuntimeError, "--license-expression"):
                MODULE.resolve_expected_license_expression(
                    "0.0.9",
                    self.published,
                    release_manifest,
                )

            actual = MODULE.resolve_expected_license_expression(
                "0.0.9",
                self.published,
                release_manifest,
                "MIT",
            )
            self.assertEqual(actual, "MIT")

    def test_current_release_readmes_are_loaded_from_docs_nuget(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "eng").mkdir()
            (root / "docs" / "nuget").mkdir(parents=True)
            release_manifest = root / "eng" / "release-manifest.json"
            release_manifest.write_text(
                json.dumps(
                    {
                        "schemaVersion": 2,
                        "version": "0.1.0-preview.3",
                        "repository": "Amir-ESH/TCJ.Framework",
                        "licenseExpression": "LGPL-3.0-only",
                        "releasePackages": {
                            "runtime": [{"id": "TCJ.Core"}],
                            "tooling": [],
                        },
                    }
                ),
                encoding="utf-8",
            )
            expected = b"# TCJ.Core\n"
            (root / "docs" / "nuget" / "TCJ.Core.md").write_bytes(expected)

            readmes = MODULE.expected_readmes_for_current_release(
                "0.1.0-preview.3",
                release_manifest,
            )

            self.assertEqual(readmes, {"TCJ.Core": expected})
            self.assertIsNone(
                MODULE.expected_readmes_for_current_release(
                    "0.1.0-preview.2",
                    release_manifest,
                )
            )


class PublishedPackageManifestTests(unittest.TestCase):
    REPOSITORY = "Amir-ESH/TCJ.Framework"

    @staticmethod
    def published_manifest(*, schema_version: int = 2) -> dict[str, object]:
        common: dict[str, object] = {
            "schemaVersion": schema_version,
            "version": "0.1.0-preview.3",
            "tag": "v0.1.0-preview.3",
            "releaseDate": "2026-08-16",
            "repository": PublishedPackageManifestTests.REPOSITORY,
            "licenseExpression": "LGPL-3.0-only",
        }
        if schema_version == 1:
            common["packages"] = ["TCJ.Core"]
        else:
            common["releasePackages"] = {
                "runtime": [{"id": "TCJ.Core"}],
                "tooling": [],
            }
        return common

    @staticmethod
    def current_manifest() -> dict[str, object]:
        return {
            "schemaVersion": 2,
            "version": "0.1.0-preview.4",
            "repository": PublishedPackageManifestTests.REPOSITORY,
            "licenseExpression": "LGPL-3.0-only",
            "releasePackages": {
                "runtime": [{"id": "TCJ.Core"}],
                "tooling": [
                    {
                        "id": "TCJ.Generators",
                        "assetPath": "analyzers/dotnet/cs",
                        "forbidAssets": ["lib/", "runtime/"],
                    }
                ],
            },
        }

    def write_manifest(self, directory: str, name: str, value: dict[str, object]) -> Path:
        path = Path(directory) / name
        path.write_text(json.dumps(value), encoding="utf-8")
        return path

    def test_schema1_published_manifest_remains_supported(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = self.write_manifest(directory, "published.json", self.published_manifest(schema_version=1))
            loaded = MODULE.load_manifest(path)
            self.assertEqual(loaded["version"], "0.1.0-preview.3")

    def test_schema2_published_manifest_supports_runtime_and_tooling(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            value = self.published_manifest()
            value["releasePackages"] = {
                "runtime": [{"id": "TCJ.Core"}],
                "tooling": [
                    {
                        "id": "TCJ.Generators",
                        "assetPath": "analyzers/dotnet/cs",
                        "forbidAssets": ["lib/", "runtime/"],
                    }
                ],
            }
            path = self.write_manifest(directory, "published.json", value)
            loaded = MODULE.load_manifest(path)
            self.assertEqual(
                [package.package_id for package in MODULE.get_release_packages(loaded)],
                ["TCJ.Core", "TCJ.Generators"],
            )

    def test_current_release_version_uses_current_release_package_set(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            release_path = self.write_manifest(directory, "release.json", self.current_manifest())
            selected = MODULE.resolve_package_manifest(
                "0.1.0-preview.4",
                self.published_manifest(),
                release_path,
            )
            self.assertEqual(
                [package.package_id for package in MODULE.get_release_packages(selected)],
                ["TCJ.Core", "TCJ.Generators"],
            )

    def test_unknown_version_does_not_reuse_wrong_package_set(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            release_path = self.write_manifest(directory, "release.json", self.current_manifest())
            with self.assertRaisesRegex(RuntimeError, "No release package set is recorded"):
                MODULE.resolve_package_manifest(
                    "0.1.0-preview.2",
                    self.published_manifest(),
                    release_path,
                )

    def test_verify_once_includes_tooling_and_uses_type_aware_validation(self) -> None:
        manifest = self.current_manifest()
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            runtime_path = output / "TCJ.Core.0.1.0-preview.4.nupkg"
            tooling_path = output / "TCJ.Generators.0.1.0-preview.4.nupkg"
            runtime_path.write_bytes(b"runtime")
            tooling_path.write_bytes(b"tooling")

            def downloaded(package_id: str, *_args: object, **_kwargs: object) -> Path:
                return runtime_path if package_id == "TCJ.Core" else tooling_path

            with (
                mock.patch.object(MODULE, "version_in_flat_container", return_value=True),
                mock.patch.object(
                    MODULE,
                    "find_catalog_entry",
                    return_value={"listed": True, "published": "2026-08-29T00:00:00Z"},
                ),
                mock.patch.object(MODULE, "download_package", side_effect=downloaded),
                mock.patch.object(MODULE, "validate_primary_package") as validate_primary,
                mock.patch.object(MODULE, "validate_tooling_release_package") as validate_tooling,
            ):
                failures = MODULE.verify_once(
                    manifest,
                    "0.1.0-preview.4",
                    "LGPL-3.0-only",
                    output,
                    "https://flat.example",
                    "https://registration.example",
                )

            self.assertEqual(failures, [])
            self.assertEqual(validate_primary.call_count, 2)
            runtime_call, tooling_call = validate_primary.call_args_list
            self.assertTrue(runtime_call.kwargs["require_runtime_assembly"])
            self.assertFalse(tooling_call.kwargs["require_runtime_assembly"])
            validate_tooling.assert_called_once()
            tooling_spec = validate_tooling.call_args.args[1]
            self.assertEqual(tooling_spec.package_id, "TCJ.Generators")
            self.assertEqual(tooling_spec.asset_path, "analyzers/dotnet/cs/")
            self.assertEqual(tooling_spec.forbid_assets, ("lib/", "runtime/"))

    def test_missing_tooling_package_is_a_failure(self) -> None:
        manifest = self.current_manifest()

        def available(package_id: str, *_args: object, **_kwargs: object) -> bool:
            return package_id != "TCJ.Generators"

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            runtime_path = output / "TCJ.Core.0.1.0-preview.4.nupkg"
            runtime_path.write_bytes(b"runtime")
            with (
                mock.patch.object(MODULE, "version_in_flat_container", side_effect=available),
                mock.patch.object(
                    MODULE,
                    "find_catalog_entry",
                    return_value={"listed": True, "published": "2026-08-29T00:00:00Z"},
                ),
                mock.patch.object(MODULE, "download_package", return_value=runtime_path),
                mock.patch.object(MODULE, "validate_primary_package"),
            ):
                failures = MODULE.verify_once(
                    manifest,
                    "0.1.0-preview.4",
                    "LGPL-3.0-only",
                    output,
                    "https://flat.example",
                    "https://registration.example",
                )

        self.assertIn(
            "TCJ.Generators 0.1.0-preview.4 is absent from the flat container",
            failures,
        )

    def write_tooling_package(self, path: Path, entries: list[str]) -> None:
        nuspec = '''<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>TCJ.Generators</id>
    <version>0.1.0-preview.4</version>
    <authors>TCJ Contributors</authors>
    <description>fixture</description>
  </metadata>
</package>
'''
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr("TCJ.Generators.nuspec", nuspec)
            for entry in entries:
                archive.writestr(entry, b"fixture")

    def test_tooling_package_missing_required_asset_path_fails(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / "TCJ.Generators.0.1.0-preview.4.nupkg"
            self.write_tooling_package(package, ["README.md"])
            spec = MODULE.ToolingPackageSpec(
                "TCJ.Generators",
                "analyzers/dotnet/cs/",
                ("lib/", "runtime/"),
            )
            with self.assertRaisesRegex(ValueError, "missing required asset path"):
                MODULE.validate_tooling_release_package(package, spec, "0.1.0-preview.4")

    def test_tooling_package_with_runtime_assets_fails(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / "TCJ.Generators.0.1.0-preview.4.nupkg"
            self.write_tooling_package(
                package,
                [
                    "analyzers/dotnet/cs/TCJ.Generators.dll",
                    "lib/net10.0/TCJ.Generators.dll",
                ],
            )
            spec = MODULE.ToolingPackageSpec(
                "TCJ.Generators",
                "analyzers/dotnet/cs/",
                ("lib/", "runtime/"),
            )
            with self.assertRaisesRegex(ValueError, "forbidden asset path lib/"):
                MODULE.validate_tooling_release_package(package, spec, "0.1.0-preview.4")


if __name__ == "__main__":
    unittest.main()
