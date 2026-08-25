#!/usr/bin/env python3
"""Validate TCJ release lifecycle metadata and, optionally, built NuGet packages."""

from __future__ import annotations

import argparse
import json
import re
import sys
import zipfile
from pathlib import Path
import xml.etree.ElementTree as ET

from sbom_common import get_release_package_ids, get_release_packages

SEMVER_PATTERN = re.compile(
    r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
    r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
)
RELEASE_STATUSES = {"development", "ready"}
PACKAGE_README_DIRECTORY = Path("docs") / "nuget"
# preview.2 is already immutable on NuGet.org with the historical GitHub README.
# Enforce the corrected package-specific README contract from the next version onward.
PACKAGE_README_POLICY_MIN_VERSION = "0.1.0-preview.3"
PACKAGE_README_PINNED_DOCS_MIN_VERSION = "0.1.0-preview.4"
FORBIDDEN_PACKAGE_README_HTML = re.compile(
    r"<\s*/?\s*(?:a|br|div|img|p|picture|span|table|tbody|td|th|thead|tr)\b",
    re.IGNORECASE,
)
MARKDOWN_LINK_PATTERN = re.compile(r"!?\[[^\]]*\]\((?P<target>[^)]+)\)")


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
        "status",
        "version",
        "tag",
        "releaseDate",
        "repository",
        "licenseExpression",
    }
    missing = sorted(required.difference(data))
    if missing:
        fail(f"Release manifest is missing fields: {', '.join(missing)}")

    if data["schemaVersion"] != 2:
        fail("Unsupported release manifest schemaVersion; expected 2.")

    status = str(data["status"])
    if status not in RELEASE_STATUSES:
        fail("Manifest status must be 'development' or 'ready'.")

    version = str(data["version"])
    if not SEMVER_PATTERN.fullmatch(version):
        fail(f"Manifest version is not valid semantic versioning: {version}")

    if data["tag"] != f"v{version}":
        fail("Manifest tag must be the version prefixed with 'v'.")

    release_date = data["releaseDate"]
    if status == "development":
        if release_date is not None:
            fail("A development manifest must set releaseDate to null.")
    elif not isinstance(release_date, str) or not re.fullmatch(
        r"\d{4}-\d{2}-\d{2}", release_date
    ):
        fail("A ready manifest must use YYYY-MM-DD for releaseDate.")

    license_expression = str(data["licenseExpression"]).strip()
    if not license_expression:
        fail("Manifest licenseExpression must be a non-empty SPDX expression.")

    try:
        get_release_packages(data)
    except ValueError as error:
        fail(str(error))

    return data


def read_msbuild_version(root: Path) -> str:
    packaging = ET.parse(root / "eng" / "Packaging.props").getroot()
    version = packaging.findtext("./PropertyGroup/Version")
    if not version:
        fail("eng/Packaging.props does not define Version.")
    return version.strip()


def read_msbuild_license_expression(root: Path) -> str:
    packaging = ET.parse(root / "eng" / "Packaging.props").getroot()
    license_expression = packaging.findtext("./PropertyGroup/PackageLicenseExpression")
    if not license_expression or not license_expression.strip():
        fail("eng/Packaging.props does not define PackageLicenseExpression.")
    return license_expression.strip()


def read_published_manifest(root: Path) -> dict[str, object]:
    path = root / "eng" / "published-release.json"
    data = json.loads(read_text(path))

    required = {
        "schemaVersion",
        "version",
        "tag",
        "releaseDate",
        "repository",
        "licenseExpression",
        "packages",
    }
    missing = sorted(required.difference(data))
    if missing:
        fail(f"Published release manifest is missing fields: {', '.join(missing)}")
    if data["schemaVersion"] != 1:
        fail("Unsupported published release manifest schemaVersion; expected 1.")

    version = str(data["version"])
    if not SEMVER_PATTERN.fullmatch(version):
        fail(f"Published version is not valid semantic versioning: {version}")
    if data["tag"] != f"v{version}":
        fail("Published release tag must be the version prefixed with 'v'.")

    license_expression = str(data["licenseExpression"]).strip()
    if not license_expression:
        fail("Published release licenseExpression must be a non-empty SPDX expression.")

    try:
        get_release_package_ids(data, "runtime")
    except ValueError as error:
        fail(str(error))

    return data


