#!/usr/bin/env python3
"""Validate TCJ resilience policy, implementation, tests, workflows, and evidence."""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
POLICY_PATH = ROOT / "eng/resilience-policy.json"
CONTRACT_PATH = ROOT / "eng/resilience-contract.json"
TEST_PROJECT = ROOT / "tests/TCJ.Resilience.Tests/TCJ.Resilience.Tests.csproj"
TEST_ROOT = TEST_PROJECT.parent


class ResilienceError(RuntimeError):
    pass


def fail(message: str) -> None:
    raise ResilienceError(message)


def read_json(path: Path) -> dict[str, Any]:
    if not path.is_file():
        fail(f"Required file is missing: {path.relative_to(ROOT).as_posix()}")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        fail(f"Malformed JSON in {path.relative_to(ROOT).as_posix()}: {error}")
    if not isinstance(value, dict):
        fail(f"{path.relative_to(ROOT).as_posix()} must contain a JSON object.")
    return value


def require_schema_one(value: dict[str, Any], name: str) -> None:
    if value.get("schemaVersion") != 1:
        fail(f"{name} schemaVersion must be 1.")


def require_unique_strings(value: Any, field: str) -> list[str]:
    if not isinstance(value, list) or not value:
        fail(f"{field} must be a non-empty array.")
    if any(not isinstance(item, str) or not item.strip() for item in value):
        fail(f"{field} must contain non-empty strings.")
    normalized = [item.strip() for item in value]
    if len(normalized) != len(set(normalized)):
        fail(f"{field} contains duplicates.")
    return normalized


def tracked_and_not_ignored(path: Path) -> None:
    if not (ROOT / ".git").exists():
        return
    relative = path.relative_to(ROOT).as_posix()
    result = subprocess.run(
        ["git", "check-ignore", "--quiet", "--", relative], cwd=ROOT,
        check=False, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True)
    if result.returncode == 0:
        fail(f"{relative} is ignored by Git and must remain tracked.")
    if result.returncode not in (0, 1):
        fail(f"Unable to inspect Git ignore state for {relative}: {result.stderr.strip()}")


def require_text(path: Path, fragments: list[str]) -> str:
    if not path.is_file():
        fail(f"Required file is missing: {path.relative_to(ROOT).as_posix()}")
    text = path.read_text(encoding="utf-8")
    missing = [fragment for fragment in fragments if fragment not in text]
    if missing:
        fail(f"{path.relative_to(ROOT).as_posix()} is missing resilience fragments: {', '.join(missing)}")
    return text


def count_scenarios() -> int:
    return sum(len(re.findall(r"\[Fact\]", path.read_text(encoding="utf-8"))) for path in TEST_ROOT.rglob("*.cs"))


def test_categories() -> set[str]:
    result: set[str] = set()
    for path in TEST_ROOT.rglob("*.cs"):
        result.update(re.findall(r'\[Trait\("Category",\s*"([^"]+)"\)\]', path.read_text(encoding="utf-8")))
    return result


def validate_bounds(policy: dict[str, Any], contract: dict[str, Any]) -> None:
    for field in ("maximumRetryAttempts", "maximumRetryDelaySeconds", "maximumOperationTimeoutSeconds", "maximumCircuitBreakSeconds"):
        if not isinstance(policy.get(field), int) or policy[field] <= 0:
            fail(f"{field} must be a positive integer.")
    if policy["maximumRetryAttempts"] > 5:
        fail("maximumRetryAttempts must remain at or below 5.")
    if policy["maximumRetryDelaySeconds"] > 30:
        fail("maximumRetryDelaySeconds must remain at or below 30.")
    if policy["maximumOperationTimeoutSeconds"] > 120:
        fail("maximumOperationTimeoutSeconds must remain at or below 120.")
    sql_provider_maximum = policy.get("sqlServerProviderMaximumRetryCount")
    if not isinstance(sql_provider_maximum, int) or sql_provider_maximum < 1 or sql_provider_maximum > 10:
        fail("sqlServerProviderMaximumRetryCount must be between 1 and 10.")

    defaults = contract.get("defaults")
    if not isinstance(defaults, dict):
        fail("resilience-contract.json defaults must be an object.")
    if defaults.get("maxRetryAttempts", 99) > policy["maximumRetryAttempts"]:
        fail("Contract retry default exceeds policy maximum.")
    if defaults.get("retryMaxDelaySeconds", 99) > policy["maximumRetryDelaySeconds"]:
        fail("Contract retry delay exceeds policy maximum.")
    if defaults.get("operationTimeoutSeconds", 999) > policy["maximumOperationTimeoutSeconds"]:
        fail("Contract operation timeout exceeds policy maximum.")
    if defaults.get("circuitBreakSeconds", 999) > policy["maximumCircuitBreakSeconds"]:
        fail("Contract circuit break duration exceeds policy maximum.")
    if defaults.get("sqlServerProviderMaxRetryCount", 999) > sql_provider_maximum:
        fail("Contract SQL Server provider retry default exceeds policy maximum.")


