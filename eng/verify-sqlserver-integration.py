#!/usr/bin/env python3
"""Validate and summarize the TCJ SQL Server Testcontainers integration gate."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_POLICY = ROOT / "eng/sqlserver-integration-policy.json"
DEFAULT_OUTPUT = ROOT / "artifacts/sqlserver-integration"
DEFAULT_RESULTS = ROOT / "TestResults/SqlServerIntegration"
EXPECTED_PROJECT = "tests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.csproj"
EXPECTED_WORKFLOW = ROOT / ".github/workflows/sqlserver-integration.yml"
EXPECTED_FIXTURE = ROOT / "tests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests/Infrastructure/SqlServerContainerFixture.cs"
EXPECTED_TEST_ROOT = ROOT / "tests/TCJ.EntityFrameworkCore.SqlServer.IntegrationTests"
EXPECTED_MIGRATION_ROOT = EXPECTED_TEST_ROOT / "Migrations"
EXPECTED_MIGRATION_TEST = EXPECTED_TEST_ROOT / "Tests/RegistrationAndMigrationIntegrationTests.cs"


class SqlServerIntegrationError(RuntimeError):
    pass


@dataclass(frozen=True)
class Policy:
    path: Path
    test_project: str
    container_image: str
    minimum_test_count: int
    startup_timeout_seconds: int
    command_timeout_seconds: int
    collect_container_logs_on_failure: bool
    require_pinned_image: bool
    require_docker_health_check: bool
    allow_external_database: bool
    database_isolation: str
    required_categories: tuple[str, ...]


@dataclass(frozen=True)
class TestResult:
    name: str
    outcome: str


def fail(message: str) -> None:
    raise SqlServerIntegrationError(message)


def read_json_object(path: Path, description: str) -> dict[str, Any]:
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


def require_string(data: dict[str, Any], key: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        fail(f"Policy property '{key}' must be a non-empty string.")
    return value.strip()


def require_int(data: dict[str, Any], key: str, minimum: int) -> int:
    value = data.get(key)
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        fail(f"Policy property '{key}' must be an integer >= {minimum}.")
    return value


def require_bool(data: dict[str, Any], key: str) -> bool:
    value = data.get(key)
    if not isinstance(value, bool):
        fail(f"Policy property '{key}' must be a boolean.")
    return value


def require_strings(data: dict[str, Any], key: str) -> tuple[str, ...]:
    value = data.get(key)
    if not isinstance(value, list) or not value:
        fail(f"Policy property '{key}' must be a non-empty array.")
    if any(not isinstance(item, str) or not item.strip() for item in value):
        fail(f"Policy property '{key}' must contain only non-empty strings.")
    return tuple(item.strip() for item in value)


def load_policy(path: Path = DEFAULT_POLICY) -> Policy:
    data = read_json_object(path, "SQL Server integration policy")
    if data.get("schemaVersion") != 1:
        fail("SQL Server integration policy schemaVersion must be 1.")

    return Policy(
        path=path,
        test_project=require_string(data, "testProject"),
        container_image=require_string(data, "containerImage"),
        minimum_test_count=require_int(data, "minimumTestCount", 12),
        startup_timeout_seconds=require_int(data, "startupTimeoutSeconds", 1),
        command_timeout_seconds=require_int(data, "commandTimeoutSeconds", 1),
        collect_container_logs_on_failure=require_bool(data, "collectContainerLogsOnFailure"),
        require_pinned_image=require_bool(data, "requirePinnedImage"),
        require_docker_health_check=require_bool(data, "requireDockerHealthCheck"),
        allow_external_database=require_bool(data, "allowExternalDatabase"),
        database_isolation=require_string(data, "databaseIsolation"),
        required_categories=require_strings(data, "requiredCategories"),
    )


def is_floating_image(image: str) -> bool:
    if "@sha256:" in image:
        return False
    if ":" not in image.rsplit("/", 1)[-1]:
        return True
    tag = image.rsplit(":", 1)[1].strip().lower()
    return not tag or tag in {"latest", "main", "master", "develop", "edge", "nightly"} or tag.endswith("-latest")


def read_xml(path: Path, description: str) -> ET.Element:
    try:
        return ET.parse(path).getroot()
    except FileNotFoundError:
        fail(f"{description} is missing: {path}")
    except (OSError, ET.ParseError) as error:
        fail(f"Unable to parse {description} at {path}: {error}")


def validate_project(policy: Policy) -> None:
    if policy.test_project != EXPECTED_PROJECT:
        fail(f"Policy testProject must be '{EXPECTED_PROJECT}'.")

    project_path = ROOT / policy.test_project
    project_root = read_xml(project_path, "SQL Server integration test project")
    target = project_root.findtext("./PropertyGroup/TargetFramework")
    if target != "net10.0":
        fail("SQL Server integration test project must explicitly target net10.0.")

    references = {
        element.attrib.get("Include", "")
        for element in project_root.findall(".//PackageReference")
    }
    if "Testcontainers.MsSql" not in references:
        fail("SQL Server integration test project must reference Testcontainers.MsSql.")

    central_root = read_xml(ROOT / "Directory.Packages.props", "central package policy")
    versions = {
        element.attrib.get("Include", ""): element.attrib.get("Version", "")
        for element in central_root.findall(".//PackageVersion")
    }
    testcontainers_version = versions.get("Testcontainers.MsSql", "")
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?", testcontainers_version):
        fail("Testcontainers.MsSql must have a centrally pinned semantic version in Directory.Packages.props.")


def validate_fixture(policy: Policy) -> None:
    try:
        text = EXPECTED_FIXTURE.read_text(encoding="utf-8")
    except FileNotFoundError:
        fail(f"SQL Server container fixture is missing: {EXPECTED_FIXTURE}")

    required_markers = [
        "new MsSqlBuilder(_policy.ContainerImage)",
        ".WithPassword(_password)",
        ".WithCleanUp(true)",
        "WaitUntilReadyAsync",
        "CanConnectAsync",
        "CreatePassword()",
        "CreateDatabaseAsync",
        "DropDatabaseAsync",
        "WriteContainerLogsAsync",
        'ReadFileAsync("/var/opt/mssql/log/errorlog")',
        "Sanitize",
    ]
    missing = [marker for marker in required_markers if marker not in text]
    if missing:
        fail("SQL Server container fixture is missing required lifecycle markers: " + ", ".join(missing))

    forbidden = ["SA_PASSWORD", "SQL_CONNECTION_STRING", "DATABASE_PASSWORD"]
    present = [marker for marker in forbidden if marker in text]
    if present:
        fail("Integration fixture must not require permanent database secret variables: " + ", ".join(present))

    if policy.require_docker_health_check and "WaitUntilReadyAsync" not in text:
        fail("Policy requires a Docker/database readiness check, but none is configured.")
    if policy.allow_external_database:
        fail("allowExternalDatabase must remain false for hermetic SQL Server integration tests.")
    if policy.database_isolation != "database-per-test":
        fail("databaseIsolation must be 'database-per-test'.")


def validate_migration_artifacts() -> None:
    required_files = [
        EXPECTED_MIGRATION_ROOT / "InitialSqlServerIntegrationMigration.cs",
        EXPECTED_MIGRATION_ROOT / "InitialSqlServerIntegrationMigration.Designer.cs",
        EXPECTED_MIGRATION_ROOT / "SqlServerIntegrationMigrationModel.cs",
        EXPECTED_MIGRATION_ROOT / "SqlServerTestDbContextModelSnapshot.cs",
    ]
    missing = [path.relative_to(ROOT).as_posix() for path in required_files if not path.is_file()]
    if missing:
        fail("SQL Server test migration metadata is incomplete: " + ", ".join(missing))

    migration_text = "\n".join(path.read_text(encoding="utf-8") for path in required_files)
    for marker in (
        '[Migration("202608080001_InitialSqlServerIntegration")]',
        "ModelSnapshot",
        "BuildTargetModel",
        "SqlServerIntegrationMigrationModel.Build(modelBuilder)",
        "SqlServerTestDbContextModelBuilder.Build(modelBuilder)",
    ):
        if marker not in migration_text:
            fail(f"SQL Server test migration metadata is missing required marker: {marker}")

    try:
        migration_test = EXPECTED_MIGRATION_TEST.read_text(encoding="utf-8")
    except FileNotFoundError:
        fail(f"SQL Server migration integration test is missing: {EXPECTED_MIGRATION_TEST}")
    if "HasPendingModelChanges()" not in migration_test:
        fail("Migration integration test must reject pending EF Core model changes.")


def validate_test_inventory(policy: Policy) -> None:
    if not EXPECTED_TEST_ROOT.exists():
        fail(f"SQL Server integration test directory is missing: {EXPECTED_TEST_ROOT}")

    sources = list(EXPECTED_TEST_ROOT.rglob("*.cs"))
    text = "\n".join(path.read_text(encoding="utf-8") for path in sources)
    test_count = len(re.findall(r"\[(?:Fact|Theory)(?:\([^\]]*\))?\]", text))
    if test_count < policy.minimum_test_count:
        fail(
            f"Integration test source contains {test_count} Fact/Theory tests, "
            f"below policy minimum {policy.minimum_test_count}."
        )

    missing_categories = [
        category
        for category in policy.required_categories
        if f'[Trait("Category", "{category}")]' not in text
    ]
    if missing_categories:
        fail("Missing required SQL Server test categories: " + ", ".join(missing_categories))


def validate_workflows() -> None:
    try:
        dedicated = EXPECTED_WORKFLOW.read_text(encoding="utf-8")
    except FileNotFoundError:
        fail(f"Dedicated SQL Server integration workflow is missing: {EXPECTED_WORKFLOW}")

    required_dedicated_markers = [
        "name: SQL Server integration",
        "pull_request:",
        "push:",
        "workflow_dispatch:",
        "workflow_call:",
        "schedule:",
        "docker version",
        "docker info",
        "verify-sqlserver-integration.py validate-config",
        "TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.csproj",
        "Category=SqlServer",
        "verify-sqlserver-integration.py verify",
        "actions/upload-artifact",
        "GITHUB_STEP_SUMMARY",
    ]
    missing = [marker for marker in required_dedicated_markers if marker not in dedicated]
    if missing:
        fail("Dedicated SQL Server integration workflow is missing required integration markers: " + ", ".join(missing))

    ci = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
    if "verify-sqlserver-integration.py validate-config" not in ci:
        fail("Normal CI must run SQL Server integration configuration validation.")

    preflight = (ROOT / ".github/workflows/release-preflight.yml").read_text(encoding="utf-8")
    release = (ROOT / ".github/workflows/release.yml").read_text(encoding="utf-8")
    if "sqlserver-integration.yml" not in preflight:
        fail("Release preflight must enforce the SQL Server integration workflow.")
    if "sqlserver-integration.yml" not in release:
        fail("Official release must enforce the SQL Server integration workflow before publication.")


def validate_pull_request_template() -> None:
    text = (ROOT / ".github/PULL_REQUEST_TEMPLATE.md").read_text(encoding="utf-8")
    required = [
        "SQL Server integration tests pass",
        "container image remains pinned",
        "No permanent database secret is required",
        "Test databases are isolated",
        "Migrations apply successfully",
        "Transaction behavior is verified",
        "Container logs are sanitized",
        "Generated SQL Server integration output is not committed",
        "Production changes discovered by integration tests are explained",
    ]
    missing = [item for item in required if item not in text]
    if missing:
        fail("Pull-request template is missing SQL Server integration checklist items: " + ", ".join(missing))


def validate_git_tracking(policy: Policy) -> None:
    ignore_text = (ROOT / ".gitignore").read_text(encoding="utf-8")
    for required_rule in (
        "TestResults/SqlServerIntegration/",
        "artifacts/sqlserver-integration/",
    ):
        if required_rule not in ignore_text:
            fail(f".gitignore is missing SQL Server integration output rule: {required_rule}")

    if not (ROOT / ".git").exists():
        return

    for path in (policy.path, ROOT / "eng/verify-sqlserver-integration.py"):
        relative = path.relative_to(ROOT).as_posix()
        ignored = subprocess.run(
            ["git", "check-ignore", "--quiet", "--no-index", "--", relative],
            cwd=ROOT,
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            text=True,
        )
        if ignored.returncode == 0:
            fail(f"SQL Server integration source is ignored by Git: {relative}")
        if ignored.returncode not in (0, 1):
            fail(f"Unable to inspect Git ignore state for {relative}: {ignored.stderr.strip()}")

        tracked = subprocess.run(
            ["git", "ls-files", "--error-unmatch", "--", relative],
            cwd=ROOT,
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            text=True,
        )
        if tracked.returncode != 0:
            fail(f"SQL Server integration source is not tracked by Git: {relative}")


def validate_config() -> Policy:
    policy = load_policy()
    if policy.require_pinned_image and is_floating_image(policy.container_image):
        fail(f"SQL Server container image must be pinned; floating image rejected: {policy.container_image}")
    if not policy.collect_container_logs_on_failure:
        fail("collectContainerLogsOnFailure must remain true.")

    validate_project(policy)
    validate_fixture(policy)
    validate_migration_artifacts()
    validate_test_inventory(policy)
    validate_workflows()
    validate_pull_request_template()
    validate_git_tracking(policy)
    return policy


def parse_trx_files(results_directory: Path) -> list[TestResult]:
    trx_files = sorted(results_directory.rglob("*.trx"))
    if not trx_files:
        fail(f"No TRX test-result files were found under {results_directory}.")

    results: list[TestResult] = []
    for trx in trx_files:
        try:
            root = ET.parse(trx).getroot()
        except (OSError, ET.ParseError) as error:
            fail(f"Unable to parse TRX result {trx}: {error}")
        for element in root.iter():
            if element.tag.rsplit("}", 1)[-1] != "UnitTestResult":
                continue
            name = element.attrib.get("testName", "").strip() or "<unnamed>"
            outcome = element.attrib.get("outcome", "Unknown").strip()
            results.append(TestResult(name=name, outcome=outcome))

    if not results:
        fail(f"TRX files under {results_directory} contain no UnitTestResult entries.")
    return results


def scan_for_credentials(paths: Iterable[Path]) -> list[str]:
    leaks: list[str] = []
    password_pattern = re.compile(r"(?i)\b(?:Password|Pwd)\s*=\s*([^;\r\n]+)")
    runtime_password_pattern = re.compile(r"Tcj!aA1_[A-F0-9]{20,}")
    permanent_secret_pattern = re.compile(r"\b(?:SA_PASSWORD|SQL_CONNECTION_STRING|DATABASE_PASSWORD)\b")

    for path in paths:
        if not path.is_file():
            continue
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue

        if runtime_password_pattern.search(text):
            leaks.append(f"{path}: generated SQL Server password pattern")
        if permanent_secret_pattern.search(text):
            leaks.append(f"{path}: permanent database secret variable name")
        for match in password_pattern.finditer(text):
            value = match.group(1).strip().strip('"\'')
            if value.lower() not in {"<redacted>", "***", "*****", "redacted"}:
                leaks.append(f"{path}: unredacted Password/Pwd value")
                break
    return leaks


def sanitize_generated_file(path: Path) -> None:
    if not path.is_file():
        return
    try:
        text = path.read_text(encoding="utf-8", errors="strict")
    except (OSError, UnicodeError):
        return

    sanitized = re.sub(r"Tcj!aA1_[A-F0-9]{20,}", "<redacted>", text)
    sanitized = re.sub(
        r"(?i)(Password|Pwd)\s*=\s*[^;\r\n]+",
        r"\1=<redacted>",
        sanitized,
    )
    if sanitized != text:
        path.write_text(sanitized, encoding="utf-8")


def tool_version(command: list[str]) -> str:
    try:
        completed = subprocess.run(command, cwd=ROOT, check=False, capture_output=True, text=True, timeout=15)
    except (OSError, subprocess.TimeoutExpired):
        return "unavailable"
    text = (completed.stdout or completed.stderr).strip().splitlines()
    return text[0] if completed.returncode == 0 and text else "unavailable"


def source_commit() -> str:
    github_sha = os.environ.get("GITHUB_SHA", "").strip()
    if github_sha:
        return github_sha
    try:
        completed = subprocess.run(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, check=False, capture_output=True, text=True, timeout=10
        )
    except (OSError, subprocess.TimeoutExpired):
        return "unknown"
    return completed.stdout.strip() if completed.returncode == 0 else "unknown"


def package_version() -> str:
    root = read_xml(ROOT / "eng/Packaging.props", "packaging properties")
    value = root.findtext("./PropertyGroup/Version")
    return value.strip() if value and value.strip() else "unknown"


def category_status(results: list[TestResult], keywords: tuple[str, ...]) -> str:
    matching = [result for result in results if any(keyword.lower() in result.name.lower() for keyword in keywords)]
    if not matching:
        return "NOT RUN"
    return "PASS" if all(result.outcome.lower() == "passed" for result in matching) else "FAIL"


def write_summary(
    output_directory: Path,
    policy: Policy,
    results: list[TestResult],
    runtime: dict[str, Any],
    leaks: list[str],
    errors: list[str],
) -> None:
    output_directory.mkdir(parents=True, exist_ok=True)
    passed = sum(result.outcome.lower() == "passed" for result in results)
    failed = sum(result.outcome.lower() == "failed" for result in results)
    skipped = sum(result.outcome.lower() in {"notexecuted", "skipped", "notrun"} for result in results)
    overall = "PASS" if not errors else "FAIL"

    summary = {
        "sourceCommitSha": source_commit(),
        "packageVersion": package_version(),
        "dotnetSdkVersion": tool_version(["dotnet", "--version"]),
        "dockerVersion": tool_version(["docker", "--version"]),
        "sqlServerImage": policy.container_image,
        "testCount": len(results),
        "passedTestCount": passed,
        "failedTestCount": failed,
        "skippedTestCount": skipped,
        "containerStartupDurationSeconds": runtime.get("startupDurationSeconds"),
        "migrationStatus": category_status(results, ("migration",)),
        "transactionTestStatus": category_status(results, ("transaction", "rollback", "commit", "nested")),
        "repositoryTestStatus": category_status(results, ("repository", "soft_delete", "missing_entity")),
        "auditingTestStatus": category_status(results, ("audit", "soft_delete_metadata")),
        "credentialLeakScanStatus": "PASS" if not leaks else "FAIL",
        "databaseIsolation": runtime.get("databaseIsolation", policy.database_isolation),
        "readinessProbe": runtime.get("readinessProbe", "unknown"),
        "migratedDatabaseCount": runtime.get("migratedDatabaseCount", 0),
        "overallStatus": overall,
        "errors": errors,
    }

    json_path = output_directory / "sqlserver-integration-summary.json"
    json_path.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")

    lines = [
        "# SQL Server integration summary",
        "",
        "| Item | Result |",
        "| --- | --- |",
        f"| Source commit SHA | `{summary['sourceCommitSha']}` |",
        f"| Package version | `{summary['packageVersion']}` |",
        f"| .NET SDK version | `{summary['dotnetSdkVersion']}` |",
        f"| Docker version | `{summary['dockerVersion']}` |",
        f"| SQL Server image | `{summary['sqlServerImage']}` |",
        f"| Test count | {summary['testCount']} |",
        f"| Passed | {summary['passedTestCount']} |",
        f"| Failed | {summary['failedTestCount']} |",
        f"| Skipped | {summary['skippedTestCount']} |",
        f"| Container startup duration | {summary['containerStartupDurationSeconds']} s |",
        f"| Migration status | **{summary['migrationStatus']}** |",
        f"| Transaction tests | **{summary['transactionTestStatus']}** |",
        f"| Repository tests | **{summary['repositoryTestStatus']}** |",
        f"| Auditing tests | **{summary['auditingTestStatus']}** |",
        f"| Credential leak scan | **{summary['credentialLeakScanStatus']}** |",
        f"| Database isolation | `{summary['databaseIsolation']}` |",
        f"| Readiness probe | **{summary['readinessProbe']}** |",
        f"| Overall | **{summary['overallStatus']}** |",
    ]
    if errors:
        lines.extend(["", "## Failures", ""] + [f"- {error}" for error in errors])

    (output_directory / "SQLSERVER_INTEGRATION_SUMMARY.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def verify(results_directory: Path, output_directory: Path) -> None:
    policy = validate_config()
    results = parse_trx_files(results_directory)
    errors: list[str] = []

    if len(results) < policy.minimum_test_count:
        errors.append(f"Test count {len(results)} is below policy minimum {policy.minimum_test_count}.")

    failed = [result.name for result in results if result.outcome.lower() == "failed"]
    skipped = [result.name for result in results if result.outcome.lower() in {"notexecuted", "skipped", "notrun"}]
    if failed:
        errors.append(f"{len(failed)} SQL Server integration test(s) failed.")
    if skipped:
        errors.append(f"{len(skipped)} SQL Server integration test(s) were skipped/not executed; critical tests may not be skipped.")

    runtime_path = results_directory / "diagnostics/runtime-summary.json"
    try:
        runtime = read_json_object(runtime_path, "SQL Server runtime diagnostic summary")
    except SqlServerIntegrationError as error:
        runtime = {}
        errors.append(str(error))

    if runtime:
        if runtime.get("containerImage") != policy.container_image:
            errors.append("Runtime SQL Server image does not match the pinned policy image.")
        if runtime.get("readinessProbe") != "passed":
            errors.append("SQL Server readiness probe did not report passed.")
        if not isinstance(runtime.get("migratedDatabaseCount"), int) or runtime.get("migratedDatabaseCount", 0) < 1:
            errors.append("No successfully migrated isolated SQL Server database was recorded.")

    scan_paths = list(results_directory.rglob("*"))
    leaks = scan_for_credentials(scan_paths)
    if leaks:
        for generated_path in scan_paths:
            sanitize_generated_file(generated_path)
        errors.append(
            f"Credential leak scan found {len(leaks)} suspicious generated artifact(s); "
            "affected text artifacts were redacted before upload."
        )

    container_log = results_directory / "diagnostics/container.log"
    if policy.collect_container_logs_on_failure and not container_log.exists():
        errors.append("Sanitized SQL Server container log was not collected.")

    sqlserver_error_log = results_directory / "diagnostics/sqlserver-error.log"
    if policy.collect_container_logs_on_failure and not sqlserver_error_log.exists():
        errors.append("Sanitized SQL Server error log was not collected.")

    test_host_log = results_directory / "diagnostics/test-host.log"
    if not test_host_log.exists():
        errors.append("Sanitized SQL Server test-host log was not collected.")

    diagnostics_directory = results_directory / "diagnostics"
    logs_directory = output_directory / "logs"
    if diagnostics_directory.exists():
        logs_directory.mkdir(parents=True, exist_ok=True)
        for diagnostic in diagnostics_directory.iterdir():
            if diagnostic.is_file():
                shutil.copy2(diagnostic, logs_directory / diagnostic.name)

    write_summary(output_directory, policy, results, runtime, leaks, errors)

    if errors:
        fail("SQL Server integration verification failed:\n- " + "\n- ".join(errors))


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("validate-config", help="Validate SQL Server integration policy and repository wiring.")
    verify_parser = subparsers.add_parser("verify", help="Verify TRX results and produce sanitized summaries.")
    verify_parser.add_argument("--results", type=Path, default=DEFAULT_RESULTS)
    verify_parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.command == "validate-config":
            policy = validate_config()
            print(
                f"SQL Server integration configuration is valid: image={policy.container_image}, "
                f"minimumTestCount={policy.minimum_test_count}."
            )
        else:
            verify(args.results.resolve(), args.output.resolve())
            print(f"SQL Server integration verification passed. Summary: {args.output / 'SQLSERVER_INTEGRATION_SUMMARY.md'}")
        return 0
    except SqlServerIntegrationError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
