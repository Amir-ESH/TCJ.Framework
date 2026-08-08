#!/usr/bin/env python3
"""Validate TCJ code-coverage policy and merge Cobertura reports."""

from __future__ import annotations

import argparse
import glob
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_POLICY = Path(__file__).resolve().with_name("coverage-policy.json")
WORKFLOWS = (
    REPOSITORY_ROOT / ".github/workflows/ci.yml",
    REPOSITORY_ROOT / ".github/workflows/release-preflight.yml",
    REPOSITORY_ROOT / ".github/workflows/release.yml",
)
TEST_PROJECT_PATTERN = "tests/**/*.csproj"
CONDITION_COVERAGE_PATTERN = re.compile(r"^\s*\d+(?:\.\d+)?%\s*\((\d+)\s*/\s*(\d+)\)\s*$")


class CoverageError(RuntimeError):
    """Raised when coverage policy or report validation fails."""


@dataclass(frozen=True)
class CoveragePolicy:
    report_pattern: str
    minimum_line_coverage: float
    minimum_branch_coverage: float
    minimum_report_count: int
    expected_packages: tuple[str, ...]
    excluded_test_projects: tuple[str, ...]


@dataclass(frozen=True)
class CoverageResult:
    line_covered: int
    line_total: int
    branch_covered: int
    branch_total: int
    package_lines: dict[str, tuple[int, int]]
    report_count: int

    @property
    def line_rate(self) -> float:
        return percentage(self.line_covered, self.line_total)

    @property
    def branch_rate(self) -> float:
        return percentage(self.branch_covered, self.branch_total)


def fail(message: str) -> None:
    raise CoverageError(message)


def percentage(covered: int, total: int) -> float:
    if total <= 0:
        return 0.0
    return covered * 100.0 / total


def load_policy(path: Path) -> CoveragePolicy:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"Unable to read coverage policy {path}: {error}")

    required = {
        "schemaVersion",
        "reportPattern",
        "minimumLineCoverage",
        "minimumBranchCoverage",
        "minimumReportCount",
        "expectedPackages",
        "excludedTestProjects",
    }
    missing = sorted(required.difference(data))
    if missing:
        fail(f"Coverage policy is missing fields: {', '.join(missing)}")
    if data["schemaVersion"] != 1:
        fail("Unsupported coverage policy schemaVersion.")

    report_pattern = str(data["reportPattern"]).strip()
    if not report_pattern or Path(report_pattern).is_absolute() or ".." in Path(report_pattern).parts:
        fail("reportPattern must be a safe repository-relative glob.")

    minimum_line = parse_rate(data["minimumLineCoverage"], "minimumLineCoverage")
    minimum_branch = parse_rate(data["minimumBranchCoverage"], "minimumBranchCoverage")

    minimum_report_count = data["minimumReportCount"]
    if not isinstance(minimum_report_count, int) or minimum_report_count <= 0:
        fail("minimumReportCount must be a positive integer.")

    packages = data["expectedPackages"]
    if not isinstance(packages, list) or not packages:
        fail("expectedPackages must be a non-empty array.")
    normalized_packages = tuple(str(item).strip() for item in packages)
    if any(not item for item in normalized_packages) or len(set(normalized_packages)) != len(normalized_packages):
        fail("expectedPackages must contain unique, non-empty package IDs.")

    excluded = data["excludedTestProjects"]
    if not isinstance(excluded, list):
        fail("excludedTestProjects must be an array.")
    normalized_excluded = tuple(str(item).strip().replace("\\", "/") for item in excluded)
    if any(not item or Path(item).is_absolute() or ".." in Path(item).parts for item in normalized_excluded):
        fail("excludedTestProjects must contain safe repository-relative paths.")
    if len(set(normalized_excluded)) != len(normalized_excluded):
        fail("excludedTestProjects must not contain duplicates.")
    for relative in normalized_excluded:
        if not (REPOSITORY_ROOT / relative).is_file():
            fail(f"excludedTestProjects entry does not exist: {relative}")

    return CoveragePolicy(
        report_pattern=report_pattern,
        minimum_line_coverage=minimum_line,
        minimum_branch_coverage=minimum_branch,
        minimum_report_count=minimum_report_count,
        expected_packages=normalized_packages,
        excluded_test_projects=normalized_excluded,
    )