def validate_implementation(policy: dict[str, Any], contract: dict[str, Any]) -> None:
    detector = require_text(ROOT / "src/TCJ.Core/Resilience/TransientFailureDetector.cs", [
        "OperationCanceledException", "TimeoutException", "DbException", "IsTransient",
        "ITransientFailureClassifier", "IsKnownPermanent", "ArgumentException", "InvalidOperationException"])
    if re.search(r"(?:SqlException|SqlError).*\b(?:1205|4060|10928|10929|40197|40501|40613)\b", detector):
        fail("TransientFailureDetector must not embed an undocumented SQL error-number list.")

    require_text(ROOT / "src/TCJ.Core/Resilience/TcjRetryPolicy.cs", [
        "MaxRetryAttempts", "Task.Delay", "TimeProvider", "UseJitter", "ResilienceRetry", "ThrowIfCancellationRequested"])
    require_text(ROOT / "src/TCJ.Core/Resilience/TcjTimeoutPolicy.cs", [
        "CancellationTokenSource", "TimeProvider", "TcjTimeoutException", "cancellationToken.IsCancellationRequested"])
    require_text(ROOT / "src/TCJ.Core/Resilience/TcjCircuitBreaker.cs", [
        "TcjCircuitState.Closed", "TcjCircuitState.Open", "TcjCircuitState.HalfOpen", "_halfOpenProbeActive"])
    require_text(ROOT / "src/TCJ.DependencyInjection/DomainEvents/TcjDomainEventResilienceOptions.cs", [
        "RetryTransientHandlerFailures", "Retries are disabled by default", "idempotent"])
    require_text(ROOT / "src/TCJ.DependencyInjection/DomainEvents/DomainEventHandlerInvoker.cs", [
        "domain_event_handler", "RetryTransientHandlerFailures", "handler.HandleAsync"])
    require_text(ROOT / "src/TCJ.EntityFrameworkCore.SqlServer/Extensions/SqlServerResilienceExtensions.cs", [
        "CreateExecutionStrategy", "CreateAsyncScope", "BeginTransactionAsync", "CommitAsync",
        "sqlserver_transaction", "retryToken", "cancellationToken"])
    require_text(ROOT / "src/TCJ.EntityFrameworkCore.SqlServer/Options/TcjSqlServerOptions.cs", [
        "MaxRetryCount is < 1 or > 10", "MaxRetryDelay > TimeSpan.FromSeconds(30)"])
    require_text(ROOT / "src/TCJ.Core/Diagnostics/ResilienceTelemetryDiagnostics.cs", [
        "ResilienceAttempts", "ResilienceRetries", "ResilienceTimeouts", "ResilienceCircuitOpen", "ResilienceFailures", '=> "custom"'])

    names = require_text(ROOT / "src/TCJ.Core/Diagnostics/TcjDiagnosticNames.cs", [])
    for name in contract.get("activities", []):
        if not isinstance(name, str) or name not in names:
            fail(f"Resilience activity is missing from diagnostic constants: {name!r}")
    for metric in contract.get("metrics", []):
        if not isinstance(metric, dict) or not isinstance(metric.get("name"), str) or metric["name"] not in names:
            fail(f"Resilience metric is missing from diagnostic constants: {metric!r}")

    allowed = set(require_unique_strings(policy.get("allowedTelemetryDimensions"), "allowedTelemetryDimensions"))
    require_unique_strings(policy.get("forbiddenSensitivePatterns"), "forbiddenSensitivePatterns")
    contract_dims = set(require_unique_strings(contract.get("telemetryDimensions"), "telemetryDimensions"))
    if contract_dims != allowed:
        fail("Contract telemetry dimensions must exactly match the resilience policy allowlist.")


