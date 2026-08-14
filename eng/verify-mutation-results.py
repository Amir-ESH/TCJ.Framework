#!/usr/bin/env python3
"""Validate TCJ mutation configuration, execution health, baseline, and score gates."""

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

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_POLICY = ROOT / "eng/mutation-policy.json"
DEFAULT_BASELINE = ROOT / "eng/mutation-baseline.json"
DEFAULT_CONFIG = ROOT / "stryker-config.json"
DEFAULT_TOOL_MANIFEST = ROOT / ".config/dotnet-tools.json"
DEFAULT_WORKFLOW = ROOT / ".github/workflows/mutation-testing.yml"
DEFAULT_TEST_PROPS = ROOT / "tests/TestProject.props"
DEFAULT_SUMMARY = ROOT / "artifacts/mutation/MUTATION_SUMMARY.md"
DEFAULT_JSON = ROOT / "artifacts/mutation/mutation-summary.json"
DEFAULT_CANDIDATE = ROOT / "artifacts/mutation/mutation-baseline-candidate.json"

REQUIRED_PROJECTS = {"TCJ.Core", "TCJ.DependencyInjection"}
REQUIRED_REPORTERS = {"html", "json"}
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


class MutationError(RuntimeError):
    pass


@dataclass(frozen=True)
class SourceExclusion:
    file: str
    declaration_contains: str
    comment: str
    reason: str


@dataclass(frozen=True)
class ProjectPolicy:
    name: str
    source_project: str
    test_project: str
    minimum_tested: int
    mutation_targets: tuple[str, ...]
    report_path: str
    html_report_path: str
    metadata_path: str
    log_path: str


@dataclass(frozen=True)
class Policy:
    path: Path
    stryker_version: str
    test_runner: str
    coverage_analysis: str
    baseline_path: str
    minimum_score: float
    allowed_regression: float
    minimum_tested: int
    minimum_killed: int
    minimum_killed_per_project: int
    maximum_compile_error_percentage: float
    maximum_runtime_errors: int
    projects: tuple[ProjectPolicy, ...]
    exclusions: tuple[str, ...]
    ignored_types: tuple[str, ...]
    ignored_justifications: dict[str, str]
    forbidden_log_markers: tuple[str, ...]
    source_exclusions: tuple[SourceExclusion, ...]
    reports_directory: str
    summary_json: str
    summary_markdown: str
    baseline_candidate: str


@dataclass(frozen=True)
class Baseline:
    path: Path
    status: str
    data: dict[str, Any]

    @property
    def score(self) -> float | None:
        value = self.data.get("mutationScore")
        return float(value) if isinstance(value, (int, float)) and not isinstance(value, bool) else None


@dataclass
class Counts:
    killed: int = 0
    survived: int = 0
    timeout: int = 0
    no_coverage: int = 0
    ignored: int = 0
    compile_error: int = 0
    runtime_error: int = 0
    pending: int = 0
    not_run: int = 0

    def add(self, other: "Counts") -> None:
        for field in self.__dataclass_fields__:
            setattr(self, field, getattr(self, field) + getattr(other, field))

    @property
    def total(self) -> int:
        return sum(getattr(self, field) for field in self.__dataclass_fields__)

    @property
    def tested(self) -> int:
        return self.killed + self.survived + self.timeout

    @property
    def detected(self) -> int:
        return self.killed + self.timeout

    @property
    def denominator(self) -> int:
        return self.killed + self.survived + self.timeout + self.no_coverage

    @property
    def score(self) -> float:
        return 0.0 if self.denominator == 0 else self.detected * 100.0 / self.denominator

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
            "mutationScore": round(self.score, 2),
        }


@dataclass(frozen=True)
class ProjectResult:
    policy: ProjectPolicy
    counts: Counts
    report_hash: str
    source_revision: str
    health_failures: tuple[str, ...]


@dataclass(frozen=True)
class Result:
    projects: tuple[ProjectResult, ...]
    totals: Counts
    health_failures: tuple[str, ...]
    policy_score_passed: bool
    baseline_score_passed: bool
    tested_passed: bool
    killed_passed: bool
    effective_minimum_score: float
    baseline_status: str

    @property
    def health_passed(self) -> bool:
        return not self.health_failures


def fail(message: str) -> None:
    raise MutationError(message)


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def read_object(path: Path, description: str) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        fail(f"{description} is missing: {path}")
    except json.JSONDecodeError as error:
        fail(f"{description} is malformed JSON at {path}: {error}")
    except OSError as error:
        fail(f"Unable to read {description} at {path}: {error}")
    if not isinstance(value, dict):
        fail(f"{description} must contain a JSON object: {path}")
    return value


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
    except FileNotFoundError:
        fail(f"Required file is missing: {path}")
    return digest.hexdigest()


