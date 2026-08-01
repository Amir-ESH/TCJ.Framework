#!/usr/bin/env python3
"""Validate TCJ release metadata and, optionally, built NuGet packages."""

from __future__ import annotations

import argparse
import json
import re
import sys
import zipfile
from pathlib import Path
import xml.etree.ElementTree as ET

SEMVER_PATTERN = re.compile(
    r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
    r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
)


def fail(message: str) -> None:
    raise ValueError(message)


def read_text(path: Path) -> str:
    if not path.is_file():
        fail(f"Required file does not exist: {path}")
    return path.read_text(encoding="utf-8")


def read_manifest(root: Path) -> dict[str, object]:
    manifest_path = root / "eng" / "release-manifest.json"
    data = json.loads(read_text(manifest_path))

    required = {
        "schemaVersion",
        "version",
        "tag",
        "releaseDate",
        "repository",
        "packages",
    }
    missing = sorted(required.difference(data))
    if missing:
        fail(f"Release manifest is missing fields: {', '.join(missing)}")

    if data["schemaVersion"] != 1:
        fail("Unsupported release manifest schemaVersion.")

    version = str(data["version"])
    if not SEMVER_PATTERN.fullmatch(version):
        fail(f"Manifest version is not valid semantic versioning: {version}")

    if data["tag"] != f"v{version}":
        fail("Manifest tag must be the version prefixed with 'v'.")

    if not re.fullmatch(r"\d{4}-\d{2}-\d{2}", str(data["releaseDate"])):
        fail("Manifest releaseDate must use YYYY-MM-DD format.")

    packages = data["packages"]
    if not isinstance(packages, list) or not packages:
        fail("Manifest packages must be a non-empty array.")

    if len(packages) != len(set(packages)):
        fail("Manifest package IDs must be unique.")

    return data


def read_msbuild_version(root: Path) -> str:
    packaging = ET.parse(root / "eng" / "Packaging.props").getroot()
    version = packaging.findtext("./PropertyGroup/Version")
    if not version:
        fail("eng/Packaging.props does not define Version.")
    return version.strip()


def read_project_package_ids(root: Path) -> list[str]:
    package_ids: list[str] = []
    for project in sorted((root / "src").glob("*/*.csproj")):
        tree = ET.parse(project).getroot()
        package_id = tree.findtext("./PropertyGroup/PackageId")
        if not package_id:
            fail(f"Project does not define PackageId: {project.relative_to(root)}")
        package_ids.append(package_id.strip())
    return package_ids


def validate_changelog(root: Path, version: str, release_date: str) -> None:
    changelog = read_text(root / "CHANGELOG.md")
    heading = f"## [{version}] - {release_date}"
    if heading not in changelog:
        fail(f"CHANGELOG.md must contain the release heading: {heading}")

    invalid = f"## [{version}] - Unreleased"
    if invalid in changelog:
        fail(f"CHANGELOG.md still marks {version} as Unreleased.")

    section_pattern = re.compile(
        rf"^## \[{re.escape(version)}\] - {re.escape(release_date)}\n(?P<body>.*?)(?=^## |\Z)",
        re.MULTILINE | re.DOTALL,
    )
    match = section_pattern.search(changelog)
    if not match or not match.group("body").strip():
        fail(f"CHANGELOG.md has no release notes for {version}.")


def validate_public_status_text(root: Path) -> None:
    checked = [root / "README.md", root / "docs" / "README.md", root / "docs" / "getting-started.md"]
    forbidden = (
        "nuget packages have not been published yet",
        "preview packages are not published yet",
        "nuget packages are not published yet",
    )
    for path in checked:
        text = read_text(path).lower()
        for phrase in forbidden:
            if phrase in text:
                fail(f"Release-ready documentation still says packages are unpublished: {path.relative_to(root)}")


def xml_metadata(nuspec_bytes: bytes) -> dict[str, str]:
    root = ET.fromstring(nuspec_bytes)
    namespace_match = re.match(r"\{([^}]+)\}", root.tag)
    ns = {"n": namespace_match.group(1)} if namespace_match else {}
    prefix = "n:" if ns else ""
    metadata = root.find(f"{prefix}metadata", ns)
    if metadata is None:
        fail("NuGet package has no metadata element.")

    def value(name: str) -> str:
        element = metadata.find(f"{prefix}{name}", ns)
        return "" if element is None or element.text is None else element.text.strip()

    repository = metadata.find(f"{prefix}repository", ns)
    license_element = metadata.find(f"{prefix}license", ns)

    return {
        "id": value("id"),
        "version": value("version"),
        "authors": value("authors"),
        "description": value("description"),
        "projectUrl": value("projectUrl"),
        "readme": value("readme"),
        "repositoryUrl": "" if repository is None else repository.attrib.get("url", "").strip(),
        "repositoryType": "" if repository is None else repository.attrib.get("type", "").strip(),
        "license": "" if license_element is None or license_element.text is None else license_element.text.strip(),
        "licenseType": "" if license_element is None else license_element.attrib.get("type", "").strip(),
    }


