#!/usr/bin/env python3
"""Generate the TCJ Framework CycloneDX release SBOM."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from sbom_common import (
    SbomError,
    build_sbom,
    read_json,
    release_package_policy,
    resolve_commit_sha,
    write_json,
)


def load_policy(root: Path) -> dict[str, object]:
    policy = read_json(root / "eng" / "sbom-policy.json")
    try:
        release_package_policy(policy)
    except SbomError:
        raise
    return policy


def write_generation_summary(sbom: dict[str, object], output: Path) -> None:
    components = sbom.get("components", [])
    metadata = sbom.get("metadata", {})
    root_component = metadata.get("component", {}) if isinstance(metadata, dict) else {}
    root_properties = root_component.get("properties", []) if isinstance(root_component, dict) else []
    values = {
        item.get("name"): item.get("value")
        for item in root_properties
        if isinstance(item, dict)
    }
    tcj_count = sum(
        1
        for component in components
        if isinstance(component, dict)
        and component.get("type") == "library"
        and str(component.get("name", "")).startswith("TCJ.")
    )
    lines = [
        "# TCJ software bill of materials",
        "",
        "Generation completed successfully.",
        "",
        f"- Release version: `{values.get('tcj:releaseVersion', 'unknown')}`",
        f"- Commit SHA: `{values.get('tcj:commitSha', 'unknown')}`",
        f"- Format: `{sbom.get('bomFormat', 'unknown')} {sbom.get('specVersion', '')}`",
        f"- Component count: **{len(components)}**",
        f"- TCJ package count: **{tcj_count}**",
        "- Verification: pending `eng/verify-sbom.py verify`",
        "",
    ]
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text("\n".join(lines), encoding="utf-8", newline="\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument(
        "--package-directory",
        type=Path,
        default=Path("artifacts/packages"),
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("artifacts/sbom"),
    )
    parser.add_argument("--commit-sha")
    parser.add_argument("--release-tag")
    args = parser.parse_args()

    root = Path(__file__).resolve().parents[1]
    policy = load_policy(root)
    commit_sha = resolve_commit_sha(root, args.commit_sha)
    release_tag = args.release_tag or f"v{args.version}"
    output_directory = args.output.resolve()
    output_directory.mkdir(parents=True, exist_ok=True)
    expected_name = f"TCJ.Framework.{args.version}{policy.get('fileExtension', '.cdx.json')}"
    sbom_path = output_directory / expected_name

    sbom = build_sbom(
        root=root,
        policy=policy,
        version=args.version,
        package_directory=args.package_directory.resolve(),
        commit_sha=commit_sha,
        release_tag=release_tag,
    )
    write_json(sbom_path, sbom)
    write_generation_summary(sbom, output_directory / "SBOM_SUMMARY.md")
    write_json(
        output_directory / "sbom-summary.json",
        {
            "status": "generated",
            "version": args.version,
            "commitSha": commit_sha,
            "releaseTag": release_tag,
            "format": sbom["bomFormat"],
            "specVersion": sbom["specVersion"],
            "componentCount": len(sbom["components"]),
            "sbom": sbom_path.name,
        },
    )
    print(f"Generated CycloneDX SBOM: {sbom_path}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, KeyError, TypeError, json.JSONDecodeError, SbomError, ValueError) as error:
        print(f"SBOM generation failed: {error}", file=sys.stderr)
        raise SystemExit(1)