def require_string(data: dict[str, Any], key: str, description: str = "Policy") -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        fail(f"{description} property '{key}' must be a non-empty string.")
    return value.strip()


def require_number(data: dict[str, Any], key: str, minimum: float, maximum: float) -> float:
    value = data.get(key)
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(float(value)):
        fail(f"Policy property '{key}' must be a finite number.")
    value = float(value)
    if value < minimum or value > maximum:
        fail(f"Policy property '{key}' must be between {minimum} and {maximum}.")
    return value


def require_integer(data: dict[str, Any], key: str, minimum: int = 0) -> int:
    value = data.get(key)
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        fail(f"Policy property '{key}' must be an integer >= {minimum}.")
    return value


def require_list(data: dict[str, Any], key: str, allow_empty: bool = False) -> tuple[str, ...]:
    value = data.get(key)
    if not isinstance(value, list) or (not allow_empty and not value):
        fail(f"Policy property '{key}' must be a {'possibly empty' if allow_empty else 'non-empty'} array.")
    if any(not isinstance(item, str) or not item.strip() for item in value):
        fail(f"Policy property '{key}' must contain non-empty strings.")
    return tuple(item.strip() for item in value)


def relative_path(value: Any, description: str) -> str:
    if not isinstance(value, str) or not value.strip():
        fail(f"{description} must be a non-empty repository-relative path.")
    path = PurePosixPath(value.replace("\\", "/").strip())
    if path.is_absolute() or ".." in path.parts:
        fail(f"{description} must stay inside the repository: {value}")
    return str(path)


def project_policy(data: dict[str, Any], index: int) -> ProjectPolicy:
    targets = require_list(data, "mutationTargets")
    if any(target.startswith("!") for target in targets):
        fail(f"projects[{index}].mutationTargets must contain positive include patterns only.")
    return ProjectPolicy(
        name=require_string(data, "name"),
        source_project=relative_path(data.get("sourceProject"), f"projects[{index}].sourceProject"),
        test_project=relative_path(data.get("testProject"), f"projects[{index}].testProject"),
        minimum_tested=require_integer(data, "minimumTestedMutants", 1),
        mutation_targets=targets,
        report_path=relative_path(data.get("reportPath"), f"projects[{index}].reportPath"),
        html_report_path=relative_path(data.get("htmlReportPath"), f"projects[{index}].htmlReportPath"),
        metadata_path=relative_path(data.get("runMetadataPath"), f"projects[{index}].runMetadataPath"),
        log_path=relative_path(data.get("consoleLogPath"), f"projects[{index}].consoleLogPath"),
    )



def source_exclusions(data: dict[str, Any]) -> tuple[SourceExclusion, ...]:
    raw = data.get("sourceLevelExclusions")
    if not isinstance(raw, list):
        fail("sourceLevelExclusions must be a JSON array.")
    exclusions: list[SourceExclusion] = []
    seen: set[str] = set()
    for index, item in enumerate(raw):
        if not isinstance(item, dict):
            fail(f"sourceLevelExclusions[{index}] must be an object.")
        file = relative_path(item.get("file"), f"sourceLevelExclusions[{index}].file")
        declaration = require_string(item, "declarationContains", f"sourceLevelExclusions[{index}]")
        comment = require_string(item, "comment", f"sourceLevelExclusions[{index}]")
        reason = require_string(item, "reason", f"sourceLevelExclusions[{index}]")
        if not comment.startswith("// Stryker disable once all:"):
            fail(f"sourceLevelExclusions[{index}].comment must be a narrow Stryker disable-once marker with a reason.")
        if file in seen:
            fail(f"sourceLevelExclusions contains duplicate file '{file}'.")
        seen.add(file)
        exclusions.append(SourceExclusion(file, declaration, comment, reason))
    return tuple(exclusions)

