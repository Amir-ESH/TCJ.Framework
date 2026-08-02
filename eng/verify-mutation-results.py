#!/usr/bin/env python3
"""Validate TCJ mutation-testing configuration and enforce its quality gate."""

from __future__ import annotations

import argparse
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
DEFAULT_STRYKER_CONFIG = REPOSITORY_ROOT / "stryker-config.json"
DEFAULT_WORKFLOW = REPOSITORY_ROOT / ".github/workflows/mutation-testing.yml"
DEFAULT_SUMMARY = REPOSITORY_ROOT / "artifacts/mutation/MUTATION_SUMMARY.md"
DEFAULT_JSON = REPOSITORY_ROOT / "artifacts/mutation/mutation-summary.json"

REQUIRED_PROJECTS = ("TCJ.Core", "TCJ.DependencyInjection")
REQUIRED_REPORTERS = {"html", "json"}
REQUIRED_WORKFLOW_FRAGMENTS = (
    "name: Mutation testing",
    "workflow_dispatch:",
    "schedule:",
    "cron:",
    "name: Run mutation tests",
    "Restore and audit dependencies",
    "Build production and test projects",
    "Install Stryker.NET",
    "Validate mutation-testing configuration",
    "Run TCJ.Core mutation tests",
    "Run TCJ.DependencyInjection mutation tests",
    "Verify mutation score",
    "Publish mutation summary",
    "Upload mutation reports",
    "artifacts/mutation/mutation-summary.json",
    "artifacts/mutation/MUTATION_SUMMARY.md",
    "artifacts/mutation/reports/",
)

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
    """Raised when mutation policy, reports, or repository configuration are invalid."""


@dataclass(frozen=True)
class MutationPolicy:
    path: Path
    minimum_mutation_score: float
    minimum_tested_mutants: int
    projects: tuple[str, ...]
    excluded_file_patterns: tuple[str, ...]
    ignored_mutation_types: tuple[str, ...]
    ignored_mutation_justifications: dict[str, str]
    reports_directory: str
    summary_json: str
    summary_markdown: str
    project_reports: dict[str, str]


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
        # Stryker treats timeouts as detected mutants when calculating the score.
        return self.killed + self.timeout

    @property
    def score_denominator(self) -> int:
        return self.killed + self.timeout + self.survived + self.no_coverage

    @property
    def mutation_score(self) -> float:
        if self.score_denominator == 0:
            return 0.0
        return self.detected * 100.0 / self.score_denominator

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
            "runtimeErrorMutants": self.runtime_error,
            "pendingMutants": self.pending,
            "notRunMutants": self.not_run,
            "mutationScore": round(self.mutation_score, 2),
        }


@dataclass(frozen=True)
class ProjectResult:
    name: str
    report_path: str
    counts: MutationCounts


@dataclass(frozen=True)
class VerificationResult:
    projects: tuple[ProjectResult, ...]
    totals: MutationCounts
    score_passed: bool
    mutant_count_passed: bool

    @property
    def passed(self) -> bool:
        return self.score_passed and self.mutant_count_passed


def fail(message: str) -> None:
    raise MutationError(message)


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


def require_number(data: dict[str, Any], key: str, *, minimum: float, maximum: float) -> float:
    value = data.get(key)
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(float(value)):
        fail(f"Policy property '{key}' must be a finite number.")
    number = float(value)
    if number < minimum or number > maximum:
        fail(f"Policy property '{key}' must be between {minimum} and {maximum}.")
    return number


def require_positive_integer(data: dict[str, Any], key: str) -> int:
    value = data.get(key)
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        fail(f"Policy property '{key}' must be a positive integer.")
    return value


def require_string_list(data: dict[str, Any], key: str, *, allow_empty: bool) -> tuple[str, ...]:
    value = data.get(key)
    if not isinstance(value, list) or (not allow_empty and not value):
        requirement = "a JSON array" if allow_empty else "a non-empty JSON array"
        fail(f"Policy property '{key}' must be {requirement}.")
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


