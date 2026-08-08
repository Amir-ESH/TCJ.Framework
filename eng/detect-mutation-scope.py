#!/usr/bin/env python3
"""Determine whether a change requires the expensive TCJ mutation-test run."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
from pathlib import Path
from typing import Any

TOOL_MANIFEST = ".config/dotnet-tools.json"
STRYKER_TOOL = "dotnet-stryker"

MUTATION_RELEVANT = re.compile(
    r"^("
    r"eng/(mutation-(policy|baseline)\.json|run-mutation-testing\.py|verify-mutation-results\.py|"
    r"tests/test_(run_mutation_testing|verify_mutation_results)\.py)|"
    r"stryker-config\.json|tests/TestProject\.props|"
    r"src/TCJ\.(Core|DependencyInjection)/|"
    r"tests/TCJ\.(Core|DependencyInjection)\.Tests/"
    r")"
)


def _git(*args: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", *args],
        check=check,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )


def changed_files(base: str, head: str) -> tuple[str, ...]:
    result = _git("diff", "--name-only", base, head)
    return tuple(line.strip() for line in result.stdout.splitlines() if line.strip())


def _manifest_at(revision: str) -> dict[str, Any] | None:
    result = _git("show", f"{revision}:{TOOL_MANIFEST}", check=False)
    if result.returncode != 0:
        return None
    try:
        data = json.loads(result.stdout)
    except json.JSONDecodeError:
        return None
    return data if isinstance(data, dict) else None


def _tool_definition(manifest: dict[str, Any] | None, tool: str) -> Any:
    if manifest is None:
        return None
    tools = manifest.get("tools")
    return tools.get(tool) if isinstance(tools, dict) else None


def requires_mutation_run(
    paths: tuple[str, ...] | list[str],
    base_manifest: dict[str, Any] | None = None,
    head_manifest: dict[str, Any] | None = None,
) -> tuple[bool, str]:
    for path in paths:
        if MUTATION_RELEVANT.search(path):
            return True, f"Mutation-relevant path changed: {path}"

    if TOOL_MANIFEST in paths:
        base_stryker = _tool_definition(base_manifest, STRYKER_TOOL)
        head_stryker = _tool_definition(head_manifest, STRYKER_TOOL)
        if base_manifest is None or head_manifest is None:
            return True, "Unable to compare dotnet-stryker tool definitions; running conservatively."
        if base_stryker != head_stryker:
            return True, "The pinned dotnet-stryker tool definition changed."
        return False, "Repository tools changed, but the pinned dotnet-stryker definition did not."

    return False, "No controlled production, test, or mutation-runtime input changed."


def write_github_output(path: str | None, run: bool) -> None:
    if not path:
        return
    output = Path(path)
    with output.open("a", encoding="utf-8") as handle:
        handle.write(f"run={'true' if run else 'false'}\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", required=True)
    parser.add_argument("--head", required=True)
    parser.add_argument("--github-output")
    args = parser.parse_args()

    paths = changed_files(args.base, args.head)
    for path in paths:
        print(path)

    base_manifest = _manifest_at(args.base) if TOOL_MANIFEST in paths else None
    head_manifest = _manifest_at(args.head) if TOOL_MANIFEST in paths else None
    run, reason = requires_mutation_run(paths, base_manifest, head_manifest)
    print(reason)
    print(f"Mutation testing required: {'yes' if run else 'no'}")
    write_github_output(args.github_output, run)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
