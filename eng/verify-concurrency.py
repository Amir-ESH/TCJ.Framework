#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
POLICY_PATH = ROOT / "eng/concurrency-policy.json"
EXPECTED_PROJECT = "tests/TCJ.Concurrency.Tests/TCJ.Concurrency.Tests.csproj"
EXPECTED_WORKFLOW = ROOT / ".github/workflows/concurrency-stress.yml"
ALLOWED_GROUPS = {"core", "aspnetcore", "sqlserver"}
REQUIRED_TRACE_FIELDS = {
    "schemaVersion", "scenario", "group", "status", "seed", "workers", "iterations",
    "operationTimeoutMilliseconds", "scenarioTimeoutSeconds", "operatingSystem", "architecture",
    "runtime", "commitSha", "startedAtUtc", "completedAtUtc", "expectedOperations",
    "completedOperations", "duplicateOperations", "missingOperations", "canceledOperations",
    "deadlockDetected", "timeoutDetected", "scopeLeakage", "identityLeakage",
    "transactionInterference", "exceptions", "timeline", "replay"
}


class VerificationError(RuntimeError):
    pass


def fail(message: str) -> None:
    raise VerificationError(message)


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def load_json(path: Path, description: str) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        fail(f"Missing {description}: {path.relative_to(ROOT) if path.is_absolute() and ROOT in path.parents else path}")
    except json.JSONDecodeError as error:
        fail(f"Malformed {description}: {error}")
    if not isinstance(value, dict):
        fail(f"{description} must contain a JSON object.")
    return value


def require_positive_int(data: dict[str, Any], key: str) -> int:
    value = data.get(key)
    require(isinstance(value, int) and not isinstance(value, bool) and value > 0,
            f"Policy property '{key}' must be a positive integer.")
    return int(value)


def require_string_list(data: dict[str, Any], key: str) -> list[str]:
    value = data.get(key)
    require(isinstance(value, list) and value, f"Policy property '{key}' must be a non-empty array.")
    require(all(isinstance(item, str) and item.strip() for item in value),
            f"Policy property '{key}' must contain non-empty strings.")
    normalized = [item.strip() for item in value]
    require(len(set(normalized)) == len(normalized), f"Policy property '{key}' must not contain duplicates.")
    return normalized