def validate_tests(policy: dict[str, Any]) -> None:
    minimum = policy.get("minimumScenarioCount")
    if not isinstance(minimum, int) or minimum < 18:
        fail("minimumScenarioCount must be at least 18.")
    actual = count_scenarios()
    if actual < minimum:
        fail(f"Resilience tests define {actual} scenarios; policy requires at least {minimum}.")

    required = set(require_unique_strings(policy.get("requiredCategories"), "requiredCategories"))
    missing = sorted(required - test_categories())
    if missing:
        fail("Resilience tests are missing required categories: " + ", ".join(missing))

    test_text = "\n".join(path.read_text(encoding="utf-8") for path in TEST_ROOT.rglob("*.cs"))
    for fragment in (
        "DeterministicFaultInjector", "FakeTimeProvider", "FailFirst", "cancellationAttempts", "History", "CancellationTokenSource",
        "CircuitBreaker_concurrent_half_open_allows_only_one_probe", "SqlServer_transient_execution_strategy",
        "SqlServer_permanent_failure", "DomainEvents_opt_in_retries_only_failing_handler",
        "durableEffects", "MeterListener", "ActivityListener"):
        if fragment not in test_text:
            fail(f"Resilience tests are missing required scenario marker: {fragment}")


def validate_automation_and_docs() -> None:
    solution = require_text(ROOT / "TCJ.slnx", ["tests/TCJ.Resilience.Tests/TCJ.Resilience.Tests.csproj"])
    if solution.count("tests/TCJ.Resilience.Tests/TCJ.Resilience.Tests.csproj") != 1:
        fail("TCJ.slnx must include the resilience test project exactly once.")

    require_text(ROOT / ".github/workflows/ci.yml", [
        "verify-resilience.py validate-config", "TCJ.Resilience.Tests.csproj", "TestResults/Resilience"])
    dedicated = require_text(ROOT / ".github/workflows/resilience.yml", [
        "workflow_call:", "schedule:", "Category=SqlServer", "RESILIENCE_SUMMARY.md",
        "artifacts/resilience/traces", "actions/upload-artifact"])
    if "paths:" not in dedicated:
        fail("Dedicated resilience workflow must use relevant push path triggers.")
    require_text(ROOT / ".github/workflows/required-pr-gate.yml", [
        "pull_request:", "uses: ./.github/workflows/resilience.yml", "name: Required PR Gate"])
    require_text(ROOT / ".github/workflows/release-preflight.yml", ["resilience", "./.github/workflows/resilience.yml"])
    require_text(ROOT / ".github/workflows/release.yml", ["resilience", "./.github/workflows/resilience.yml"])
    require_text(ROOT / ".github/workflows/published-package-smoke.yml", ["resilience", "TCJ_RESILIENCE_SMOKE"])
    require_text(ROOT / ".github/workflows/performance-benchmarks.yml", ["eng/resilience-policy.json", "eng/resilience-contract.json"])
    require_text(ROOT / ".github/workflows/sqlserver-integration.yml", [
        "verify-resilience.py validate-config", "Category=SqlServer", "TCJ.Resilience.Tests.csproj"])
    require_text(ROOT / ".github/workflows/concurrency-stress.yml", [
        "verify-resilience.py validate-config", "Category=Concurrency&Category!=SqlServer", "TCJ.Resilience.Tests.csproj"])
    require_text(ROOT / "benchmarks/TCJ.Benchmarks/Benchmarks/ResilienceBenchmarks.cs", [
        "NoPolicy", "PolicyConfiguredNoFailure", "OneRetry", "RetryExhaustion", "TimeoutSetup",
        "CircuitBreakerClosed", "CircuitBreakerOpenFastFail", "MemoryDiagnoser"])
    require_text(ROOT / "docs/resilience.md", [
        "Operation-level retry", "Transaction-level retry", "Domain-event", "idempotency", "fault injection",
        "Circuit breaker", "caller cancellation", "SQL Server"])
    require_text(ROOT / ".gitignore", ["artifacts/resilience/"])
    require_text(ROOT / ".github/PULL_REQUEST_TEMPLATE.md", ["## Resilience", "Resilience tests pass"])

    if (ROOT / ".git").exists():
        deleted = subprocess.run(
            ["git", "diff", "--diff-filter=D", "--name-only", "HEAD", "--", "src"],
            cwd=ROOT, check=False, capture_output=True, text=True)
        if deleted.returncode != 0:
            fail("Unable to inspect deleted production files: " + deleted.stderr.strip())
        if deleted.stdout.strip():
            fail("Step 42 must not delete production files without independent justification: " +
                 ", ".join(line for line in deleted.stdout.splitlines() if line.strip()))


def validate_configuration() -> tuple[dict[str, Any], dict[str, Any]]:
    policy = read_json(POLICY_PATH)
    contract = read_json(CONTRACT_PATH)
    require_schema_one(policy, "Resilience policy")
    require_schema_one(contract, "Resilience contract")
    tracked_and_not_ignored(POLICY_PATH)
    tracked_and_not_ignored(CONTRACT_PATH)
    tracked_and_not_ignored(TEST_PROJECT)
    validate_bounds(policy, contract)
    validate_implementation(policy, contract)
    validate_tests(policy)
    validate_automation_and_docs()
    return policy, contract


