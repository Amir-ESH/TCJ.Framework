#!/usr/bin/env python3
"""Validate deterministic-build configuration and compare two TCJ package builds."""

from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile
from dataclasses import asdict, dataclass, field
from pathlib import Path, PurePosixPath
from typing import Any, Iterable

from sbom_common import get_release_package_ids, read_json as read_release_json

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_POLICY = ROOT / "eng/reproducibility-policy.json"
DEFAULT_BUILD_A = ROOT / "artifacts/reproducibility/build-a/packages"
DEFAULT_BUILD_B = ROOT / "artifacts/reproducibility/build-b/packages"
DEFAULT_OUTPUT = ROOT / "artifacts/reproducibility/report"
SUMMARY_NAME = "REPRODUCIBILITY_SUMMARY.md"
JSON_NAME = "reproducibility-summary.json"
CORE_PROPERTIES_PREFIX = "package/services/metadata/core-properties/"
CANONICAL_CORE_PROPERTIES_PATH = CORE_PROPERTIES_PREFIX + "core-properties.psmdcp"
CORE_PROPERTIES_CONTENT_TYPE = "application/vnd.openxmlformats-package.core-properties+xml"
REQUIRED_NORMALIZATIONS = {
    "nuget-core-properties-created",
    "nuget-core-properties-part-name",
}
FORBIDDEN_PROJECT_PROPERTIES = {
    "Deterministic",
    "ContinuousIntegrationBuild",
    "DebugType",
    "EmbedUntrackedSources",
    "DeterministicSourcePaths",
    "PathMap",
    "ReproducibleBuildRoot",
}


class ReproducibilityError(RuntimeError):
    pass


@dataclass(frozen=True)
class Policy:
    schema_version: int
    required_packages: tuple[str, ...]
    compare_package_contents: bool
    compare_symbol_package_contents: bool
    require_assembly_equality: bool
    require_portable_pdb_equality: bool
    require_xml_documentation_equality: bool
    require_source_link_equality: bool
    require_nuspec_equality: bool
    require_nuget_metadata_equality: bool
    report_archive_byte_equality: bool
    require_archive_byte_equality: bool
    required_content_patterns: dict[str, tuple[str, ...]]
    approved_container_normalizations: tuple[dict[str, str], ...]


@dataclass
class NormalizationEvent:
    package: str
    package_type: str
    rule: str
    path: str
    detail: str


@dataclass
class Difference:
    package: str
    package_type: str
    path: str
    category: str
    build_a_hash: str | None
    build_b_hash: str | None
    build_a_size: int | None
    build_b_size: int | None
    structural_difference: str
    normalized: bool
    blocking: bool


@dataclass
class PackageArtifact:
    package_id: str
    version: str
    package_type: str
    path: Path
    archive_sha256: str
    archive_size: int
    entries: dict[str, bytes]
    original_entries: tuple[str, ...]
    entry_timestamps: dict[str, tuple[int, int, int, int, int, int]]
    source_links: dict[str, Any]
    normalization_events: list[NormalizationEvent]


@dataclass
class ComparisonSummary:
    schemaVersion: int = 1
    status: str = "FAIL"
    gitCommitSha: str = "unknown"
    packageVersion: str = ""
    dotnetSdkVersion: str = "unknown"
    operatingSystem: str = ""
    buildAPath: str = ""
    buildBPath: str = ""
    expectedPackageCount: int = 0
    comparedNupkgCount: int = 0
    comparedSnupkgCount: int = 0
    packageContentEquality: bool = False
    assemblyEquality: bool = False
    portablePdbEquality: bool = False
    sourceLinkEquality: bool = False
    xmlDocumentationEquality: bool = False
    nuspecEquality: bool = False
    nugetMetadataEquality: bool = False
    archiveByteEquality: bool = False
    normalizedContainerDifferences: list[dict[str, Any]] = field(default_factory=list)
    differences: list[dict[str, Any]] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)