def validate_policy_data(policy: dict[str, Any]) -> list[dict[str, Any]]:
    require(policy.get("schemaVersion") == 1, "Concurrency policy schemaVersion must be 1.")
    require(policy.get("testProject") == EXPECTED_PROJECT, f"Concurrency policy testProject must be '{EXPECTED_PROJECT}'.")
    for key in (
        "minimumScenarioCount", "pullRequestWorkers", "pullRequestIterations", "scheduledWorkers",
        "scheduledIterations", "sqlServerPullRequestWorkers", "sqlServerPullRequestIterations",
        "sqlServerScheduledWorkers", "sqlServerScheduledIterations", "operationTimeoutMilliseconds",
        "maximumScenarioSeconds", "maximumScheduledScenarioSeconds"
    ):
        require_positive_int(policy, key)

    require(policy.get("requireDeterministicSeed") is True, "Deterministic concurrency seeds must be required.")
    require(policy.get("requireDeadlockDetection") is True, "Deadlock detection must be required.")
    require(policy.get("requireFailureTrace") is True, "Failure traces must be required.")

    pull_seeds = policy.get("pullRequestSeeds")
    scheduled_seeds = policy.get("scheduledSeeds")
    require(isinstance(pull_seeds, list) and pull_seeds and all(isinstance(seed, int) for seed in pull_seeds),
            "pullRequestSeeds must contain deterministic integer seeds.")
    require(isinstance(scheduled_seeds, list) and len(scheduled_seeds) >= 3 and all(isinstance(seed, int) for seed in scheduled_seeds),
            "scheduledSeeds must contain at least three deterministic integer seeds.")
    require(len(set(pull_seeds)) == len(pull_seeds), "pullRequestSeeds must be unique.")
    require(len(set(scheduled_seeds)) == len(scheduled_seeds), "scheduledSeeds must be unique.")

    categories = require_string_list(policy, "requiredCategories")
    required_categories = {
        "Concurrency", "Stress", "DependencyInjection", "DomainEvents", "RequestScope",
        "CurrentUser", "Cancellation", "Transactions", "SqlServer", "ScheduledStress"
    }
    require(required_categories <= set(categories),
            f"Concurrency policy is missing required categories: {sorted(required_categories - set(categories))}")

    image = policy.get("sqlServerContainerImage")
    require(isinstance(image, str) and image and ":" in image and not image.endswith(":latest"),
            "sqlServerContainerImage must be explicitly pinned.")

    scenarios = policy.get("scenarios")
    require(isinstance(scenarios, list), "Concurrency policy scenarios must be an array.")
    require(len(scenarios) >= int(policy["minimumScenarioCount"]),
            f"Concurrency scenario count {len(scenarios)} is below policy minimum {policy['minimumScenarioCount']}.")
    names: set[str] = set()
    covered_categories: set[str] = set()
    for scenario in scenarios:
        require(isinstance(scenario, dict), "Each concurrency scenario must be an object.")
        name = scenario.get("name")
        group = scenario.get("group")
        scenario_categories = scenario.get("categories")
        require(isinstance(name, str) and re.fullmatch(r"[A-Za-z][A-Za-z0-9]+", name) is not None,
                "Concurrency scenario names must be non-empty method identifiers.")
        require(name not in names, f"Duplicate concurrency scenario name: {name}")
        names.add(name)
        require(group in ALLOWED_GROUPS, f"Concurrency scenario '{name}' has unsupported group '{group}'.")
        require(isinstance(scenario_categories, list) and scenario_categories,
                f"Concurrency scenario '{name}' must declare categories.")
        covered_categories.update(str(item) for item in scenario_categories)

    contract_categories = set(categories) - {"Concurrency", "Stress"}
    require(contract_categories <= covered_categories,
            f"Scenario inventory does not cover required categories: {sorted(contract_categories - covered_categories)}")
    return scenarios


def read_xml(path: Path, description: str) -> ET.Element:
    try:
        return ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        fail(f"Unable to parse {description} at {path}: {error}")


def validate_project_dependencies(project: ET.Element) -> None:
    packages = {item.attrib.get("Include", "") for item in project.findall(".//PackageReference")}
    for package in ("Microsoft.AspNetCore.TestHost", "Microsoft.EntityFrameworkCore.InMemory", "Testcontainers.MsSql"):
        require(package in packages, f"Concurrency test project must reference {package}.")

    frameworks = {item.attrib.get("Include", "") for item in project.findall(".//FrameworkReference")}
    require("Microsoft.AspNetCore.App" in frameworks,
            "Concurrency test project must reference Microsoft.AspNetCore.App.")
    require("Microsoft.Extensions.DependencyInjection" not in packages,
            "Concurrency test project must not directly reference Microsoft.Extensions.DependencyInjection when Microsoft.AspNetCore.App provides it.")


def validate_project(policy: dict[str, Any]) -> None:
    project_path = ROOT / policy["testProject"]
    require(project_path.is_file(), f"Missing concurrency test project: {policy['testProject']}")
    project = read_xml(project_path, "concurrency test project")
    require(project.findtext("./PropertyGroup/TargetFramework") == "net10.0",
            "Concurrency test project must explicitly target net10.0.")
    validate_project_dependencies(project)
    refs = {item.attrib.get("Include", "").replace("\\", "/") for item in project.findall(".//ProjectReference")}
    expected_refs = {
        "../../src/TCJ.Core/TCJ.Core.csproj",
        "../../src/TCJ.DependencyInjection/TCJ.DependencyInjection.csproj",
        "../../src/TCJ.EntityFrameworkCore/TCJ.EntityFrameworkCore.csproj",
        "../../src/TCJ.EntityFrameworkCore.SqlServer/TCJ.EntityFrameworkCore.SqlServer.csproj",
        "../../src/TCJ.AspNetCore/TCJ.AspNetCore.csproj",
    }
    require(expected_refs <= refs, f"Concurrency test project is missing production project references: {sorted(expected_refs - refs)}")
    solution = (ROOT / "TCJ.slnx").read_text(encoding="utf-8")
    require(policy["testProject"] in solution, "TCJ.slnx must include the concurrency stress-test project.")