def validate_primary_package(
    path: Path,
    package_id: str,
    version: str,
    repository: str,
) -> None:
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        nuspecs = [name for name in names if name.lower().endswith(".nuspec")]
        if len(nuspecs) != 1:
            fail(f"{path.name} must contain exactly one .nuspec file.")

        metadata = xml_metadata(archive.read(nuspecs[0]))
        expected_project_url = f"https://github.com/{repository}"
        expected_repository_url = f"{expected_project_url}.git"

        expected = {
            "id": package_id,
            "version": version,
            "projectUrl": expected_project_url,
            "repositoryUrl": expected_repository_url,
            "repositoryType": "git",
            "license": "MIT",
            "licenseType": "expression",
            "readme": "README.md",
        }
        for key, expected_value in expected.items():
            if metadata[key].lower() != expected_value.lower():
                fail(
                    f"{path.name} metadata {key!r} is {metadata[key]!r}; "
                    f"expected {expected_value!r}."
                )

        if not metadata["authors"]:
            fail(f"{path.name} has no authors metadata.")
        if not metadata["description"]:
            fail(f"{path.name} has no description metadata.")

        required_files = {"README.md", "LICENSE.txt"}
        missing = sorted(required_files.difference(names))
        if missing:
            fail(f"{path.name} is missing package files: {', '.join(missing)}")

        dlls = [name for name in names if re.fullmatch(r"lib/net10\.0/[^/]+\.dll", name)]
        if not dlls:
            fail(f"{path.name} has no net10.0 library assembly.")


def validate_symbol_package(path: Path) -> None:
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        pdbs = [name for name in names if re.fullmatch(r"lib/net10\.0/[^/]+\.pdb", name)]
        if not pdbs:
            fail(f"{path.name} has no net10.0 portable PDB.")


def validate_packages(root: Path, package_directory: Path, manifest: dict[str, object]) -> None:
    version = str(manifest["version"])
    repository = str(manifest["repository"])
    package_ids = [str(item) for item in manifest["packages"]]

    if not package_directory.is_absolute():
        package_directory = root / package_directory
    if not package_directory.is_dir():
        fail(f"Package directory does not exist: {package_directory}")

    expected_primary = {f"{package_id}.{version}.nupkg" for package_id in package_ids}
    expected_symbols = {f"{package_id}.{version}.snupkg" for package_id in package_ids}
    actual_primary = {path.name for path in package_directory.glob("*.nupkg")}
    actual_symbols = {path.name for path in package_directory.glob("*.snupkg")}

    if actual_primary != expected_primary:
        fail(
            "Primary package set mismatch. "
            f"Expected {sorted(expected_primary)}, found {sorted(actual_primary)}."
        )
    if actual_symbols != expected_symbols:
        fail(
            "Symbol package set mismatch. "
            f"Expected {sorted(expected_symbols)}, found {sorted(actual_symbols)}."
        )

    for package_id in package_ids:
        validate_primary_package(
            package_directory / f"{package_id}.{version}.nupkg",
            package_id,
            version,
            repository,
        )
        validate_symbol_package(
            package_directory / f"{package_id}.{version}.snupkg"
        )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--repository-root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
    )
    parser.add_argument("--package-directory", type=Path)
    args = parser.parse_args()

    root = args.repository_root.resolve()
    manifest = read_manifest(root)
    version = str(manifest["version"])
    release_date = str(manifest["releaseDate"])
    package_ids = [str(item) for item in manifest["packages"]]

    packaging_version = read_msbuild_version(root)
    if packaging_version != version:
        fail(
            f"eng/Packaging.props version {packaging_version!r} does not match "
            f"release manifest version {version!r}."
        )

    project_package_ids = read_project_package_ids(root)
    if set(project_package_ids) != set(package_ids) or len(project_package_ids) != len(package_ids):
        fail(
            "Project PackageId set does not match release manifest. "
            f"Expected {sorted(package_ids)}, found {sorted(project_package_ids)}."
        )

    validate_changelog(root, version, release_date)
    validate_public_status_text(root)

    if args.package_directory is not None:
        validate_packages(root, args.package_directory, manifest)

    print(f"Release metadata verified: {manifest['tag']} ({release_date})")
    for package_id in package_ids:
        print(f"  {package_id}")
    if args.package_directory is not None:
        print(f"Package contents verified: {args.package_directory}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, ET.ParseError, json.JSONDecodeError, zipfile.BadZipFile) as error:
        print(f"Release verification failed: {error}", file=sys.stderr)
        raise SystemExit(1)
