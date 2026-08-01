#!/usr/bin/env python3
"""Check whether release package IDs already exist on NuGet.org."""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


def load_package_ids(root: Path) -> list[str]:
    manifest = json.loads(
        (root / "eng" / "release-manifest.json").read_text(encoding="utf-8")
    )
    return [str(item) for item in manifest["packages"]]


def package_exists(package_id: str) -> bool:
    url = (
        "https://api.nuget.org/v3-flatcontainer/"
        f"{package_id.lower()}/index.json"
    )
    request = Request(
        url,
        headers={"User-Agent": "TCJ-Framework-release-preflight/1.0"},
    )

    last_error: Exception | None = None
    for attempt in range(1, 4):
        try:
            with urlopen(request, timeout=20) as response:
                if response.status != 200:
                    raise RuntimeError(
                        f"Unexpected NuGet.org status {response.status} for {package_id}."
                    )
                json.load(response)
                return True
        except HTTPError as error:
            if error.code == 404:
                return False
            last_error = error
        except (URLError, TimeoutError, json.JSONDecodeError) as error:
            last_error = error

        if attempt < 3:
            time.sleep(attempt * 2)

    raise RuntimeError(
        f"Unable to query NuGet.org for {package_id}: {last_error}"
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--policy",
        choices=("available", "existing", "report-only"),
        default="available",
        help=(
            "available: fail if an ID exists; existing: fail if an ID is absent; "
            "report-only: never fail based on existence."
        ),
    )
    parser.add_argument(
        "--repository-root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
    )
    args = parser.parse_args()

    failures: list[str] = []
    for package_id in load_package_ids(args.repository_root.resolve()):
        exists = package_exists(package_id)
        status = "EXISTS" if exists else "AVAILABLE"
        print(f"{package_id}: {status}")

        if args.policy == "available" and exists:
            failures.append(f"{package_id} already exists on NuGet.org")
        elif args.policy == "existing" and not exists:
            failures.append(f"{package_id} does not exist on NuGet.org")

    if failures:
        print("NuGet package ID policy failed:", file=sys.stderr)
        for failure in failures:
            print(f"  - {failure}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, KeyError, json.JSONDecodeError) as error:
        print(f"NuGet package ID check failed: {error}", file=sys.stderr)
        raise SystemExit(1)
