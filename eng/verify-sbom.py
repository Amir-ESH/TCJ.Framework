#!/usr/bin/env python3
"""Validate TCJ SBOM configuration and verify generated CycloneDX documents."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path
from typing import Any

from sbom_common import (
    CYCLONEDX_FORMAT,
    CYCLONEDX_SPEC_VERSION,
    HASH_ALGORITHM,
    SbomError,
    component_hash,
    dependency_lookup,
    discover_release_packages,
    load_assets_graph,
    nuget_purl,
    package_ref,
    property_map,
    read_json,
    sha256,
    symbol_ref,
    write_json,
)

POLICY_REQUIRED_KEYS = {
    "schemaVersion": int,
    "format": str,
    "specVersion": str,
    "fileExtension": str,
    "requiredPackages": list,
    "requireDirectDependencies": bool,
    "requireTransitiveDependencies": bool,
    "requireHashes": bool,
    "requireLicenses": bool,
    "requireRepositoryReference": bool,
    "requireCommitSha": bool,
    "requireReleaseVersion": bool,
    "repository": str,
}


def fail(message: str) -> None:
    raise SbomError(message)


def read_text(path: Path) -> str:
    if not path.is_file():
        fail(f"Required file does not exist: {path}")
    return path.read_text(encoding="utf-8")


def require_fragments(path: Path, fragments: tuple[str, ...]) -> None:
    text = read_text(path)
    missing = [fragment for fragment in fragments if fragment not in text]
    if missing:
        fail(
            f"{path} is missing SBOM integration: "
            + ", ".join(repr(item) for item in missing)
        )


def load_policy(root: Path) -> dict[str, Any]:
    path = root / "eng" / "sbom-policy.json"
    policy = read_json(path)
    for key, expected_type in POLICY_REQUIRED_KEYS.items():
        if key not in policy:
            fail(f"eng/sbom-policy.json is missing required property {key}.")
        if not isinstance(policy[key], expected_type):
            fail(f"eng/sbom-policy.json property {key} must be {expected_type.__name__}.")

    if policy["schemaVersion"] != 1:
        fail("Unsupported SBOM policy schemaVersion; expected 1.")
    if policy["format"] != CYCLONEDX_FORMAT:
        fail(f"SBOM policy format must be {CYCLONEDX_FORMAT}.")
    if policy["specVersion"] != CYCLONEDX_SPEC_VERSION:
        fail(f"SBOM policy specVersion must be {CYCLONEDX_SPEC_VERSION}.")
    if policy["fileExtension"] != ".cdx.json":
        fail("SBOM policy fileExtension must be .cdx.json.")
    packages = policy["requiredPackages"]
    if not packages or not all(isinstance(item, str) and item.strip() for item in packages):
        fail("SBOM policy requiredPackages must contain non-empty package IDs.")
    if len({item.casefold() for item in packages}) != len(packages):
        fail("SBOM policy requiredPackages must be unique.")
    if not policy["repository"].strip() or "/" not in policy["repository"]:
        fail("SBOM policy repository must use owner/name form.")
    return policy


def ensure_policy_tracked(root: Path) -> None:
    policy = root / "eng" / "sbom-policy.json"
    try:
        completed = subprocess.run(
            ["git", "check-ignore", "-q", str(policy.relative_to(root))],
            cwd=root,
            check=False,
        )
    except OSError as error:
        fail(f"Unable to verify Git ignore status: {error}")
    if completed.returncode == 0:
        fail("eng/sbom-policy.json is ignored by Git.")


def validate_configuration(root: Path) -> dict[str, Any]:
    policy = load_policy(root)
    manifest = read_json(root / "eng" / "release-manifest.json")
    manifest_packages = manifest.get("packages")
    if manifest_packages != policy["requiredPackages"]:
        fail("SBOM policy requiredPackages must exactly match release-manifest packages.")
    if manifest.get("repository") != policy["repository"]:
        fail("SBOM policy repository must match release-manifest repository.")

    ensure_policy_tracked(root)
    for relative in (
        "eng/generate-sbom.py",
        "eng/sbom_common.py",
        "eng/verify-sbom.py",
        "eng/tests/test_verify_sbom.py",
        "docs/software-bill-of-materials.md",
    ):
        if not (root / relative).is_file():
            fail(f"Required SBOM file does not exist: {relative}")

    ignore_text = read_text(root / ".gitignore")
    for fragment in ("artifacts/sbom/", "*.cdx.json", "!eng/sbom-policy.json"):
        if fragment not in ignore_text:
            fail(f".gitignore is missing SBOM rule {fragment!r}.")

    common = (
        "python3 eng/verify-sbom.py validate-config",
        "python3 eng/generate-sbom.py",
        "python3 eng/verify-sbom.py verify",
        "artifacts/sbom/SBOM_SUMMARY.md",
        "artifacts/sbom/sbom-summary.json",
    )
    require_fragments(root / ".github" / "workflows" / "ci.yml", common)
    require_fragments(root / ".github" / "workflows" / "release-preflight.yml", common)
    require_fragments(
        root / ".github" / "workflows" / "release.yml",
        common
        + (
            "artifacts/sbom/*.cdx.json",
            "uses: actions/attest@v4",
        ),
    )
    require_fragments(
        root / "eng" / "release-integrity.py",
        (
            ".cdx.json",
            "--sbom",
            "SBOM",
        ),
    )
    return policy


def _components(sbom: dict[str, Any]) -> list[dict[str, Any]]:
    value = sbom.get("components")
    if not isinstance(value, list):
        fail("SBOM components must be an array.")
    if not all(isinstance(item, dict) for item in value):
        fail("Every SBOM component must be an object.")
    return value


def _dependencies(sbom: dict[str, Any]) -> dict[str, set[str]]:
    value = sbom.get("dependencies")
    if not isinstance(value, list):
        fail("SBOM dependencies must be an array.")
    result: dict[str, set[str]] = {}
    for item in value:
        if not isinstance(item, dict) or not isinstance(item.get("ref"), str):
            fail("Every SBOM dependency entry must contain a string ref.")
        ref = item["ref"]
        if ref in result:
            fail(f"Duplicate SBOM dependency entry for {ref}.")
        children = item.get("dependsOn", [])
        if not isinstance(children, list) or not all(isinstance(child, str) for child in children):
            fail(f"SBOM dependency entry {ref} has an invalid dependsOn array.")
        if len(set(children)) != len(children):
            fail(f"SBOM dependency entry {ref} contains duplicate dependency references.")
        result[ref] = set(children)
    return result


def _has_license(component: dict[str, Any]) -> bool:
    licenses = component.get("licenses")
    if not isinstance(licenses, list) or not licenses:
        return False
    return any(
        isinstance(item, dict)
        and (
            isinstance(item.get("expression"), str)
            or isinstance(item.get("license"), dict)
        )
        for item in licenses
    )


def _repository_references(sbom: dict[str, Any], repository: str) -> bool:
    metadata = sbom.get("metadata")
    if not isinstance(metadata, dict):
        return False
    component = metadata.get("component")
    if not isinstance(component, dict):
        return False
    references = component.get("externalReferences")
    expected = f"https://github.com/{repository}"
    return isinstance(references, list) and any(
        isinstance(item, dict)
        and isinstance(item.get("url"), str)
        and item["url"].startswith(expected)
        for item in references
    )


def write_summary(path: Path, summary: dict[str, Any]) -> None:
    status = summary.get("status", "FAIL")
    lines = [
        "# TCJ software bill of materials",
        "",
        f"Overall status: **{status}**",
        "",
        f"- Release version: `{summary.get('version', 'unknown')}`",
        f"- Commit SHA: `{summary.get('commitSha', 'unknown')}`",
        f"- Format: `{summary.get('format', 'unknown')}`",
        f"- Component count: **{summary.get('componentCount', 0)}**",
        f"- Direct dependency count: **{summary.get('directDependencyCount', 0)}**",
        f"- Transitive dependency count: **{summary.get('transitiveDependencyCount', 0)}**",
        f"- TCJ package count: **{summary.get('tcjPackageCount', 0)}**",
        f"- Components with license metadata: **{summary.get('licensedComponentCount', 0)}**",
        f"- Components missing optional metadata: **{summary.get('missingOptionalMetadataCount', 0)}**",
        f"- Hash verification: **{summary.get('hashVerification', 'not-run')}**",
        f"- Package coverage: **{summary.get('packageCoverage', 'not-run')}**",
    ]
    error = summary.get("error")
    if error:
        lines.extend(("", "## Failure", "", str(error)))
    lines.append("")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines), encoding="utf-8", newline="\n")


def verify_document(
    *,
    root: Path,
    policy: dict[str, Any],
    version: str,
    package_directory: Path,
    sbom_path: Path,
) -> dict[str, Any]:
    sbom = read_json(sbom_path)
    if sbom.get("bomFormat") != policy["format"]:
        fail(f"Unsupported SBOM format: {sbom.get('bomFormat')!r}.")
    if sbom.get("specVersion") != policy["specVersion"]:
        fail(f"Unsupported CycloneDX specVersion: {sbom.get('specVersion')!r}.")
    if not isinstance(sbom.get("serialNumber"), str) or not sbom["serialNumber"].startswith("urn:uuid:"):
        fail("SBOM serialNumber must be a UUID URN.")

    metadata = sbom.get("metadata")
    if not isinstance(metadata, dict):
        fail("SBOM metadata must be an object.")
    metadata_properties = property_map(metadata.get("properties"))
    commit_sha = metadata_properties.get("tcj:commitSha")
    release_version = metadata_properties.get("tcj:releaseVersion")
    repository = metadata_properties.get("tcj:repository")
    release_tag = metadata_properties.get("tcj:releaseTag")
    if policy["requireCommitSha"] and not commit_sha:
        fail("SBOM metadata is missing tcj:commitSha.")
    if policy["requireReleaseVersion"] and release_version != version:
        fail(f"SBOM release version mismatch: expected {version}, found {release_version!r}.")
    if repository != policy["repository"]:
        fail(f"SBOM repository metadata mismatch: expected {policy['repository']}, found {repository!r}.")
    if not release_tag:
        fail("SBOM metadata is missing tcj:releaseTag.")
    if policy["requireRepositoryReference"] and not _repository_references(sbom, policy["repository"]):
        fail("SBOM metadata does not contain the required GitHub repository reference.")

    required_packages = policy["requiredPackages"]
    package_set = discover_release_packages(package_directory, required_packages, version)
    assets = load_assets_graph(root, required_packages)
    components = _components(sbom)
    dependencies = _dependencies(sbom)

    refs: dict[str, dict[str, Any]] = {}
    purls: dict[str, str] = {}
    artifact_files: dict[str, dict[str, Any]] = {}
    for component in components:
        ref = component.get("bom-ref")
        if not isinstance(ref, str) or not ref:
            fail("Every SBOM component must contain a non-empty bom-ref.")
        if ref in refs:
            fail(f"Duplicate SBOM component bom-ref: {ref}.")
        refs[ref] = component
        purl = component.get("purl")
        if isinstance(purl, str):
            if purl in purls:
                fail(f"Duplicate SBOM component package URL: {purl}.")
            purls[purl] = ref
        component_properties = property_map(component.get("properties"))
        artifact_file = component_properties.get("tcj:artifactFile")
        if artifact_file:
            if artifact_file in artifact_files:
                fail(f"Duplicate SBOM artifact representation: {artifact_file}.")
            artifact_files[artifact_file] = component

    tcj_components: dict[str, dict[str, Any]] = {}
    unexpected_tcj: list[str] = []
    for component in components:
        if component.get("type") != "library":
            continue
        name = component.get("name")
        if not isinstance(name, str) or not name.startswith("TCJ."):
            continue
        if name not in required_packages:
            unexpected_tcj.append(name)
        elif name in tcj_components:
            fail(f"Duplicate TCJ component: {name}.")
        else:
            tcj_components[name] = component
    if unexpected_tcj:
        fail("Unexpected TCJ components: " + ", ".join(sorted(set(unexpected_tcj))))
    missing_tcj = [item for item in required_packages if item not in tcj_components]
    if missing_tcj:
        fail("SBOM is missing required TCJ packages: " + ", ".join(missing_tcj))

    expected_artifacts: dict[str, Path] = {}
    for package_id in required_packages:
        expected_artifacts[package_set.primary[package_id].name] = package_set.primary[package_id]
        expected_artifacts[package_set.symbols[package_id].name] = package_set.symbols[package_id]
        component = tcj_components[package_id]
        if component.get("version") != version:
            fail(
                f"TCJ component {package_id} has version {component.get('version')!r}; expected {version}."
            )
        expected_purl = nuget_purl(package_id, version)
        if component.get("purl") != expected_purl:
            fail(f"TCJ component {package_id} must use package URL {expected_purl}.")

    missing_artifacts = sorted(set(expected_artifacts) - set(artifact_files), key=str.casefold)
    unexpected_artifacts = sorted(set(artifact_files) - set(expected_artifacts), key=str.casefold)
    if missing_artifacts:
        fail("Release package files are not represented in the SBOM: " + ", ".join(missing_artifacts))
    if unexpected_artifacts:
        fail("SBOM represents unexpected release package files: " + ", ".join(unexpected_artifacts))

    for filename, path in expected_artifacts.items():
        component = artifact_files[filename]
        digest = component_hash(component.get("hashes"))
        if policy["requireHashes"] and not digest:
            fail(f"SBOM component for {filename} is missing a {HASH_ALGORITHM} hash.")
        actual = sha256(path)
        if digest != actual:
            fail(f"SBOM hash mismatch for {filename}: expected {actual}, found {digest}.")

    all_known_refs = set(refs)
    metadata_component = metadata.get("component")
    if isinstance(metadata_component, dict) and isinstance(metadata_component.get("bom-ref"), str):
        all_known_refs.add(metadata_component["bom-ref"])
    for parent, children in dependencies.items():
        if parent not in all_known_refs:
            fail(f"SBOM dependency graph contains unresolved parent reference {parent}.")
        missing = sorted(children - all_known_refs, key=str.casefold)
        if missing:
            fail(f"SBOM dependency {parent} references missing components: {', '.join(missing)}.")

    external_components: dict[tuple[str, str], dict[str, Any]] = {}
    for pair in assets.dependencies:
        ref = package_ref(*pair)
        component = refs.get(ref)
        if component is None:
            fail(f"SBOM is missing restored dependency {pair[0]} {pair[1]}.")
        external_components[pair] = component
    expected_external_refs = {package_ref(*pair) for pair in assets.dependencies}
    actual_external_refs = {
        ref
        for ref, component in refs.items()
        if component.get("type") == "library"
        and not str(component.get("name", "")).startswith("TCJ.")
    }
    unexpected_external = sorted(actual_external_refs - expected_external_refs, key=str.casefold)
    if unexpected_external:
        fail("SBOM contains unexpected restored dependencies: " + ", ".join(unexpected_external))

    for pair, component in external_components.items():
        nupkg_path, _ = assets.package_files[pair]
        digest = component_hash(component.get("hashes"))
        if policy["requireHashes"] and digest != sha256(nupkg_path):
            fail(f"Dependency hash mismatch or missing hash for {pair[0]} {pair[1]}.")

    library_components = [component for component in components if component.get("type") == "library"]
    if policy["requireLicenses"]:
        missing_licenses = sorted(
            f"{component.get('name')} {component.get('version')}"
            for component in library_components
            if not _has_license(component)
        )
        if missing_licenses:
            fail("SBOM components are missing required license metadata: " + ", ".join(missing_licenses))

    expected_edges: dict[str, set[str]] = {}
    direct_external: set[tuple[str, str]] = set()
    for package_id in required_packages:
        ref = package_ref(package_id, version)
        expected_edges[ref] = set()
        for dependency_id in package_set.metadata[package_id].dependencies:
            if dependency_id in package_set.primary:
                expected_edges[ref].add(package_ref(dependency_id, version))
            else:
                pair = dependency_lookup(assets, dependency_id)
                direct_external.add(pair)
                expected_edges[ref].add(package_ref(*pair))
    for pair, children in assets.dependencies.items():
        expected_edges[package_ref(*pair)] = {package_ref(*child) for child in children}
    for package_id in required_packages:
        expected_edges[symbol_ref(package_set.symbols[package_id].name)] = {
            package_ref(package_id, version)
        }

    for ref, expected_children in expected_edges.items():
        actual_children = dependencies.get(ref)
        if actual_children is None:
            fail(f"SBOM dependency graph is missing an entry for {ref}.")
        if actual_children != expected_children:
            fail(
                f"SBOM dependency relationship mismatch for {ref}: expected "
                f"{sorted(expected_children)}, found {sorted(actual_children)}."
            )

    if policy["requireDirectDependencies"] and not direct_external:
        fail("SBOM does not contain any direct external dependency metadata.")
    transitive = set(assets.dependencies) - direct_external
    if policy["requireTransitiveDependencies"] and not transitive:
        fail("SBOM does not contain any transitive dependency metadata.")

    missing_optional = sum(
        1
        for component in library_components
        if not component.get("externalReferences") or component.get("author") in (None, "Unknown")
    )
    return {
        "status": "PASS",
        "version": version,
        "commitSha": commit_sha,
        "releaseTag": release_tag,
        "format": f"{sbom['bomFormat']} {sbom['specVersion']}",
        "componentCount": len(components),
        "directDependencyCount": len(direct_external),
        "transitiveDependencyCount": len(transitive),
        "tcjPackageCount": len(tcj_components),
        "licensedComponentCount": sum(1 for component in library_components if _has_license(component)),
        "missingOptionalMetadataCount": missing_optional,
        "hashVerification": "passed",
        "packageCoverage": "passed",
        "sbom": sbom_path.name,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("validate-config")
    verify = subparsers.add_parser("verify")
    verify.add_argument("--version", required=True)
    verify.add_argument(
        "--package-directory",
        type=Path,
        default=Path("artifacts/packages"),
    )
    verify.add_argument("--sbom", type=Path, required=True)
    verify.add_argument(
        "--summary",
        type=Path,
        default=Path("artifacts/sbom/SBOM_SUMMARY.md"),
    )
    verify.add_argument(
        "--json",
        type=Path,
        default=Path("artifacts/sbom/sbom-summary.json"),
    )
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]

    if args.command == "validate-config":
        policy = validate_configuration(root)
        print(
            f"SBOM configuration is valid for {len(policy['requiredPackages'])} release packages."
        )
        return 0

    policy = load_policy(root)
    summary: dict[str, Any]
    try:
        summary = verify_document(
            root=root,
            policy=policy,
            version=args.version,
            package_directory=args.package_directory.resolve(),
            sbom_path=args.sbom.resolve(),
        )
    except (OSError, KeyError, TypeError, json.JSONDecodeError, SbomError, ValueError) as error:
        summary = {
            "status": "FAIL",
            "version": args.version,
            "format": f"{policy.get('format', 'unknown')} {policy.get('specVersion', '')}".strip(),
            "error": str(error),
        }
        write_summary(args.summary.resolve(), summary)
        write_json(args.json.resolve(), summary)
        raise

    write_summary(args.summary.resolve(), summary)
    write_json(args.json.resolve(), summary)
    print(
        f"Verified {summary['componentCount']} SBOM components for release {args.version}."
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, KeyError, TypeError, json.JSONDecodeError, SbomError, ValueError) as error:
        print(f"SBOM verification failed: {error}", file=sys.stderr)
        raise SystemExit(1)
