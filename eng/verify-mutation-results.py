#!/usr/bin/env python3
"""Validate TCJ mutation configuration, report health, baseline, and quality gates."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import subprocess
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any, Iterable

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_POLICY = REPOSITORY_ROOT / "eng/mutation-policy.json"
DEFAULT_BASELINE = REPOSITORY_ROOT / "eng/mutation-baseline.json"
DEFAULT_STRYKER_CONFIG = REPOSITORY_ROOT / "stryker-config.json"
DEFAULT_TOOL_MANIFEST = REPOSITORY_ROOT / ".config/dotnet-tools.json"
DEFAULT_WORKFLOW = REPOSITORY_ROOT / ".github/workflows/mutation-testing.yml"
DEFAULT_CI_WORKFLOW = REPOSITORY_ROOT / ".github/workflows/ci.yml"
DEFAULT_TEST_PROPS = REPOSITORY_ROOT / "tests/TestProject.props"
DEFAULT_SUMMARY = REPOSITORY_ROOT / "artifacts/mutation/MUTATION_SUMMARY.md"
DEFAULT_JSON = REPOSITORY_ROOT / "artifacts/mutation/mutation-summary.json"
DEFAULT_CANDIDATE = REPOSITORY_ROOT / "artifacts/mutation/mutation-baseline-candidate.json"

REQUIRED_PROJECTS = ("TCJ.Core", "TCJ.DependencyInjection")
REQUIRED_REPORTERS = {"html", "json"}
TERMINAL_VALID_STATUSES = {"killed", "survived", "timeout", "no_coverage", "ignored", "compile_error"}
STATUS_NAMES = {
    "killed": "killed",
    "survived": "survived",
    "timeout": "timeout",
    "nocoverage": "no_coverage",
    "ignored": "ignored",
    "compileerror": "compile_error",
    "builderror": "compile_error",
    "runtimeerror": "runtime_error",
    "pending": "pending",
    "notrun": "not_run",
}

REQUIRED_WORKFLOW_FRAGMENTS = (
    "name: Mutation testing",
    "workflow_call:",
    "workflow_dispatch:",
    "capture-baseline",
    "schedule:",
    "name: Run mutation tests",
    "dotnet tool restore",
    "run-mutation-testing.py",
    "verify-mutation-results.py",
    "Upload mutation reports",
    "mutation-baseline-candidate.json",
)
REQUIRED_CI_FRAGMENTS = (
    "Detect mutation-testing changes",
    "Mutation quality gate",
    "uses: ./.github/workflows/mutation-testing.yml",
    "mode: verify",
    "needs: [mutation-scope, mutation-testing]",
    "needs.mutation-testing.result == 'skipped'",
    "validate-baseline",
)


class MutationError(RuntimeError):
    """Raised when mutation policy, reports, or repository configuration are invalid."""


@dataclass(frozen=True)
class ProjectPolicy:
    name: str
    source_project: str
    test_project: str
    minimum_tested_mutants: int
    mutation_targets: tuple[str, ...]
    report_path: str
    html_report_path: str
    run_metadata_path: str
    console_log_path: str


@dataclass(frozen=True)
class MutationPolicy:
    path: Path
    stryker_version: str
    test_runner: str
    coverage_analysis: str
    baseline_path: str
    require_recorded_baseline: bool
    minimum_mutation_score: float
    allowed_baseline_score_regression: float
    minimum_tested_mutants: int
    minimum_killed_mutants: int
    minimum_killed_mutants_per_project: int
    maximum_compile_error_percentage: float
    maximum_runtime_error_mutants: int
    projects: tuple[ProjectPolicy, ...]
    excluded_file_patterns: tuple[str, ...]
    scope_notes: tuple[str, ...]
    ignored_mutation_types: tuple[str, ...]
    ignored_mutation_justifications: dict[str, str]
    forbidden_runner_log_markers: tuple[str, ...]
    reports_directory: str
    summary_json: str
    summary_markdown: str
    baseline_candidate: str


@dataclass(frozen=True)
class MutationBaseline:
    path: Path
    status: str
    data: dict[str, Any]

    @property
    def is_recorded(self) -> bool:
        return self.status == "recorded"

    @property
    def mutation_score(self) -> float | None:
        value = self.data.get("mutationScore")
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            return None
        return float(value)


@dataclass
class MutationCounts:
    killed: int = 0
    survived: int = 0
    timeout: int = 0
    no_coverage: int = 0
    ignored: int = 0
    compile_error: int = 0
    runtime_error: int = 0
    pending: int = 0
    not_run: int = 0

    def add(self, other: "MutationCounts") -> None:
        for field_name in self.__dataclass_fields__:
            setattr(self, field_name, getattr(self, field_name) + getattr(other, field_name))

    @property
    def total(self) -> int:
        return sum(getattr(self, field_name) for field_name in self.__dataclass_fields__)

    @property
    def tested(self) -> int:
        return self.killed + self.survived + self.timeout

    @property
    def detected(self) -> int:
        return self.killed + self.timeout

    @property
    def score_denominator(self) -> int:
        return self.killed + self.timeout + self.survived + self.no_coverage

    @property
    def mutation_score(self) -> float:
        return 0.0 if self.score_denominator == 0 else self.detected * 100.0 / self.score_denominator

    @property
    def compile_error_percentage(self) -> float:
        return 0.0 if self.total == 0 else self.compile_error * 100.0 / self.total

    def as_dict(self) -> dict[str, int | float]:
        return {
            "totalMutants": self.total,
            "testedMutants": self.tested,
            "killedMutants": self.killed,
            "survivedMutants": self.survived,
            "timeoutMutants": self.timeout,
            "noCoverageMutants": self.no_coverage,
            "ignoredMutants": self.ignored,
            "compileErrorMutants": self.compile_error,
            "compileErrorPercentage": round(self.compile_error_percentage, 2),
            "runtimeErrorMutants": self.runtime_error,
            "pendingMutants": self.pending,
            "notRunMutants": self.not_run,
            "mutationScore": round(self.mutation_score, 2),
        }


@dataclass(frozen=True)
class RunMetadata:
    path: str
    source_revision: str
    stryker_version: str
    test_runner: str
    coverage_analysis: str
    status: str
    exit_code: int
    report_sha256: str
    policy_sha256: str
    console_log_path: str
    console_log_sha256: str


@dataclass(frozen=True)
class ProjectResult:
    policy: ProjectPolicy
    counts: MutationCounts
    test_count: int
    report_sha256: str
    metadata: RunMetadata
    health_failures: tuple[str, ...]


@dataclass(frozen=True)
class VerificationResult:
    projects: tuple[ProjectResult, ...]
    totals: MutationCounts
    policy_score_passed: bool
    baseline_score_passed: bool
    mutant_count_passed: bool
    killed_count_passed: bool
    health_failures: tuple[str, ...]
    baseline_status: str
    effective_minimum_score: float

    @property
    def health_passed(self) -> bool:
        return not self.health_failures

    @property
    def passed(self) -> bool:
        return (
            self.health_passed
            and self.policy_score_passed
            and self.baseline_score_passed
            and self.mutant_count_passed
            and self.killed_count_passed
            and self.baseline_status == "recorded"
        )


def fail(message: str) -> None:
    raise MutationError(message)


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
    except FileNotFoundError:
        fail(f"Required file is missing: {path}")
    return digest.hexdigest()


def read_json_object(path: Path, description: str) -> dict[str, Any]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        fail(f"{description} is missing: {path}")
    except OSError as error:
        fail(f"Unable to read {description} at {path}: {error}")
    except json.JSONDecodeError as error:
        fail(f"{description} is malformed JSON at {path}: {error}")
    if not isinstance(data, dict):
        fail(f"{description} must contain a JSON object: {path}")
    return data


def require_number(data: dict[str, Any], key: str, minimum: float, maximum: float) -> float:
    value = data.get(key)
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(float(value)):
        fail(f"Policy property '{key}' must be a finite number.")
    number = float(value)
    if number < minimum or number > maximum:
        fail(f"Policy property '{key}' must be between {minimum} and {maximum}.")
    return number


def require_integer(data: dict[str, Any], key: str, minimum: int = 0) -> int:
    value = data.get(key)
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        fail(f"Policy property '{key}' must be an integer greater than or equal to {minimum}.")
    return value


def require_bool(data: dict[str, Any], key: str) -> bool:
    value = data.get(key)
    if not isinstance(value, bool):
        fail(f"Policy property '{key}' must be a boolean.")
    return value


def require_string(data: dict[str, Any], key: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        fail(f"Policy property '{key}' must be a non-empty string.")
    return value.strip()


def require_sha256(data: dict[str, Any], key: str, description: str) -> str:
    value = data.get(key)
    if (
        not isinstance(value, str)
        or len(value) != 64
        or any(character not in "0123456789abcdef" for character in value.lower())
    ):
        fail(f"{description} property '{key}' must be a SHA-256 hex digest.")
    return value.lower()


def require_non_negative_count(data: dict[str, Any], key: str, description: str) -> int:
    value = data.get(key)
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        fail(f"{description} property '{key}' must be a non-negative integer.")
    return value


def require_string_list(data: dict[str, Any], key: str, allow_empty: bool = False) -> tuple[str, ...]:
    value = data.get(key)
    if not isinstance(value, list) or (not allow_empty and not value):
        fail(f"Policy property '{key}' must be {'an' if allow_empty else 'a non-empty'} array.")
    if any(not isinstance(item, str) or not item.strip() for item in value):
        fail(f"Policy property '{key}' must contain only non-empty strings.")
    return tuple(item.strip() for item in value)


def validate_repository_relative_path(value: Any, description: str) -> str:
    if not isinstance(value, str) or not value.strip():
        fail(f"{description} must be a non-empty repository-relative path.")
    normalized = value.replace("\\", "/").strip()
    path = PurePosixPath(normalized)
    if path.is_absolute() or ".." in path.parts:
        fail(f"{description} must stay inside the repository: {value}")
    return str(path)


def load_project_policy(data: dict[str, Any], index: int) -> ProjectPolicy:
    prefix = f"projects[{index}]"
    name = require_string(data, "name")
    source = validate_repository_relative_path(data.get("sourceProject"), f"{prefix}.sourceProject")
    test = validate_repository_relative_path(data.get("testProject"), f"{prefix}.testProject")
    minimum_tested = require_integer(data, "minimumTestedMutants", minimum=1)
    targets = require_string_list(data, "mutationTargets")
    if any(target.startswith("!") for target in targets):
        fail(f"{prefix}.mutationTargets must contain positive include patterns only.")
    if "**/*.cs" in targets or "**/*" in targets:
        fail(f"{prefix}.mutationTargets is too broad for the controlled Step 29 baseline.")
    report = validate_repository_relative_path(data.get("reportPath"), f"{prefix}.reportPath")
    html = validate_repository_relative_path(data.get("htmlReportPath"), f"{prefix}.htmlReportPath")
    metadata = validate_repository_relative_path(data.get("runMetadataPath"), f"{prefix}.runMetadataPath")
    console_log = validate_repository_relative_path(data.get("consoleLogPath"), f"{prefix}.consoleLogPath")
    return ProjectPolicy(name, source, test, minimum_tested, targets, report, html, metadata, console_log)


def load_policy(path: Path) -> MutationPolicy:
    data = read_json_object(path, "Mutation policy")
    if data.get("schemaVersion") != 2:
        fail("Mutation policy schemaVersion must be 2.")

    projects_data = data.get("projects")
    if not isinstance(projects_data, list) or not projects_data:
        fail("Mutation policy projects must be a non-empty array.")
    projects = tuple(load_project_policy(item, index) if isinstance(item, dict) else fail(f"projects[{index}] must be an object.") for index, item in enumerate(projects_data))
    names = tuple(project.name for project in projects)
    if len(set(names)) != len(names):
        fail("Mutation project names must be unique.")
    for required in REQUIRED_PROJECTS:
        if required not in names:
            fail(f"Mutation policy must include initial project '{required}'.")

    ignored_types = require_string_list(data, "ignoredMutationTypes", allow_empty=True)
    justifications_data = data.get("ignoredMutationJustifications")
    if not isinstance(justifications_data, dict):
        fail("ignoredMutationJustifications must be an object.")
    justifications: dict[str, str] = {}
    for key, value in justifications_data.items():
        if not isinstance(key, str) or not key.strip() or not isinstance(value, str) or not value.strip():
            fail("Ignored mutation justifications require non-empty string keys and values.")
        justifications[key.strip()] = value.strip()
    if set(ignored_types) != set(justifications):
        fail("Every ignored mutation type must have exactly one justification.")

    paths = data.get("reportPaths")
    if not isinstance(paths, dict):
        fail("reportPaths must be an object.")

    return MutationPolicy(
        path=path,
        stryker_version=require_string(data, "strykerVersion"),
        test_runner=require_string(data, "testRunner"),
        coverage_analysis=require_string(data, "coverageAnalysis"),
        baseline_path=validate_repository_relative_path(data.get("baselinePath"), "baselinePath"),
        require_recorded_baseline=require_bool(data, "requireRecordedBaseline"),
        minimum_mutation_score=require_number(data, "minimumMutationScore", 0.0, 100.0),
        allowed_baseline_score_regression=require_number(data, "allowedBaselineScoreRegression", 0.0, 100.0),
        minimum_tested_mutants=require_integer(data, "minimumTestedMutants", 1),
        minimum_killed_mutants=require_integer(data, "minimumKilledMutants", 1),
        minimum_killed_mutants_per_project=require_integer(data, "minimumKilledMutantsPerProject", 1),
        maximum_compile_error_percentage=require_number(data, "maximumCompileErrorPercentage", 0.0, 100.0),
        maximum_runtime_error_mutants=require_integer(data, "maximumRuntimeErrorMutants", 0),
        projects=projects,
        excluded_file_patterns=require_string_list(data, "excludedFilePatterns"),
        scope_notes=require_string_list(data, "scopeNotes"),
        ignored_mutation_types=ignored_types,
        ignored_mutation_justifications=justifications,
        forbidden_runner_log_markers=require_string_list(data, "forbiddenRunnerLogMarkers"),
        reports_directory=validate_repository_relative_path(paths.get("reportsDirectory"), "reportPaths.reportsDirectory"),
        summary_json=validate_repository_relative_path(paths.get("summaryJson"), "reportPaths.summaryJson"),
        summary_markdown=validate_repository_relative_path(paths.get("summaryMarkdown"), "reportPaths.summaryMarkdown"),
        baseline_candidate=validate_repository_relative_path(paths.get("baselineCandidate"), "reportPaths.baselineCandidate"),
    )


def validate_recorded_counts(data: dict[str, Any], description: str) -> MutationCounts:
    counts = MutationCounts(
        killed=require_non_negative_count(data, "killedMutants", description),
        survived=require_non_negative_count(data, "survivedMutants", description),
        timeout=require_non_negative_count(data, "timeoutMutants", description),
        no_coverage=require_non_negative_count(data, "noCoverageMutants", description),
        ignored=require_non_negative_count(data, "ignoredMutants", description),
        compile_error=require_non_negative_count(data, "compileErrorMutants", description),
        runtime_error=require_non_negative_count(data, "runtimeErrorMutants", description),
        pending=require_non_negative_count(data, "pendingMutants", description),
        not_run=require_non_negative_count(data, "notRunMutants", description),
    )
    total = require_non_negative_count(data, "totalMutants", description)
    tested = require_non_negative_count(data, "testedMutants", description)
    if total != counts.total:
        fail(f"{description} totalMutants does not match its status counts.")
    if tested != counts.tested:
        fail(f"{description} testedMutants does not match killed + survived + timeout.")
    compile_percentage = data.get("compileErrorPercentage")
    if (
        isinstance(compile_percentage, bool)
        or not isinstance(compile_percentage, (int, float))
        or abs(float(compile_percentage) - round(counts.compile_error_percentage, 2)) > 0.011
    ):
        fail(f"{description} compileErrorPercentage does not match its mutant counts.")
    score = data.get("mutationScore")
    if isinstance(score, bool) or not isinstance(score, (int, float)) or not 0 <= float(score) <= 100:
        fail(f"{description} mutationScore must be between 0 and 100.")
    if abs(float(score) - round(counts.mutation_score, 2)) > 0.011:
        fail(f"{description} mutationScore does not match its mutant counts.")
    return counts


def validate_baseline_projects(
    projects: Any,
    policy: MutationPolicy,
    *,
    description: str,
) -> tuple[dict[str, Any], ...]:
    if not isinstance(projects, list) or len(projects) != len(policy.projects):
        fail(f"{description} projects must exactly match policy projects.")
    project_by_name = {project.name: project for project in policy.projects}
    validated: list[dict[str, Any]] = []
    seen: set[str] = set()
    for item in projects:
        if not isinstance(item, dict):
            fail(f"{description} project entries must be objects.")
        name = item.get("name")
        if not isinstance(name, str) or name not in project_by_name or name in seen:
            fail(f"{description} projects must exactly match policy projects.")
        seen.add(name)
        counts = validate_recorded_counts(item, f"{description} project '{name}'")
        require_sha256(item, "reportSha256", f"{description} project '{name}'")
        if counts.tested < project_by_name[name].minimum_tested_mutants:
            fail(f"{description} project '{name}' has too few tested mutants.")
        if counts.killed < policy.minimum_killed_mutants_per_project:
            fail(f"{description} project '{name}' has too few killed mutants.")
        if counts.compile_error_percentage > policy.maximum_compile_error_percentage + 1e-9:
            fail(f"{description} project '{name}' exceeds the compile-error policy.")
        if counts.runtime_error > policy.maximum_runtime_error_mutants or counts.pending or counts.not_run:
            fail(f"{description} project '{name}' contains incomplete or runtime-error mutants.")
        validated.append(item)
    if seen != set(project_by_name):
        fail(f"{description} projects must exactly match policy projects.")
    return tuple(validated)


def load_baseline(path: Path, policy: MutationPolicy) -> MutationBaseline:
    data = read_json_object(path, "Mutation baseline")
    if data.get("schemaVersion") != 1:
        fail("Mutation baseline schemaVersion must be 1.")
    status = data.get("status")
    if status not in {"pending", "recorded"}:
        fail("Mutation baseline status must be 'pending' or 'recorded'.")
    if status == "pending":
        reason = data.get("reason")
        if not isinstance(reason, str) or not reason.strip():
            fail("A pending mutation baseline must explain why it is pending.")
        return MutationBaseline(path, status, data)

    required_strings = (
        "recordedAtUtc",
        "reviewedAtUtc",
        "reviewedBy",
        "reviewNotes",
        "sourceRevision",
        "strykerVersion",
        "testRunner",
        "coverageAnalysis",
    )
    for key in required_strings:
        if not isinstance(data.get(key), str) or not data[key].strip():
            fail(f"Recorded mutation baseline property '{key}' must be a non-empty string.")
    if data["strykerVersion"] != policy.stryker_version:
        fail("Recorded mutation baseline Stryker version does not match policy.")
    if data["testRunner"] != policy.test_runner or data["coverageAnalysis"] != policy.coverage_analysis:
        fail("Recorded mutation baseline runner configuration does not match policy.")
    if data.get("reviewRequired") is not False or data.get("survivedMutantsReviewed") is not True:
        fail("Recorded mutation baseline must contain an explicit survived-mutant review attestation.")
    require_sha256(data, "reportSetSha256", "Recorded mutation baseline")
    counts = validate_recorded_counts(data, "Recorded mutation baseline")
    if counts.mutation_score + 1e-9 < policy.minimum_mutation_score:
        fail("Recorded mutation baseline score is below the repository policy.")
    if counts.tested < policy.minimum_tested_mutants:
        fail("Recorded mutation baseline has too few tested mutants.")
    if counts.killed < policy.minimum_killed_mutants:
        fail("Recorded mutation baseline has too few killed mutants.")
    if counts.compile_error_percentage > policy.maximum_compile_error_percentage + 1e-9:
        fail("Recorded mutation baseline exceeds the compile-error policy.")
    if counts.runtime_error > policy.maximum_runtime_error_mutants or counts.pending or counts.not_run:
        fail("Recorded mutation baseline contains incomplete or runtime-error mutants.")
    validate_baseline_projects(data.get("projects"), policy, description="Recorded mutation baseline")
    return MutationBaseline(path, status, data)


def load_baseline_candidate(path: Path, policy: MutationPolicy) -> dict[str, Any]:
    data = read_json_object(path, "Mutation baseline candidate")
    if data.get("schemaVersion") != 1 or data.get("status") != "candidate":
        fail("Mutation baseline candidate must use schemaVersion 1 and status 'candidate'.")
    for key in ("generatedAtUtc", "sourceRevision", "strykerVersion", "testRunner", "coverageAnalysis"):
        if not isinstance(data.get(key), str) or not data[key].strip():
            fail(f"Mutation baseline candidate property '{key}' must be a non-empty string.")
    if data["strykerVersion"] != policy.stryker_version:
        fail("Mutation baseline candidate Stryker version does not match policy.")
    if data["testRunner"] != policy.test_runner or data["coverageAnalysis"] != policy.coverage_analysis:
        fail("Mutation baseline candidate runner configuration does not match policy.")
    if data.get("reviewRequired") is not True or data.get("survivedMutantsReviewed") is not False:
        fail("Mutation baseline candidate must remain unreviewed until explicitly accepted.")
    require_sha256(data, "reportSetSha256", "Mutation baseline candidate")
    counts = validate_recorded_counts(data, "Mutation baseline candidate")
    if counts.mutation_score + 1e-9 < policy.minimum_mutation_score:
        fail("Mutation baseline candidate score is below the repository policy.")
    if counts.tested < policy.minimum_tested_mutants or counts.killed < policy.minimum_killed_mutants:
        fail("Mutation baseline candidate does not meet aggregate mutant-count policy.")
    if counts.compile_error_percentage > policy.maximum_compile_error_percentage + 1e-9:
        fail("Mutation baseline candidate exceeds the compile-error policy.")
    if counts.runtime_error > policy.maximum_runtime_error_mutants or counts.pending or counts.not_run:
        fail("Mutation baseline candidate contains incomplete or runtime-error mutants.")
    validate_baseline_projects(data.get("projects"), policy, description="Mutation baseline candidate")
    return data


def accept_baseline_candidate(
    candidate_path: Path,
    output_path: Path,
    policy: MutationPolicy,
    reviewed_by: str,
    review_notes: str,
) -> dict[str, Any]:
    if not reviewed_by.strip():
        fail("--reviewed-by is required when accepting a baseline.")
    if not review_notes.strip():
        fail("--review-notes is required when accepting a baseline.")
    candidate = load_baseline_candidate(candidate_path, policy)
    baseline = dict(candidate)
    baseline.update({
        "status": "recorded",
        "recordedAtUtc": utc_now(),
        "reviewedAtUtc": utc_now(),
        "reviewedBy": reviewed_by.strip(),
        "reviewNotes": review_notes.strip(),
        "reviewRequired": False,
        "survivedMutantsReviewed": True,
    })
    baseline.pop("generatedAtUtc", None)
    baseline.pop("reviewInstructions", None)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(baseline, indent=2) + "\n", encoding="utf-8", newline="\n")
    load_baseline(output_path, policy)
    return baseline


def validate_git_tracking(repository_root: Path, paths: Iterable[Path]) -> None:
    if not (repository_root / ".git").exists():
        fail("Git metadata is unavailable; use --skip-git-check only for an exported source archive.")
    for path in paths:
        relative = path.resolve().relative_to(repository_root.resolve())
        ignored = subprocess.run(["git", "check-ignore", "-q", "--", str(relative)], cwd=repository_root, check=False)
        if ignored.returncode == 0:
            fail(f"Required mutation file is ignored by Git: {relative}")
        tracked = subprocess.run(["git", "ls-files", "--error-unmatch", "--", str(relative)], cwd=repository_root, text=True, capture_output=True, check=False)
        if tracked.returncode != 0:
            fail(f"Required mutation file is not tracked by Git: {relative}")


def validate_configuration(
    repository_root: Path,
    policy_path: Path,
    baseline_path: Path,
    stryker_config_path: Path,
    tool_manifest_path: Path,
    workflow_path: Path,
    ci_workflow_path: Path,
    test_props_path: Path,
    check_git: bool,
) -> tuple[MutationPolicy, MutationBaseline]:
    policy = load_policy(policy_path)
    baseline = load_baseline(baseline_path, policy)

    config = read_json_object(stryker_config_path, "Stryker configuration").get("stryker-config")
    if not isinstance(config, dict):
        fail("Stryker configuration must contain a stryker-config object.")
    reporters = config.get("reporters")
    if not isinstance(reporters, list) or not REQUIRED_REPORTERS.issubset({str(item).lower() for item in reporters}):
        fail("Stryker configuration must enable HTML and JSON reporters.")
    if config.get("test-runner") != policy.test_runner:
        fail("Stryker test-runner must match mutation policy.")
    if config.get("coverage-analysis") != policy.coverage_analysis:
        fail("Stryker coverage-analysis must match mutation policy.")
    if policy.test_runner != "mtp":
        fail("Step 29 remediation requires the MTP runner for the repository's xUnit v3 tests.")
    if policy.coverage_analysis != "off":
        fail("Step 29 remediation requires coverage-analysis=off until optimized capture is proven valid.")
    thresholds = config.get("thresholds")
    if not isinstance(thresholds, dict) or thresholds.get("break") != 0:
        fail("Stryker break threshold must remain 0; the repository verifier owns the aggregate gate.")

    tool_manifest = read_json_object(tool_manifest_path, ".NET tool manifest")
    tool = tool_manifest.get("tools", {}).get("dotnet-stryker") if isinstance(tool_manifest.get("tools"), dict) else None
    if not isinstance(tool, dict) or tool.get("version") != policy.stryker_version:
        fail("The local dotnet-stryker tool version must match mutation policy.")

    workflow_text = workflow_path.read_text(encoding="utf-8")
    for fragment in REQUIRED_WORKFLOW_FRAGMENTS:
        if fragment not in workflow_text:
            fail(f"Mutation workflow is missing required fragment: {fragment}")
    ci_text = ci_workflow_path.read_text(encoding="utf-8")
    for fragment in REQUIRED_CI_FRAGMENTS:
        if fragment not in ci_text:
            fail(f"CI workflow is missing required mutation gate fragment: {fragment}")
    props_text = test_props_path.read_text(encoding="utf-8")
    if "<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>" not in props_text:
        fail("tests/TestProject.props must enable the Microsoft Testing Platform runner.")

    expected_paths = {project.report_path for project in policy.projects}
    if len(expected_paths) != len(policy.projects):
        fail("Project report paths must be unique.")
    for project in policy.projects:
        for relative in (project.source_project, project.test_project):
            if not (repository_root / relative).is_file():
                fail(f"Configured project file is missing: {relative}")

    if check_git:
        tracked = [
            policy_path,
            baseline_path,
            stryker_config_path,
            tool_manifest_path,
            workflow_path,
            ci_workflow_path,
            test_props_path,
            repository_root / "eng/run-mutation-testing.py",
            Path(__file__).resolve(),
        ]
        validate_git_tracking(repository_root, tracked)
    return policy, baseline


def normalize_status(value: Any, report_path: Path) -> str:
    if not isinstance(value, str) or not value.strip():
        fail(f"A mutant in {report_path} has no valid status.")
    normalized = "".join(character for character in value.lower() if character.isalnum())
    if normalized not in STATUS_NAMES:
        fail(f"Unsupported mutant status '{value}' in {report_path}.")
    return STATUS_NAMES[normalized]


def report_identifies_project(data: dict[str, Any], project: str) -> bool:
    root = data.get("projectRoot")
    if isinstance(root, str) and PurePosixPath(root.replace("\\", "/").rstrip("/")).name.lower() == project.lower():
        return True
    files = data.get("files")
    if isinstance(files, dict):
        return any(project.lower() in {part.lower() for part in PurePosixPath(str(name).replace("\\", "/")).parts} for name in files)
    return False


def parse_run_metadata(
    repository_root: Path,
    path: Path,
    project: ProjectPolicy,
    policy: MutationPolicy,
    report_path: Path,
) -> RunMetadata:
    data = read_json_object(path, f"Run metadata for {project.name}")
    if data.get("schemaVersion") != 1 or data.get("project") != project.name:
        fail(f"Run metadata does not identify project '{project.name}': {path}")
    required = (
        "sourceRevision", "strykerVersion", "testRunner", "coverageAnalysis", "status",
        "reportSha256", "policySha256", "consoleLogPath", "consoleLogSha256"
    )
    for key in required:
        if not isinstance(data.get(key), str) or not data[key].strip():
            fail(f"Run metadata property '{key}' is invalid for {project.name}.")
    exit_code = data.get("exitCode")
    if isinstance(exit_code, bool) or not isinstance(exit_code, int):
        fail(f"Run metadata exitCode is invalid for {project.name}.")
    if data["strykerVersion"] != policy.stryker_version:
        fail(f"Stryker version mismatch for {project.name}.")
    if data["testRunner"] != policy.test_runner or data["coverageAnalysis"] != policy.coverage_analysis:
        fail(f"Runner configuration mismatch for {project.name}.")
    actual_hash = sha256_file(report_path)
    if data["reportSha256"] != actual_hash:
        fail(f"Run metadata report hash mismatch for {project.name}.")
    if data["policySha256"] != sha256_file(policy.path):
        fail(f"Run metadata policy hash mismatch for {project.name}.")
    if data["consoleLogPath"] != project.console_log_path:
        fail(f"Run metadata console-log path mismatch for {project.name}.")
    console_log_path = repository_root / project.console_log_path
    if data["consoleLogSha256"] != sha256_file(console_log_path):
        fail(f"Run metadata console-log hash mismatch for {project.name}.")
    return RunMetadata(
        str(path), data["sourceRevision"], data["strykerVersion"], data["testRunner"],
        data["coverageAnalysis"], data["status"], exit_code, data["reportSha256"], data["policySha256"],
        data["consoleLogPath"], data["consoleLogSha256"]
    )


def parse_report(repository_root: Path, project: ProjectPolicy, policy: MutationPolicy) -> ProjectResult:
    report_path = repository_root / project.report_path
    data = read_json_object(report_path, f"Stryker report for {project.name}")
    schema_version = data.get("schemaVersion")
    if str(schema_version) != "2":
        fail(f"Stryker report schemaVersion must be 2 for {project.name}.")
    if not report_identifies_project(data, project.name):
        fail(f"Stryker report does not identify configured project '{project.name}': {report_path}")

    test_files = data.get("testFiles")
    if not isinstance(test_files, dict) or not test_files:
        fail(f"Stryker report for '{project.name}' contains no testFiles metadata.")
    test_count = 0
    for test_file in test_files.values():
        if not isinstance(test_file, dict) or not isinstance(test_file.get("tests"), list):
            fail(f"Stryker report for '{project.name}' contains malformed test metadata.")
        test_count += len(test_file["tests"])
    if test_count == 0:
        fail(f"Stryker report for '{project.name}' discovered zero tests.")

    files = data.get("files")
    if not isinstance(files, dict) or not files:
        fail(f"Stryker report for '{project.name}' contains no source files.")
    counts = MutationCounts()
    for file_name, file_data in files.items():
        if not isinstance(file_name, str) or not isinstance(file_data, dict):
            fail(f"Stryker report for '{project.name}' contains an invalid file entry.")
        mutants = file_data.get("mutants")
        if not isinstance(mutants, list):
            fail(f"Stryker report file '{file_name}' has no mutants array.")
        for mutant in mutants:
            if not isinstance(mutant, dict):
                fail(f"Stryker report file '{file_name}' contains an invalid mutant.")
            field = normalize_status(mutant.get("status"), report_path)
            setattr(counts, field, getattr(counts, field) + 1)
    if counts.total == 0:
        fail(f"Stryker report for '{project.name}' contains no mutants.")

    if not (repository_root / project.html_report_path).is_file():
        fail(f"Expected HTML mutation report is missing for {project.name}: {project.html_report_path}")
    metadata = parse_run_metadata(
        repository_root, repository_root / project.run_metadata_path, project, policy, report_path
    )

    failures: list[str] = []
    console_text = (repository_root / project.console_log_path).read_text(encoding="utf-8", errors="replace").lower()
    for marker in policy.forbidden_runner_log_markers:
        if marker.lower() in console_text:
            failures.append(f"{project.name}: runner log contains invalid-execution marker '{marker}'")
    if metadata.status != "success" or metadata.exit_code != 0:
        failures.append(f"{project.name}: Stryker runner did not complete successfully")
    if counts.tested < project.minimum_tested_mutants:
        failures.append(f"{project.name}: tested mutants {counts.tested} < {project.minimum_tested_mutants}")
    if counts.killed < policy.minimum_killed_mutants_per_project:
        failures.append(f"{project.name}: killed mutants {counts.killed} < {policy.minimum_killed_mutants_per_project}")
    if counts.tested > 0 and counts.killed == 0 and counts.survived == counts.tested:
        failures.append(f"{project.name}: degenerate all-survived result is not a valid baseline")
    if counts.compile_error_percentage > policy.maximum_compile_error_percentage + 1e-9:
        failures.append(
            f"{project.name}: compile-error rate {counts.compile_error_percentage:.2f}% > "
            f"{policy.maximum_compile_error_percentage:.2f}%"
        )
    if counts.runtime_error > policy.maximum_runtime_error_mutants:
        failures.append(f"{project.name}: runtime-error mutants {counts.runtime_error} exceed policy")
    if counts.pending or counts.not_run:
        failures.append(f"{project.name}: pending or not-run mutants make the result incomplete")
    return ProjectResult(project, counts, test_count, sha256_file(report_path), metadata, tuple(failures))


def effective_minimum_score(policy: MutationPolicy, baseline: MutationBaseline) -> float:
    if not baseline.is_recorded or baseline.mutation_score is None:
        return policy.minimum_mutation_score
    return max(policy.minimum_mutation_score, baseline.mutation_score - policy.allowed_baseline_score_regression)


def collect_results(policy: MutationPolicy, baseline: MutationBaseline, repository_root: Path) -> VerificationResult:
    projects = tuple(parse_report(repository_root, project, policy) for project in policy.projects)
    totals = MutationCounts()
    health_failures: list[str] = []
    source_revisions = set()
    for project in projects:
        totals.add(project.counts)
        health_failures.extend(project.health_failures)
        source_revisions.add(project.metadata.source_revision)
    if len(source_revisions) != 1:
        health_failures.append("Project reports were not produced from the same source revision")

    minimum_score = effective_minimum_score(policy, baseline)
    return VerificationResult(
        projects=projects,
        totals=totals,
        policy_score_passed=totals.mutation_score + 1e-9 >= policy.minimum_mutation_score,
        baseline_score_passed=totals.mutation_score + 1e-9 >= minimum_score,
        mutant_count_passed=totals.tested >= policy.minimum_tested_mutants,
        killed_count_passed=totals.killed >= policy.minimum_killed_mutants,
        health_failures=tuple(health_failures),
        baseline_status=baseline.status,
        effective_minimum_score=minimum_score,
    )


def status_label(value: bool) -> str:
    return "PASS" if value else "FAIL"


def render_markdown(result: VerificationResult, policy: MutationPolicy, mode: str) -> str:
    totals = result.totals
    baseline_ready = result.baseline_status == "recorded" or mode == "capture-baseline"
    overall = (
        result.health_passed
        and result.policy_score_passed
        and result.mutant_count_passed
        and result.killed_count_passed
        and (result.baseline_score_passed if mode == "verify" else True)
        and baseline_ready
    )
    lines = [
        "# TCJ mutation testing",
        "",
        f"**Mode:** {mode}",
        f"**Overall status:** {status_label(overall)}",
        f"**Baseline status:** {result.baseline_status}",
        "",
        "## Result health",
        "",
        f"- Execution health: **{status_label(result.health_passed)}**",
        f"- Killed mutants: **{totals.killed}**",
        f"- Compile-error rate: **{totals.compile_error_percentage:.2f}%**",
    ]
    if result.health_failures:
        lines.extend(["", "### Health failures", ""])
        lines.extend(f"- {item}" for item in result.health_failures)

    lines.extend([
        "",
        "## Quality gate",
        "",
        "| Gate | Actual | Required | Status |",
        "| --- | ---: | ---: | :---: |",
        f"| Mutation score | {totals.mutation_score:.2f}% | {policy.minimum_mutation_score:.2f}% | {status_label(result.policy_score_passed)} |",
        f"| Recorded-baseline floor | {totals.mutation_score:.2f}% | {result.effective_minimum_score:.2f}% | {status_label(result.baseline_score_passed)} |",
        f"| Tested mutants | {totals.tested} | {policy.minimum_tested_mutants} | {status_label(result.mutant_count_passed)} |",
        f"| Killed mutants | {totals.killed} | {policy.minimum_killed_mutants} | {status_label(result.killed_count_passed)} |",
        "",
        "## Mutant outcomes",
        "",
        "| Metric | Count |",
        "| --- | ---: |",
        f"| Total mutants | {totals.total} |",
        f"| Tested mutants | {totals.tested} |",
        f"| Killed mutants | {totals.killed} |",
        f"| Survived mutants | {totals.survived} |",
        f"| Timeout mutants | {totals.timeout} |",
        f"| No-coverage mutants | {totals.no_coverage} |",
        f"| Ignored mutants | {totals.ignored} |",
        f"| Compile-error mutants | {totals.compile_error} |",
        f"| Runtime-error mutants | {totals.runtime_error} |",
        "",
        "## Project results",
        "",
        "| Project | Tests | Score | Tested | Killed | Survived | Compile errors | Health |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: | :---: |",
    ])
    for project in result.projects:
        counts = project.counts
        lines.append(
            f"| {project.policy.name} | {project.test_count} | {counts.mutation_score:.2f}% | "
            f"{counts.tested} | {counts.killed} | {counts.survived} | {counts.compile_error} | "
            f"{status_label(not project.health_failures)} |"
        )
    lines.extend([
        "",
        "A result with zero killed mutants or an all-survived outcome is rejected as an invalid execution, even if its JSON schema is valid.",
        "Survived mutants must be reviewed in the HTML reports before a recorded baseline is committed.",
        "",
    ])
    return "\n".join(lines)


def render_json(result: VerificationResult, policy: MutationPolicy, mode: str) -> dict[str, Any]:
    return {
        "schemaVersion": 2,
        "generatedAtUtc": utc_now(),
        "mode": mode,
        "status": "pass" if (
            result.health_passed
            and result.policy_score_passed
            and result.mutant_count_passed
            and result.killed_count_passed
            and (result.baseline_score_passed if mode == "verify" else True)
            and (result.baseline_status == "recorded" or mode == "capture-baseline")
        ) else "fail",
        "baselineStatus": result.baseline_status,
        "minimumMutationScore": policy.minimum_mutation_score,
        "effectiveMinimumMutationScore": round(result.effective_minimum_score, 2),
        "minimumTestedMutants": policy.minimum_tested_mutants,
        "minimumKilledMutants": policy.minimum_killed_mutants,
        "healthFailures": list(result.health_failures),
        "totals": result.totals.as_dict(),
        "projects": [
            {
                "name": project.policy.name,
                "reportPath": project.policy.report_path,
                "reportSha256": project.report_sha256,
                "sourceRevision": project.metadata.source_revision,
                "testCount": project.test_count,
                "healthFailures": list(project.health_failures),
                **project.counts.as_dict(),
            }
            for project in result.projects
        ],
    }


def write_outputs(result: VerificationResult, policy: MutationPolicy, mode: str, summary_path: Path, json_path: Path) -> None:
    summary_path.parent.mkdir(parents=True, exist_ok=True)
    json_path.parent.mkdir(parents=True, exist_ok=True)
    summary_path.write_text(render_markdown(result, policy, mode), encoding="utf-8", newline="\n")
    json_path.write_text(json.dumps(render_json(result, policy, mode), indent=2) + "\n", encoding="utf-8", newline="\n")


def report_set_hash(projects: tuple[ProjectResult, ...]) -> str:
    digest = hashlib.sha256()
    for project in sorted(projects, key=lambda item: item.policy.name):
        digest.update(project.policy.name.encode("utf-8"))
        digest.update(b"\0")
        digest.update(project.report_sha256.encode("ascii"))
        digest.update(b"\n")
    return digest.hexdigest()


def write_baseline_candidate(result: VerificationResult, policy: MutationPolicy, path: Path) -> None:
    source_revisions = {project.metadata.source_revision for project in result.projects}
    source_revision = next(iter(source_revisions)) if len(source_revisions) == 1 else "inconsistent"
    totals = result.totals.as_dict()
    candidate = {
        "schemaVersion": 1,
        "status": "candidate",
        "generatedAtUtc": utc_now(),
        "sourceRevision": source_revision,
        "strykerVersion": policy.stryker_version,
        "testRunner": policy.test_runner,
        "coverageAnalysis": policy.coverage_analysis,
        **totals,
        "reportSetSha256": report_set_hash(result.projects),
        "projects": [
            {
                "name": project.policy.name,
                **project.counts.as_dict(),
                "reportSha256": project.report_sha256,
            }
            for project in result.projects
        ],
        "reviewRequired": True,
        "survivedMutantsReviewed": False,
        "reviewInstructions": (
            "Review every survived mutant in both HTML reports, then run the accept-baseline command "
            "with reviewer identity and review notes. Do not copy this candidate over eng/mutation-baseline.json."
        ),
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(candidate, indent=2) + "\n", encoding="utf-8", newline="\n")


def gate_failures(result: VerificationResult, policy: MutationPolicy, mode: str) -> list[str]:
    failures = list(result.health_failures)
    if not result.policy_score_passed:
        failures.append(f"mutation score {result.totals.mutation_score:.2f}% is below {policy.minimum_mutation_score:.2f}%")
    if mode == "verify" and not result.baseline_score_passed:
        failures.append(f"mutation score {result.totals.mutation_score:.2f}% is below recorded baseline floor {result.effective_minimum_score:.2f}%")
    if not result.mutant_count_passed:
        failures.append(f"tested mutant count {result.totals.tested} is below {policy.minimum_tested_mutants}")
    if not result.killed_count_passed:
        failures.append(f"killed mutant count {result.totals.killed} is below {policy.minimum_killed_mutants}")
    if mode == "verify" and policy.require_recorded_baseline and result.baseline_status != "recorded":
        failures.append("mutation baseline is pending; capture, review, and commit a real baseline before merging")
    return failures


def execute_gate(
    policy: MutationPolicy,
    baseline: MutationBaseline,
    repository_root: Path,
    summary_path: Path,
    json_path: Path,
    mode: str,
    candidate_path: Path | None = None,
) -> VerificationResult:
    result = collect_results(policy, baseline, repository_root)
    write_outputs(result, policy, mode, summary_path, json_path)
    failures = gate_failures(result, policy, mode)
    if not failures and mode == "capture-baseline":
        assert candidate_path is not None
        write_baseline_candidate(result, policy, candidate_path)
    if failures:
        fail("; ".join(failures))
    return result


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("validate-config", "validate-baseline", "verify", "capture-baseline", "accept-baseline"))
    parser.add_argument("--repository-root", type=Path, default=REPOSITORY_ROOT)
    parser.add_argument("--policy", type=Path, default=DEFAULT_POLICY)
    parser.add_argument("--baseline", type=Path, default=DEFAULT_BASELINE)
    parser.add_argument("--stryker-config", type=Path, default=DEFAULT_STRYKER_CONFIG)
    parser.add_argument("--tool-manifest", type=Path, default=DEFAULT_TOOL_MANIFEST)
    parser.add_argument("--workflow", type=Path, default=DEFAULT_WORKFLOW)
    parser.add_argument("--ci-workflow", type=Path, default=DEFAULT_CI_WORKFLOW)
    parser.add_argument("--test-props", type=Path, default=DEFAULT_TEST_PROPS)
    parser.add_argument("--summary", type=Path, default=DEFAULT_SUMMARY)
    parser.add_argument("--json", type=Path, default=DEFAULT_JSON)
    parser.add_argument("--candidate", type=Path, default=DEFAULT_CANDIDATE)
    parser.add_argument("--output-baseline", type=Path, default=DEFAULT_BASELINE)
    parser.add_argument("--reviewed-by")
    parser.add_argument("--review-notes")
    parser.add_argument("--skip-git-check", action="store_true")
    return parser.parse_args(argv)


def resolve(path: Path, root: Path) -> Path:
    return path if path.is_absolute() else root / path


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    root = args.repository_root.resolve()
    paths = {name: resolve(getattr(args, name), root).resolve() for name in (
        "policy", "baseline", "stryker_config", "tool_manifest", "workflow", "ci_workflow", "test_props", "summary", "json", "candidate"
    )}
    try:
        policy, baseline = validate_configuration(
            root,
            paths["policy"], paths["baseline"], paths["stryker_config"], paths["tool_manifest"],
            paths["workflow"], paths["ci_workflow"], paths["test_props"], check_git=not args.skip_git_check,
        )
        if args.command == "validate-config":
            print(f"Mutation-testing configuration validation succeeded; baseline status={baseline.status}.")
        elif args.command == "validate-baseline":
            if policy.require_recorded_baseline and not baseline.is_recorded:
                fail("Mutation baseline is pending and cannot satisfy the merge gate.")
            print(f"Recorded mutation baseline is valid: score={baseline.mutation_score:.2f}%.")
        elif args.command == "accept-baseline":
            output_baseline = resolve(args.output_baseline, root).resolve()
            accepted = accept_baseline_candidate(
                paths["candidate"],
                output_baseline,
                policy,
                args.reviewed_by or "",
                args.review_notes or "",
            )
            print(
                f"Mutation baseline accepted: score={accepted['mutationScore']:.2f}%, "
                f"reviewedBy={accepted['reviewedBy']}."
            )
        else:
            mode = "capture-baseline" if args.command == "capture-baseline" else "verify"
            result = execute_gate(policy, baseline, root, paths["summary"], paths["json"], mode, paths["candidate"])
            print(
                f"Mutation {mode} passed: score={result.totals.mutation_score:.2f}%, "
                f"tested={result.totals.tested}, killed={result.totals.killed}."
            )
    except (MutationError, OSError, subprocess.SubprocessError, ValueError) as error:
        print(f"Mutation verification failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
