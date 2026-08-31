from __future__ import annotations

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


SCRIPT = Path(__file__).resolve().parents[1] / "verify-release.py"
SPEC = importlib.util.spec_from_file_location("verify_release", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class PublishedReleaseManifestTests(unittest.TestCase):
    def write_manifest(self, root: Path, value: dict[str, object]) -> None:
        eng = root / "eng"
        eng.mkdir(parents=True)
        (eng / "published-release.json").write_text(
            json.dumps(value),
            encoding="utf-8",
        )

    @staticmethod
    def common_manifest(schema_version: int) -> dict[str, object]:
        return {
            "schemaVersion": schema_version,
            "version": "0.1.0-preview.3",
            "tag": "v0.1.0-preview.3",
            "releaseDate": "2026-08-16",
            "repository": "Amir-ESH/TCJ.Framework",
            "licenseExpression": "LGPL-3.0-only",
        }

    def test_schema1_published_manifest_remains_supported(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = self.common_manifest(1)
            manifest["packages"] = ["TCJ.Core"]
            self.write_manifest(root, manifest)

            loaded = MODULE.read_published_manifest(root)

            self.assertEqual(loaded["version"], "0.1.0-preview.3")

    def test_schema2_published_manifest_supports_tooling(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = self.common_manifest(2)
            manifest["releasePackages"] = {
                "runtime": [{"id": "TCJ.Core"}],
                "tooling": [
                    {
                        "id": "TCJ.Generators",
                        "assetPath": "analyzers/dotnet/cs",
                        "forbidAssets": ["lib/", "runtime/"],
                    }
                ],
            }
            self.write_manifest(root, manifest)

            loaded = MODULE.read_published_manifest(root)

            self.assertEqual(
                [package.package_id for package in MODULE.get_release_packages(loaded)],
                ["TCJ.Core", "TCJ.Generators"],
            )


class PublicPackageInventoryTests(unittest.TestCase):
    def write_public_docs(self, root: Path, contents: dict[str, str]) -> None:
        for relative_path, text in contents.items():
            path = root / relative_path
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")

    def test_public_package_inventory_accepts_all_release_packages(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            complete = "TCJ.Core\nTCJ.Generators\n"
            self.write_public_docs(
                root,
                {
                    "README.md": complete,
                    "docs/README.md": complete,
                    "docs/packages/index.md": complete,
                },
            )

            MODULE.validate_public_package_inventory(
                root, ["TCJ.Core", "TCJ.Generators"]
            )

    def test_public_package_inventory_rejects_missing_tooling_package(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_public_docs(
                root,
                {
                    "README.md": "TCJ.Core\n",
                    "docs/README.md": "TCJ.Core\nTCJ.Generators\n",
                    "docs/packages/index.md": "TCJ.Core\nTCJ.Generators\n",
                },
            )

            with self.assertRaisesRegex(ValueError, "README.md does not mention TCJ.Generators"):
                MODULE.validate_public_package_inventory(
                    root, ["TCJ.Core", "TCJ.Generators"]
                )


class ReleasePackageLicenseTests(unittest.TestCase):
    PACKAGE_ID = "TCJ.Core"
    VERSION = "0.1.0-preview.2"
    REPOSITORY = "Amir-ESH/TCJ.Framework"

    def write_package(
        self,
        path: Path,
        *,
        license_expression: str = "LGPL-3.0-only",
        readme: str | bytes | None = None,
    ) -> None:
        nuspec = f'''<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>{self.PACKAGE_ID}</id>
    <version>{self.VERSION}</version>
    <authors>TCJ Contributors</authors>
    <description>TCJ release verification fixture.</description>
    <projectUrl>https://github.com/{self.REPOSITORY}</projectUrl>
    <repository type="git" url="https://github.com/{self.REPOSITORY}.git" />
    <license type="expression">{license_expression}</license>
    <readme>README.md</readme>
  </metadata>
</package>
'''
        if readme is None:
            readme = f"# {self.PACKAGE_ID}\n\n[Repository](https://github.com/{self.REPOSITORY})\n"

        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr(f"{self.PACKAGE_ID}.nuspec", nuspec)
            archive.writestr("README.md", readme)
            archive.writestr("LICENSE.txt", "license")
            archive.writestr(f"lib/net10.0/{self.PACKAGE_ID}.dll", b"fixture")

    def test_lgpl_expression_is_accepted_for_current_packages(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / f"{self.PACKAGE_ID}.{self.VERSION}.nupkg"
            self.write_package(package)

            MODULE.validate_primary_package(
                package,
                self.PACKAGE_ID,
                self.VERSION,
                self.REPOSITORY,
                "LGPL-3.0-only",
            )

    def test_legacy_mit_package_is_accepted_when_manifest_expects_mit(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / f"{self.PACKAGE_ID}.{self.VERSION}.nupkg"
            self.write_package(package, license_expression="MIT")

            MODULE.validate_primary_package(
                package,
                self.PACKAGE_ID,
                self.VERSION,
                self.REPOSITORY,
                "MIT",
            )

    def test_wrong_license_expression_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / f"{self.PACKAGE_ID}.{self.VERSION}.nupkg"
            self.write_package(package, license_expression="MIT")

            with self.assertRaisesRegex(ValueError, "LGPL-3.0-only"):
                MODULE.validate_primary_package(
                    package,
                    self.PACKAGE_ID,
                    self.VERSION,
                    self.REPOSITORY,
                    "LGPL-3.0-only",
                )

    def test_raw_html_readme_is_rejected_when_policy_is_enforced(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / f"{self.PACKAGE_ID}.{self.VERSION}.nupkg"
            self.write_package(
                package,
                readme=f'<p align="center">{self.PACKAGE_ID}</p>',
            )

            with self.assertRaisesRegex(ValueError, "raw HTML"):
                MODULE.validate_primary_package(
                    package,
                    self.PACKAGE_ID,
                    self.VERSION,
                    self.REPOSITORY,
                    "LGPL-3.0-only",
                    enforce_readme_policy=True,
                )

    def test_relative_readme_link_is_rejected_when_policy_is_enforced(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / f"{self.PACKAGE_ID}.{self.VERSION}.nupkg"
            self.write_package(
                package,
                readme=f"# {self.PACKAGE_ID}\n\n[Guide](../docs/guide.md)\n",
            )

            with self.assertRaisesRegex(ValueError, "relative or unsupported link"):
                MODULE.validate_primary_package(
                    package,
                    self.PACKAGE_ID,
                    self.VERSION,
                    self.REPOSITORY,
                    "LGPL-3.0-only",
                    enforce_readme_policy=True,
                )

    def test_packed_readme_must_match_repository_source(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / f"{self.PACKAGE_ID}.{self.VERSION}.nupkg"
            self.write_package(package)

            with self.assertRaisesRegex(ValueError, "does not match"):
                MODULE.validate_primary_package(
                    package,
                    self.PACKAGE_ID,
                    self.VERSION,
                    self.REPOSITORY,
                    "LGPL-3.0-only",
                    expected_readme=b"different readme",
                )

    def test_readme_policy_starts_with_preview_3(self) -> None:
        self.assertFalse(MODULE.readme_policy_required("0.1.0-preview.2"))
        self.assertTrue(MODULE.readme_policy_required("0.1.0-preview.3"))
        self.assertTrue(MODULE.readme_policy_required("0.1.0-preview.4"))


class ReleaseProjectInventoryTests(unittest.TestCase):
    def test_build_time_projects_are_not_treated_as_release_packages(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runtime_project = root / "src/TCJ.Core/TCJ.Core.csproj"
            analyzer_project = root / "src/TCJ.Analyzers/TCJ.Analyzers.csproj"
            runtime_project.parent.mkdir(parents=True)
            analyzer_project.parent.mkdir(parents=True)
            runtime_project.write_text(
                "<Project><PropertyGroup><PackageId>TCJ.Core</PackageId></PropertyGroup></Project>",
                encoding="utf-8",
            )
            analyzer_project.write_text("<Project />", encoding="utf-8")

            package_ids = MODULE.read_project_package_ids(root, ["TCJ.Core"])

            self.assertEqual(["TCJ.Core"], package_ids)


class ReleasePackageReadmeConfigurationTests(unittest.TestCase):
    SOURCE = "$(MSBuildThisFileDirectory)..\\docs\\nuget\\$(MSBuildProjectName).md"

    def write_packaging(
        self,
        root: Path,
        *,
        package_readme_file: str = "README.md",
        package_path: str = "README.md",
        link: str | None = None,
    ) -> None:
        eng = root / "eng"
        eng.mkdir(parents=True)
        link_attribute = "" if link is None else f' Link="{link}"'
        (eng / "Packaging.props").write_text(
            f'''<Project>
  <PropertyGroup>
    <PackageReadmeFile>{package_readme_file}</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <None Include="{self.SOURCE}" Pack="true" PackagePath="{package_path}"{link_attribute} />
  </ItemGroup>
</Project>
''',
            encoding="utf-8",
        )

    def test_package_readme_configuration_uses_declared_package_path(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_packaging(root)

            MODULE.validate_package_readme_configuration(root)

    def test_package_root_directory_does_not_rename_readme(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_packaging(root, package_path="/", link="README.md")

            with self.assertRaisesRegex(ValueError, "PackagePath must exactly match"):
                MODULE.validate_package_readme_configuration(root)

    def test_package_readme_path_must_match_package_readme_file(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_packaging(
                root,
                package_readme_file="README.md",
                package_path="docs/README.md",
            )

            with self.assertRaisesRegex(ValueError, "PackagePath must exactly match"):
                MODULE.validate_package_readme_configuration(root)

    def test_link_cannot_be_used_to_rename_package_readme(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_packaging(root, link="README.md")

            with self.assertRaisesRegex(ValueError, "must not use Link"):
                MODULE.validate_package_readme_configuration(root)


if __name__ == "__main__":
    unittest.main()