def load_policy(path: Path) -> MutationPolicy:
    data = read_json_object(path, "Mutation policy")

    if data.get("schemaVersion") != 1:
        fail("Mutation policy schemaVersion must be 1.")

    minimum_score = require_number(data, "minimumMutationScore", minimum=0.0, maximum=100.0)
    minimum_tested = require_positive_integer(data, "minimumTestedMutants")
    projects = require_string_list(data, "projects", allow_empty=False)
    if len(set(projects)) != len(projects):
        fail("Mutation policy projects must be unique.")
    for required_project in REQUIRED_PROJECTS:
        if required_project not in projects:
            fail(f"Mutation policy must include initial project '{required_project}'.")

    exclusions = require_string_list(data, "excludedFilePatterns", allow_empty=False)
    ignored_types = require_string_list(data, "ignoredMutationTypes", allow_empty=True)

    justifications_data = data.get("ignoredMutationJustifications")
    if not isinstance(justifications_data, dict):
        fail("Policy property 'ignoredMutationJustifications' must be a JSON object.")
    justifications: dict[str, str] = {}
    for mutation_type, reason in justifications_data.items():
        if not isinstance(mutation_type, str) or not mutation_type.strip():
            fail("Ignored mutation justification keys must be non-empty strings.")
        if not isinstance(reason, str) or not reason.strip():
            fail(f"Ignored mutation type '{mutation_type}' requires a non-empty justification.")
        justifications[mutation_type.strip()] = reason.strip()

    missing_justifications = sorted(set(ignored_types) - set(justifications))
    extra_justifications = sorted(set(justifications) - set(ignored_types))
    if missing_justifications:
        fail("Ignored mutation types require justifications: " + ", ".join(missing_justifications))
    if extra_justifications:
        fail("Mutation justifications exist for types that are not ignored: " + ", ".join(extra_justifications))

    report_paths = data.get("reportPaths")
    if not isinstance(report_paths, dict):
        fail("Policy property 'reportPaths' must be a JSON object.")

    reports_directory = validate_repository_relative_path(
        report_paths.get("reportsDirectory"), "reportPaths.reportsDirectory"
    )
    summary_json = validate_repository_relative_path(
        report_paths.get("summaryJson"), "reportPaths.summaryJson"
    )
    summary_markdown = validate_repository_relative_path(
        report_paths.get("summaryMarkdown"), "reportPaths.summaryMarkdown"
    )

    project_reports_data = report_paths.get("projectReports")
    if not isinstance(project_reports_data, dict):
        fail("Policy property 'reportPaths.projectReports' must be a JSON object.")
    if set(project_reports_data) != set(projects):
        missing = sorted(set(projects) - set(project_reports_data))
        extra = sorted(set(project_reports_data) - set(projects))
        details = []
        if missing:
            details.append("missing " + ", ".join(missing))
        if extra:
            details.append("unexpected " + ", ".join(extra))
        fail("Project report mapping does not match configured projects: " + "; ".join(details))

    project_reports: dict[str, str] = {}
    reports_prefix = reports_directory.rstrip("/") + "/"
    for project in projects:
        report_path = validate_repository_relative_path(
            project_reports_data[project], f"reportPaths.projectReports.{project}"
        )
        if not report_path.startswith(reports_prefix) or not report_path.endswith(".json"):
            fail(f"Report path for '{project}' must be a JSON file under {reports_directory}.")
        project_reports[project] = report_path

    return MutationPolicy(
        path=path,
        minimum_mutation_score=minimum_score,
        minimum_tested_mutants=minimum_tested,
        projects=projects,
        excluded_file_patterns=exclusions,
        ignored_mutation_types=ignored_types,
        ignored_mutation_justifications=justifications,
        reports_directory=reports_directory,
        summary_json=summary_json,
        summary_markdown=summary_markdown,
        project_reports=project_reports,
    )