def parse_trx_results(results: Path) -> tuple[int, int, int]:
    files = sorted(results.rglob("*.trx")) if results.is_dir() else []
    if not files:
        fail(f"No TRX files found under {results}.")
    total = passed = failed = 0
    for path in files:
        root = ET.parse(path).getroot()
        counters = next((element for element in root.iter() if element.tag.endswith("Counters")), None)
        if counters is None:
            fail(f"TRX file {path.name} has no Counters element.")
        total += int(counters.attrib.get("executed", counters.attrib.get("total", "0")))
        passed += int(counters.attrib.get("passed", "0"))
        failed += int(counters.attrib.get("failed", "0")) + int(counters.attrib.get("error", "0"))
    if total <= 0:
        fail("Resilience TRX files contain no executed tests.")
    if failed:
        fail(f"Resilience test evidence contains {failed} failed/error tests.")
    if passed != total:
        fail(f"Resilience evidence is incomplete: executed={total}, passed={passed}.")
    return total, passed, failed


def scan_traces(traces: Path, forbidden_patterns: tuple[str, ...] = ()) -> dict[str, Any]:
    files = sorted(traces.rglob("*.json")) if traces.is_dir() else []
    if not files:
        fail(f"No deterministic resilience traces found under {traces}.")
    forbidden = ("TCJ_TEST_PASSWORD_MARKER", "TCJ_TEST_TOKEN_MARKER", "TCJ_TEST_CONNECTION_STRING_MARKER")
    leaks: list[str] = []
    for path in files:
        text = path.read_text(encoding="utf-8", errors="replace")
        lowered = text.lower()
        if any(marker in text for marker in forbidden) or any(
            pattern.lower() in lowered for pattern in forbidden_patterns
        ):
            leaks.append(path.as_posix())
        try:
            json.loads(text)
        except json.JSONDecodeError as error:
            fail(f"Malformed resilience trace {path}: {error}")
    if leaks:
        fail("Sensitive test markers leaked into resilience traces: " + ", ".join(leaks))
    return {"count": len(files), "files": [path.name for path in files]}


def write_report(output: Path, executed: int, traces: dict[str, Any], policy: dict[str, Any]) -> None:
    output.mkdir(parents=True, exist_ok=True)
    summary = {
        "schemaVersion": 1,
        "status": "pass",
        "executedTests": executed,
        "minimumScenarioCount": policy["minimumScenarioCount"],
        "traceCount": traces["count"],
        "requiredCategories": policy["requiredCategories"],
    }
    (output / "resilience-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    lines = [
        "# Resilience verification", "", "Status: **PASS**", "",
        f"- Executed tests: **{executed}**",
        f"- Minimum source scenarios: **{policy['minimumScenarioCount']}**",
        f"- Deterministic traces: **{traces['count']}**",
        f"- Categories: {', '.join(policy['requiredCategories'])}", "",
        "The verifier confirmed bounded policy configuration, required deterministic scenarios, telemetry contracts, workflow/release enforcement, and clean trace evidence.", ""
    ]
    (output / "RESILIENCE_SUMMARY.md").write_text("\n".join(lines), encoding="utf-8")
    (output / "attempt-traces.json").write_text(json.dumps(traces, indent=2) + "\n", encoding="utf-8")


def verify(results: Path, traces: Path, output: Path) -> None:
    policy, _ = validate_configuration()
    executed, _, _ = parse_trx_results(results)
    if executed < policy["minimumScenarioCount"]:
        fail(f"Executed resilience scenarios ({executed}) are below policy minimum ({policy['minimumScenarioCount']}).")
    trace_summary = scan_traces(
        traces,
        tuple(require_unique_strings(policy.get("forbiddenSensitivePatterns"), "forbiddenSensitivePatterns")),
    )
    write_report(output, executed, trace_summary, policy)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("validate-config")
    verify_parser = sub.add_parser("verify")
    verify_parser.add_argument("--results", type=Path, required=True)
    verify_parser.add_argument("--traces", type=Path, required=True)
    verify_parser.add_argument("--output", type=Path, required=True)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        if args.command == "validate-config":
            validate_configuration()
            print("Resilience configuration validation passed.")
        else:
            verify(args.results, args.traces, args.output)
            print("Resilience verification passed.")
        return 0
    except (ResilienceError, OSError, ET.ParseError) as error:
        print(f"Resilience verification failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
