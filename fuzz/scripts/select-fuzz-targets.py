#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
POLICY = json.loads((ROOT / "eng/fuzzing-policy.json").read_text(encoding="utf-8"))
ALL_TARGETS = list(POLICY["requiredFuzzTargets"])

FULL_SCOPE_FILES = {
    "eng/fuzzing-policy.json",
    "eng/verify-fuzzing.py",
    "fuzz/targets.json",
    "Directory.Build.props",
    "Directory.Packages.props",
    "global.json",
    ".github/workflows/fuzzing.yml",
}
FULL_SCOPE_PREFIXES = (
    "fuzz/TCJ.FuzzTests/",
    "fuzz/scripts/",
    "fuzz/corpus/",
    "tests/TCJ.PropertyTests/",
)


def select_targets(changed_files: list[str]) -> list[str]:
    normalized = [item.replace("\\", "/").lstrip("./") for item in changed_files if item.strip()]
    if not normalized:
        return ALL_TARGETS.copy()
    if any(path in FULL_SCOPE_FILES or path.startswith(FULL_SCOPE_PREFIXES) for path in normalized):
        return ALL_TARGETS.copy()

    selected: set[str] = set()
    for path in normalized:
        if path.startswith("src/TCJ.DependencyInjection/"):
            selected.add("DependencyScanning")
        elif path.startswith("src/TCJ.Core/Guards/"):
            selected.add("Check")
        elif path.startswith("src/TCJ.Core/Results/"):
            selected.add("ResultComposition")
        elif path.endswith("src/TCJ.Core/Extensions/StringExtensions.cs") or path == "src/TCJ.Core/Extensions/StringExtensions.cs":
            selected.add("StringExtensions")
        elif path.endswith("src/TCJ.Core/Extensions/EnumerableExtensions.cs") or path.endswith("src/TCJ.Core/Extensions/CollectionExtensions.cs"):
            selected.add("EnumerableExtensions")
        elif path.startswith("src/TCJ.Core/"):
            # Unknown foundational Core changes can affect multiple public surfaces.
            return ALL_TARGETS.copy()

    return [name for name in ALL_TARGETS if name in selected] or ALL_TARGETS.copy()


def git_changed_files(base: str, head: str) -> list[str]:
    proc = subprocess.run(
        ["git", "diff", "--name-only", f"{base}...{head}"],
        cwd=ROOT,
        text=True,
        capture_output=True,
        check=False,
    )
    if proc.returncode != 0:
        raise SystemExit(proc.stderr.strip() or "Unable to determine changed files for fuzz scope.")
    return [line for line in proc.stdout.splitlines() if line.strip()]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base")
    parser.add_argument("--head")
    parser.add_argument("--changed-file", action="append", default=[])
    parser.add_argument("--all", action="store_true")
    parser.add_argument("--format", choices=["csv", "lines"], default="csv")
    args = parser.parse_args()

    if args.all:
        targets = ALL_TARGETS
    elif args.changed_file:
        targets = select_targets(args.changed_file)
    elif args.base and args.head:
        targets = select_targets(git_changed_files(args.base, args.head))
    else:
        parser.error("Use --all, --changed-file, or both --base and --head.")

    print(",".join(targets) if args.format == "csv" else "\n".join(targets))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