def load_policy(path: Path) -> Policy:
    data = read_object(path, "Mutation policy")
    if data.get("schemaVersion") != 2:
        fail("Mutation policy schemaVersion must be 2.")
    raw_projects = data.get("projects")
    if not isinstance(raw_projects, list) or not raw_projects or any(not isinstance(x, dict) for x in raw_projects):
        fail("Mutation policy projects must be a non-empty array of objects.")
    projects = tuple(project_policy(item, i) for i, item in enumerate(raw_projects))
    names = {project.name for project in projects}
    if len(names) != len(projects):
        fail("Mutation project names must be unique.")
    missing = REQUIRED_PROJECTS - names
    if missing:
        fail("Mutation policy is missing required projects: " + ", ".join(sorted(missing)))

    ignored = require_list(data, "ignoredMutationTypes", allow_empty=True)
    reasons = data.get("ignoredMutationJustifications")
    if not isinstance(reasons, dict) or any(
        not isinstance(key, str) or not key.strip() or not isinstance(value, str) or not value.strip()
        for key, value in reasons.items()
    ):
        fail("ignoredMutationJustifications must contain non-empty string keys and values.")
    normalized_reasons = {str(key).strip(): str(value).strip() for key, value in reasons.items()}
    if set(ignored) != set(normalized_reasons):
        fail("Every ignored mutation type must have exactly one justification.")

    paths = data.get("reportPaths")
    if not isinstance(paths, dict):
        fail("reportPaths must be a JSON object.")

    return Policy(
        path=path,
        stryker_version=require_string(data, "strykerVersion"),
        test_runner=require_string(data, "testRunner").lower(),
        coverage_analysis=require_string(data, "coverageAnalysis"),
        baseline_path=relative_path(data.get("baselinePath"), "baselinePath"),
        minimum_score=require_number(data, "minimumMutationScore", 0, 100),
        allowed_regression=require_number(data, "allowedBaselineScoreRegression", 0, 100),
        minimum_tested=require_integer(data, "minimumTestedMutants", 1),
        minimum_killed=require_integer(data, "minimumKilledMutants", 1),
        minimum_killed_per_project=require_integer(data, "minimumKilledMutantsPerProject", 1),
        maximum_compile_error_percentage=require_number(data, "maximumCompileErrorPercentage", 0, 100),
        maximum_runtime_errors=require_integer(data, "maximumRuntimeErrorMutants", 0),
        projects=projects,
        exclusions=require_list(data, "excludedFilePatterns"),
        ignored_types=ignored,
        ignored_justifications=normalized_reasons,
        forbidden_log_markers=require_list(data, "forbiddenRunnerLogMarkers"),
        source_exclusions=source_exclusions(data),
        reports_directory=relative_path(paths.get("reportsDirectory"), "reportPaths.reportsDirectory"),
        summary_json=relative_path(paths.get("summaryJson"), "reportPaths.summaryJson"),
        summary_markdown=relative_path(paths.get("summaryMarkdown"), "reportPaths.summaryMarkdown"),
        baseline_candidate=relative_path(paths.get("baselineCandidate"), "reportPaths.baselineCandidate"),
    )


def load_baseline(path: Path, policy: Policy) -> Baseline:
    data = read_object(path, "Mutation baseline")
    if data.get("schemaVersion") != 1:
        fail("Mutation baseline schemaVersion must be 1.")
    status = data.get("status")
    if status not in {"pending", "recorded"}:
        fail("Mutation baseline status must be 'pending' or 'recorded'.")
    if status == "pending":
        require_string(data, "reason", "Mutation baseline")
        return Baseline(path, status, data)

    for key in (
        "recordedAtUtc", "reviewedAtUtc", "reviewedBy", "reviewNotes", "sourceRevision",
        "strykerVersion", "testRunner", "coverageAnalysis", "reportSetSha256"
    ):
        require_string(data, key, "Recorded mutation baseline")
    if data["strykerVersion"] != policy.stryker_version:
        fail("Recorded baseline Stryker version does not match policy.")
    if data["testRunner"].lower() != policy.test_runner or data["coverageAnalysis"] != policy.coverage_analysis:
        fail("Recorded baseline runner settings do not match policy.")
    if data.get("survivedMutantsReviewed") is not True:
        fail("Recorded baseline must attest that survived mutants were reviewed.")
    score = data.get("mutationScore")
    if isinstance(score, bool) or not isinstance(score, (int, float)) or not 0 <= float(score) <= 100:
        fail("Recorded baseline mutationScore must be between 0 and 100.")
    if float(score) + 1e-9 < policy.minimum_score:
        fail("Recorded baseline mutationScore is below policy.")
    return Baseline(path, status, data)


def validate_git_tracking(root: Path, paths: Iterable[Path]) -> None:
    inside = subprocess.run(
        ["git", "rev-parse", "--is-inside-work-tree"], cwd=root, text=True, capture_output=True, check=False
    )
    if inside.returncode != 0 or inside.stdout.strip() != "true":
        fail("Git metadata is unavailable; use --skip-git-check only for exported source archives.")
    for path in paths:
        try:
            relative = path.resolve().relative_to(root.resolve()).as_posix()
        except ValueError:
            fail(f"Required mutation file must stay inside the repository: {path}")
        ignored = subprocess.run(["git", "check-ignore", "-q", "--", relative], cwd=root, check=False)
        if ignored.returncode == 0:
            fail(f"Required mutation file is ignored by Git: {relative}")
        tracked = subprocess.run(
            ["git", "ls-files", "--error-unmatch", "--", relative], cwd=root, text=True, capture_output=True, check=False
        )
        if tracked.returncode != 0:
            fail(f"Required mutation file is not tracked by Git: {relative}")


