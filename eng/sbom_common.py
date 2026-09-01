#!/usr/bin/env python3
"""Shared CycloneDX SBOM helpers for TCJ release automation."""

from __future__ import annotations

import hashlib
import json
import os
import re
import subprocess
import uuid
import xml.etree.ElementTree as ET
import zipfile
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable
from urllib.parse import quote

CYCLONEDX_FORMAT = "CycloneDX"
CYCLONEDX_SPEC_VERSION = "1.6"
HASH_ALGORITHM = "SHA-256"
COMMIT_PATTERN = re.compile(r"^[0-9a-fA-F]{7,64}$")


class SbomError(ValueError):
    """Raised when SBOM input or output violates repository policy."""


def fail(message: str) -> None:
    raise SbomError(message)


def read_json(path: Path) -> dict[str, Any]:
    if not path.is_file():
        fail(f"Required JSON file does not exist: {path}")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        fail(f"Malformed JSON in {path}: {error}")
    if not isinstance(value, dict):
        fail(f"JSON root must be an object: {path}")
    return value


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, indent=2, ensure_ascii=False, sort_keys=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def nuget_purl(package_id: str, version: str) -> str:
    return f"pkg:nuget/{quote(package_id, safe='')}@{quote(version, safe='.-')}"


def package_ref(package_id: str, version: str) -> str:
    return nuget_purl(package_id, version)


def symbol_ref(filename: str) -> str:
    return f"urn:tcj:symbol-package:{quote(filename, safe='.-')}"


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def first_child(element: ET.Element, name: str) -> ET.Element | None:
    return next((child for child in element if local_name(child.tag) == name), None)


def child_text(element: ET.Element, name: str) -> str | None:
    child = first_child(element, name)
    if child is None or child.text is None:
        return None
    text = child.text.strip()
    return text or None


def normalize_dependency_version(version_range: str | None) -> str | None:
    if version_range is None:
        return None
    value = version_range.strip()
    if not value:
        return None
    if value.startswith("[") or value.startswith("("):
        inner = value[1:-1] if value[-1:] in ("[", "]", ")") else value[1:]
        lower = inner.split(",", 1)[0].strip()
        return lower or None
    return value


@dataclass(frozen=True)
class NuspecMetadata:
    package_id: str
    version: str
    authors: str | None
    license_expression: str | None
    license_name: str | None
    license_url: str | None
    project_url: str | None
    repository_url: str | None
    repository_commit: str | None
    dependencies: tuple[str, ...]


def dependency_ids(dependency_root: ET.Element | None, source: str) -> tuple[str, ...]:
    if dependency_root is None:
        return ()

    names: dict[str, str] = {}
    ranges_by_scope: dict[str, dict[str, str | None]] = {}

    def add(scope: str, element: ET.Element) -> None:
        dependency_id = element.attrib.get("id", "").strip()
        if not dependency_id:
            fail(f"NuGet dependency without an id in {source}")

        key = dependency_id.casefold()
        version_value = element.attrib.get("version")
        scoped = ranges_by_scope.setdefault(scope, {})
        if key in scoped and scoped[key] != version_value:
            fail(
                f"NuGet dependency {dependency_id} has conflicting version ranges "
                f"inside dependency group {scope!r} in {source}."
            )
        scoped[key] = version_value
        names.setdefault(key, dependency_id)

    ungrouped_scope = "<ungrouped>"
    group_index = 0
    for child in dependency_root:
        child_name = local_name(child.tag)
        if child_name == "dependency":
            add(ungrouped_scope, child)
            continue
        if child_name != "group":
            continue

        group_index += 1
        target_framework = child.attrib.get("targetFramework", "").strip()
        scope = target_framework or f"<group-{group_index}>"
        for dependency in child:
            if local_name(dependency.tag) == "dependency":
                add(scope, dependency)

    return tuple(sorted(names.values(), key=str.casefold))