def validate_sources(policy: dict[str, Any], scenarios: list[dict[str, Any]]) -> None:
    root = ROOT / "tests/TCJ.Concurrency.Tests"
    sources = sorted(root.rglob("*.cs"))
    require(sources, "Concurrency test sources are missing.")
    text = "\n".join(path.read_text(encoding="utf-8") for path in sources)
    for scenario in scenarios:
        marker = rf"\[Fact\][\s\S]{{0,300}}\b{re.escape(scenario['name'])}\s*\("
        require(re.search(marker, text) is not None, f"Missing [Fact] implementation for concurrency scenario: {scenario['name']}")
    for category in policy["requiredCategories"]:
        require(f'[Trait("Category", "{category}")]' in text,
                f"Concurrency test sources are missing required category trait: {category}")

    runner = (root / "Infrastructure/StressRunner.cs").read_text(encoding="utf-8")
    required_runner_markers = [
        "CountdownEvent", "TaskCompletionSource", "WaitAsync", "Task.WhenAny", "Random(workerSeed)",
        "DuplicateOperations", "MissingOperations", "DeadlockDetected", "TimeoutDetected",
        "settings.FailureDirectory", "JsonNamingPolicy.CamelCase", "ReplayMetadata"
    ]
    missing = [marker for marker in required_runner_markers if marker not in runner]
    require(not missing, f"StressRunner is missing required orchestration markers: {missing}")
    settings = (root / "Infrastructure/StressSettings.cs").read_text(encoding="utf-8")
    for marker in ("GITHUB_SHA", "TCJ_STRESS_WORKERS", "TCJ_STRESS_ITERATIONS", "TCJ_STRESS_OPERATION_TIMEOUT_MS", "TCJ_STRESS_SCENARIO_TIMEOUT_SECONDS"):
        require(marker in settings, f"StressSettings is missing replay/configuration marker: {marker}")

    replay = root / "scripts/replay-stress.py"
    require(replay.is_file(), "Concurrency replay script is missing.")
    replay_text = replay.read_text(encoding="utf-8")
    for marker in ("--scenario", "--seed", "TCJ_STRESS_SEED", "FullyQualifiedName~"):
        require(marker in replay_text, f"Concurrency replay script is missing marker: {marker}")

    registration = (ROOT / "src/TCJ.DependencyInjection/Extensions/ServiceCollectionExtensions.cs").read_text(encoding="utf-8")
    require("lock (services)" in registration,
            "TCJ dependency registration must serialize its own mutations for concurrent calls on one IServiceCollection.")


def validate_sql_contract(policy: dict[str, Any]) -> None:
    sql_policy = load_json(ROOT / "eng/sqlserver-integration-policy.json", "SQL Server integration policy")
    require(policy["sqlServerContainerImage"] == sql_policy.get("containerImage"),
            "Concurrency stress tests must use the pinned SQL Server image from Step 35.")
    fixture = (ROOT / "tests/TCJ.Concurrency.Tests/Fixtures/SqlServerStressFixture.cs").read_text(encoding="utf-8")
    for marker in ("MsSqlBuilder", "WithCleanUp(true)", "EnsureCreatedAsync", "CreateDatabaseAsync", "DropDatabaseAsync"):
        require(marker in fixture, f"SQL Server concurrency fixture is missing marker: {marker}")


