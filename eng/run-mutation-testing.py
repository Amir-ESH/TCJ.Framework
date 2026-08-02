#!/usr/bin/env python3
"""Run one policy-defined Stryker.NET mutation project reproducibly."""

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
DEFAULT_STRYKER_CONFIG = REPOSITORY_ROOT / "stryker-config.json"


class RunnerError(RuntimeError):
    """Raised when the mutation runner cannot construct a valid run."""


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
        raise RunnerError(f"{description} must contain a JSON object: {path}")
    return value


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def source_revision(repository_root: Path) -> str:
    github_sha = os.environ.get("GITHUB_SHA", "").strip()
    if github_sha:
        return github_sha
    completed = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=repository_root,
        text=True,
        capture_output=True,
        check=False,
    )
    if completed.returncode == 0 and completed.stdout.strip():
        return completed.stdout.strip()
    return "local-uncommitted"


def resolve_project(policy: dict[str, Any], name: str) -> dict[str, Any]:
    projects = policy.get("projects")
    if not isinstance(projects, list):
        raise RunnerError("Mutation policy projects must be an array.")
    matches = [project for project in projects if isinstance(project, dict) and project.get("name") == name]
    if len(matches) != 1:
        raise RunnerError(f"Mutation project '{name}' was not found exactly once in the policy.")
    return matches[0]


def require_relative_file(repository_root: Path, value: Any, description: str) -> Path:
    if not isinstance(value, str) or not value.strip():
        raise RunnerError(f"{description} must be a non-empty path.")
    candidate = (repository_root / value).resolve()
    try:
        candidate.relative_to(repository_root.resolve())
    except ValueError as error:
        raise RunnerError(f"{description} must stay inside the repository: {value}") from error
    if not candidate.is_file():
        raise RunnerError(f"{description} is missing: {candidate}")
    return candidate


def build_effective_config(
    base_config: dict[str, Any],
    policy: dict[str, Any],
    project: dict[str, Any],
) -> dict[str, Any]:
    raw = base_config.get("stryker-config")
    if not isinstance(raw, dict):
        raise RunnerError("stryker-config.json must contain a 'stryker-config' object.")

    targets = project.get("mutationTargets")
    exclusions = policy.get("excludedFilePatterns")
    if not isinstance(targets, list) or not targets or any(not isinstance(item, str) or not item for item in targets):
        raise RunnerError(f"Project '{project.get('name')}' must define mutationTargets.")
    if not isinstance(exclusions, list) or any(not isinstance(item, str) or not item for item in exclusions):
        raise RunnerError("Mutation policy excludedFilePatterns must be an array of strings.")

    source_project = project.get("sourceProject")
    if not isinstance(source_project, str) or not source_project.strip():
        raise RunnerError(f"Project '{project.get('name')}' has no sourceProject.")

    effective = dict(raw)
    effective["project"] = Path(source_project).name
    effective["mutate"] = [*targets, *[f"!{pattern}" if not pattern.startswith("!") else pattern for pattern in exclusions]]
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
    parser.add_argument("--stryker-config", type=Path, default=DEFAULT_STRYKER_CONFIG)
    args = parser.parse_args(argv)

    repository_root = args.repository_root.resolve()
    policy_path = args.policy if args.policy.is_absolute() else repository_root / args.policy
    config_path = args.stryker_config if args.stryker_config.is_absolute() else repository_root / args.stryker_config

    try:
        policy = read_object(policy_path, "Mutation policy")
        base_config = read_object(config_path, "Stryker configuration")
        project = resolve_project(policy, args.project)
        test_project = require_relative_file(repository_root, project.get("testProject"), "testProject")
        require_relative_file(repository_root, project.get("sourceProject"), "sourceProject")

        report_value = project.get("reportPath")
        metadata_value = project.get("runMetadataPath")
        log_value = project.get("consoleLogPath")
        if not all(isinstance(value, str) and value for value in (report_value, metadata_value, log_value)):
            raise RunnerError("Project reportPath, runMetadataPath, and consoleLogPath must be strings.")
        project_output = (repository_root / report_value).resolve().parent.parent
        metadata_path = (repository_root / metadata_value).resolve()
        project_output.mkdir(parents=True, exist_ok=True)

        effective = build_effective_config(base_config, policy, project)
        effective_bytes = (json.dumps(effective, indent=2) + "\n").encode("utf-8")
        config_digest = hashlib.sha256(effective_bytes).hexdigest()

        started_at = utc_now()
        with tempfile.NamedTemporaryFile(
            mode="wb",
            prefix=f"stryker-{args.project}-",
            suffix=".json",
            dir=repository_root / "artifacts/mutation",
            delete=False,
        ) as temporary:
            temporary.write(effective_bytes)
            effective_config_path = Path(temporary.name)

        command = [
            "dotnet",
            "tool",
            "run",
            "dotnet-stryker",
            "--",
            "--config-file",
            str(effective_config_path),
            "--output",
            str(project_output),
            "--skip-version-check",
            "--verbosity",
            "info",
        ]
        log_path = (repository_root / log_value).resolve()
        try:
            log_path.relative_to(repository_root)
        except ValueError as error:
            raise RunnerError("consoleLogPath must stay inside the repository.") from error
        try:
            exit_code = tee_process(command, test_project.parent, log_path)
        finally:
            effective_config_path.unlink(missing_ok=True)

        report_path = (repository_root / report_value).resolve()
        metadata = {
            "schemaVersion": 1,
            "project": args.project,
            "sourceRevision": source_revision(repository_root),
            "strykerVersion": policy.get("strykerVersion"),
            "testRunner": policy.get("testRunner"),
            "coverageAnalysis": policy.get("coverageAnalysis"),
            "configurationSha256": config_digest,
            "policySha256": sha256_file(policy_path),
            "startedAtUtc": started_at,
            "completedAtUtc": utc_now(),
            "exitCode": exit_code,
            "status": "success" if exit_code == 0 and report_path.is_file() else "failure",
            "reportPath": str(Path(report_value).as_posix()),
            "reportSha256": sha256_file(report_path) if report_path.is_file() else None,
            "consoleLogPath": str(Path(log_value).as_posix()),
            "consoleLogSha256": sha256_file(log_path) if log_path.is_file() else None,
        }
        metadata_path.parent.mkdir(parents=True, exist_ok=True)
        metadata_path.write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8", newline="\n")

        if exit_code != 0:
            print(f"Stryker failed for {args.project} with exit code {exit_code}.", file=sys.stderr)
        elif not report_path.is_file():
            print(f"Stryker did not create the expected report: {report_path}", file=sys.stderr)
            return 1
        return exit_code
    except (RunnerError, OSError, subprocess.SubprocessError) as error:
        print(f"Mutation runner failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