def parse_nuspec_xml(data: bytes | str, source: str) -> NuspecMetadata:
    try:
        root = ET.fromstring(data)
    except ET.ParseError as error:
        fail(f"Malformed NuGet metadata in {source}: {error}")

    metadata = first_child(root, "metadata")
    if metadata is None:
        fail(f"NuGet metadata does not contain a metadata element: {source}")

    package_id = child_text(metadata, "id")
    version = child_text(metadata, "version")
    if not package_id or not version:
        fail(f"NuGet metadata is missing id or version: {source}")

    license_element = first_child(metadata, "license")
    license_expression: str | None = None
    license_name: str | None = None
    if license_element is not None and license_element.text:
        license_value = license_element.text.strip()
        if license_element.attrib.get("type", "").casefold() == "expression":
            license_expression = license_value
        elif license_value:
            license_name = f"License file: {license_value}"

    repository = first_child(metadata, "repository")
    dependencies = dependency_ids(first_child(metadata, "dependencies"), source)

    return NuspecMetadata(
        package_id=package_id,
        version=version,
        authors=child_text(metadata, "authors"),
        license_expression=license_expression,
        license_name=license_name,
        license_url=child_text(metadata, "licenseUrl"),
        project_url=child_text(metadata, "projectUrl"),
        repository_url=(repository.attrib.get("url", "").strip() if repository is not None else None) or None,
        repository_commit=(repository.attrib.get("commit", "").strip() if repository is not None else None) or None,
        dependencies=dependencies,
    )


def read_nupkg_metadata(path: Path) -> NuspecMetadata:
    if not path.is_file():
        fail(f"NuGet package does not exist: {path}")
    try:
        with zipfile.ZipFile(path) as archive:
            nuspec_names = sorted(
                (name for name in archive.namelist() if name.casefold().endswith(".nuspec")),
                key=lambda name: (name.count("/"), name.casefold()),
            )
            if not nuspec_names:
                fail(f"NuGet package does not contain a .nuspec file: {path}")
            return parse_nuspec_xml(archive.read(nuspec_names[0]), str(path))
    except zipfile.BadZipFile as error:
        fail(f"Invalid NuGet package archive {path}: {error}")


def read_nuspec_file(path: Path) -> NuspecMetadata:
    if not path.is_file():
        fail(f"NuGet metadata file does not exist: {path}")
    return parse_nuspec_xml(path.read_bytes(), str(path))


def license_entries(metadata: NuspecMetadata) -> list[dict[str, object]]:
    if metadata.license_expression:
        return [{"expression": metadata.license_expression}]
    if metadata.license_name or metadata.license_url:
        license_value: dict[str, str] = {
            "name": metadata.license_name or "Declared package license"
        }
        if metadata.license_url:
            license_value["url"] = metadata.license_url
        return [{"license": license_value}]
    return []


def external_references(metadata: NuspecMetadata) -> list[dict[str, str]]:
    references: list[dict[str, str]] = []
    if metadata.project_url:
        references.append({"type": "website", "url": metadata.project_url})
    if metadata.repository_url:
        references.append({"type": "vcs", "url": metadata.repository_url})
    return references


def properties(**values: str | int | bool | None) -> list[dict[str, str]]:
    result: list[dict[str, str]] = []
    for name, value in values.items():
        if value is None:
            continue
        result.append({"name": name.replace("__", ":"), "value": str(value).lower() if isinstance(value, bool) else str(value)})
    return result


def property_map(value: object) -> dict[str, str]:
    if not isinstance(value, list):
        return {}
    result: dict[str, str] = {}
    for item in value:
        if isinstance(item, dict) and isinstance(item.get("name"), str) and isinstance(item.get("value"), str):
            result[item["name"]] = item["value"]
    return result