def validate_documentation() -> None:
    doc = ROOT / "docs/concurrency-stress-testing.md"
    require(doc.is_file(), "Concurrency and thread-safety documentation is missing.")
    text = doc.read_text(encoding="utf-8")
    for marker in (
        "Thread-safe", "Thread-compatible", "Request-scoped only", "Single-operation only",
        "Not safe for concurrent use", "DbContext is not thread-safe", "replay-stress.py",
        "AddTcjDependencyInjection", "DomainEventDispatcher", "HttpContextCurrentUserProvider"
    ):
        require(marker in text, f"Concurrency documentation is missing contract marker: {marker}")
    toc = (ROOT / "docs/toc.yml").read_text(encoding="utf-8")
    require("concurrency-stress-testing.md" in toc, "Concurrency documentation must be linked from docs/toc.yml.")


def validate_workflows() -> None:
    require(EXPECTED_WORKFLOW.is_file(), "Dedicated concurrency stress workflow is missing.")
    workflow = EXPECTED_WORKFLOW.read_text(encoding="utf-8")
    markers = [
        "name: Concurrency stress", "push:", "workflow_dispatch:", "workflow_call:", "schedule:",
        "Run core stress tests", "Run ASP.NET Core stress tests", "Run SQL Server stress tests",
        "verify-concurrency.py validate-config", "verify-concurrency.py verify", "GITHUB_STEP_SUMMARY",
        "actions/upload-artifact", "TCJ_STRESS_SEED", "scheduledSeeds", "cancel-in-progress:",
        "Testcontainers.MsSql", "sanitized-sqlserver.log"
    ]
    missing = [marker for marker in markers if marker not in workflow]
    require(not missing, f"Concurrency workflow is missing required markers: {missing}")
    require("retry" not in workflow.lower(), "Concurrency workflow must not hide failures with automatic retries.")

    gate = (ROOT / ".github/workflows/required-pr-gate.yml").read_text(encoding="utf-8")
    require("pull_request:" in gate and "uses: ./.github/workflows/concurrency-stress.yml" in gate,
            "Required PR Gate must route relevant pull requests through concurrency stress validation.")

    ci = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
    require("verify-concurrency.py validate-config" in ci, "Normal CI must validate concurrency configuration.")
    require("Category!=Concurrency" in ci, "Normal coverage test run must exclude the dedicated stress suite.")

    for relative in (".github/workflows/release-preflight.yml", ".github/workflows/release.yml"):
        text = (ROOT / relative).read_text(encoding="utf-8")
        require("concurrency-stress.yml" in text and "concurrency-stress" in text,
                f"{relative} must enforce the commit-matched concurrency stress workflow.")

    template = (ROOT / ".github/PULL_REQUEST_TEMPLATE.md").read_text(encoding="utf-8")
    for item in (
        "Concurrency stress tests pass", "Deterministic stress seeds are replayable",
        "Thread-safety contracts are documented", "Generated concurrency traces are not committed"
    ):
        require(item in template, f"Pull request template is missing concurrency checklist item: {item}")


def check_git(policy: dict[str, Any], skip_git: bool) -> None:
    if skip_git or not (ROOT / ".git").exists():
        return
    required = [
        POLICY_PATH,
        ROOT / policy["testProject"],
        ROOT / "eng/verify-concurrency.py",
        EXPECTED_WORKFLOW,
        ROOT / "docs/concurrency-stress-testing.md",
    ]
    for path in required:
        check = subprocess.run(["git", "check-ignore", "-q", str(path.relative_to(ROOT))], cwd=ROOT, check=False)
        require(check.returncode != 0, f"Required concurrency file is ignored by Git: {path.relative_to(ROOT)}")
    generated = subprocess.run(
        ["git", "ls-files", "TestResults/Concurrency", "artifacts/concurrency"],
        cwd=ROOT, text=True, capture_output=True, check=False)
    require(not generated.stdout.strip(), "Generated concurrency output must not be tracked by Git.")