def validate_configuration(
    root: Path,
    policy_path: Path,
    baseline_path: Path,
    config_path: Path,
    tool_manifest_path: Path,
    workflow_path: Path,
    test_props_path: Path,
    check_git: bool,
) -> tuple[Policy, Baseline]:
    policy = load_policy(policy_path)
    baseline = load_baseline(baseline_path, policy)

    config = read_object(config_path, "Stryker configuration").get("stryker-config")
    if not isinstance(config, dict):
        fail("Stryker configuration must contain a 'stryker-config' object.")
    reporters = config.get("reporters")
    if not isinstance(reporters, list) or not REQUIRED_REPORTERS.issubset({str(x).lower() for x in reporters}):
        fail("Stryker configuration must enable HTML and JSON reporters.")
    if str(config.get("test-runner", "")).lower() != policy.test_runner or policy.test_runner != "mtp":
        fail("Stryker must use the MTP runner for the repository's xUnit v3 tests.")
    if config.get("coverage-analysis") != policy.coverage_analysis or policy.coverage_analysis != "off":
        fail("Stryker coverage-analysis must remain 'off' until optimized capture is proven trustworthy.")
    if config.get("concurrency") != 1:
        fail("Stryker concurrency must be 1 while the MTP runner reuses test hosts.")
    if config.get("disable-mix-mutants") is not True:
        fail("Stryker disable-mix-mutants must be true for the initial MTP baseline.")
    thresholds = config.get("thresholds")
    if not isinstance(thresholds, dict) or thresholds.get("break") != 0:
        fail("Stryker break threshold must remain 0; the repository verifier owns the gate.")
    if float(thresholds.get("low", -1)) != policy.minimum_score:
        fail("Stryker low threshold must match minimumMutationScore.")
    if "project" in config or "mutate" in config:
        fail("Shared Stryker config must not hard-code a project or mutation scope.")
    ignored = config.get("ignore-mutations", [])
    if tuple(ignored) != policy.ignored_types:
        fail("Stryker ignore-mutations must exactly match policy.")

    manifest = read_object(tool_manifest_path, ".NET tool manifest")
    tools = manifest.get("tools")
    tool = tools.get("dotnet-stryker") if isinstance(tools, dict) else None
    if not isinstance(tool, dict) or tool.get("version") != policy.stryker_version:
        fail("Pinned dotnet-stryker version must match policy.")

    props = test_props_path.read_text(encoding="utf-8")
    for required in (
        "<OutputType>Exe</OutputType>",
        "<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>",
    ):
        if required not in props:
            fail(f"tests/TestProject.props is missing required MTP setting: {required}")

    workflow = workflow_path.read_text(encoding="utf-8")
    required_workflow_fragments = (
        "name: Mutation testing",
        "workflow_dispatch:",
        "schedule:",
        "workflow_call:",
        "push:",
        "name: Run mutation tests",
        "Run TCJ.Core mutation tests",
        "Run TCJ.DependencyInjection mutation tests",
        "mutation-baseline-candidate.json",
        "Upload mutation reports",
    )
    for fragment in required_workflow_fragments:
        if fragment not in workflow:
            fail(f"Mutation workflow is missing required fragment: {fragment}")
    if "Require a recorded baseline" in workflow:
        fail("Mutation workflow must not stop before Stryker runs when the baseline is pending.")
    gate_workflow = (root / ".github/workflows/required-pr-gate.yml").read_text(encoding="utf-8")
    for fragment in ("pull_request:", "uses: ./.github/workflows/mutation-testing.yml", "name: Required PR Gate"):
        if fragment not in gate_workflow:
            fail(f"Required PR Gate is missing mutation integration fragment: {fragment}")
    if "pull_request:\n    paths:" in gate_workflow or "pull_request:\r\n    paths:" in gate_workflow:
        fail("Required PR Gate must not use a top-level pull_request paths filter.")


    for exclusion in policy.source_exclusions:
        source_path = root / exclusion.file
        if not source_path.is_file():
            fail(f"Source-level mutation exclusion file is missing: {exclusion.file}")
        source_text = source_path.read_text(encoding="utf-8")
        marker = exclusion.comment + "\n    " + exclusion.declaration_contains
        if marker not in source_text:
            fail(
                "Source-level mutation exclusion is missing or no longer immediately precedes its "
                f"documented declaration: {exclusion.file}"
            )

    for project in policy.projects:
        for relative in (project.source_project, project.test_project):
            if not (root / relative).is_file():
                fail(f"Configured project file is missing: {relative}")

    if check_git:
        validate_git_tracking(
            root,
            (
                policy_path, baseline_path, config_path, tool_manifest_path, workflow_path,
                test_props_path, root / "eng/run-mutation-testing.py", Path(__file__).resolve(),
                *(root / exclusion.file for exclusion in policy.source_exclusions),
            ),
        )
    return policy, baseline