def resolve_commit_sha(root: Path, explicit: str | None) -> str:
    candidates = [explicit, os.environ.get("GITHUB_SHA")]
    for candidate in candidates:
        if candidate and COMMIT_PATTERN.fullmatch(candidate.strip()):
            return candidate.strip().lower()
    try:
        completed = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=root,
            check=True,
            capture_output=True,
            text=True,
        )
        candidate = completed.stdout.strip()
        if COMMIT_PATTERN.fullmatch(candidate):
            return candidate.lower()
    except (OSError, subprocess.CalledProcessError):
        pass
    fail("A Git commit SHA is required. Pass --commit-sha or run inside a Git checkout.")


def utc_timestamp() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


@dataclass(frozen=True)
class ReleasePackage:
    """Normalized release package metadata from either manifest schema."""

    package_id: str
    package_type: str
    asset_path: str | None = None
    forbid_assets: tuple[str, ...] = ()


def get_release_packages(manifest: dict[str, Any]) -> tuple[ReleasePackage, ...]:
    """Read release packages from the current or legacy release manifest schema."""
    release_packages = manifest.get("releasePackages")
    if release_packages is None:
        legacy_packages = manifest.get("packages")
        if not isinstance(legacy_packages, list) or not legacy_packages:
            raise ValueError("Release manifest packages must be a non-empty array.")
        result: list[ReleasePackage] = []
        for index, value in enumerate(legacy_packages):
            if not isinstance(value, str) or not value.strip():
                raise ValueError(f"Release manifest packages[{index}] must be a non-empty string.")
            result.append(ReleasePackage(value.strip(), "runtime"))
        if len({item.package_id.casefold() for item in result}) != len(result):
            raise ValueError("Release manifest package IDs must be unique.")
        return tuple(result)

    if not isinstance(release_packages, dict):
        raise ValueError("Release manifest releasePackages must be an object.")

    runtime = release_packages.get("runtime")
    tooling = release_packages.get("tooling")
    if not isinstance(runtime, list) or not runtime:
        raise ValueError("Release manifest releasePackages.runtime must be a non-empty array.")
    if not isinstance(tooling, list):
        raise ValueError("Release manifest releasePackages.tooling must be an array.")

    result = []
    for index, item in enumerate(runtime):
        if not isinstance(item, dict) or not isinstance(item.get("id"), str) or not item["id"].strip():
            raise ValueError(f"Release manifest releasePackages.runtime[{index}] must contain a non-empty package ID.")
        result.append(ReleasePackage(item["id"].strip(), "runtime"))

    for index, item in enumerate(tooling):
        if not isinstance(item, dict) or not isinstance(item.get("id"), str) or not item["id"].strip():
            raise ValueError(f"Release manifest releasePackages.tooling[{index}] must contain a non-empty package ID.")
        asset_path = item.get("assetPath")
        forbid_assets = item.get("forbidAssets", [])
        if not isinstance(asset_path, str) or not asset_path.strip():
            raise ValueError(f"Release manifest tooling package {item.get('id')!r} must define a non-empty assetPath.")
        if not isinstance(forbid_assets, list) or not all(isinstance(value, str) and value.strip() for value in forbid_assets):
            raise ValueError(f"Release manifest tooling package {item.get('id')!r} forbidAssets must be an array of non-empty strings.")
        result.append(
            ReleasePackage(
                item["id"].strip(),
                "tooling",
                asset_path.strip().replace("\\", "/").rstrip("/"),
                tuple(value.strip().replace("\\", "/").rstrip("/") for value in forbid_assets),
            )
        )

    if len({item.package_id.casefold() for item in result}) != len(result):
        raise ValueError("Release manifest package IDs must be unique across runtime and tooling packages.")
    return tuple(result)


def get_release_package_ids(
    manifest: dict[str, Any], package_type: str | None = None
) -> tuple[str, ...]:
    packages = get_release_packages(manifest)
    if package_type is not None and package_type not in {"runtime", "tooling"}:
        raise ValueError(f"Unsupported release package type: {package_type}")
    return tuple(
        item.package_id for item in packages if package_type is None or item.package_type == package_type
    )