def read_package_validation_config(root: Path) -> str:
    path = root / "eng" / "PackageValidation.props"
    document = ET.parse(path).getroot()

    published_version = document.findtext(
        "./PropertyGroup/TCJPublishedPackageVersion"
    )
    enabled = document.findtext("./PropertyGroup/EnablePackageValidation")
    baseline = document.findtext(
        "./PropertyGroup/PackageValidationBaselineVersion"
    )

    if not published_version or not published_version.strip():
        fail("eng/PackageValidation.props must define TCJPublishedPackageVersion.")
    if (enabled or "").strip().lower() != "true":
        fail("eng/PackageValidation.props must enable package validation.")
    if (baseline or "").strip() != "$(TCJPublishedPackageVersion)":
        fail(
            "PackageValidationBaselineVersion must reference "
            "$(TCJPublishedPackageVersion)."
        )

    packaging = ET.parse(root / "eng" / "Packaging.props").getroot()
    imports = [
        item.attrib.get("Project", "")
        for item in packaging.findall("./Import")
    ]
    if not any(value.endswith("PackageValidation.props") for value in imports):
        fail("eng/Packaging.props must import eng/PackageValidation.props.")

    return published_version.strip()


def semver_key(version: str) -> tuple[object, ...]:
    match = SEMVER_PATTERN.fullmatch(version)
    if match is None:
        fail(f"Invalid semantic version: {version}")

    major, minor, patch = (int(match.group(index)) for index in range(1, 4))
    prerelease = match.group(4)
    if prerelease is None:
        return (major, minor, patch, 1, ())

    identifiers: list[tuple[int, object]] = []
    for identifier in prerelease.split("."):
        if identifier.isdigit():
            identifiers.append((0, int(identifier)))
        else:
            identifiers.append((1, identifier))
    return (major, minor, patch, 0, tuple(identifiers))


def read_project_package_ids(root: Path, expected_package_ids: list[str]) -> list[str]:
    package_ids: list[str] = []
    for expected_package_id in expected_package_ids:
        project = root / "src" / expected_package_id / f"{expected_package_id}.csproj"
        if not project.is_file():
            fail(f"Release package project is missing: {project.relative_to(root)}")

        tree = ET.parse(project).getroot()
        package_id = tree.findtext("./PropertyGroup/PackageId")
        if not package_id:
            fail(f"Project does not define PackageId: {project.relative_to(root)}")
        resolved_package_id = package_id.strip()
        if project.stem != resolved_package_id:
            fail(
                f"Project name {project.stem!r} must match PackageId "
                f"{resolved_package_id!r} because package README selection uses "
                "$(MSBuildProjectName)."
            )
        package_ids.append(resolved_package_id)
    return package_ids


def package_readme_source(root: Path, package_id: str) -> Path:
    return root / PACKAGE_README_DIRECTORY / f"{package_id}.md"


def validate_package_readme_text(text: str, package_id: str, source: str, version: str | None = None) -> None:
    if not text.strip():
        fail(f"Package README is empty: {source}")
    if package_id.casefold() not in text.casefold():
        fail(f"Package README {source} must identify {package_id}.")
    if FORBIDDEN_PACKAGE_README_HTML.search(text):
        fail(
            f"Package README {source} contains raw HTML. NuGet package READMEs "
            "must use repository-approved Markdown only."
        )

    for match in MARKDOWN_LINK_PATTERN.finditer(text):
        target = match.group("target").strip().strip("<>")
        if not target:
            fail(f"Package README {source} contains an empty Markdown link target.")
        target_url = target.split(None, 1)[0]
        if target_url.startswith(("https://", "mailto:", "#")):
            if (
                version is not None
                and semver_key(version) >= semver_key(PACKAGE_README_PINNED_DOCS_MIN_VERSION)
                and target_url.startswith("https://github.com/Amir-ESH/TCJ.Framework/blob/")
                and not target_url.startswith(
                    f"https://github.com/Amir-ESH/TCJ.Framework/blob/v{version}/"
                )
            ):
                fail(
                    f"Package README {source} must pin repository documentation links "
                    f"to immutable tag v{version}; found {target_url!r}."
                )
            continue
        fail(
            f"Package README {source} contains a relative or unsupported link "
            f"target: {target_url!r}. Use an absolute HTTPS URL or an in-document anchor."
        )