def validate_stryker_config(path: Path, policy: MutationPolicy) -> None:
    data = read_json_object(path, "Stryker configuration")
    config = data.get("stryker-config")
    if not isinstance(config, dict):
        fail("Stryker configuration must contain a 'stryker-config' JSON object.")

    reporters = config.get("reporters")
    if not isinstance(reporters, list) or any(not isinstance(item, str) for item in reporters):
        fail("Stryker reporters must be a JSON array of strings.")
    normalized_reporters = {item.lower() for item in reporters}
    missing_reporters = sorted(REQUIRED_REPORTERS - normalized_reporters)
    if missing_reporters:
        fail("Stryker configuration is missing reporters: " + ", ".join(missing_reporters))

    if config.get("configuration") != "Release":
        fail("Stryker configuration must run the Release build configuration.")
    if config.get("report-file-name") != "mutation-report":
        fail("Stryker report-file-name must be 'mutation-report'.")
    if "project" in config or "test-projects" in config:
        fail("The shared Stryker configuration must not hard-code a project or test project.")

    thresholds = config.get("thresholds")
    if not isinstance(thresholds, dict):
        fail("Stryker configuration must define thresholds.")
    if thresholds.get("break") != 0:
        fail("Stryker per-project break threshold must be 0; the repository verifier owns the aggregate gate.")
    if thresholds.get("low") != policy.minimum_mutation_score:
        fail("Stryker low threshold must match the policy minimumMutationScore baseline.")

    ignored_types = config.get("ignore-mutations", [])
    if not isinstance(ignored_types, list) or any(not isinstance(item, str) for item in ignored_types):
        fail("Stryker ignore-mutations must be a JSON array of strings.")
    if tuple(ignored_types) != policy.ignored_mutation_types:
        fail("Stryker ignored mutation types must exactly match the mutation policy.")

    mutate = config.get("mutate")
    if not isinstance(mutate, list) or any(not isinstance(item, str) for item in mutate):
        fail("Stryker mutate must be a JSON array of strings.")
    if "**/*.cs" not in mutate:
        fail("Stryker mutate must explicitly include C# source files.")
    missing_exclusions = [pattern for pattern in policy.excluded_file_patterns if f"!{pattern}" not in mutate]
    if missing_exclusions:
        fail("Stryker mutate is missing policy exclusions: " + ", ".join(missing_exclusions))


def validate_workflow(path: Path, policy: MutationPolicy) -> None:
    try:
        text = path.read_text(encoding="utf-8")
    except FileNotFoundError:
        fail(f"Mutation workflow is missing: {path}")
    except OSError as error:
        fail(f"Unable to read mutation workflow at {path}: {error}")

    missing_fragments = [fragment for fragment in REQUIRED_WORKFLOW_FRAGMENTS if fragment not in text]
    for project in policy.projects:
        report_path = PurePosixPath(policy.project_reports[project])
        output_directory = str(report_path.parents[1])
        if project not in text:
            missing_fragments.append(project)
        if output_directory not in text:
            missing_fragments.append(output_directory)
    if missing_fragments:
        fail("Mutation workflow is missing required fragments: " + ", ".join(sorted(set(missing_fragments))))