@dataclass(frozen=True)
class ToolingPackageSpec:
    package_id: str
    asset_path: str
    forbid_assets: tuple[str, ...]


@dataclass(frozen=True)
class ReleasePackageSet:
    primary: dict[str, Path]
    symbols: dict[str, Path]
    metadata: dict[str, NuspecMetadata]
    tooling: dict[str, Path]


def release_package_policy(policy: dict[str, Any]) -> tuple[tuple[str, ...], tuple[ToolingPackageSpec, ...]]:
    release_packages = policy.get("releasePackages")
    if release_packages is None:
        legacy_runtime = policy.get("requiredPackages")
        if isinstance(legacy_runtime, list) and legacy_runtime:
            return tuple(legacy_runtime), ()
        fail("SBOM policy releasePackages must be an object.")
    if not isinstance(release_packages, dict):
        fail("SBOM policy releasePackages must be an object.")

    runtime = release_packages.get("runtime")
    tooling = release_packages.get("tooling")
    if not isinstance(runtime, list) or not runtime:
        fail("SBOM policy releasePackages.runtime must be a non-empty array.")
    if not isinstance(tooling, list):
        fail("SBOM policy releasePackages.tooling must be an array.")

    runtime_ids: list[str] = []
    for item in runtime:
        if not isinstance(item, dict) or not isinstance(item.get("id"), str) or not item["id"].strip():
            fail("SBOM policy releasePackages.runtime entries must contain non-empty package IDs.")
        runtime_ids.append(item["id"].strip())

    tooling_specs: list[ToolingPackageSpec] = []
    for item in tooling:
        if not isinstance(item, dict) or not isinstance(item.get("id"), str) or not item["id"].strip():
            fail("SBOM policy releasePackages.tooling entries must contain non-empty package IDs.")
        asset_path = item.get("assetPath")
        forbid_assets = item.get("forbidAssets", [])
        if not isinstance(asset_path, str) or not asset_path.strip():
            fail(f"SBOM policy tooling package {item['id']!r} must define a non-empty assetPath.")
        if not isinstance(forbid_assets, list) or not all(isinstance(value, str) and value.strip() for value in forbid_assets):
            fail(f"SBOM policy tooling package {item['id']!r} forbidAssets must be an array of non-empty strings.")
        tooling_specs.append(
            ToolingPackageSpec(
                package_id=item["id"].strip(),
                asset_path=asset_path.strip().replace("\\", "/").rstrip("/") + "/",
                forbid_assets=tuple(
                    value.strip().replace("\\", "/").rstrip("/") + "/" for value in forbid_assets
                ),
            )
        )

    all_ids = [*runtime_ids, *(item.package_id for item in tooling_specs)]
    normalized = [item.casefold() for item in all_ids]
    if len(normalized) != len(set(normalized)):
        fail("SBOM policy release package IDs must be unique across runtime and tooling packages.")

    return tuple(runtime_ids), tuple(tooling_specs)


def _package_entries(path: Path) -> list[str]:
    try:
        with zipfile.ZipFile(path) as archive:
            return [name.replace("\\", "/") for name in archive.namelist()]
    except zipfile.BadZipFile as error:
        fail(f"Invalid NuGet package archive {path}: {error}")


def validate_tooling_package(path: Path, spec: ToolingPackageSpec, version: str) -> NuspecMetadata:
    metadata = read_nupkg_metadata(path)
    if metadata.package_id.casefold() != spec.package_id.casefold() or metadata.version != version:
        fail(
            f"Package metadata mismatch in {path.name}: expected {spec.package_id} {version}, "
            f"found {metadata.package_id} {metadata.version}."
        )

    entries = _package_entries(path)
    asset_prefix = spec.asset_path.casefold()
    if not any(entry.casefold().startswith(asset_prefix) for entry in entries):
        fail(f"Tooling package {path.name} is missing required asset path {spec.asset_path}")
    for forbidden in spec.forbid_assets:
        prefix = forbidden.casefold()
        if any(entry.casefold().startswith(prefix) for entry in entries):
            fail(f"Tooling package {path.name} contains forbidden asset path {forbidden}")
    return metadata


