#!/usr/bin/env python3
"""Generate and verify release checksums and validate provenance automation."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

CHECKSUM_PATTERN = re.compile(r"^(?P<digest>[0-9a-f]{64}) [ *](?P<name>[^/\\]+)$")
PACKAGE_SUFFIXES = (".nupkg", ".snupkg")
SBOM_SUFFIX = ".cdx.json"


def fail(message: str) -> None:
    raise ValueError(message)


def read_text(path: Path) -> str:
    if not path.is_file():
        fail(f"Required file does not exist: {path}")
    return path.read_text(encoding="utf-8")


def load_release_manifest(root: Path) -> dict[str, object]:
    path = root / "eng" / "release-manifest.json"
    data = json.loads(read_text(path))
    packages = data.get("packages")
    version = data.get("version")
    if not isinstance(packages, list) or not packages:
        fail("eng/release-manifest.json must contain a non-empty packages array.")
    if not all(isinstance(item, str) and item.strip() for item in packages):
        fail("Release package IDs must be non-empty strings.")
    if not isinstance(version, str) or not version.strip():
        fail("eng/release-manifest.json must contain a version.")
    return data


def expected_package_names(
    manifest: dict[str, object],
    version: str,
) -> list[str]:
    names: list[str] = []
    for package_id_value in manifest["packages"]:
        package_id = str(package_id_value)
        names.append(f"{package_id}.{version}.nupkg")
        names.append(f"{package_id}.{version}.snupkg")
    return sorted(names, key=str.casefold)


def expected_sbom_name(version: str) -> str:
    return f"TCJ.Framework.{version}{SBOM_SUFFIX}"


def release_package_files(
    package_directory: Path,
    expected_names: list[str],
) -> list[Path]:
    if not package_directory.is_dir():
        fail(f"Package directory does not exist: {package_directory}")

    actual = sorted(
        (
            path
            for path in package_directory.iterdir()
            if path.is_file() and path.name.endswith(PACKAGE_SUFFIXES)
        ),
        key=lambda item: item.name.casefold(),
    )
    actual_names = [path.name for path in actual]
    if actual_names != expected_names:
        missing = sorted(set(expected_names).difference(actual_names), key=str.casefold)
        unexpected = sorted(set(actual_names).difference(expected_names), key=str.casefold)
        details: list[str] = []
        if missing:
            details.append(f"missing: {', '.join(missing)}")
        if unexpected:
            details.append(f"unexpected: {', '.join(unexpected)}")
        fail("Release package set is invalid (" + "; ".join(details) + ").")
    return actual


def resolve_sbom_file(sbom: Path | None, version: str) -> Path:
    expected_name = expected_sbom_name(version)
    resolved = sbom or Path("artifacts/sbom") / expected_name
    if not resolved.is_file():
        fail(f"Release SBOM does not exist: {resolved}")
    if resolved.name != expected_name:
        fail(f"Release SBOM must be named {expected_name}, found {resolved.name}.")
    siblings = sorted(
        path.name
        for path in resolved.parent.glob(f"*{SBOM_SUFFIX}")
        if path.is_file()
    )
    if siblings != [expected_name]:
        fail(
            "Release SBOM set is invalid; expected exactly "
            f"{expected_name}, found: {', '.join(siblings) or 'none'}."
        )
    return resolved


def release_files(
    package_directory: Path,
    expected_package_files: list[str],
    sbom: Path | None,
    version: str,
) -> list[Path]:
    files = release_package_files(package_directory, expected_package_files)
    files.append(resolve_sbom_file(sbom, version))
    return sorted(files, key=lambda item: item.name.casefold())


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_checksums(
    root: Path,
    package_directory: Path,
    output: Path,
    version: str | None,
    sbom: Path | None,
) -> None:
    manifest = load_release_manifest(root)
    resolved_version = version or str(manifest["version"])
    package_names = expected_package_names(manifest, resolved_version)
    files = release_files(package_directory, package_names, sbom, resolved_version)

    output.parent.mkdir(parents=True, exist_ok=True)
    lines = [f"{sha256(path)} *{path.name}" for path in files]
    output.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"Wrote {len(lines)} SHA-256 checksums, including the release SBOM, to {output}.")


def parse_checksums(path: Path) -> dict[str, str]:
    lines = read_text(path).splitlines()
    if not lines:
        fail(f"Checksum file is empty: {path}")

    entries: dict[str, str] = {}
    for line_number, line in enumerate(lines, start=1):
        match = CHECKSUM_PATTERN.fullmatch(line)
        if match is None:
            fail(f"Invalid checksum line {line_number} in {path}: {line!r}")
        name = match.group("name")
        if name in entries:
            fail(f"Duplicate checksum entry for {name}.")
        entries[name] = match.group("digest")
    return entries


def verify_checksums(
    root: Path,
    package_directory: Path,
    checksums: Path,
    version: str | None,
    sbom: Path | None,
) -> None:
    manifest = load_release_manifest(root)
    resolved_version = version or str(manifest["version"])
    package_names = expected_package_names(manifest, resolved_version)
    files = release_files(package_directory, package_names, sbom, resolved_version)
    expected_names = sorted((path.name for path in files), key=str.casefold)
    entries = parse_checksums(checksums)

    if sorted(entries, key=str.casefold) != expected_names:
        missing = sorted(set(expected_names).difference(entries), key=str.casefold)
        unexpected = sorted(set(entries).difference(expected_names), key=str.casefold)
        details: list[str] = []
        if missing:
            details.append(f"missing: {', '.join(missing)}")
        if unexpected:
            details.append(f"unexpected: {', '.join(unexpected)}")
        fail("Checksum manifest release set is invalid (" + "; ".join(details) + ").")

    failures: list[str] = []
    for path in files:
        actual = sha256(path)
        expected = entries[path.name]
        if actual != expected:
            failures.append(f"{path.name}: expected {expected}, got {actual}")
    if failures:
        fail("Checksum verification failed:\n  - " + "\n  - ".join(failures))

    print(f"Verified {len(files)} release artifact checksums, including the SBOM.")


def require_fragments(path: Path, fragments: tuple[str, ...]) -> None:
    text = read_text(path)
    missing = [fragment for fragment in fragments if fragment not in text]
    if missing:
        fail(
            f"{path} is missing release-integrity configuration: "
            + ", ".join(repr(item) for item in missing)
        )


def validate_configuration(root: Path) -> None:
    release = root / ".github" / "workflows" / "release.yml"
    preflight = root / ".github" / "workflows" / "release-preflight.yml"
    ci = root / ".github" / "workflows" / "ci.yml"

    common = (
        "python3 eng/release-integrity.py validate-config",
        "python3 eng/release-integrity.py write",
        "python3 eng/release-integrity.py verify",
        "--sbom",
        "artifacts/release/SHA256SUMS",
        "artifacts/sbom/",
    )
    require_fragments(
        release,
        (
            "id-token: write",
            "attestations: write",
            "artifact-metadata: write",
            "uses: actions/attest@v4",
            "artifacts/sbom/*.cdx.json",
        )
        + common,
    )
    require_fragments(preflight, common)
    require_fragments(ci, common)

    for path in (ci, preflight):
        text = read_text(path)
        if "uses: actions/attest@" in text:
            fail(
                "Artifact attestations must only be created by the tagged Release "
                f"workflow, not {path.relative_to(root)}."
            )

    print("Release checksum, SBOM, and provenance automation is configured correctly.")


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("validate-config")

    for command in ("write", "verify"):
        child = subparsers.add_parser(command)
        child.add_argument(
            "--package-directory",
            type=Path,
            default=Path("artifacts/packages"),
        )
        child.add_argument(
            "--checksums",
            type=Path,
            default=Path("artifacts/release/SHA256SUMS"),
        )
        child.add_argument("--version")
        child.add_argument("--sbom", type=Path)

    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]

    if args.command == "validate-config":
        validate_configuration(root)
        return 0

    package_directory = args.package_directory.resolve()
    checksums = args.checksums.resolve()
    sbom = args.sbom.resolve() if args.sbom else None
    if args.command == "write":
        write_checksums(root, package_directory, checksums, args.version, sbom)
    else:
        verify_checksums(root, package_directory, checksums, args.version, sbom)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as error:
        print(f"Release integrity verification failed: {error}", file=sys.stderr)
        raise SystemExit(1)