def validate_config(skip_git: bool = False) -> None:
    policy = load_json(POLICY_PATH, "concurrency policy")
    scenarios = validate_policy_data(policy)
    validate_project(policy)
    validate_sources(policy, scenarios)
    validate_sql_contract(policy)
    validate_documentation()
    validate_workflows()
    check_git(policy, skip_git)
    groups = {group: sum(1 for scenario in scenarios if scenario["group"] == group) for group in sorted(ALLOWED_GROUPS)}
    print(
        "Concurrency configuration is valid: "
        f"scenarios={len(scenarios)}, core={groups['core']}, aspnetcore={groups['aspnetcore']}, "
        f"sqlserver={groups['sqlserver']}, pr={policy['pullRequestWorkers']}x{policy['pullRequestIterations']}, "
        f"scheduled={policy['scheduledWorkers']}x{policy['scheduledIterations']}."
    )


def parse_trx(directory: Path) -> dict[str, list[str]]:
    files = sorted(directory.rglob("*.trx"))
    require(files, f"No TRX files found under {directory}.")
    results: dict[str, list[str]] = {}
    for file in files:
        root = ET.parse(file).getroot()
        for node in root.iter():
            if not node.tag.endswith("UnitTestResult"):
                continue
            name = node.attrib.get("testName", "")
            outcome = node.attrib.get("outcome", "")
            results.setdefault(name, []).append(outcome)
    return results


def validate_trace_data(trace: dict[str, Any], scenario: str, group: str) -> int:
    missing_fields = sorted(REQUIRED_TRACE_FIELDS - set(trace))
    require(not missing_fields, f"Trace for {scenario} is missing required fields: {missing_fields}")
    require(trace.get("schemaVersion") == 1, f"Trace for {scenario} has unsupported schemaVersion.")
    require(trace.get("scenario") == scenario, f"Trace scenario identity mismatch for {scenario}.")
    require(trace.get("group") == group, f"Trace group mismatch for {scenario}.")
    require(trace.get("status") == "Pass", f"Concurrency scenario did not pass: {scenario}")
    require(isinstance(trace.get("seed"), int), f"Trace for {scenario} is missing deterministic seed.")
    require(isinstance(trace.get("workers"), int) and trace["workers"] > 0, f"Trace for {scenario} has invalid workers.")
    require(isinstance(trace.get("iterations"), int) and trace["iterations"] > 0, f"Trace for {scenario} has invalid iterations.")
    require(trace.get("expectedOperations") == trace["workers"] * trace["iterations"],
            f"Trace expected operation count is invalid for {scenario}.")
    require(trace.get("completedOperations") == trace.get("expectedOperations"),
            f"Missing operations detected for {scenario}.")
    require(trace.get("duplicateOperations") == 0, f"Duplicate operations detected for {scenario}.")
    require(trace.get("missingOperations") == 0, f"Missing-operation violation detected for {scenario}.")
    require(trace.get("deadlockDetected") is False, f"Deadlock detected for {scenario}.")
    require(trace.get("timeoutDetected") is False, f"Timeout detected for {scenario}.")
    require(trace.get("scopeLeakage") == 0, f"Scope leakage detected for {scenario}.")
    require(trace.get("identityLeakage") == 0, f"Identity leakage detected for {scenario}.")
    require(trace.get("transactionInterference") == 0, f"Transaction interference detected for {scenario}.")
    require(trace.get("exceptions") == [], f"Unresolved stress exceptions detected for {scenario}.")
    replay = trace.get("replay")
    require(isinstance(replay, dict), f"Trace for {scenario} is missing replay metadata.")
    require(replay.get("scenario") == scenario and replay.get("seed") == trace.get("seed"),
            f"Replay metadata does not match trace identity for {scenario}.")
    require(replay.get("workers") == trace.get("workers") and replay.get("iterations") == trace.get("iterations"),
            f"Replay metadata does not preserve workers/iterations for {scenario}.")
    command = replay.get("command")
    require(isinstance(command, str) and scenario in command and str(trace["seed"]) in command,
            f"Trace for {scenario} is missing a usable replay command.")
    return int(trace["seed"])