def validate_package_readme_configuration(root: Path) -> None:
    packaging_path = root / "eng" / "Packaging.props"
    packaging = ET.parse(packaging_path).getroot()
    readme_name = packaging.findtext("./PropertyGroup/PackageReadmeFile")
    if (readme_name or "").strip() != "README.md":
        fail("eng/Packaging.props PackageReadmeFile must remain README.md.")

    expected_source = (
        "$(MSBuildThisFileDirectory)../docs/nuget/$(MSBuildProjectName).md"
    )
    readme_items = [
        item
        for item in packaging.findall("./ItemGroup/None")
        if item.attrib.get("Include", "").replace("\\", "/") == expected_source
    ]
    if len(readme_items) != 1:
        fail(
            "eng/Packaging.props must pack exactly one package README source from "
            "docs/nuget/$(MSBuildProjectName).md."
        )

    item = readme_items[0]
    if item.attrib.get("Pack", "").strip().lower() != "true":
        fail('The package README item must set Pack="true".')

    package_path = item.attrib.get("PackagePath", "").replace("\\", "/").strip()
    if package_path != readme_name.strip():
        fail(
            "The package README item PackagePath must exactly match "
            f"PackageReadmeFile ({readme_name.strip()})."
        )

    if item.attrib.get("Link", "").strip():
        fail(
            "The package README item must not use Link to rename the packed file; "
            "PackagePath must define the package-internal README path."
        )


def validate_package_readme_sources(root: Path, package_ids: list[str], version: str) -> None:
    validate_package_readme_configuration(root)
    for package_id in package_ids:
        path = package_readme_source(root, package_id)
        text = read_text(path)
        validate_package_readme_text(
            text,
            package_id,
            path.relative_to(root).as_posix(),
            version,
        )


def readme_policy_required(version: str) -> bool:
    return semver_key(version) >= semver_key(PACKAGE_README_POLICY_MIN_VERSION)


def validate_ready_changelog(root: Path, version: str, release_date: str) -> None:
    changelog = read_text(root / "CHANGELOG.md")
    heading = f"## [{version}] - {release_date}"
    if heading not in changelog:
        fail(f"CHANGELOG.md must contain the release heading: {heading}")

    section_pattern = re.compile(
        rf"^## \[{re.escape(version)}\] - {re.escape(release_date)}\n"
        rf"(?P<body>.*?)(?=^## |^\[Unreleased\]:|\Z)",
        re.MULTILINE | re.DOTALL,
    )
    match = section_pattern.search(changelog)
    if not match or not match.group("body").strip():
        fail(f"CHANGELOG.md has no release notes for {version}.")


def validate_development_changelog(root: Path) -> None:
    changelog = read_text(root / "CHANGELOG.md")
    section_pattern = re.compile(
        r"^## \[Unreleased\]\n(?P<body>.*?)(?=^## |\Z)",
        re.MULTILINE | re.DOTALL,
    )
    match = section_pattern.search(changelog)
    if not match:
        fail("CHANGELOG.md must contain an [Unreleased] section.")
    body = match.group("body").strip()
    if not body:
        fail("CHANGELOG.md [Unreleased] must describe current development changes.")


def validate_public_status_text(root: Path) -> None:
    checked = [
        root / "README.md",
        root / "docs" / "README.md",
        root / "docs" / "getting-started.md",
    ]
    forbidden = (
        "nuget packages have not been published yet",
        "preview packages are not published yet",
        "nuget packages are not published yet",
    )
    for path in checked:
        text = read_text(path).lower()
        for phrase in forbidden:
            if phrase in text:
                fail(
                    "Public documentation still says packages are unpublished: "
                    f"{path.relative_to(root)}"
                )


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
    expected_license_expression: str,
    *,
    expected_readme: bytes | None = None,
    enforce_readme_policy: bool = False,
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
            "license": expected_license_expression,
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

        readme_bytes = archive.read("README.md")
        if expected_readme is not None and readme_bytes != expected_readme:
            fail(
                f"{path.name} README.md does not match the repository source "
                f"{PACKAGE_README_DIRECTORY.as_posix()}/{package_id}.md."
            )
        if enforce_readme_policy or expected_readme is not None:
            try:
                readme_text = readme_bytes.decode("utf-8")
            except UnicodeDecodeError as error:
                fail(f"{path.name} README.md must be valid UTF-8: {error}")
            validate_package_readme_text(readme_text, package_id, f"{path.name}:README.md", version)

        dlls = [name for name in names if re.fullmatch(r"lib/net10\.0/[^/]+\.dll", name)]
        if not dlls:
            fail(f"{path.name} has no net10.0 library assembly.")


def validate_symbol_package(path: Path) -> None:
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        pdbs = [name for name in names if re.fullmatch(r"lib/net10\.0/[^/]+\.pdb", name)]
        if not pdbs:
            fail(f"{path.name} has no net10.0 portable PDB.")




def is_tooling_package(path: Path) -> bool:
    with zipfile.ZipFile(path) as archive:
        return any(
            name.casefold().startswith("analyzers/dotnet/cs/")
            for name in archive.namelist()
        )