def discover_release_packages(
    package_directory: Path,
    required_packages: Iterable[str],
    version: str,
    tooling_packages: Iterable[ToolingPackageSpec] = (),
) -> ReleasePackageSet:
    if not package_directory.is_dir():
        fail(f"Package directory does not exist: {package_directory}")

    required = list(required_packages)
    tooling = list(tooling_packages)
    primary: dict[str, Path] = {}
    symbols: dict[str, Path] = {}
    metadata: dict[str, NuspecMetadata] = {}
    tooling_paths: dict[str, Path] = {}
    expected_names: set[str] = set()
    tooling_names: set[str] = set()
    for package_id in required:
        primary_name = f"{package_id}.{version}.nupkg"
        symbol_name = f"{package_id}.{version}.snupkg"
        expected_names.update((primary_name, symbol_name))
        primary_path = package_directory / primary_name
        symbol_path = package_directory / symbol_name
        if not primary_path.is_file():
            fail(f"Required release package is missing: {primary_path}")
        if not symbol_path.is_file():
            fail(f"Required symbol package is missing: {symbol_path}")
        package_metadata = read_nupkg_metadata(primary_path)
        if package_metadata.package_id != package_id or package_metadata.version != version:
            fail(
                f"Package metadata mismatch in {primary_name}: expected {package_id} {version}, "
                f"found {package_metadata.package_id} {package_metadata.version}."
            )
        primary[package_id] = primary_path
        symbols[package_id] = symbol_path
        metadata[package_id] = package_metadata

    for spec in tooling:
        primary_name = f"{spec.package_id}.{version}.nupkg"
        tooling_names.add(primary_name)
        primary_path = package_directory / primary_name
        if not primary_path.is_file():
            fail(f"Required tooling package is missing: {primary_path}")
        tooling_paths[spec.package_id] = primary_path
        metadata[spec.package_id] = validate_tooling_package(primary_path, spec, version)

    actual_names = {
        path.name
        for path in package_directory.iterdir()
        if path.is_file() and path.name.casefold().endswith((".nupkg", ".snupkg"))
    }
    allowed_names = expected_names | tooling_names
    if actual_names != allowed_names:
        missing = sorted(allowed_names - actual_names, key=str.casefold)
        unexpected = sorted(actual_names - allowed_names, key=str.casefold)
        details: list[str] = []
        if missing:
            details.append("missing: " + ", ".join(missing))
        if unexpected:
            details.append("unexpected: " + ", ".join(unexpected))
        fail("Release package set is invalid (" + "; ".join(details) + ").")

    return ReleasePackageSet(primary, symbols, metadata, tooling_paths)


@dataclass
class AssetsGraph:
    package_versions: dict[str, tuple[str, str]]
    dependencies: dict[tuple[str, str], set[tuple[str, str]]]
    direct_by_project: dict[str, set[tuple[str, str]]]
    package_files: dict[tuple[str, str], tuple[Path, Path]]


def _select_target(data: dict[str, Any], source: Path) -> tuple[str, dict[str, Any]]:
    targets = data.get("targets")
    if not isinstance(targets, dict) or not targets:
        fail(f"project.assets.json has no targets: {source}")
    target_name = next((name for name in targets if str(name).split("/", 1)[0] == "net10.0"), None)
    if target_name is None:
        target_name = next(iter(targets))
    target = targets[target_name]
    if not isinstance(target, dict):
        fail(f"Invalid target {target_name} in {source}")
    return str(target_name), target