def normalize_status(value: Any, path: Path) -> str:
    if not isinstance(value, str) or not value.strip():
        fail(f"A mutant in {path} has no valid status.")
    key = "".join(character for character in value.lower() if character.isalnum())
    if key not in STATUS_NAMES:
        fail(f"Unsupported mutant status '{value}' in {path}.")
    return STATUS_NAMES[key]


def report_identifies_project(data: dict[str, Any], name: str) -> bool:
    root = data.get("projectRoot")
    if isinstance(root, str) and PurePosixPath(root.replace("\\", "/").rstrip("/")).name.lower() == name.lower():
        return True
    files = data.get("files")
    if isinstance(files, dict):
        return any(name.lower() in {part.lower() for part in PurePosixPath(str(path).replace("\\", "/")).parts} for path in files)
    return False


def parse_project(root: Path, project: ProjectPolicy, policy: Policy) -> ProjectResult:
    report_path = root / project.report_path
    data = read_object(report_path, f"Stryker report for {project.name}")
    if str(data.get("schemaVersion")) != "2":
        fail(f"Stryker report schemaVersion must be 2 for {project.name}.")
    if not report_identifies_project(data, project.name):
        fail(f"Stryker report does not identify configured project '{project.name}': {report_path}")
    files = data.get("files")
    if not isinstance(files, dict) or not files:
        fail(f"Stryker report for {project.name} has no source files.")

    counts = Counts()
    for file_name, file_data in files.items():
        if not isinstance(file_name, str) or not isinstance(file_data, dict):
            fail(f"Stryker report for {project.name} contains an invalid file entry.")
        mutants = file_data.get("mutants")
        if not isinstance(mutants, list):
            fail(f"Stryker report file '{file_name}' has no mutants array.")
        for mutant in mutants:
            if not isinstance(mutant, dict):
                fail(f"Stryker report file '{file_name}' contains an invalid mutant.")
            field = normalize_status(mutant.get("status"), report_path)
            setattr(counts, field, getattr(counts, field) + 1)
    if counts.total == 0:
        fail(f"Stryker report for {project.name} contains no mutants.")
    if not (root / project.html_report_path).is_file():
        fail(f"Expected HTML report is missing for {project.name}: {project.html_report_path}")

    metadata_path = root / project.metadata_path
    metadata = read_object(metadata_path, f"Run metadata for {project.name}")
    if metadata.get("schemaVersion") != 1 or metadata.get("project") != project.name:
        fail(f"Run metadata does not identify project {project.name}.")
    if metadata.get("status") != "success" or metadata.get("exitCode") != 0:
        metadata_failure = True
    else:
        metadata_failure = False
    if metadata.get("strykerVersion") != policy.stryker_version:
        fail(f"Stryker version mismatch for {project.name}.")
    if str(metadata.get("testRunner", "")).lower() != policy.test_runner or metadata.get("coverageAnalysis") != policy.coverage_analysis:
        fail(f"Runner settings mismatch for {project.name}.")
    report_hash = sha256_file(report_path)
    if metadata.get("reportSha256") != report_hash:
        fail(f"Run metadata report hash mismatch for {project.name}.")
    if metadata.get("policySha256") != sha256_file(policy.path):
        fail(f"Run metadata policy hash mismatch for {project.name}.")
    log_path = root / project.log_path
    log_hash = sha256_file(log_path)
    if metadata.get("consoleLogPath") != project.log_path or metadata.get("consoleLogSha256") != log_hash:
        fail(f"Run metadata console-log hash mismatch for {project.name}.")

    failures: list[str] = []
    log_text = log_path.read_text(encoding="utf-8", errors="replace").lower()
    for marker in policy.forbidden_log_markers:
        if marker.lower() in log_text:
            failures.append(f"{project.name}: runner log contains invalid-execution marker '{marker}'")
    if metadata_failure:
        failures.append(f"{project.name}: Stryker runner did not complete successfully")
    if counts.tested < project.minimum_tested:
        failures.append(f"{project.name}: tested mutants {counts.tested} < {project.minimum_tested}")
    if counts.killed < policy.minimum_killed_per_project:
        failures.append(f"{project.name}: killed mutants {counts.killed} < {policy.minimum_killed_per_project}")
    if counts.tested > 0 and counts.killed == 0 and counts.survived == counts.tested:
        failures.append(f"{project.name}: degenerate all-survived result is invalid")
    if counts.compile_error_percentage > policy.maximum_compile_error_percentage + 1e-9:
        failures.append(
            f"{project.name}: compile-error rate {counts.compile_error_percentage:.2f}% > "
            f"{policy.maximum_compile_error_percentage:.2f}%"
        )
    if counts.runtime_error > policy.maximum_runtime_errors:
        failures.append(f"{project.name}: runtime-error mutants exceed policy")
    if counts.pending or counts.not_run:
        failures.append(f"{project.name}: pending or not-run mutants make the result incomplete")

    revision = metadata.get("sourceRevision")
    if not isinstance(revision, str) or not revision:
        fail(f"Run metadata sourceRevision is invalid for {project.name}.")
    return ProjectResult(project, counts, report_hash, revision, tuple(failures))