def write_summary(output: Path, group: str, scenario_count: int, seeds: set[int], trace_count: int, trx_count: int) -> None:
    output.mkdir(parents=True, exist_ok=True)
    data = {
        "schemaVersion": 1,
        "group": group,
        "status": "Pass",
        "scenarioCount": scenario_count,
        "seedCount": len(seeds),
        "seeds": sorted(seeds),
        "traceCount": trace_count,
        "trxResultCount": trx_count,
    }
    (output / "concurrency-summary.json").write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    lines = [
        "# Concurrency stress", "", f"- Status: **PASS**", f"- Group: `{group}`",
        f"- Required scenarios: {scenario_count}", f"- Deterministic seeds: {', '.join(map(str, sorted(seeds)))}",
        f"- Verified traces: {trace_count}", f"- TRX results: {trx_count}", "",
        "No deadlocks, timeouts, duplicate/missing operations, scope leakage, identity leakage, or transaction interference were reported."
    ]
    (output / "CONCURRENCY_SUMMARY.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def verify(results: Path, traces: Path, failures: Path, output: Path, group: str, minimum_seeds: int) -> None:
    require(group in ALLOWED_GROUPS, f"Unsupported concurrency group: {group}")
    policy = load_json(POLICY_PATH, "concurrency policy")
    scenarios = [item for item in validate_policy_data(policy) if item["group"] == group]
    required_names = {item["name"] for item in scenarios}
    trx = parse_trx(results)

    matched: dict[str, list[str]] = {name: [] for name in required_names}
    for test_name, outcomes in trx.items():
        for scenario in required_names:
            if test_name == scenario or test_name.endswith("." + scenario) or scenario in test_name:
                matched[scenario].extend(outcomes)
    for scenario, outcomes in matched.items():
        require(outcomes, f"Critical concurrency scenario was skipped or missing from TRX results: {scenario}")
        require(all(outcome.lower() == "passed" for outcome in outcomes),
                f"Concurrency scenario has a failed/skipped result: {scenario}: {outcomes}")

    if failures.exists():
        unresolved = sorted(path for path in failures.rglob("*.json") if path.is_file())
        require(not unresolved, f"Unresolved concurrency failure traces exist: {[path.name for path in unresolved]}")

    seed_sets: dict[str, set[int]] = {name: set() for name in required_names}
    trace_count = 0
    for path in sorted(traces.rglob("*.json")):
        trace = load_json(path, "concurrency trace")
        scenario = trace.get("scenario")
        if scenario not in required_names:
            continue
        seed_sets[scenario].add(validate_trace_data(trace, str(scenario), group))
        trace_count += 1

    for scenario, seeds in seed_sets.items():
        require(len(seeds) >= minimum_seeds,
                f"Concurrency scenario '{scenario}' has {len(seeds)} deterministic seed trace(s); expected at least {minimum_seeds}.")
    all_seeds = set().union(*seed_sets.values()) if seed_sets else set()
    write_summary(output, group, len(required_names), all_seeds, trace_count, sum(len(values) for values in trx.values()))
    print(f"Concurrency verification passed: group={group}, scenarios={len(required_names)}, seeds={len(all_seeds)}, traces={trace_count}.")


def main() -> int:
    parser = argparse.ArgumentParser(description="Validate TCJ concurrency stress-test policy and results.")
    sub = parser.add_subparsers(dest="command", required=True)
    validate = sub.add_parser("validate-config")
    validate.add_argument("--skip-git", action="store_true")
    verify_parser = sub.add_parser("verify")
    verify_parser.add_argument("--results", type=Path, required=True)
    verify_parser.add_argument("--traces", type=Path, required=True)
    verify_parser.add_argument("--failures", type=Path, default=Path("artifacts/concurrency/failures"))
    verify_parser.add_argument("--output", type=Path, required=True)
    verify_parser.add_argument("--group", choices=sorted(ALLOWED_GROUPS), required=True)
    verify_parser.add_argument("--minimum-seeds", type=int, default=1)
    args = parser.parse_args()
    try:
        if args.command == "validate-config":
            validate_config(args.skip_git)
        else:
            require(args.minimum_seeds > 0, "--minimum-seeds must be greater than zero.")
            verify(args.results, args.traces, args.failures, args.output, args.group, args.minimum_seeds)
        return 0
    except VerificationError as error:
        print(f"Concurrency verification failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