def fail(message: str) -> None:
    raise ReproducibilityError(message)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def read_json(path: Path) -> Any:
    if not path.is_file():
        fail(f"Required JSON file is missing: {path}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        fail(f"Invalid JSON in {path}: {error}")


def require_bool(raw: dict[str, Any], name: str) -> bool:
    value = raw.get(name)
    if not isinstance(value, bool):
        fail(f"Reproducibility policy property {name} must be boolean.")
    return value


def load_policy(path: Path = DEFAULT_POLICY) -> Policy:
    raw = read_json(path)
    if not isinstance(raw, dict):
        fail("Reproducibility policy must be a JSON object.")
    if raw.get("schemaVersion") != 1:
        fail("Reproducibility policy schemaVersion must be 1.")

    packages = raw.get("requiredPackages")
    if not isinstance(packages, list) or not packages:
        fail("Reproducibility policy requiredPackages must be a non-empty array.")
    if any(not isinstance(item, str) or not item.strip() for item in packages):
        fail("Reproducibility policy requiredPackages must contain non-empty strings.")
    normalized_packages = tuple(item.strip() for item in packages)
    if len({item.casefold() for item in normalized_packages}) != len(normalized_packages):
        fail("Reproducibility policy requiredPackages must be unique.")

    patterns = raw.get("requiredContentPatterns")
    if not isinstance(patterns, dict):
        fail("Reproducibility policy requiredContentPatterns must be an object.")
    normalized_patterns: dict[str, tuple[str, ...]] = {}
    for package_type in ("nupkg", "snupkg"):
        value = patterns.get(package_type)
        if not isinstance(value, list) or not value:
            fail(f"requiredContentPatterns.{package_type} must be a non-empty array.")
        if any(not isinstance(item, str) or not item.strip() for item in value):
            fail(f"requiredContentPatterns.{package_type} must contain non-empty strings.")
        normalized_patterns[package_type] = tuple(item.strip() for item in value)

    normalizations = raw.get("approvedContainerNormalizations")
    if not isinstance(normalizations, list):
        fail("approvedContainerNormalizations must be an array.")
    normalized_rules: list[dict[str, str]] = []
    seen_rules: set[str] = set()
    for item in normalizations:
        if not isinstance(item, dict):
            fail("Every approved container normalization must be an object.")
        rule_id = item.get("id")
        description = item.get("description")
        if not isinstance(rule_id, str) or not rule_id.strip():
            fail("Every approved container normalization must have a non-empty id.")
        if not isinstance(description, str) or not description.strip():
            fail(f"Normalization {rule_id!r} must have a non-empty description.")
        if rule_id in seen_rules:
            fail(f"Duplicate approved container normalization: {rule_id}")
        seen_rules.add(rule_id)
        normalized_rules.append({"id": rule_id, "description": description.strip()})
    missing_rules = REQUIRED_NORMALIZATIONS.difference(seen_rules)
    if missing_rules:
        fail("Reproducibility policy is missing required normalization rules: " + ", ".join(sorted(missing_rules)))

    policy = Policy(
        schema_version=1,
        required_packages=normalized_packages,
        compare_package_contents=require_bool(raw, "comparePackageContents"),
        compare_symbol_package_contents=require_bool(raw, "compareSymbolPackageContents"),
        require_assembly_equality=require_bool(raw, "requireAssemblyEquality"),
        require_portable_pdb_equality=require_bool(raw, "requirePortablePdbEquality"),
        require_xml_documentation_equality=require_bool(raw, "requireXmlDocumentationEquality"),
        require_source_link_equality=require_bool(raw, "requireSourceLinkEquality"),
        require_nuspec_equality=require_bool(raw, "requireNuspecEquality"),
        require_nuget_metadata_equality=require_bool(raw, "requireNuGetMetadataEquality"),
        report_archive_byte_equality=require_bool(raw, "reportArchiveByteEquality"),
        require_archive_byte_equality=require_bool(raw, "requireArchiveByteEquality"),
        required_content_patterns=normalized_patterns,
        approved_container_normalizations=tuple(normalized_rules),
    )
    if not policy.compare_package_contents or not policy.compare_symbol_package_contents:
        fail("Extracted primary and symbol package comparisons must remain enabled.")
    return policy


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def parse_msbuild_properties(path: Path) -> list[tuple[str, str, str]]:
    if not path.is_file():
        fail(f"Required MSBuild file is missing: {path}")
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as error:
        fail(f"Invalid MSBuild XML in {path}: {error}")
    result: list[tuple[str, str, str]] = []
    for group in root:
        if local_name(group.tag) != "PropertyGroup":
            continue
        for child in group:
            result.append((local_name(child.tag), (child.text or "").strip(), child.attrib.get("Condition", "")))
    return result


def property_values(properties: Iterable[tuple[str, str, str]], name: str) -> list[tuple[str, str]]:
    return [(value, condition) for key, value, condition in properties if key == name]


def require_property(properties: list[tuple[str, str, str]], name: str, expected: str) -> None:
    values = property_values(properties, name)
    if not any(value.casefold() == expected.casefold() for value, _ in values):
        fail(f"Directory.Build.props must centrally set {name} to {expected}.")


def read_text(path: Path) -> str:
    if not path.is_file():
        fail(f"Required file is missing: {path}")
    return path.read_text(encoding="utf-8")


def require_fragments(path: Path, fragments: Iterable[str]) -> None:
    text = read_text(path)
    missing = [fragment for fragment in fragments if fragment not in text]
    if missing:
        fail(f"{path} is missing reproducibility integration: " + ", ".join(repr(item) for item in missing))


def ensure_git_tracking(root: Path, policy_path: Path, verifier_path: Path, *, check_git: bool) -> None:
    ignore_text = read_text(root / ".gitignore")
    for fragment in (
        "artifacts/reproducibility/",
        "!eng/reproducibility-policy.json",
        "!eng/verify-reproducible-build.py",
    ):
        if fragment not in ignore_text:
            fail(f".gitignore is missing reproducibility rule {fragment!r}.")
    if not check_git:
        return
    if not (root / ".git").exists():
        fail("Git metadata is required to verify that the reproducibility policy is tracked. Use --skip-git-check only for exported source archives.")
    for path in (policy_path, verifier_path):
        relative = path.resolve().relative_to(root.resolve()).as_posix()
        ignored = subprocess.run(
            ["git", "check-ignore", "--quiet", "--no-index", "--", relative],
            cwd=root,
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            text=True,
        )
        if ignored.returncode == 0:
            fail(f"{relative} is ignored by Git and must remain tracked.")
        if ignored.returncode not in (0, 1):
            fail(f"Unable to inspect Git ignore state for {relative}: {ignored.stderr.strip()}")
        tracked = subprocess.run(
            ["git", "ls-files", "--error-unmatch", "--", relative],
            cwd=root,
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            text=True,
        )
        if tracked.returncode != 0:
            fail(f"{relative} is not tracked by Git.")


def validate_configuration(root: Path = ROOT, *, check_git: bool = True) -> Policy:
    policy_path = root / "eng/reproducibility-policy.json"
    verifier_path = root / "eng/verify-reproducible-build.py"
    policy = load_policy(policy_path)

    central = parse_msbuild_properties(root / "Directory.Build.props")
    require_property(central, "Deterministic", "true")
    require_property(central, "DebugType", "portable")
    require_property(central, "EmbedUntrackedSources", "true")
    require_property(central, "DeterministicSourcePaths", "true")
    path_maps = property_values(central, "PathMap")
    if not any("$(MSBuildThisFileDirectory)=/_/" in value for value, _ in path_maps):
        fail("Directory.Build.props must normalize repository source paths with PathMap.")
    isolated_maps = [
        (value, condition)
        for value, condition in path_maps
        if "$(ReproducibleBuildRoot)" in value
    ]
    if not any(
        value.startswith("$(ReproducibleBuildRoot)=/_/artifacts/reproducibility/build,")
        and "$(MSBuildThisFileDirectory)=/_/" in value
        and "ReproducibleBuildRoot" in condition
        for value, condition in isolated_maps
    ):
        fail(
            "Directory.Build.props must map ReproducibleBuildRoot to one canonical path "
            "before the repository PathMap so isolated generated sources remain deterministic."
        )
    ci_values = property_values(central, "ContinuousIntegrationBuild")
    if not any(value.casefold() == "true" and "CI" in condition for value, condition in ci_values):
        fail("Directory.Build.props must enable ContinuousIntegrationBuild when CI is true.")

    packaging = read_text(root / "eng/Packaging.props")
    for fragment in (
        "<PublishRepositoryUrl>true</PublishRepositoryUrl>",
        "<RepositoryType>git</RepositoryType>",
        "<RepositoryUrl>https://github.com/Amir-ESH/TCJ.Framework.git</RepositoryUrl>",
    ):
        if fragment not in packaging:
            fail(f"eng/Packaging.props is missing repository metadata {fragment}.")
    for property_name in ("DebugType", "EmbedUntrackedSources", "Deterministic", "PathMap"):
        if f"<{property_name}>" in packaging:
            fail(f"eng/Packaging.props must inherit central {property_name} configuration instead of repeating it.")

    manifest = read_release_json(root / "eng/release-manifest.json")
    try:
        manifest_packages = get_release_package_ids(manifest, "runtime")
    except ValueError as error:
        fail(str(error))
    if manifest_packages != policy.required_packages:
        fail("Reproducibility policy requiredPackages must exactly match the runtime packages in eng/release-manifest.json.")

    project_files: list[Path] = []
    for package_id in policy.required_packages:
        project = root / "src" / package_id / f"{package_id}.csproj"
        if not project.is_file():
            fail(f"Production package project is missing: {project.relative_to(root)}")
        project_files.append(project)

    found_package_ids: set[str] = set()
    for project in project_files:
        text = read_text(project)
        if "eng\\Packaging.props" not in text and "eng/Packaging.props" not in text:
            fail(f"Production project does not import central packaging settings: {project.relative_to(root)}")
        try:
            xml_root = ET.parse(project).getroot()
        except ET.ParseError as error:
            fail(f"Invalid project XML in {project}: {error}")
        for element in xml_root.iter():
            name = local_name(element.tag)
            if name in FORBIDDEN_PROJECT_PROPERTIES:
                fail(f"{project.relative_to(root)} must not override central deterministic property {name}.")
            if name == "PackageId" and element.text:
                found_package_ids.add(element.text.strip())
    if found_package_ids != set(policy.required_packages):
        fail("Production PackageId values do not exactly match the reproducibility policy.")

    ensure_git_tracking(root, policy_path, verifier_path, check_git=check_git)

    for relative in (
        "eng/tests/test_verify_reproducible_build.py",
        "docs/reproducible-builds.md",
        ".github/workflows/reproducible-builds.yml",
    ):
        if not (root / relative).is_file():
            fail(f"Required reproducibility file does not exist: {relative}")

    workflow_common = (
        "global-json-file: global.json",
        "CI: true",
        "python3 eng/verify-reproducible-build.py validate-config",
    )
    require_fragments(root / ".github/workflows/ci.yml", workflow_common)
    isolated_fragments = workflow_common + (
        "artifacts/reproducibility/build-a",
        "artifacts/reproducibility/build-b",
        "--artifacts-path",
        "python3 eng/verify-reproducible-build.py compare",
        "REPRODUCIBILITY_SUMMARY.md",
        "reproducibility-summary.json",
    )
    dedicated = root / ".github/workflows/reproducible-builds.yml"
    require_fragments(
        dedicated,
        isolated_fragments
        + (
            "name: Reproducible builds",
            "workflow_dispatch:",
            "schedule:",
            "workflow_call:",
            "name: Compare package builds",
            "uses: actions/upload-artifact@v7",
        ),
    )
    require_fragments(
        root / ".github/workflows/required-pr-gate.yml",
        ("pull_request:", "uses: ./.github/workflows/reproducible-builds.yml", "name: Required PR Gate"),
    )
    isolated_workflows = (
        dedicated,
        root / ".github/workflows/release-preflight.yml",
        root / ".github/workflows/release.yml",
    )
    for workflow in isolated_workflows:
        workflow_text = read_text(workflow)
        expected_path_map_arguments = 6
        actual_path_map_arguments = workflow_text.count('-p:ReproducibleBuildRoot="$root"')
        if actual_path_map_arguments != expected_path_map_arguments:
            fail(
                f"{workflow.relative_to(root)} must pass ReproducibleBuildRoot to all six "
                f"isolated restore/build/pack commands; found {actual_path_map_arguments}."
            )

    for workflow in (root / ".github/workflows/release-preflight.yml", root / ".github/workflows/release.yml"):
        require_fragments(
            workflow,
            isolated_fragments
            + (
                "Promote verified Build A",
                "artifacts/reproducibility/report",
            ),
        )
    return policy


def safe_zip_name(name: str) -> str:
    if (
        not name
        or "\\" in name
        or "//" in name
        or "\x00" in name
        or name.startswith(("/", "\\"))
        or re.match(r"^[A-Za-z]:", name)
    ):
        fail(f"Package contains an invalid or unsafe ZIP path: {name!r}")
    path = PurePosixPath(name)
    if any(part in ("", ".", "..") for part in path.parts):
        fail(f"Package contains an invalid or unsafe ZIP path: {name!r}")
    return path.as_posix()


def parse_identity(entries: dict[str, bytes], archive: Path) -> tuple[str, str]:
    nuspecs = [path for path in entries if path.casefold().endswith(".nuspec") and "/" not in path]
    if len(nuspecs) != 1:
        fail(f"{archive.name} must contain exactly one root NuSpec file; found {len(nuspecs)}.")
    try:
        root = ET.fromstring(entries[nuspecs[0]])
    except ET.ParseError as error:
        fail(f"Invalid NuSpec XML in {archive.name}: {error}")
    metadata = next((item for item in root.iter() if local_name(item.tag) == "metadata"), None)
    if metadata is None:
        fail(f"{archive.name} NuSpec is missing metadata.")
    package_id = next(((item.text or "").strip() for item in metadata if local_name(item.tag) == "id"), "")
    version = next(((item.text or "").strip() for item in metadata if local_name(item.tag) == "version"), "")
    if not package_id or not version:
        fail(f"{archive.name} NuSpec must contain id and version.")
    return package_id, version


def normalize_core_properties(data: bytes) -> tuple[bytes, str | None]:
    try:
        ET.fromstring(data)
    except ET.ParseError as error:
        fail(f"Invalid NuGet core-properties XML: {error}")

    pattern = re.compile(
        rb"(<(?:[A-Za-z_][\w.-]*:)?created\b[^>]*>)(.*?)(</(?:[A-Za-z_][\w.-]*:)?created\s*>)",
        re.DOTALL,
    )
    match = pattern.search(data)
    if not match:
        # OPC core properties are optional. Newer NuGet writers may emit a
        # core-properties part without dcterms:created, which is already free
        # of the package-creation timestamp that this rule normalizes.
        return data, None

    original = match.group(2).decode("utf-8", errors="replace").strip()
    normalized = data[: match.start(2)] + b"1970-01-01T00:00:00Z" + data[match.end(2) :]
    try:
        ET.fromstring(normalized)
    except ET.ParseError as error:
        fail(f"Normalized NuGet core-properties XML is invalid: {error}")
    return normalized, original


def replace_xml_attribute(data: bytes, name: str, old_value: str, new_value: str) -> bytes:
    pattern = re.compile(
        rb"(\b" + re.escape(name.encode("utf-8")) + rb"\s*=\s*)([\"'])(" + re.escape(old_value.encode("utf-8")) + rb")(\2)"
    )
    replaced, count = pattern.subn(
        lambda match: match.group(1) + match.group(2) + new_value.encode("utf-8") + match.group(4),
        data,
        count=1,
    )
    if count != 1:
        fail(f"Unable to normalize XML attribute {name}={old_value!r}.")
    return replaced


def normalize_relationships(data: bytes) -> tuple[bytes, str]:
    try:
        root = ET.fromstring(data)
    except ET.ParseError as error:
        fail(f"Invalid package relationship XML: {error}")
    matches: list[ET.Element] = []
    for relationship in root.iter():
        if local_name(relationship.tag) != "Relationship":
            continue
        relation_type = relationship.attrib.get("Type", "")
        target = relationship.attrib.get("Target", "")
        if relation_type.endswith("/metadata/core-properties") or CORE_PROPERTIES_PREFIX in target.lstrip("/").casefold():
            matches.append(relationship)
    if len(matches) != 1:
        fail(f"Package root relationships must reference exactly one NuGet core-properties part; found {len(matches)}.")

    relationship = matches[0]
    original_id = relationship.attrib.get("Id", "")
    original_target = relationship.attrib.get("Target", "")
    if not original_id or not original_target:
        fail("NuGet core-properties relationship must contain Id and Target attributes.")
    normalized = replace_xml_attribute(data, "Id", original_id, "R-core-properties")
    normalized = replace_xml_attribute(
        normalized,
        "Target",
        original_target,
        "/" + CANONICAL_CORE_PROPERTIES_PATH,
    )
    try:
        ET.fromstring(normalized)
    except ET.ParseError as error:
        fail(f"Normalized package relationship XML is invalid: {error}")
    return normalized, f"Id={original_id}, Target={original_target}"


def normalize_content_types(data: bytes) -> tuple[bytes, str | None]:
    try:
        root = ET.fromstring(data)
    except ET.ParseError as error:
        fail(f"Invalid package content-types XML: {error}")

    overrides: list[ET.Element] = []
    defaults: list[ET.Element] = []
    for element in root.iter():
        element_name = local_name(element.tag)
        content_type = element.attrib.get("ContentType", "").casefold()
        if element_name == "Override":
            part_name = element.attrib.get("PartName", "")
            if (
                part_name.lstrip("/").casefold().startswith(CORE_PROPERTIES_PREFIX)
                or content_type == CORE_PROPERTIES_CONTENT_TYPE
            ):
                overrides.append(element)
        elif element_name == "Default":
            extension = element.attrib.get("Extension", "").lstrip(".").casefold()
            if extension == "psmdcp" or content_type == CORE_PROPERTIES_CONTENT_TYPE:
                defaults.append(element)

    declaration_count = len(overrides) + len(defaults)
    if declaration_count != 1:
        fail(
            "Package content types must declare exactly one NuGet core-properties "
            f"content type; found {declaration_count}."
        )

    if defaults:
        declaration = defaults[0]
        extension = declaration.attrib.get("Extension", "").lstrip(".").casefold()
        content_type = declaration.attrib.get("ContentType", "").casefold()
        if extension != "psmdcp" or content_type != CORE_PROPERTIES_CONTENT_TYPE:
            fail(
                "NuGet core-properties default content type must use Extension=\"psmdcp\" "
                f"and ContentType=\"{CORE_PROPERTIES_CONTENT_TYPE}\"."
            )
        # NuGet commonly registers all .psmdcp parts through a Default entry.
        # This representation contains no generated part name and therefore
        # requires no content-types normalization.
        return data, None

    original = overrides[0].attrib.get("PartName", "")
    content_type = overrides[0].attrib.get("ContentType", "").casefold()
    if not original:
        fail("NuGet core-properties content type override must contain PartName.")
    if content_type != CORE_PROPERTIES_CONTENT_TYPE:
        fail(
            "NuGet core-properties content type override has an unexpected ContentType."
        )
    normalized = replace_xml_attribute(
        data,
        "PartName",
        original,
        "/" + CANONICAL_CORE_PROPERTIES_PATH,
    )
    try:
        ET.fromstring(normalized)
    except ET.ParseError as error:
        fail(f"Normalized package content-types XML is invalid: {error}")
    return normalized, original


def extract_json_object(data: bytes, marker: bytes = b'"documents"') -> dict[str, Any] | None:
    search_from = 0
    while True:
        marker_index = data.find(marker, search_from)
        if marker_index < 0:
            return None
        start = data.rfind(b"{", 0, marker_index + 1)
        while start >= 0:
            depth = 0
            in_string = False
            escaped = False
            for index in range(start, len(data)):
                byte = data[index]
                if in_string:
                    if escaped:
                        escaped = False
                    elif byte == 0x5C:
                        escaped = True
                    elif byte == 0x22:
                        in_string = False
                    continue
                if byte == 0x22:
                    in_string = True
                elif byte == 0x7B:
                    depth += 1
                elif byte == 0x7D:
                    depth -= 1
                    if depth == 0:
                        try:
                            value = json.loads(data[start : index + 1].decode("utf-8"))
                        except (UnicodeDecodeError, json.JSONDecodeError):
                            break
                        if isinstance(value, dict) and isinstance(value.get("documents"), dict):
                            return value
                        break
            start = data.rfind(b"{", 0, start)
        search_from = marker_index + len(marker)


def canonical_source_link(value: dict[str, Any]) -> dict[str, Any]:
    documents = value.get("documents")
    if not isinstance(documents, dict) or not documents:
        fail("Source Link metadata must contain a non-empty documents map.")
    if any(not isinstance(key, str) or not isinstance(item, str) for key, item in documents.items()):
        fail("Source Link documents must map strings to strings.")
    return {"documents": dict(sorted(documents.items()))}



def package_pattern_matches(path: str, pattern: str) -> bool:
    if pattern in {"[Content_Types].xml", "_rels/.rels"}:
        return path == pattern
    return fnmatch.fnmatchcase(path, pattern)

def load_package(path: Path, policy: Policy, expected_version: str) -> PackageArtifact:
    package_type = "snupkg" if path.name.casefold().endswith(".snupkg") else "nupkg"
    original_entries: list[str] = []
    entry_timestamps: dict[str, tuple[int, int, int, int, int, int]] = {}
    entries: dict[str, bytes] = {}
    seen_casefold: set[str] = set()
    try:
        with zipfile.ZipFile(path, "r") as archive:
            for info in archive.infolist():
                if info.is_dir():
                    continue
                canonical = safe_zip_name(info.filename)
                folded = canonical.casefold()
                if folded in seen_casefold:
                    fail(f"{path.name} contains duplicate package entry {canonical!r}.")
                seen_casefold.add(folded)
                original_entries.append(canonical)
                entry_timestamps[canonical] = info.date_time
                entries[canonical] = archive.read(info)
    except zipfile.BadZipFile as error:
        fail(f"Invalid NuGet ZIP archive {path}: {error}")

    package_id, version = parse_identity(entries, path)
    if version != expected_version:
        fail(f"{path.name} version {version!r} does not match expected version {expected_version!r}.")
    if package_id not in policy.required_packages and not any(
        entry.casefold().startswith("analyzers/dotnet/cs/") for entry in entries
    ):
        fail(f"Unexpected TCJ package {package_id!r} in {path.parent}.")

    is_tooling_package = any(
        entry.casefold().startswith("analyzers/dotnet/cs/")
        for entry in entries
    )
    if package_type == "nupkg" and is_tooling_package:
        patterns = (
            "_rels/.rels",
            "[Content_Types].xml",
            "*.nuspec",
            "analyzers/dotnet/cs/**",
            "package/services/metadata/core-properties/*.psmdcp",
        )
    else:
        patterns = policy.required_content_patterns[package_type]
    for pattern in patterns:
        if not any(package_pattern_matches(entry, pattern) for entry in entries):
            fail(f"{path.name} is missing required package content matching {pattern!r}.")

    normalizations: list[NormalizationEvent] = []
    core_paths = [entry for entry in entries if entry.casefold().startswith(CORE_PROPERTIES_PREFIX) and entry.casefold().endswith(".psmdcp")]
    if len(core_paths) != 1:
        fail(f"{path.name} must contain exactly one NuGet core-properties part; found {len(core_paths)}.")
    core_path = core_paths[0]
    core_bytes, created_value = normalize_core_properties(entries.pop(core_path))
    entries[CANONICAL_CORE_PROPERTIES_PATH] = core_bytes
    if created_value is not None:
        normalizations.append(
            NormalizationEvent(
                package=package_id,
                package_type=package_type,
                rule="nuget-core-properties-created",
                path=core_path,
                detail=f"original dcterms:created={created_value}",
            )
        )
    if core_path != CANONICAL_CORE_PROPERTIES_PATH:
        normalizations.append(
            NormalizationEvent(
                package=package_id,
                package_type=package_type,
                rule="nuget-core-properties-part-name",
                path=core_path,
                detail=f"canonical path={CANONICAL_CORE_PROPERTIES_PATH}",
            )
        )

    relationships_path = next((entry for entry in entries if entry.casefold() == "_rels/.rels"), None)
    if relationships_path is None:
        fail(f"{path.name} is missing _rels/.rels.")
    relationships, relationship_detail = normalize_relationships(entries[relationships_path])
    entries[relationships_path] = relationships
    normalizations.append(
        NormalizationEvent(
            package=package_id,
            package_type=package_type,
            rule="nuget-core-properties-part-name",
            path=relationships_path,
            detail=relationship_detail or "canonicalized core-properties relationship",
        )
    )

    content_types_path = next((entry for entry in entries if entry.casefold() == "[content_types].xml"), None)
    if content_types_path is None:
        fail(f"{path.name} is missing [Content_Types].xml.")
    content_types, content_type_detail = normalize_content_types(entries[content_types_path])
    entries[content_types_path] = content_types
    if content_type_detail is not None:
        normalizations.append(
            NormalizationEvent(
                package=package_id,
                package_type=package_type,
                rule="nuget-core-properties-part-name",
                path=content_types_path,
                detail=f"original core-properties PartName={content_type_detail}",
            )
        )

    source_links: dict[str, Any] = {}
    for entry, data in entries.items():
        if entry.casefold().endswith(".pdb"):
            source_link = extract_json_object(data)
            if source_link is None:
                fail(f"Portable PDB {entry} in {path.name} does not contain Source Link metadata.")
            source_links[entry] = canonical_source_link(source_link)

    return PackageArtifact(
        package_id=package_id,
        version=version,
        package_type=package_type,
        path=path,
        archive_sha256=sha256_file(path),
        archive_size=path.stat().st_size,
        entries=entries,
        original_entries=tuple(original_entries),
        entry_timestamps=entry_timestamps,
        source_links=source_links,
        normalization_events=normalizations,
    )


def discover_packages(directory: Path, policy: Policy, expected_version: str) -> dict[tuple[str, str], PackageArtifact]:
    if not directory.is_dir():
        fail(f"Package directory does not exist: {directory}")
    paths = sorted(
        path for path in directory.iterdir()
        if path.is_file() and path.name.casefold().endswith((".nupkg", ".snupkg"))
    )
    if not paths:
        fail(f"Package directory does not contain NuGet packages: {directory}")
    result: dict[tuple[str, str], PackageArtifact] = {}
    for path in paths:
        package = load_package(path, policy, expected_version)
        key = (package.package_id.casefold(), package.package_type)
        if key in result:
            fail(f"Duplicate package identity {package.package_id} ({package.package_type}) in {directory}.")
        result[key] = package

    expected = {
        (package_id.casefold(), package_type)
        for package_id in policy.required_packages
        for package_type in ("nupkg", "snupkg")
    }
    actual = set(result)
    tooling = {
        key for key, package in result.items()
        if key[0] not in {item.casefold() for item in policy.required_packages}
        and any(entry.casefold().startswith("analyzers/dotnet/cs/") for entry in package.entries)
    }
    missing = sorted(expected.difference(actual))
    unexpected = sorted(actual.difference(expected).difference(tooling))
    if missing:
        fail("Missing expected packages: " + ", ".join(f"{package_id}.{kind}" for package_id, kind in missing))
    if unexpected:
        fail("Unexpected packages: " + ", ".join(f"{package_id}.{kind}" for package_id, kind in unexpected))
    return result


def category_for(path: str) -> str:
    folded = path.casefold()
    if folded.endswith(".dll"):
        return "assembly"
    if folded.endswith(".pdb"):
        return "portable-pdb"
    if folded.endswith(".nuspec"):
        return "nuspec"
    if folded.endswith(".xml") and folded.startswith("lib/"):
        return "xml-documentation"
    if folded in ("_rels/.rels", "[content_types].xml") or folded.startswith(CORE_PROPERTIES_PREFIX):
        return "nuget-metadata"
    return "package-content"


def first_structural_difference(a: bytes | None, b: bytes | None) -> str:
    if a is None:
        return "Path exists only in Build B."
    if b is None:
        return "Path exists only in Build A."
    limit = min(len(a), len(b))
    for index in range(limit):
        if a[index] != b[index]:
            if a[:2000].decode("utf-8", errors="ignore") and b[:2000].decode("utf-8", errors="ignore"):
                a_text = a.decode("utf-8", errors="replace").splitlines()
                b_text = b.decode("utf-8", errors="replace").splitlines()
                for line_number, (left, right) in enumerate(zip(a_text, b_text), start=1):
                    if left != right:
                        return f"First differing text line {line_number}: Build A length {len(left)}, Build B length {len(right)}."
                if len(a_text) != len(b_text):
                    return f"Text line count differs: Build A {len(a_text)}, Build B {len(b_text)}."
            return f"First differing byte offset: {index}."
    return f"File size differs after {limit} equal bytes."


def create_difference(package: PackageArtifact, path: str, category: str, a: bytes | None, b: bytes | None, *, blocking: bool) -> Difference:
    return Difference(
        package=package.package_id,
        package_type=package.package_type,
        path=path,
        category=category,
        build_a_hash=sha256_bytes(a) if a is not None else None,
        build_b_hash=sha256_bytes(b) if b is not None else None,
        build_a_size=len(a) if a is not None else None,
        build_b_size=len(b) if b is not None else None,
        structural_difference=first_structural_difference(a, b),
        normalized=False,
        blocking=blocking,
    )


def compare_artifacts(a: PackageArtifact, b: PackageArtifact, policy: Policy) -> tuple[list[Difference], list[NormalizationEvent], bool]:
    differences: list[Difference] = []
    normalizations: list[NormalizationEvent] = []
    events_a: dict[str, list[tuple[str, str]]] = {}
    events_b: dict[str, list[tuple[str, str]]] = {}
    for event in a.normalization_events:
        events_a.setdefault(event.rule, []).append((event.path, event.detail))
    for event in b.normalization_events:
        events_b.setdefault(event.rule, []).append((event.path, event.detail))
    for rule in sorted(set(events_a) | set(events_b)):
        left = sorted(events_a.get(rule, []))
        right = sorted(events_b.get(rule, []))
        if left != right:
            normalizations.append(
                NormalizationEvent(
                    package=a.package_id,
                    package_type=a.package_type,
                    rule=rule,
                    path=", ".join(sorted({item[0] for item in left + right})),
                    detail=f"Build A={left}; Build B={right}",
                )
            )
    archive_equal = a.archive_sha256 == b.archive_sha256
    if not archive_equal and policy.report_archive_byte_equality:
        container_details: list[str] = []
        if a.original_entries != b.original_entries:
            limit = min(len(a.original_entries), len(b.original_entries))
            first = next((index for index in range(limit) if a.original_entries[index] != b.original_entries[index]), limit)
            left = a.original_entries[first] if first < len(a.original_entries) else "<missing>"
            right = b.original_entries[first] if first < len(b.original_entries) else "<missing>"
            container_details.append(
                f"ZIP entry order first differs at position {first + 1}: Build A={left!r}, Build B={right!r}"
            )
        changed_timestamps = [
            path
            for path in sorted(set(a.entry_timestamps) | set(b.entry_timestamps))
            if a.entry_timestamps.get(path) != b.entry_timestamps.get(path)
        ]
        if changed_timestamps:
            first_path = changed_timestamps[0]
            container_details.append(
                f"ZIP entry timestamps differ for {len(changed_timestamps)} entries; first {first_path!r}: "
                f"Build A={a.entry_timestamps.get(first_path)}, Build B={b.entry_timestamps.get(first_path)}"
            )
        if not container_details:
            container_details.append("other ZIP compression or container metadata differs")
        differences.append(
            Difference(
                package=a.package_id,
                package_type=a.package_type,
                path=a.path.name,
                category="archive-container",
                build_a_hash=a.archive_sha256,
                build_b_hash=b.archive_sha256,
                build_a_size=a.archive_size,
                build_b_size=b.archive_size,
                structural_difference="; ".join(container_details) + "; extracted package contents are evaluated separately.",
                normalized=True,
                blocking=policy.require_archive_byte_equality,
            )
        )

    all_paths = sorted(set(a.entries) | set(b.entries))
    for path in all_paths:
        left = a.entries.get(path)
        right = b.entries.get(path)
        if left == right:
            continue
        category = category_for(path)
        blocking = True
        if category == "assembly":
            blocking = policy.require_assembly_equality
        elif category == "portable-pdb":
            blocking = policy.require_portable_pdb_equality
        elif category == "xml-documentation":
            blocking = policy.require_xml_documentation_equality
        elif category == "nuspec":
            blocking = policy.require_nuspec_equality
        elif category == "nuget-metadata":
            blocking = policy.require_nuget_metadata_equality
        elif a.package_type == "snupkg":
            blocking = policy.compare_symbol_package_contents
        else:
            blocking = policy.compare_package_contents
        differences.append(create_difference(a, path, category, left, right, blocking=blocking))

    pdb_paths = sorted(set(a.source_links) | set(b.source_links))
    for path in pdb_paths:
        left = a.source_links.get(path)
        right = b.source_links.get(path)
        if left != right:
            left_bytes = json.dumps(left, sort_keys=True).encode("utf-8") if left is not None else None
            right_bytes = json.dumps(right, sort_keys=True).encode("utf-8") if right is not None else None
            differences.append(
                create_difference(
                    a,
                    path + "#SourceLink",
                    "source-link",
                    left_bytes,
                    right_bytes,
                    blocking=policy.require_source_link_equality,
                )
            )
    return differences, normalizations, archive_equal


def git_commit(root: Path) -> str:
    value = os.environ.get("GITHUB_SHA")
    if value:
        return value
    if (root / ".git").exists():
        completed = subprocess.run(
            ["git", "rev-parse", "HEAD"], cwd=root, check=False, capture_output=True, text=True
        )
        if completed.returncode == 0 and completed.stdout.strip():
            return completed.stdout.strip()
    return "unknown"


def dotnet_sdk_version(root: Path) -> str:
    try:
        completed = subprocess.run(
            ["dotnet", "--version"], cwd=root, check=False, capture_output=True, text=True
        )
        if completed.returncode == 0 and completed.stdout.strip():
            return completed.stdout.strip()
    except OSError:
        pass
    try:
        value = read_json(root / "global.json")
        return str(value["sdk"]["version"])
    except (ReproducibilityError, KeyError, TypeError):
        return "unknown"


def relative_or_absolute(path: Path, root: Path) -> str:
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return path.resolve().as_posix()


def write_difference_reports(output: Path, differences: list[Difference]) -> None:
    directory = output / "differences"
    directory.mkdir(parents=True, exist_ok=True)
    grouped: dict[tuple[str, str], list[Difference]] = {}
    for difference in differences:
        grouped.setdefault((difference.package, difference.package_type), []).append(difference)
    for (package, package_type), items in grouped.items():
        lines = [
            f"# {package}.{package_type}",
            "",
        ]
        for item in items:
            lines.extend(
                [
                    f"## {item.path}",
                    "",
                    f"- Category: `{item.category}`",
                    f"- Build A SHA-256: `{item.build_a_hash or 'missing'}`",
                    f"- Build B SHA-256: `{item.build_b_hash or 'missing'}`",
                    f"- Build A size: `{item.build_a_size if item.build_a_size is not None else 'missing'}`",
                    f"- Build B size: `{item.build_b_size if item.build_b_size is not None else 'missing'}`",
                    f"- Structural difference: {item.structural_difference}",
                    f"- Normalized/container-only: `{'yes' if item.normalized else 'no'}`",
                    f"- Blocking: `{'yes' if item.blocking else 'no'}`",
                    "",
                ]
            )
        (directory / f"{package}.{package_type}.txt").write_text("\n".join(lines), encoding="utf-8")

        for item in items:
            if item.category not in {"assembly", "portable-pdb", "source-link", "nuspec", "xml-documentation"}:
                continue
            safe_name = re.sub(r"[^A-Za-z0-9_.-]+", "_", Path(item.path.split("#", 1)[0]).name)
            suffix = "sourcelink" if item.category == "source-link" else safe_name
            focused = [
                f"Package: {package}",
                f"Package type: {package_type}",
                f"Path: {item.path}",
                f"Category: {item.category}",
                f"Build A SHA-256: {item.build_a_hash or 'missing'}",
                f"Build B SHA-256: {item.build_b_hash or 'missing'}",
                f"Build A size: {item.build_a_size if item.build_a_size is not None else 'missing'}",
                f"Build B size: {item.build_b_size if item.build_b_size is not None else 'missing'}",
                f"First useful structural difference: {item.structural_difference}",
                f"Normalized: {'yes' if item.normalized else 'no'}",
                f"Blocking: {'yes' if item.blocking else 'no'}",
            ]
            (directory / f"{package}.{suffix}.txt").write_text("\n".join(focused) + "\n", encoding="utf-8")


def status_icon(value: bool) -> str:
    return "PASS" if value else "FAIL"


def write_summary(output: Path, summary: ComparisonSummary) -> None:
    output.mkdir(parents=True, exist_ok=True)
    (output / JSON_NAME).write_text(json.dumps(asdict(summary), indent=2) + "\n", encoding="utf-8")

    warning_count = sum(1 for item in summary.differences if item.get("normalized") and not item.get("blocking"))
    lines = [
        "# Reproducible build summary",
        "",
        f"**Overall result:** `{summary.status}`",
        "",
        "## Build identity",
        "",
        f"- Git commit: `{summary.gitCommitSha}`",
        f"- Package version: `{summary.packageVersion}`",
        f"- .NET SDK: `{summary.dotnetSdkVersion}`",
        f"- Operating system: `{summary.operatingSystem}`",
        f"- Build A: `{summary.buildAPath}`",
        f"- Build B: `{summary.buildBPath}`",
        "",
        "## Comparison",
        "",
        "| Check | Result |",
        "| --- | --- |",
        f"| Expected package IDs | {summary.expectedPackageCount} |",
        f"| Compared `.nupkg` files | {summary.comparedNupkgCount} |",
        f"| Compared `.snupkg` files | {summary.comparedSnupkgCount} |",
        f"| Extracted package contents | {status_icon(summary.packageContentEquality)} |",
        f"| Assemblies | {status_icon(summary.assemblyEquality)} |",
        f"| Portable PDBs | {status_icon(summary.portablePdbEquality)} |",
        f"| Source Link metadata | {status_icon(summary.sourceLinkEquality)} |",
        f"| XML documentation | {status_icon(summary.xmlDocumentationEquality)} |",
        f"| NuSpec | {status_icon(summary.nuspecEquality)} |",
        f"| NuGet metadata | {status_icon(summary.nugetMetadataEquality)} |",
        f"| Raw archive bytes | {status_icon(summary.archiveByteEquality)} |",
        "",
        f"Normalized/container-only warnings: `{warning_count}`",
        "",
    ]
    if summary.normalizedContainerDifferences:
        lines.extend(["## Approved normalization observations", ""])
        for event in summary.normalizedContainerDifferences:
            lines.append(
                f"- `{event['package']}.{event['package_type']}` `{event['path']}` — "
                f"`{event['rule']}`: {event['detail']}"
            )
        lines.append("")
    blocking = [item for item in summary.differences if item.get("blocking")]
    warnings = [item for item in summary.differences if not item.get("blocking")]
    if blocking:
        lines.extend(["## Blocking differences", ""])
        for item in blocking:
            lines.append(f"- `{item['package']}.{item['package_type']}` `{item['path']}` ({item['category']})")
        lines.append("")
    if warnings:
        lines.extend(["## Non-blocking differences", ""])
        for item in warnings:
            lines.append(f"- `{item['package']}.{item['package_type']}` `{item['path']}` ({item['category']})")
        lines.append("")
    if summary.errors:
        lines.extend(["## Errors", ""] + [f"- {item}" for item in summary.errors] + [""])
    (output / SUMMARY_NAME).write_text("\n".join(lines), encoding="utf-8")


def compare_package_sets(root: Path, policy: Policy, version: str, build_a: Path, build_b: Path, output: Path) -> ComparisonSummary:
    summary = ComparisonSummary(
        gitCommitSha=git_commit(root),
        packageVersion=version,
        dotnetSdkVersion=dotnet_sdk_version(root),
        operatingSystem=f"{platform.system()} {platform.release()} ({platform.machine()})",
        buildAPath=relative_or_absolute(build_a, root),
        buildBPath=relative_or_absolute(build_b, root),
        expectedPackageCount=len(policy.required_packages),
    )
    try:
        packages_a = discover_packages(build_a, policy, version)
        packages_b = discover_packages(build_b, policy, version)
        if set(packages_a) != set(packages_b):
            fail("Build A and Build B contain different package identities.")

        all_differences: list[Difference] = []
        all_normalizations: list[NormalizationEvent] = []
        archive_equal = True
        for key in sorted(packages_a):
            left = packages_a[key]
            right = packages_b[key]
            if left.package_id != right.package_id or left.version != right.version or left.package_type != right.package_type:
                fail(f"Package identity mismatch for {left.package_id} ({left.package_type}).")
            differences, normalizations, pair_archive_equal = compare_artifacts(left, right, policy)
            all_differences.extend(differences)
            all_normalizations.extend(normalizations)
            archive_equal = archive_equal and pair_archive_equal

        summary.comparedNupkgCount = sum(1 for _, kind in packages_a if kind == "nupkg")
        summary.comparedSnupkgCount = sum(1 for _, kind in packages_a if kind == "snupkg")
        blocking_categories = {item.category for item in all_differences if item.blocking}
        summary.packageContentEquality = not any(
            item.blocking and item.category not in {"archive-container"} for item in all_differences
        )
        summary.assemblyEquality = "assembly" not in blocking_categories
        summary.portablePdbEquality = "portable-pdb" not in blocking_categories
        summary.sourceLinkEquality = "source-link" not in blocking_categories
        summary.xmlDocumentationEquality = "xml-documentation" not in blocking_categories
        summary.nuspecEquality = "nuspec" not in blocking_categories
        summary.nugetMetadataEquality = "nuget-metadata" not in blocking_categories
        summary.archiveByteEquality = archive_equal
        summary.normalizedContainerDifferences = [asdict(item) for item in all_normalizations]
        summary.differences = [asdict(item) for item in all_differences]
        blocking = [item for item in all_differences if item.blocking]
        summary.status = "FAIL" if blocking else ("PASS_WITH_WARNINGS" if all_differences else "PASS")
        write_difference_reports(output, all_differences)
    except ReproducibilityError as error:
        summary.errors.append(str(error))
        summary.status = "FAIL"
    write_summary(output, summary)
    if summary.status == "FAIL":
        if summary.errors:
            detail = summary.errors[0]
        else:
            blocking = [item for item in summary.differences if item.get("blocking")]
            preview = "; ".join(
                f"{item['package']}.{item['package_type']}:{item['path']} ({item['category']})"
                for item in blocking[:5]
            )
            remainder = len(blocking) - 5
            suffix = f"; plus {remainder} more" if remainder > 0 else ""
            detail = (
                "Blocking package differences were detected: "
                f"{preview}{suffix}. See {relative_or_absolute(output / SUMMARY_NAME, root)} "
                "and the differences directory."
            )
        fail(detail)
    return summary


def create_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    validate = subparsers.add_parser("validate-config", help="Validate deterministic-build configuration.")
    validate.add_argument("--root", type=Path, default=ROOT)
    validate.add_argument("--skip-git-check", action="store_true")

    compare = subparsers.add_parser("compare", help="Compare two independently built package directories.")
    compare.add_argument("--root", type=Path, default=ROOT)
    compare.add_argument("--policy", type=Path, default=DEFAULT_POLICY)
    compare.add_argument("--version", required=True)
    compare.add_argument("--build-a", type=Path, default=DEFAULT_BUILD_A)
    compare.add_argument("--build-b", type=Path, default=DEFAULT_BUILD_B)
    compare.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = create_parser().parse_args(argv)
    try:
        if args.command == "validate-config":
            validate_configuration(args.root.resolve(), check_git=not args.skip_git_check)
            print("Reproducibility configuration is valid.")
            return 0
        policy_path = args.policy
        if not policy_path.is_absolute():
            policy_path = args.root / policy_path
        policy = load_policy(policy_path.resolve())
        summary = compare_package_sets(
            args.root.resolve(),
            policy,
            args.version,
            args.build_a.resolve(),
            args.build_b.resolve(),
            args.output.resolve(),
        )
        print(
            f"Reproducibility verification {summary.status}: "
            f"{summary.comparedNupkgCount} primary and {summary.comparedSnupkgCount} symbol packages compared."
        )
        return 0
    except ReproducibilityError as error:
        print(f"Reproducibility verification failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())