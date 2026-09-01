#!/usr/bin/env python3
"""Stage runtime release packages for package-consumer compatibility validation."""

from __future__ import annotations

import argparse
import filecmp
import shutil
import sys
from pathlib import Path

from sbom_common import SbomError, get_release_package_ids, read_json

ROOT = Path(__file__).resolve().parents[1]


class StagingError(RuntimeError):
    pass


def fail(message: str) -> None:
    raise StagingError(message)


def stage_runtime_packages(
    manifest_path: Path,
    source: Path,
    destination: Path,
    version: str,
) -> tuple[str, ...]:
    manifest = read_json(manifest_path)
    manifest_version = manifest.get("version")
    if manifest_version != version:
        fail(
            "Release manifest version does not match the requested compatibility version: "
            f"manifest={manifest_version!r}, requested={version!r}."
        )

    runtime_package_ids = get_release_package_ids(manifest, "runtime")
    if not runtime_package_ids:
        fail("Release manifest does not define any runtime packages.")

    source = source.resolve()
    destination = destination.resolve()
    if not source.is_dir():
        fail(f"Release package directory does not exist: {source}")
    if (
        source == destination
        or source in destination.parents
        or destination in source.parents
    ):
        fail("Compatibility package source and destination must not overlap.")

    expected_files = tuple(
        filename
        for package_id in runtime_package_ids
        for filename in (
            f"{package_id}.{version}.nupkg",
            f"{package_id}.{version}.snupkg",
        )
    )
    missing = [filename for filename in expected_files if not (source / filename).is_file()]
    if missing:
        fail(f"Missing runtime release package artifacts: {', '.join(sorted(missing))}")

    if destination.exists():
        shutil.rmtree(destination)
    destination.mkdir(parents=True, exist_ok=True)

    for filename in expected_files:
        source_path = source / filename
        destination_path = destination / filename
        shutil.copy2(source_path, destination_path)
        if not filecmp.cmp(source_path, destination_path, shallow=False):
            fail(f"Compatibility feed package differs from promoted release package: {filename}")

    actual_files = tuple(sorted(path.name for path in destination.iterdir() if path.is_file()))
    if actual_files != tuple(sorted(expected_files)):
        fail(
            "Compatibility feed package set mismatch after staging. "
            f"Expected {sorted(expected_files)}, found {list(actual_files)}."
        )

    return runtime_package_ids


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument(
        "--manifest",
        type=Path,
        default=ROOT / "eng" / "release-manifest.json",
    )
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--destination", type=Path, required=True)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    package_ids = stage_runtime_packages(
        args.manifest.resolve(),
        args.source,
        args.destination,
        args.version,
    )
    print(
        "Compatibility feed staged from release-manifest runtime packages: "
        + ", ".join(package_ids)
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, SbomError, StagingError, TypeError, ValueError) as error:
        print(f"Compatibility package staging failed: {error}", file=sys.stderr)
        raise SystemExit(1)
