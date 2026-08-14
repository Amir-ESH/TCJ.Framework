#!/usr/bin/env python3
"""Run one policy-defined Stryker.NET project and record reproducible metadata."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_POLICY = REPOSITORY_ROOT / "eng/mutation-policy.json"
DEFAULT_CONFIG = REPOSITORY_ROOT / "stryker-config.json"


class RunnerError(RuntimeError):
    pass


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def read_object(path: Path, description: str) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise RunnerError(f"{description} is missing: {path}") from error
    except json.JSONDecodeError as error:
        raise RunnerError(f"{description} is malformed JSON: {path}: {error}") from error
    if not isinstance(value, dict):
        raise RunnerError(f"{description} must be a JSON object: {path}")
    return value


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def source_revision(root: Path) -> str:
    github_sha = os.environ.get("GITHUB_SHA", "").strip()
    if github_sha:
        return github_sha
    completed = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=root,
        text=True,
        capture_output=True,
        check=False,
    )
    return completed.stdout.strip() if completed.returncode == 0 else "local-uncommitted"


def resolve_project(policy: dict[str, Any], name: str) -> dict[str, Any]:
    projects = policy.get("projects")
    if not isinstance(projects, list):
        raise RunnerError("Mutation policy projects must be an array.")
    matches = [item for item in projects if isinstance(item, dict) and item.get("name") == name]
    if len(matches) != 1:
        raise RunnerError(f"Mutation project '{name}' was not found exactly once in policy.")
    return matches[0]


def repository_file(root: Path, value: Any, description: str) -> Path:
    if not isinstance(value, str) or not value.strip():
        raise RunnerError(f"{description} must be a non-empty path.")
    candidate = (root / value).resolve()
    try:
        candidate.relative_to(root.resolve())
    except ValueError as error:
        raise RunnerError(f"{description} must stay inside the repository: {value}") from error
    if not candidate.is_file():
        raise RunnerError(f"{description} is missing: {candidate}")
    return candidate


def build_effective_config(
    base_config: dict[str, Any], policy: dict[str, Any], project: dict[str, Any]
) -> dict[str, Any]:
    raw = base_config.get("stryker-config")
    if not isinstance(raw, dict):
        raise RunnerError("stryker-config.json must contain a 'stryker-config' object.")

    targets = project.get("mutationTargets")
    exclusions = policy.get("excludedFilePatterns")
    if not isinstance(targets, list) or not targets or any(not isinstance(x, str) or not x for x in targets):
        raise RunnerError(f"Project '{project.get('name')}' must define mutationTargets.")
    if not isinstance(exclusions, list) or any(not isinstance(x, str) or not x for x in exclusions):
        raise RunnerError("excludedFilePatterns must be an array of strings.")

    source_project = project.get("sourceProject")
    if not isinstance(source_project, str) or not source_project:
        raise RunnerError(f"Project '{project.get('name')}' must define sourceProject.")

    effective = dict(raw)
    effective["project"] = Path(source_project).name
    effective["mutate"] = [
        *targets,
        *[pattern if pattern.startswith("!") else f"!{pattern}" for pattern in exclusions],
    ]
    return {"stryker-config": effective}


def tee_process(command: list[str], cwd: Path, log_path: Path) -> int:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    with log_path.open("w", encoding="utf-8", newline="\n") as log:
        process = subprocess.Popen(
            command,
            cwd=cwd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1,
        )
        assert process.stdout is not None
        for line in process.stdout:
            sys.stdout.write(line)
            log.write(line)
        return process.wait()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", required=True)
    parser.add_argument("--repository-root", type=Path, default=REPOSITORY_ROOT)
    parser.add_argument("--policy", type=Path, default=DEFAULT_POLICY)
    parser.add_argument("--stryker-config", type=Path, default=DEFAULT_CONFIG)
    args = parser.parse_args(argv)

    root = args.repository_root.resolve()
    policy_path = args.policy if args.policy.is_absolute() else root / args.policy
    config_path = args.stryker_config if args.stryker_config.is_absolute() else root / args.stryker_config

    try:
        policy = read_object(policy_path, "Mutation policy")
        base_config = read_object(config_path, "Stryker configuration")
        project = resolve_project(policy, args.project)
        test_project = repository_file(root, project.get("testProject"), "testProject")
        repository_file(root, project.get("sourceProject"), "sourceProject")

        required_paths = ("reportPath", "runMetadataPath", "consoleLogPath")
        if any(not isinstance(project.get(key), str) or not project[key] for key in required_paths):
            raise RunnerError("Project reportPath, runMetadataPath, and consoleLogPath are required.")

        report_path = (root / project["reportPath"]).resolve()
        output_dir = report_path.parent.parent
        metadata_path = (root / project["runMetadataPath"]).resolve()
        log_path = (root / project["consoleLogPath"]).resolve()
        for candidate in (report_path, output_dir, metadata_path, log_path):
            candidate.relative_to(root)
        output_dir.mkdir(parents=True, exist_ok=True)

        effective = build_effective_config(base_config, policy, project)
        effective_bytes = (json.dumps(effective, indent=2) + "\n").encode("utf-8")
        config_hash = hashlib.sha256(effective_bytes).hexdigest()
        started_at = utc_now()

        temp_parent = root / "artifacts/mutation"
        temp_parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(
            mode="wb",
            prefix=f"stryker-{args.project}-",
            suffix=".json",
            dir=temp_parent,
            delete=False,
        ) as stream:
            stream.write(effective_bytes)
            temp_config = Path(stream.name)

        command = [
            "dotnet",
            "tool",
            "run",
            "dotnet-stryker",
            "--",
            "--config-file",
            str(temp_config),
            "--output",
            str(output_dir),
            "--skip-version-check",
            "--verbosity",
            "info",
        ]

        try:
            exit_code = tee_process(command, test_project.parent, log_path)
        finally:
            temp_config.unlink(missing_ok=True)

        metadata = {
            "schemaVersion": 1,
            "project": args.project,
            "sourceRevision": source_revision(root),
            "strykerVersion": policy.get("strykerVersion"),
            "testRunner": policy.get("testRunner"),
            "coverageAnalysis": policy.get("coverageAnalysis"),
            "configurationSha256": config_hash,
            "policySha256": sha256_file(policy_path),
            "startedAtUtc": started_at,
            "completedAtUtc": utc_now(),
            "exitCode": exit_code,
            "status": "success" if exit_code == 0 and report_path.is_file() else "failure",
            "reportPath": project["reportPath"],
            "reportSha256": sha256_file(report_path) if report_path.is_file() else None,
            "consoleLogPath": project["consoleLogPath"],
            "consoleLogSha256": sha256_file(log_path) if log_path.is_file() else None,
        }
        metadata_path.parent.mkdir(parents=True, exist_ok=True)
        metadata_path.write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8", newline="\n")

        if exit_code != 0:
            print(f"Stryker failed for {args.project} with exit code {exit_code}.", file=sys.stderr)
            return exit_code
        if not report_path.is_file():
            print(f"Expected Stryker report was not created: {report_path}", file=sys.stderr)
            return 1
        return 0
    except (RunnerError, OSError, subprocess.SubprocessError, ValueError) as error:
        print(f"Mutation runner failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