def load_assets_graph(root: Path, required_packages: Iterable[str]) -> AssetsGraph:
    package_versions: dict[str, tuple[str, str]] = {}
    dependencies: dict[tuple[str, str], set[tuple[str, str]]] = {}
    direct_by_project: dict[str, set[tuple[str, str]]] = {}
    package_files: dict[tuple[str, str], tuple[Path, Path]] = {}

    for project_id in required_packages:
        assets_path = root / "src" / project_id / "obj" / "project.assets.json"
        data = read_json(assets_path)
        target_name, target = _select_target(data, assets_path)

        resolved_by_id: dict[str, tuple[str, str]] = {}
        for key, entry in target.items():
            if not isinstance(entry, dict) or entry.get("type") != "package":
                continue
            if "/" not in key:
                fail(f"Invalid package key {key!r} in {assets_path}")
            package_id, version = key.rsplit("/", 1)
            normalized = package_id.casefold()
            pair = (package_id, version)
            previous = package_versions.get(normalized)
            if previous is not None and previous[1] != version:
                fail(
                    f"Dependency {package_id} resolves to both {previous[1]} and {version} across project assets."
                )
            package_versions[normalized] = pair
            resolved_by_id[normalized] = pair
            dependencies.setdefault(pair, set())

        for key, entry in target.items():
            if not isinstance(entry, dict) or entry.get("type") != "package" or "/" not in key:
                continue
            package_id, version = key.rsplit("/", 1)
            pair = (package_id, version)
            dependency_values = entry.get("dependencies", {})
            if not isinstance(dependency_values, dict):
                fail(f"Invalid dependency map for {key} in {assets_path}")
            for dependency_id in dependency_values:
                resolved = resolved_by_id.get(str(dependency_id).casefold()) or package_versions.get(str(dependency_id).casefold())
                if resolved is None:
                    fail(f"Unable to resolve dependency {dependency_id} from {key} in {assets_path}")
                dependencies[pair].add(resolved)

        project = data.get("project", {})
        frameworks = project.get("frameworks", {}) if isinstance(project, dict) else {}
        framework_name = target_name.split("/", 1)[0]
        framework = frameworks.get(framework_name, {}) if isinstance(frameworks, dict) else {}
        declared = framework.get("dependencies", {}) if isinstance(framework, dict) else {}
        direct: set[tuple[str, str]] = set()
        if isinstance(declared, dict):
            for dependency_id, descriptor in declared.items():
                target_type = descriptor.get("target") if isinstance(descriptor, dict) else None
                if target_type not in (None, "Package"):
                    continue
                resolved = resolved_by_id.get(str(dependency_id).casefold()) or package_versions.get(str(dependency_id).casefold())
                if resolved is not None:
                    direct.add(resolved)
        direct_by_project[project_id] = direct

        libraries = data.get("libraries", {})
        package_folders = data.get("packageFolders", {})
        folder_paths = [Path(path) for path in package_folders] if isinstance(package_folders, dict) else []
        if not isinstance(libraries, dict):
            fail(f"Invalid libraries object in {assets_path}")
        for pair in dependencies:
            key = f"{pair[0]}/{pair[1]}"
            library = libraries.get(key)
            if not isinstance(library, dict) or library.get("type") != "package":
                continue
            relative = library.get("path")
            if not isinstance(relative, str):
                continue
            for folder in folder_paths:
                package_root = folder / relative
                nupkg_candidates = sorted(package_root.glob("*.nupkg"), key=lambda path: path.name.casefold())
                nuspec_candidates = sorted(package_root.glob("*.nuspec"), key=lambda path: path.name.casefold())
                if nupkg_candidates and nuspec_candidates:
                    package_files[pair] = (nupkg_candidates[0], nuspec_candidates[0])
                    break

    missing_files = sorted(
        (f"{package_id}/{version}" for package_id, version in dependencies if (package_id, version) not in package_files),
        key=str.casefold,
    )
    if missing_files:
        fail(
            "NuGet cache metadata is missing for restored dependencies: "
            + ", ".join(missing_files)
        )

    return AssetsGraph(package_versions, dependencies, direct_by_project, package_files)