def collect(root: Path, policy: Policy, baseline: Baseline) -> Result:
    projects = tuple(parse_project(root, project, policy) for project in policy.projects)
    totals = Counts()
    failures: list[str] = []
    revisions: set[str] = set()
    for project in projects:
        totals.add(project.counts)
        failures.extend(project.health_failures)
        revisions.add(project.source_revision)
    if len(revisions) != 1:
        failures.append("Project reports were produced from different source revisions")
    effective = policy.minimum_score
    if baseline.status == "recorded" and baseline.score is not None:
        effective = max(effective, baseline.score - policy.allowed_regression)
    return Result(
        projects=projects,
        totals=totals,
        health_failures=tuple(failures),
        policy_score_passed=totals.score + 1e-9 >= policy.minimum_score,
        # Baseline candidates persist mutationScore rounded to two decimals. Compare
        # the current score at the same precision so an unchanged mutant outcome
        # cannot fail merely because its exact fraction rounds up in the baseline.
        baseline_score_passed=round(totals.score, 2) + 1e-9 >= effective,
        tested_passed=totals.tested >= policy.minimum_tested,
        killed_passed=totals.killed >= policy.minimum_killed,
        effective_minimum_score=effective,
        baseline_status=baseline.status,
    )


def status(value: bool) -> str:
    return "PASS" if value else "FAIL"


def render_markdown(result: Result, policy: Policy, mode: str) -> str:
    totals = result.totals
    overall = (
        result.health_passed
        and result.policy_score_passed
        and result.tested_passed
        and result.killed_passed
        and (result.baseline_score_passed if result.baseline_status == "recorded" else mode == "capture-baseline")
    )
    lines = [
        "# TCJ mutation testing",
        "",
        f"**Mode:** {mode}",
        f"**Overall status:** {status(overall)}",
        f"**Baseline status:** {result.baseline_status}",
        "",
        "## Quality gate",
        "",
        "| Gate | Actual | Required | Status |",
        "| --- | ---: | ---: | :---: |",
        f"| Execution health | {'healthy' if result.health_passed else 'invalid'} | healthy | {status(result.health_passed)} |",
        f"| Mutation score | {totals.score:.2f}% | {policy.minimum_score:.2f}% | {status(result.policy_score_passed)} |",
        f"| Recorded baseline floor | {totals.score:.2f}% | {result.effective_minimum_score:.2f}% | {status(result.baseline_score_passed)} |",
        f"| Tested mutants | {totals.tested} | {policy.minimum_tested} | {status(result.tested_passed)} |",
        f"| Killed mutants | {totals.killed} | {policy.minimum_killed} | {status(result.killed_passed)} |",
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
        "| Project | Score | Tested | Killed | Survived | Compile errors | Health |",
        "| --- | ---: | ---: | ---: | ---: | ---: | :---: |",
    ]
    for project in result.projects:
        counts = project.counts
        lines.append(
            f"| {project.policy.name} | {counts.score:.2f}% | {counts.tested} | {counts.killed} | "
            f"{counts.survived} | {counts.compile_error} | {status(not project.health_failures)} |"
        )
    if result.health_failures:
        lines.extend(["", "## Execution-health failures", ""])
        lines.extend(f"- {failure}" for failure in result.health_failures)
    if result.baseline_status == "pending":
        lines.extend([
            "",
            "A valid baseline candidate is generated only after all execution-health and policy gates pass. "
            "Review both HTML reports, accept the candidate, and commit `eng/mutation-baseline.json`.",
        ])
    lines.extend(["", "Survived mutants must be reviewed before accepting a baseline.", ""])
    return "\n".join(lines)


