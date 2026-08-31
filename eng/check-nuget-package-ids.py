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

from sbom_common import get_release_package_ids, read_json


def load_package_ids(root: Path) -> list[str]:
    manifest = read_json(root / "eng" / "release-manifest.json")
    return list(get_release_package_ids(manifest))


def load_published_package_ids(root: Path) -> set[str]:
    manifest = read_json(root / "eng" / "published-release.json")
    return set(get_release_package_ids(manifest))


def expected_exists(
    policy: str,
    package_id: str,
    published_package_ids: set[str],
) -> bool | None:
    if policy == "available":
        return False
    if policy == "existing":
        return True
    if policy == "transition":
        return package_id in published_package_ids
    return None


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
        choices=("available", "existing", "transition", "report-only"),
        default="transition",
        help=(
            "available: fail if an ID exists; existing: fail if an ID is absent; "
            "transition: published IDs must exist and newly introduced IDs must be available; "
            "report-only: never fail based on existence."
        ),
    )
    parser.add_argument(
        "--repository-root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
    )
    args = parser.parse_args()

    root = args.repository_root.resolve()
    package_ids = load_package_ids(root)
    published_package_ids = (
        load_published_package_ids(root)
        if args.policy == "transition"
        else set()
    )

    failures: list[str] = []
    for package_id in package_ids:
        exists = package_exists(package_id)
        status = "EXISTS" if exists else "AVAILABLE"
        print(f"{package_id}: {status}")

        expectation = expected_exists(
            args.policy,
            package_id,
            published_package_ids,
        )
        if expectation is True and not exists:
            failures.append(f"{package_id} does not exist on NuGet.org")
        elif expectation is False and exists:
            failures.append(f"{package_id} already exists on NuGet.org")

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