def run_git(repository_root: Path, arguments: list[str]) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(
            ["git", *arguments],
            cwd=repository_root,
            check=False,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
    except FileNotFoundError:
        fail("Git is required to validate that mutation configuration remains tracked.")


def is_git_repository(repository_root: Path) -> bool:
    result = run_git(repository_root, ["rev-parse", "--is-inside-work-tree"])
    return result.returncode == 0 and result.stdout.strip() == "true"


def validate_git_tracking(repository_root: Path, paths: Iterable[Path]) -> None:
    if not is_git_repository(repository_root):
        fail(f"Repository tracking validation requires a Git work tree: {repository_root}")

    for path in paths:
        try:
            relative = path.resolve().relative_to(repository_root.resolve()).as_posix()
        except ValueError:
            fail(f"Tracked mutation configuration must be inside the repository: {path}")

        ignored = run_git(repository_root, ["check-ignore", "--no-index", "--quiet", "--", relative])
        if ignored.returncode == 0:
            fail(f"Mutation configuration is ignored by Git: {relative}")
        if ignored.returncode not in (1,):
            fail(f"Unable to check Git ignore rules for {relative}: {ignored.stderr.strip()}")

        tracked = run_git(repository_root, ["ls-files", "--error-unmatch", "--", relative])
        if tracked.returncode != 0:
            fail(f"Mutation configuration is not tracked by Git: {relative}")


def validate_configuration(
    repository_root: Path,
    policy_path: Path,
    stryker_config_path: Path,
    workflow_path: Path,
    *,
    check_git: bool,
) -> MutationPolicy:
    policy = load_policy(policy_path)
    validate_stryker_config(stryker_config_path, policy)
    validate_workflow(workflow_path, policy)
    if check_git:
        validate_git_tracking(
            repository_root,
            (policy_path, stryker_config_path, workflow_path, Path(__file__).resolve()),
        )
    return policy


def normalize_status(value: Any, report_path: Path) -> str:
    if not isinstance(value, str) or not value.strip():
        fail(f"A mutant in {report_path} has no valid status.")
    normalized = "".join(character for character in value.lower() if character.isalnum())
    try:
        return STATUS_NAMES[normalized]
    except KeyError:
        fail(f"Unsupported mutant status '{value}' in {report_path}.")


def report_identifies_project(data: dict[str, Any], project: str) -> bool:
    expected = project.lower()
    project_root = data.get("projectRoot")
    if isinstance(project_root, str) and project_root.strip():
        root_name = PurePosixPath(project_root.replace("\\", "/").rstrip("/")).name.lower()
        if root_name == expected:
            return True

    files = data.get("files")
    if isinstance(files, dict):
        for file_name in files:
            if not isinstance(file_name, str):
                continue
            parts = {part.lower() for part in PurePosixPath(file_name.replace("\\", "/")).parts}
            if expected in parts:
                return True
    return False


def parse_report(path: Path, project: str) -> MutationCounts:
    data = read_json_object(path, f"Stryker report for {project}")
    if not report_identifies_project(data, project):
        fail(f"Stryker report does not identify configured project '{project}': {path}")

    files = data.get("files")
    if not isinstance(files, dict) or not files:
        fail(f"Stryker report for '{project}' must contain at least one source file: {path}")

    counts = MutationCounts()
    for file_name, file_data in files.items():
        if not isinstance(file_name, str) or not isinstance(file_data, dict):
            fail(f"Stryker report for '{project}' contains an invalid file entry: {path}")
        mutants = file_data.get("mutants")
        if not isinstance(mutants, list):
            fail(f"Stryker report file '{file_name}' has no mutants array: {path}")
        for mutant in mutants:
            if not isinstance(mutant, dict):
                fail(f"Stryker report file '{file_name}' contains an invalid mutant: {path}")
            status_field = normalize_status(mutant.get("status"), path)
            setattr(counts, status_field, getattr(counts, status_field) + 1)

    if counts.total == 0:
        fail(f"Stryker report for '{project}' contains no mutants: {path}")
    return counts


def collect_results(policy: MutationPolicy, repository_root: Path) -> VerificationResult:
    totals = MutationCounts()
    projects: list[ProjectResult] = []

    for project in policy.projects:
        relative_report = policy.project_reports[project]
        report_path = repository_root / relative_report
        counts = parse_report(report_path, project)
        totals.add(counts)
        projects.append(ProjectResult(project, relative_report, counts))

    score_passed = totals.mutation_score + 1e-9 >= policy.minimum_mutation_score
    mutant_count_passed = totals.tested >= policy.minimum_tested_mutants
    return VerificationResult(tuple(projects), totals, score_passed, mutant_count_passed)


def status_label(passed: bool) -> str:
    return "PASS" if passed else "FAIL"


def render_markdown(result: VerificationResult, policy: MutationPolicy) -> str:
    totals = result.totals
    lines = [
        "# TCJ mutation testing",
        "",
        f"**Overall status:** {status_label(result.passed)}",
        "",
        "## Quality gate",
        "",
        "| Gate | Actual | Minimum | Status |",
        "| --- | ---: | ---: | :---: |",
        (
            f"| Mutation score | {totals.mutation_score:.2f}% | "
            f"{policy.minimum_mutation_score:.2f}% | {status_label(result.score_passed)} |"
        ),
        (
            f"| Tested mutants | {totals.tested} | {policy.minimum_tested_mutants} | "
            f"{status_label(result.mutant_count_passed)} |"
        ),
        "",
        "## Mutant outcomes",
        "",
        "| Metric | Count |",
        "| --- | ---: |",
        f"| Total mutants | {totals.total} |",
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
        "| Project | Score | Tested | Killed | Survived | Timeout | No coverage | Ignored |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
    ]

    for project in result.projects:
        counts = project.counts
        lines.append(
            f"| {project.name} | {counts.mutation_score:.2f}% | {counts.tested} | "
            f"{counts.killed} | {counts.survived} | {counts.timeout} | "
            f"{counts.no_coverage} | {counts.ignored} |"
        )

    lines.extend(
        [
            "",
            "Survived mutants are potential assertion gaps. Review the HTML reports before changing exclusions or the baseline.",
            "",
        ]
    )
    return "\n".join(lines)


def render_json(result: VerificationResult, policy: MutationPolicy) -> dict[str, Any]:
    return {
        "schemaVersion": 1,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "status": "pass" if result.passed else "fail",
        "minimumMutationScore": policy.minimum_mutation_score,
        "minimumTestedMutants": policy.minimum_tested_mutants,
        "totals": result.totals.as_dict(),
        "projects": [
            {
                "name": project.name,
                "reportPath": project.report_path,
                **project.counts.as_dict(),
            }
            for project in result.projects
        ],
        "outputs": {
            "reportsDirectory": policy.reports_directory,
            "summaryJson": policy.summary_json,
            "summaryMarkdown": policy.summary_markdown,
        },
    }


def write_outputs(
    result: VerificationResult,
    policy: MutationPolicy,
    summary_path: Path,
    json_path: Path,
) -> None:
    summary_path.parent.mkdir(parents=True, exist_ok=True)
    json_path.parent.mkdir(parents=True, exist_ok=True)
    summary_path.write_text(render_markdown(result, policy), encoding="utf-8", newline="\n")
    json_path.write_text(
        json.dumps(render_json(result, policy), indent=2, sort_keys=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def verify(
    policy: MutationPolicy,
    repository_root: Path,
    summary_path: Path,
    json_path: Path,
) -> VerificationResult:
    result = collect_results(policy, repository_root)
    write_outputs(result, policy, summary_path, json_path)

    failures: list[str] = []
    if not result.score_passed:
        failures.append(
            f"mutation score {result.totals.mutation_score:.2f}% is below "
            f"{policy.minimum_mutation_score:.2f}%"
        )
    if not result.mutant_count_passed:
        failures.append(
            f"tested mutant count {result.totals.tested} is below "
            f"{policy.minimum_tested_mutants}"
        )
    if failures:
        fail("; ".join(failures))
    return result


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("validate-config", "verify"))
    parser.add_argument("--repository-root", type=Path, default=REPOSITORY_ROOT)
    parser.add_argument("--policy", type=Path, default=DEFAULT_POLICY)
    parser.add_argument("--stryker-config", type=Path, default=DEFAULT_STRYKER_CONFIG)
    parser.add_argument("--workflow", type=Path, default=DEFAULT_WORKFLOW)
    parser.add_argument("--summary", type=Path, default=DEFAULT_SUMMARY)
    parser.add_argument("--json", type=Path, default=DEFAULT_JSON)
    parser.add_argument(
        "--skip-git-check",
        action="store_true",
        help="Skip tracked/ignored validation for exported source archives without .git metadata.",
    )
    return parser.parse_args(argv)


def resolve_input(path: Path, repository_root: Path) -> Path:
    return path if path.is_absolute() else repository_root / path


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    repository_root = args.repository_root.resolve()
    policy_path = resolve_input(args.policy, repository_root).resolve()
    stryker_config_path = resolve_input(args.stryker_config, repository_root).resolve()
    workflow_path = resolve_input(args.workflow, repository_root).resolve()
    summary_path = resolve_input(args.summary, repository_root).resolve()
    json_path = resolve_input(args.json, repository_root).resolve()

    try:
        if args.command == "validate-config":
            validate_configuration(
                repository_root,
                policy_path,
                stryker_config_path,
                workflow_path,
                check_git=not args.skip_git_check,
            )
            print("Mutation-testing configuration validation succeeded.")
        else:
            policy = load_policy(policy_path)
            result = verify(policy, repository_root, summary_path, json_path)
            print(
                "Mutation quality gate passed: "
                f"score={result.totals.mutation_score:.2f}%, "
                f"tested={result.totals.tested}."
            )
    except MutationError as error:
        print(f"Mutation verification failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