def render_json(result: Result, policy: Policy, mode: str) -> dict[str, Any]:
    return {
        "schemaVersion": 2,
        "generatedAtUtc": utc_now(),
        "mode": mode,
        "baselineStatus": result.baseline_status,
        "status": "pass" if (
            result.health_passed and result.policy_score_passed and result.tested_passed and result.killed_passed
            and (result.baseline_score_passed if result.baseline_status == "recorded" else mode == "capture-baseline")
        ) else "fail",
        "minimumMutationScore": policy.minimum_score,
        "effectiveMinimumMutationScore": round(result.effective_minimum_score, 2),
        "minimumTestedMutants": policy.minimum_tested,
        "minimumKilledMutants": policy.minimum_killed,
        "healthFailures": list(result.health_failures),
        "totals": result.totals.as_dict(),
        "projects": [
            {
                "name": project.policy.name,
                "reportPath": project.policy.report_path,
                "reportSha256": project.report_hash,
                "sourceRevision": project.source_revision,
                "healthFailures": list(project.health_failures),
                **project.counts.as_dict(),
            }
            for project in result.projects
        ],
    }


def write_outputs(result: Result, policy: Policy, mode: str, summary_path: Path, json_path: Path) -> None:
    summary_path.parent.mkdir(parents=True, exist_ok=True)
    json_path.parent.mkdir(parents=True, exist_ok=True)
    summary_path.write_text(render_markdown(result, policy, mode), encoding="utf-8", newline="\n")
    json_path.write_text(json.dumps(render_json(result, policy, mode), indent=2) + "\n", encoding="utf-8", newline="\n")


def report_set_hash(projects: tuple[ProjectResult, ...]) -> str:
    digest = hashlib.sha256()
    for project in sorted(projects, key=lambda item: item.policy.name):
        digest.update(project.policy.name.encode())
        digest.update(b"\0")
        digest.update(project.report_hash.encode())
        digest.update(b"\n")
    return digest.hexdigest()


def write_candidate(result: Result, policy: Policy, path: Path) -> None:
    revisions = {project.source_revision for project in result.projects}
    candidate = {
        "schemaVersion": 1,
        "status": "candidate",
        "generatedAtUtc": utc_now(),
        "sourceRevision": next(iter(revisions)),
        "strykerVersion": policy.stryker_version,
        "testRunner": policy.test_runner,
        "coverageAnalysis": policy.coverage_analysis,
        **result.totals.as_dict(),
        "reportSetSha256": report_set_hash(result.projects),
        "projects": [
            {"name": project.policy.name, **project.counts.as_dict(), "reportSha256": project.report_hash}
            for project in result.projects
        ],
        "reviewRequired": True,
        "survivedMutantsReviewed": False,
        "reviewInstructions": "Review both HTML reports, then use accept-baseline with reviewer identity and notes.",
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(candidate, indent=2) + "\n", encoding="utf-8", newline="\n")


def validate_candidate(path: Path, policy: Policy) -> dict[str, Any]:
    data = read_object(path, "Mutation baseline candidate")
    if data.get("schemaVersion") != 1 or data.get("status") != "candidate":
        fail("Mutation baseline candidate must use schemaVersion 1 and status 'candidate'.")
    if data.get("reviewRequired") is not True or data.get("survivedMutantsReviewed") is not False:
        fail("Mutation baseline candidate must remain unreviewed until explicitly accepted.")
    if data.get("strykerVersion") != policy.stryker_version:
        fail("Candidate Stryker version does not match policy.")
    if str(data.get("testRunner", "")).lower() != policy.test_runner or data.get("coverageAnalysis") != policy.coverage_analysis:
        fail("Candidate runner settings do not match policy.")
    score = data.get("mutationScore")
    if isinstance(score, bool) or not isinstance(score, (int, float)) or float(score) + 1e-9 < policy.minimum_score:
        fail("Candidate mutation score is below policy.")
    killed = data.get("killedMutants")
    tested = data.get("testedMutants")
    if not isinstance(killed, int) or killed < policy.minimum_killed:
        fail("Candidate killed-mutant count is below policy.")
    if not isinstance(tested, int) or tested < policy.minimum_tested:
        fail("Candidate tested-mutant count is below policy.")
    return data


def accept_candidate(candidate_path: Path, output_path: Path, policy: Policy, reviewed_by: str, notes: str) -> dict[str, Any]:
    if not reviewed_by.strip():
        fail("--reviewed-by is required when accepting a baseline.")
    if not notes.strip():
        fail("--review-notes is required when accepting a baseline.")
    candidate = validate_candidate(candidate_path, policy)
    accepted = dict(candidate)
    accepted.update({
        "status": "recorded",
        "recordedAtUtc": utc_now(),
        "reviewedAtUtc": utc_now(),
        "reviewedBy": reviewed_by.strip(),
        "reviewNotes": notes.strip(),
        "reviewRequired": False,
        "survivedMutantsReviewed": True,
    })
    accepted.pop("generatedAtUtc", None)
    accepted.pop("reviewInstructions", None)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(accepted, indent=2) + "\n", encoding="utf-8", newline="\n")
    load_baseline(output_path, policy)
    return accepted