def validate_tooling_package(path: Path) -> None:
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if not any(name.casefold().startswith("analyzers/dotnet/cs/") for name in names):
            fail(f"{path.name} has no analyzer/compiler tooling assets.")
        if any(name.casefold().startswith(("lib/", "runtime/")) for name in names):
            fail(f"{path.name} must not contain runtime package assets.")

def validate_packages(root: Path, package_directory: Path, manifest: dict[str, object]) -> None:
    version = str(manifest["version"])
    repository = str(manifest["repository"])
    package_ids = list(get_release_package_ids(manifest))

    if not package_directory.is_absolute():
        package_directory = root / package_directory
    if not package_directory.is_dir():
        fail(f"Package directory does not exist: {package_directory}")

    expected_primary = {f"{package_id}.{version}.nupkg" for package_id in package_ids}
    expected_symbols = {f"{package_id}.{version}.snupkg" for package_id in package_ids}
    all_primary_paths = sorted(package_directory.glob("*.nupkg"))
    tooling_paths = {path.name for path in all_primary_paths if is_tooling_package(path)}
    actual_primary = {path.name for path in all_primary_paths if path.name not in tooling_paths}
    actual_symbols = {path.name for path in package_directory.glob("*.snupkg")}

    for tooling_path in all_primary_paths:
        if tooling_path.name in tooling_paths:
            validate_tooling_package(tooling_path)

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
        readme_path = package_readme_source(root, package_id)
        validate_primary_package(
            package_directory / f"{package_id}.{version}.nupkg",
            package_id,
            version,
            repository,
            str(manifest["licenseExpression"]),
            expected_readme=readme_path.read_bytes(),
            enforce_readme_policy=True,
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
    parser.add_argument(
        "--require-ready",
        action="store_true",
        help="Fail unless the manifest is ready for an immutable public release.",
    )
    args = parser.parse_args()

    root = args.repository_root.resolve()
    manifest = read_manifest(root)
    status = str(manifest["status"])
    version = str(manifest["version"])
    package_ids = list(get_release_package_ids(manifest))

    if args.require_ready and status != "ready":
        fail(
            "Release manifest is not ready. Set status to 'ready', add a releaseDate, "
            "and move the version notes from [Unreleased] to a dated changelog section."
        )

    packaging_version = read_msbuild_version(root)
    if packaging_version != version:
        fail(
            f"eng/Packaging.props version {packaging_version!r} does not match "
            f"release manifest version {version!r}."
        )

    packaging_license = read_msbuild_license_expression(root)
    manifest_license = str(manifest["licenseExpression"])
    if packaging_license != manifest_license:
        fail(
            f"eng/Packaging.props license {packaging_license!r} does not match "
            f"release manifest license {manifest_license!r}."
        )

    published_manifest = read_published_manifest(root)
    published_version = str(published_manifest["version"])
    validation_baseline = read_package_validation_config(root)

    if validation_baseline != published_version:
        fail(
            "Package validation baseline does not match the immutable published "
            f"release: {validation_baseline!r} != {published_version!r}."
        )
    if published_manifest["repository"] != manifest["repository"]:
        fail("Published and development manifests must use the same repository.")
    published_package_ids = set(get_release_package_ids(published_manifest, "runtime"))
    current_runtime_package_ids = set(get_release_package_ids(manifest, "runtime"))
    if not published_package_ids.issubset(current_runtime_package_ids):
        fail("Published runtime packages must remain present in the current release manifest.")
    if semver_key(version) <= semver_key(published_version):
        fail(
            f"Development version {version!r} must be newer than published "
            f"baseline {published_version!r}."
        )

    project_package_ids = read_project_package_ids(root, package_ids)
    if set(project_package_ids) != set(package_ids) or len(project_package_ids) != len(package_ids):
        fail(
            "Project PackageId set does not match release manifest. "
            f"Expected {sorted(package_ids)}, found {sorted(project_package_ids)}."
        )

    validate_package_readme_sources(root, package_ids, version)

    if status == "ready":
        validate_ready_changelog(root, version, str(manifest["releaseDate"]))
    else:
        validate_development_changelog(root)

    validate_public_status_text(root)

    if args.package_directory is not None:
        validate_packages(root, args.package_directory, manifest)

    if status == "ready":
        print(f"Release metadata verified: {manifest['tag']} ({manifest['releaseDate']})")
    else:
        print(f"Development metadata verified: {version} (not release-ready)")
    print(f"Package validation baseline: {published_version}")
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