def dependency_lookup(graph: AssetsGraph, dependency_id: str) -> tuple[str, str]:
    result = graph.package_versions.get(dependency_id.casefold())
    if result is None:
        fail(f"Dependency {dependency_id} is declared by a release package but missing from project.assets.json.")
    return result


def component_hash(value: object) -> str | None:
    if not isinstance(value, list):
        return None
    for item in value:
        if isinstance(item, dict) and item.get("alg") == HASH_ALGORITHM and isinstance(item.get("content"), str):
            return item["content"].lower()
    return None


def build_sbom(
    *,
    root: Path,
    policy: dict[str, Any],
    version: str,
    package_directory: Path,
    commit_sha: str,
    release_tag: str,
) -> dict[str, Any]:
    required_packages, tooling_packages = release_package_policy(policy)
    repository = policy["repository"]
    package_set = discover_release_packages(
        package_directory, required_packages, version, tooling_packages
    )
    assets = load_assets_graph(root, required_packages)

    components: list[dict[str, Any]] = []
    dependency_edges: dict[str, set[str]] = {}
    direct_external: set[tuple[str, str]] = set()

    for package_id in required_packages:
        metadata = package_set.metadata[package_id]
        ref = package_ref(package_id, version)
        direct_refs: set[str] = set()
        for dependency_id in metadata.dependencies:
            if dependency_id in package_set.primary:
                direct_refs.add(package_ref(dependency_id, version))
            else:
                pair = dependency_lookup(assets, dependency_id)
                direct_external.add(pair)
                direct_refs.add(package_ref(*pair))
        dependency_edges[ref] = direct_refs
        components.append(
            {
                "type": "library",
                "bom-ref": ref,
                "group": "TCJ",
                "name": package_id,
                "version": version,
                "author": metadata.authors or "TCJ Contributors",
                "supplier": {"name": "TCJ Contributors"},
                "hashes": [{"alg": HASH_ALGORITHM, "content": sha256(package_set.primary[package_id])}],
                "licenses": license_entries(metadata),
                "purl": ref,
                "externalReferences": external_references(metadata),
                "properties": properties(
                    tcj__artifactFile=package_set.primary[package_id].name,
                    tcj__artifactType="nuget-package",
                    tcj__dependencyScope="release-package",
                    tcj__repository=repository,
                ),
            }
        )

        symbol_path = package_set.symbols[package_id]
        symbol_bom_ref = symbol_ref(symbol_path.name)
        dependency_edges[symbol_bom_ref] = {ref}
        components.append(
            {
                "type": "file",
                "bom-ref": symbol_bom_ref,
                "name": symbol_path.name,
                "version": version,
                "hashes": [{"alg": HASH_ALGORITHM, "content": sha256(symbol_path)}],
                "properties": properties(
                    tcj__artifactFile=symbol_path.name,
                    tcj__artifactType="nuget-symbol-package",
                    tcj__relatedPackage=package_id,
                ),
            }
        )

    release_package_ids = {
        *required_packages,
        *(spec.package_id for spec in tooling_packages),
    }
    for spec in tooling_packages:
        package_id = spec.package_id
        package_path = package_set.tooling[package_id]
        metadata = package_set.metadata[package_id]
        ref = package_ref(package_id, version)
        direct_refs: set[str] = set()
        for dependency_id in metadata.dependencies:
            if dependency_id in release_package_ids:
                direct_refs.add(package_ref(dependency_id, version))
            else:
                pair = dependency_lookup(assets, dependency_id)
                direct_external.add(pair)
                direct_refs.add(package_ref(*pair))
        dependency_edges[ref] = direct_refs
        components.append(
            {
                "type": "library",
                "bom-ref": ref,
                "group": "TCJ",
                "name": package_id,
                "version": version,
                "author": metadata.authors or "TCJ Contributors",
                "supplier": {"name": "TCJ Contributors"},
                "hashes": [{"alg": HASH_ALGORITHM, "content": sha256(package_path)}],
                "licenses": license_entries(metadata),
                "purl": ref,
                "externalReferences": external_references(metadata),
                "properties": properties(
                    tcj__artifactFile=package_path.name,
                    tcj__artifactType="nuget-tooling-package",
                    tcj__dependencyScope="release-package",
                    tcj__repository=repository,
                ),
            }
        )

    external_metadata: dict[tuple[str, str], NuspecMetadata] = {}
    for pair in sorted(assets.dependencies, key=lambda item: (item[0].casefold(), item[1])):
        nupkg_path, nuspec_path = assets.package_files[pair]
        metadata = read_nuspec_file(nuspec_path)
        if metadata.package_id.casefold() != pair[0].casefold() or metadata.version != pair[1]:
            fail(f"NuGet cache metadata mismatch for {pair[0]} {pair[1]}.")
        external_metadata[pair] = metadata
        ref = package_ref(*pair)
        dependency_edges[ref] = {package_ref(*child) for child in assets.dependencies[pair]}
        components.append(
            {
                "type": "library",
                "bom-ref": ref,
                "name": pair[0],
                "version": pair[1],
                "author": metadata.authors or "Unknown",
                "hashes": [{"alg": HASH_ALGORITHM, "content": sha256(nupkg_path)}],
                "licenses": license_entries(metadata),
                "purl": ref,
                "externalReferences": external_references(metadata),
                "properties": properties(
                    tcj__dependencyScope="direct" if pair in direct_external else "transitive",
                    tcj__artifactType="nuget-dependency",
                    tcj__packageSource="https://api.nuget.org/v3/index.json",
                ),
            }
        )

    root_ref = f"urn:tcj:framework:{quote(version, safe='.-')}"
    dependency_edges[root_ref] = {
        *(package_ref(package_id, version) for package_id in required_packages),
        *(package_ref(spec.package_id, version) for spec in tooling_packages),
        *(symbol_ref(package_set.symbols[package_id].name) for package_id in required_packages),
    }

    components.sort(key=lambda item: str(item["bom-ref"]).casefold())
    dependencies = [
        {"ref": ref, "dependsOn": sorted(children, key=str.casefold)}
        for ref, children in sorted(dependency_edges.items(), key=lambda item: item[0].casefold())
    ]

    serial = uuid.uuid5(
        uuid.NAMESPACE_URL,
        f"https://github.com/{repository}@{version}#{commit_sha}",
    )
    repository_url = f"https://github.com/{repository}"
    return {
        "bomFormat": CYCLONEDX_FORMAT,
        "specVersion": policy.get("specVersion", CYCLONEDX_SPEC_VERSION),
        "serialNumber": f"urn:uuid:{serial}",
        "version": 1,
        "metadata": {
            "timestamp": utc_timestamp(),
            "tools": {
                "components": [
                    {
                        "type": "application",
                        "name": "TCJ SBOM generator",
                        "version": str(policy.get("generatorVersion", 1)),
                    }
                ]
            },
            "supplier": {"name": "TCJ Contributors"},
            "component": {
                "type": "application",
                "bom-ref": root_ref,
                "name": "TCJ.Framework",
                "version": version,
                "externalReferences": [
                    {"type": "vcs", "url": f"{repository_url}/tree/{commit_sha}"},
                    {"type": "website", "url": repository_url},
                ],
                "properties": properties(
                    tcj__repository=repository,
                    tcj__commitSha=commit_sha,
                    tcj__releaseVersion=version,
                    tcj__releaseTag=release_tag,
                ),
            },
            "properties": properties(
                tcj__repository=repository,
                tcj__commitSha=commit_sha,
                tcj__releaseVersion=version,
                tcj__releaseTag=release_tag,
            ),
        },
        "components": components,
        "dependencies": dependencies,
    }