def gate_failures(result: Result, policy: Policy, mode: str) -> list[str]:
    failures = list(result.health_failures)
    if not result.policy_score_passed:
        failures.append(f"mutation score {result.totals.score:.2f}% is below {policy.minimum_score:.2f}%")
    if not result.tested_passed:
        failures.append(f"tested mutant count {result.totals.tested} is below {policy.minimum_tested}")
    if not result.killed_passed:
        failures.append(f"killed mutant count {result.totals.killed} is below {policy.minimum_killed}")
    if result.baseline_status == "recorded" and not result.baseline_score_passed:
        failures.append(
            f"mutation score {result.totals.score:.2f}% is below recorded baseline floor "
            f"{result.effective_minimum_score:.2f}%"
        )
    if mode == "verify" and result.baseline_status == "pending":
        failures.append(
            "mutation baseline is pending; a valid candidate was generated. Review and accept it, commit "
            "eng/mutation-baseline.json, then rerun CI"
        )
    return failures


def execute_gate(
    root: Path,
    policy: Policy,
    baseline: Baseline,
    mode: str,
    summary_path: Path,
    json_path: Path,
    candidate_path: Path,
) -> Result:
    result = collect(root, policy, baseline)
    write_outputs(result, policy, mode, summary_path, json_path)
    preliminary_failures = gate_failures(result, policy, "capture-baseline")
    if not preliminary_failures:
        write_candidate(result, policy, candidate_path)
    failures = gate_failures(result, policy, mode)
    if failures:
        fail("; ".join(failures))
    return result


def resolve(path: Path, root: Path) -> Path:
    return path if path.is_absolute() else root / path


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("validate-config", "validate-baseline", "verify", "capture-baseline", "accept-baseline"))
    parser.add_argument("--repository-root", type=Path, default=ROOT)
    parser.add_argument("--policy", type=Path, default=DEFAULT_POLICY)
    parser.add_argument("--baseline", type=Path, default=DEFAULT_BASELINE)
    parser.add_argument("--stryker-config", type=Path, default=DEFAULT_CONFIG)
    parser.add_argument("--tool-manifest", type=Path, default=DEFAULT_TOOL_MANIFEST)
    parser.add_argument("--workflow", type=Path, default=DEFAULT_WORKFLOW)
    parser.add_argument("--test-props", type=Path, default=DEFAULT_TEST_PROPS)
    parser.add_argument("--summary", type=Path, default=DEFAULT_SUMMARY)
    parser.add_argument("--json", type=Path, default=DEFAULT_JSON)
    parser.add_argument("--candidate", type=Path, default=DEFAULT_CANDIDATE)
    parser.add_argument("--output-baseline", type=Path, default=DEFAULT_BASELINE)
    parser.add_argument("--reviewed-by")
    parser.add_argument("--review-notes")
    parser.add_argument("--skip-git-check", action="store_true")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    root = args.repository_root.resolve()
    paths = {
        name: resolve(getattr(args, name), root).resolve()
        for name in (
            "policy", "baseline", "stryker_config", "tool_manifest", "workflow", "test_props",
            "summary", "json", "candidate", "output_baseline"
        )
    }
    try:
        policy, baseline = validate_configuration(
            root,
            paths["policy"], paths["baseline"], paths["stryker_config"], paths["tool_manifest"],
            paths["workflow"], paths["test_props"], check_git=not args.skip_git_check,
        )
        if args.command == "validate-config":
            print(f"Mutation-testing configuration is valid; baseline status={baseline.status}.")
        elif args.command == "validate-baseline":
            if baseline.status != "recorded":
                fail("Mutation baseline is pending. Run Stryker, review the generated candidate, and accept it.")
            print(f"Recorded mutation baseline is valid: score={baseline.score:.2f}%.")
        elif args.command == "accept-baseline":
            accepted = accept_candidate(
                paths["candidate"], paths["output_baseline"], policy,
                args.reviewed_by or "", args.review_notes or "",
            )
            print(f"Mutation baseline accepted: score={accepted['mutationScore']:.2f}%.")
        else:
            mode = "capture-baseline" if args.command == "capture-baseline" else "verify"
            result = execute_gate(
                root, policy, baseline, mode, paths["summary"], paths["json"], paths["candidate"]
            )
            print(
                f"Mutation {mode} passed: score={result.totals.score:.2f}%, "
                f"tested={result.totals.tested}, killed={result.totals.killed}."
            )
    except (MutationError, OSError, subprocess.SubprocessError, ValueError) as error:
        print(f"Mutation verification failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