def parse_rate(value: object, field: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        fail(f"{field} must be a number between 0 and 100.")
    rate = float(value)
    if rate < 0 or rate > 100:
        fail(f"{field} must be between 0 and 100.")
    return rate


def validate_config(policy: CoveragePolicy) -> None:
    manifest_path = REPOSITORY_ROOT / "eng/release-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest_packages = tuple(str(item) for item in manifest.get("packages", []))
    if manifest_packages != policy.expected_packages:
        fail("coverage-policy expectedPackages must match release-manifest packages in order.")

    all_test_projects = sorted(REPOSITORY_ROOT.glob(TEST_PROJECT_PATTERN))
    test_projects = [
        project
        for project in all_test_projects
        if project.relative_to(REPOSITORY_ROOT).as_posix() not in policy.excluded_test_projects
    ]
    if len(test_projects) != policy.minimum_report_count:
        fail(
            "minimumReportCount must match the number of coverage-participating test projects: "
            f"expected {len(test_projects)}, found {policy.minimum_report_count}."
        )

    runsettings_path = REPOSITORY_ROOT / "tests/coverlet.runsettings"
    root = ET.parse(runsettings_path).getroot()
    collector = root.find("./DataCollectionRunSettings/DataCollectors/DataCollector")
    if collector is None or collector.get("friendlyName") != "XPlat Code Coverage":
        fail("tests/coverlet.runsettings must configure the XPlat Code Coverage collector.")
    configuration = collector.find("Configuration")
    if configuration is None:
        fail("tests/coverlet.runsettings is missing collector Configuration.")
    expected_values = {
        "Format": "cobertura",
        "Include": "[TCJ.*]*",
        "Exclude": "[*.Tests]*",
        "UseSourceLink": "true",
    }
    for element_name, expected in expected_values.items():
        actual = configuration.findtext(element_name)
        if actual is None or actual.strip() != expected:
            fail(
                f"tests/coverlet.runsettings must set {element_name} to {expected!r}; "
                f"found {actual!r}."
            )

    test_props = (REPOSITORY_ROOT / "tests/TestProject.props").read_text(encoding="utf-8")
    if '<PackageReference Include="coverlet.collector">' not in test_props:
        fail("tests/TestProject.props must reference coverlet.collector with private assets.")

    required_fragments = (
        'python3 eng/verify-coverage.py validate-config',
        '--collect:"XPlat Code Coverage"',
        '--filter "Category!=SqlServer&Category!=AspNetCore"',
        '--settings tests/coverlet.runsettings',
        'python3 eng/verify-coverage.py verify',
        'artifacts/coverage/COVERAGE_SUMMARY.md',
        'TestResults/**/coverage.cobertura.xml',
    )
    for workflow in WORKFLOWS:
        text = workflow.read_text(encoding="utf-8")
        for fragment in required_fragments:
            if fragment not in text:
                fail(f"{workflow.relative_to(REPOSITORY_ROOT)} is missing coverage integration: {fragment}")

    print(
        "Coverage policy configuration is valid: "
        f"line >= {policy.minimum_line_coverage:.1f}%, "
        f"branch >= {policy.minimum_branch_coverage:.1f}%, "
        f"reports >= {policy.minimum_report_count}."
    )


def discover_reports(pattern: str) -> list[Path]:
    expanded = glob.glob(str(REPOSITORY_ROOT / pattern), recursive=True)
    return sorted({Path(item).resolve() for item in expanded if Path(item).is_file()})


def normalize_filename(value: str) -> str:
    normalized = value.replace("\\", "/")
    while "//" in normalized.replace("://", ":§§"):
        protected = normalized.replace("://", ":§§")
        protected = protected.replace("//", "/")
        normalized = protected.replace(":§§", "://")
    return normalized.lower()


def match_package(filename: str, packages: Iterable[str]) -> str | None:
    normalized = normalize_filename(filename)
    for package in packages:
        marker = package.lower()
        path_markers = (
            f"/src/{marker}/",
            f"src/{marker}/",
            f"/{marker}/",
        )
        if any(token in normalized for token in path_markers):
            return package
    return None


def parse_condition_coverage(value: str | None) -> tuple[int, int]:
    if not value:
        return (0, 0)
    match = CONDITION_COVERAGE_PATTERN.fullmatch(value)
    if match is None:
        fail(f"Unsupported Cobertura condition-coverage value: {value!r}")
    return (int(match.group(1)), int(match.group(2)))


def merge_reports(reports: list[Path], policy: CoveragePolicy) -> CoverageResult:
    if len(reports) < policy.minimum_report_count:
        fail(
            f"Expected at least {policy.minimum_report_count} Cobertura reports, "
            f"but found {len(reports)}."
        )

    line_hits: dict[tuple[str, int], int] = {}
    line_packages: dict[tuple[str, int], str] = {}
    branch_hits: dict[tuple[str, int], tuple[int, int]] = {}

    for report in reports:
        try:
            root = ET.parse(report).getroot()
        except (OSError, ET.ParseError) as error:
            fail(f"Unable to parse Cobertura report {report}: {error}")
        if root.tag != "coverage":
            fail(f"Coverage report {report} does not have a Cobertura coverage root.")

        for class_node in root.findall("./packages/package/classes/class"):
            filename = class_node.get("filename", "").strip()
            package = match_package(filename, policy.expected_packages)
            if package is None:
                continue
            normalized_filename = normalize_filename(filename)
            for line in class_node.findall("./lines/line"):
                number_text = line.get("number")
                hits_text = line.get("hits")
                if number_text is None or hits_text is None:
                    fail(f"Cobertura line in {report} is missing number or hits.")
                try:
                    number = int(number_text)
                    hits = int(hits_text)
                except ValueError as error:
                    fail(f"Invalid Cobertura line data in {report}: {error}")
                if number <= 0 or hits < 0:
                    fail(f"Invalid Cobertura line values in {report}: line={number}, hits={hits}")
                key = (normalized_filename, number)
                line_hits[key] = max(line_hits.get(key, 0), hits)
                line_packages[key] = package

                if line.get("branch", "false").lower() == "true":
                    covered, total = parse_condition_coverage(line.get("condition-coverage"))
                    previous_covered, previous_total = branch_hits.get(key, (0, 0))
                    branch_hits[key] = (max(previous_covered, covered), max(previous_total, total))

    if not line_hits:
        fail("Coverage reports contain no production lines for the expected TCJ packages.")

    package_line_keys: dict[str, list[tuple[str, int]]] = {
        package: [] for package in policy.expected_packages
    }
    for key, package in line_packages.items():
        package_line_keys[package].append(key)

    missing_packages = [package for package, keys in package_line_keys.items() if not keys]
    if missing_packages:
        fail("Coverage reports are missing production assemblies: " + ", ".join(missing_packages))

    package_lines = {
        package: (
            sum(1 for key in keys if line_hits[key] > 0),
            len(keys),
        )
        for package, keys in package_line_keys.items()
    }

    line_covered = sum(1 for hits in line_hits.values() if hits > 0)
    line_total = len(line_hits)
    branch_covered = sum(covered for covered, _ in branch_hits.values())
    branch_total = sum(total for _, total in branch_hits.values())

    if branch_total <= 0:
        fail("Coverage reports contain no branch coverage data.")

    return CoverageResult(
        line_covered=line_covered,
        line_total=line_total,
        branch_covered=branch_covered,
        branch_total=branch_total,
        package_lines=package_lines,
        report_count=len(reports),
    )


def status(rate: float, minimum: float) -> str:
    return "PASS" if rate + 1e-9 >= minimum else "FAIL"


def render_markdown(result: CoverageResult, policy: CoveragePolicy) -> str:
    lines = [
        "# TCJ code coverage",
        "",
        f"Merged **{result.report_count}** Cobertura reports using unique source lines.",
        "",
        "| Metric | Covered | Total | Coverage | Minimum | Status |",
        "|---|---:|---:|---:|---:|:---:|",
        (
            f"| Lines | {result.line_covered} | {result.line_total} | "
            f"{result.line_rate:.2f}% | {policy.minimum_line_coverage:.2f}% | "
            f"{status(result.line_rate, policy.minimum_line_coverage)} |"
        ),
        (
            f"| Branches | {result.branch_covered} | {result.branch_total} | "
            f"{result.branch_rate:.2f}% | {policy.minimum_branch_coverage:.2f}% | "
            f"{status(result.branch_rate, policy.minimum_branch_coverage)} |"
        ),
        "",
        "## Line coverage by package",
        "",
        "| Package | Covered | Total | Coverage |",
        "|---|---:|---:|---:|",
    ]
    for package in policy.expected_packages:
        covered, total = result.package_lines[package]
        lines.append(f"| `{package}` | {covered} | {total} | {percentage(covered, total):.2f}% |")
    lines.append("")
    return "\n".join(lines)


def write_outputs(
    result: CoverageResult,
    policy: CoveragePolicy,
    summary_path: Path,
    json_path: Path,
) -> None:
    summary_path.parent.mkdir(parents=True, exist_ok=True)
    summary_path.write_text(render_markdown(result, policy), encoding="utf-8", newline="\n")

    payload = {
        "schemaVersion": 1,
        "reports": result.report_count,
        "line": {
            "covered": result.line_covered,
            "total": result.line_total,
            "rate": round(result.line_rate, 4),
            "minimum": policy.minimum_line_coverage,
        },
        "branch": {
            "covered": result.branch_covered,
            "total": result.branch_total,
            "rate": round(result.branch_rate, 4),
            "minimum": policy.minimum_branch_coverage,
        },
        "packages": {
            package: {
                "lineCovered": covered,
                "lineTotal": total,
                "lineRate": round(percentage(covered, total), 4),
            }
            for package, (covered, total) in result.package_lines.items()
        },
    }
    json_path.parent.mkdir(parents=True, exist_ok=True)
    json_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8", newline="\n")


def verify(policy: CoveragePolicy, summary_path: Path, json_path: Path) -> None:
    reports = discover_reports(policy.report_pattern)
    result = merge_reports(reports, policy)
    write_outputs(result, policy, summary_path, json_path)

    failures: list[str] = []
    if result.line_rate + 1e-9 < policy.minimum_line_coverage:
        failures.append(
            f"line coverage {result.line_rate:.2f}% is below {policy.minimum_line_coverage:.2f}%"
        )
    if result.branch_rate + 1e-9 < policy.minimum_branch_coverage:
        failures.append(
            f"branch coverage {result.branch_rate:.2f}% is below {policy.minimum_branch_coverage:.2f}%"
        )

    print(
        f"Coverage: lines {result.line_rate:.2f}% "
        f"({result.line_covered}/{result.line_total}), branches {result.branch_rate:.2f}% "
        f"({result.branch_covered}/{result.branch_total}), reports {result.report_count}."
    )
    print(f"Coverage summary: {summary_path}")

    if failures:
        fail("Coverage quality gate failed: " + "; ".join(failures))

    print("Coverage quality gate passed.")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=("validate-config", "verify"))
    parser.add_argument("--policy", type=Path, default=DEFAULT_POLICY)
    parser.add_argument(
        "--summary",
        type=Path,
        default=REPOSITORY_ROOT / "artifacts/coverage/COVERAGE_SUMMARY.md",
    )
    parser.add_argument(
        "--json",
        type=Path,
        default=REPOSITORY_ROOT / "artifacts/coverage/coverage-summary.json",
    )
    args = parser.parse_args()

    policy = load_policy(args.policy.resolve())
    if args.command == "validate-config":
        validate_config(policy)
    else:
        verify(policy, args.summary.resolve(), args.json.resolve())
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (CoverageError, OSError, KeyError, json.JSONDecodeError, ET.ParseError) as error:
        print(f"Coverage verification failed: {error}", file=sys.stderr)
        raise SystemExit(1)
