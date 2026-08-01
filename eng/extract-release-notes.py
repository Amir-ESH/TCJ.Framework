#!/usr/bin/env python3
"""Extract one release section from CHANGELOG.md."""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--changelog", type=Path, default=Path("CHANGELOG.md"))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    text = args.changelog.read_text(encoding="utf-8")
    pattern = re.compile(
        rf"^## \[{re.escape(args.version)}\] - (?P<date>\d{{4}}-\d{{2}}-\d{{2}})\n"
        rf"(?P<body>.*?)(?=^## |^\[Unreleased\]:|\Z)",
        re.MULTILINE | re.DOTALL,
    )
    match = pattern.search(text)
    if not match:
        print(
            f"No dated CHANGELOG.md section found for {args.version}.",
            file=sys.stderr,
        )
        return 1

    body = match.group("body").strip()
    if not body:
        print(f"Release notes for {args.version} are empty.", file=sys.stderr)
        return 1

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        f"# TCJ Framework {args.version}\n\n"
        f"Released {match.group('date')}.\n\n"
        f"{body}\n",
        encoding="utf-8",
    )
    print(f"Release notes written to {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
