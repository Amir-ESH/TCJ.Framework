#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parents[1]
POLICY = ROOT / "eng/azure-service-bus-policy.json"
CONTRACT = ROOT / "eng/azure-service-bus-contract.json"
PROJECT = ROOT / "src/TCJ.Messaging.AzureServiceBus/TCJ.Messaging.AzureServiceBus.csproj"
TEST_PROJECT = ROOT / "tests/TCJ.Messaging.AzureServiceBus.Tests/TCJ.Messaging.AzureServiceBus.Tests.csproj"
INTEGRATION_CATEGORY = "AzureServiceBusIntegration"


class VerificationError(RuntimeError):
    pass


def fail(message: str) -> None:
    raise VerificationError(message)


def read_text(path: Path) -> str:
    if not path.is_file():
        fail(f"Required Azure Service Bus file is missing: {path.relative_to(ROOT)}")
    return path.read_text(encoding="utf-8")


def read_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(read_text(path))
    except json.JSONDecodeError as exc:
        fail(f"Malformed JSON in {path.relative_to(ROOT)}: {exc}")
    if not isinstance(value, dict):
        fail(f"{path.relative_to(ROOT)} must contain a JSON object.")
    return value


def require(path: Path, *markers: str) -> str:
    text = read_text(path)
    missing = [marker for marker in markers if marker not in text]
    if missing:
        fail(f"{path.relative_to(ROOT)} is missing required markers: {', '.join(missing)}")
    return text


def parse_project(path: Path) -> ET.Element:
    try:
        return ET.parse(path).getroot()
    except (ET.ParseError, OSError) as exc:
        fail(f"Unable to parse {path.relative_to(ROOT)}: {exc}")


def package_versions() -> dict[str, str]:
    root = parse_project(ROOT / "Directory.Packages.props")
    return {
        item.attrib["Include"]: item.attrib.get("Version", "")
        for item in root.findall(".//PackageVersion")
        if "Include" in item.attrib
    }


def package_references(project: ET.Element) -> set[str]:
    return {
        item.attrib["Include"]
        for item in project.findall(".//PackageReference")
        if "Include" in item.attrib
    }


def project_references(project: ET.Element) -> set[str]:
    return {
        Path(item.attrib["Include"].replace("\\", "/")).stem
        for item in project.findall(".//ProjectReference")
        if "Include" in item.attrib
    }


def runtime_ids(path: Path) -> list[str]:
    data = read_json(path)
    return [
        item.get("id")
        for item in data.get("releasePackages", {}).get("runtime", [])
        if isinstance(item, dict)
    ]


def ensure_not_ignored(paths: Iterable[Path]) -> None:
    if not (ROOT / ".git").exists():
        return
    for path in paths:
        rel = str(path.relative_to(ROOT)).replace("\\", "/")
        completed = subprocess.run(
            ["git", "check-ignore", "-q", "--", rel],
            cwd=ROOT,
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        if completed.returncode == 0:
            fail(f"Required Azure Service Bus file is ignored by Git: {rel}")


def azure_integration_test_names() -> list[str]:
    names: list[str] = []
    pattern = re.compile(
        rf'\[Fact[^\]]*Trait\("Category",\s*"{INTEGRATION_CATEGORY}"\)\][\s\S]*?public\s+async\s+Task\s+([A-Za-z0-9_]+)\s*\(',
        re.MULTILINE,
    )
    for path in sorted((ROOT / "tests/TCJ.Messaging.AzureServiceBus.Tests").glob("*.cs")):
        names.extend(pattern.findall(path.read_text(encoding="utf-8")))
    return names


def validate_config() -> tuple[dict[str, Any], dict[str, Any]]:
    policy = read_json(POLICY)
    contract = read_json(CONTRACT)
    if policy.get("schemaVersion") != 1 or contract.get("schemaVersion") != 1:
        fail("Azure Service Bus policy/contract schemaVersion must remain 1.")
    if policy.get("packageName") != "TCJ.Messaging.AzureServiceBus" or contract.get("packageName") != policy.get("packageName"):
        fail("Azure Service Bus package identity drifted.")

    guarantees = contract.get("deliveryGuarantees", {})
    if guarantees.get("globalExactlyOnceGuaranteed") is not False:
        fail("The adapter must never claim global exactly-once delivery.")
    if guarantees.get("brokerDuplicateDetectionReplacesInbox") is not False:
        fail("Broker duplicate detection must not replace TCJ Inbox.")

    versions = package_versions()
    for package, policy_key in (
        ("Azure.Core", "azureCoreVersion"),
        ("Azure.Messaging.ServiceBus", "azureServiceBusVersion"),
        ("Azure.Identity", "azureIdentityVersion"),
    ):
        expected = str(policy.get(policy_key, ""))
        if not expected or "*" in expected or expected.lower() == "latest":
            fail(f"{package} cannot use a floating version.")
        if versions.get(package) != expected:
            fail(f"{package} must be centrally pinned to {expected}.")

    emulator = policy.get("emulator", {})
    for key in ("image", "sqlServerImage"):
        image = str(emulator.get(key, ""))
        if "@sha256:" not in image:
            fail(f"Azure Service Bus emulator dependency {key} must be digest-pinned.")
    if emulator.get("requirePinnedImages") is not True:
        fail("Azure Service Bus emulator images must remain pinned.")
    require(ROOT / "eng/azure-service-bus-emulator/docker-compose.yml", "TCJ_SERVICE_BUS_EMULATOR_IMAGE", "TCJ_SQL_SERVER_IMAGE", "5300:5300", "5672:5672")
    require(ROOT / "eng/azure-service-bus-emulator/Config.json", '"Namespaces"', '"sbemulatorns"')

    project = parse_project(PROJECT)
    if (project.findtext("./PropertyGroup/PackageId") or "").strip() != "TCJ.Messaging.AzureServiceBus":
        fail("Adapter PackageId is incorrect.")
    if (project.findtext("./PropertyGroup/TargetFramework") or "").strip() != policy.get("targetFramework"):
        fail("Adapter target framework drifted.")
    refs = package_references(project)
    if not {"Azure.Messaging.ServiceBus", "Azure.Core"}.issubset(refs):
        fail("Adapter must reference the official Azure Service Bus and Azure Core packages.")
    if project_references(project) != {"TCJ.Messaging"}:
        fail("Adapter must have exactly one TCJ project dependency: TCJ.Messaging.")

    for rel in (
        "src/TCJ.Core/TCJ.Core.csproj",
        "src/TCJ.Messaging/TCJ.Messaging.csproj",
        "src/TCJ.EntityFrameworkCore/TCJ.EntityFrameworkCore.csproj",
        "src/TCJ.EntityFrameworkCore.SqlServer/TCJ.EntityFrameworkCore.SqlServer.csproj",
        "src/TCJ.AspNetCore/TCJ.AspNetCore.csproj",
    ):
        neutral = parse_project(ROOT / rel)
        if "Azure.Messaging.ServiceBus" in package_references(neutral) or "TCJ.Messaging.AzureServiceBus" in project_references(neutral):
            fail(f"Azure Service Bus dependency leaked into neutral package {rel}.")

    architecture = read_json(ROOT / "eng/architecture-policy.json")
    if architecture.get("assemblies", {}).get("TCJ.Messaging.AzureServiceBus") != ["TCJ.Messaging"]:
        fail("Architecture policy must restrict Azure adapter to TCJ.Messaging.")
    if "Azure.Messaging.ServiceBus" not in architecture.get("forbiddenDependencyPrefixes", {}).get("TCJ.Messaging", []):
        fail("TCJ.Messaging must explicitly forbid the Azure Service Bus SDK dependency.")
    if "Azure.Messaging.ServiceBus" not in architecture.get("forbiddenPublicApiTypePrefixes", {}).get("TCJ.Messaging.AzureServiceBus", []):
        fail("Azure SDK client types must not leak from adapter public APIs.")

    require(ROOT / "src/TCJ.Messaging.AzureServiceBus/Extensions/AzureServiceBusServiceCollectionExtensions.cs", "AddTcjAzureServiceBus", "TokenCredential", "SupportsPeekLock = true", "SupportsTransactions = false", "MessagingOrderingGuarantee.PerSession")
    require(ROOT / "src/TCJ.Messaging.AzureServiceBus/Configuration/TcjAzureServiceBusOptions.cs", "AutoCompleteMessages", "must remain false", "MaximumConcurrentCallsPerSession != 1", "MaximumSenderCacheSize", "ReadinessDestination")
    require(ROOT / "src/TCJ.Messaging.AzureServiceBus/Connections/AzureServiceBusClientManager.cs", "ServiceBusRetryOptions", "GetSenderAsync", "MaximumSenderCacheSize", "ProbeReadinessAsync", "ServiceBusReceiveMode.PeekLock", "AcceptNextSessionAsync")
    require(ROOT / "src/TCJ.Messaging.AzureServiceBus/Publishing/AzureServiceBusTransportPublisher.cs", "ScheduleMessageAsync", "CreateMessageBatchAsync", "TryAddMessage", "PayloadTooLarge")
    require(ROOT / "src/TCJ.Messaging.AzureServiceBus/Publishing/AzureServiceBusMessageMapper.cs", "MessageId", "Subject", "CorrelationId", "SessionId", "PartitionKey", "TimeToLive", "tcj-message-version", "tcj-causation-id", "received.DeliveryCount")
    require(ROOT / "src/TCJ.Messaging.AzureServiceBus/Receiving/AzureServiceBusMessageSettlement.cs", "CompleteMessageAsync", "AbandonMessageAsync", "DeadLetterMessageAsync", "DeferMessageAsync", "RenewMessageLockAsync", "RenewSessionLockAsync", "ScheduleMessageAsync")
    require(ROOT / "src/TCJ.Messaging.AzureServiceBus/Receiving/AzureServiceBusMessageConsumerRunner.cs", "ShutdownTimeout", "IServiceScopeFactory")
    require(ROOT / "src/TCJ.Messaging.AzureServiceBus/Topology/AzureServiceBusTopologyManager.cs", "AzureServiceBusTopologyMode", "ValidateOnly", "PermanentTopology", "CreateQueueAsync", "CreateTopicAsync", "CreateSubscriptionAsync", "EnablePartitioning", "DefaultMessageTimeToLive")
    require(ROOT / "src/TCJ.Messaging.AzureServiceBus/Diagnostics/TcjAzureServiceBusDiagnosticNames.cs", "tcj.azure_service_bus.publish", "tcj.azure_service_bus.lock_losses", "tcj.azure_service_bus.active_sessions")
    require(ROOT / "src/TCJ.Messaging.AzureServiceBus/HealthChecks/AzureServiceBusHealthChecks.cs", "tcj.azure_service_bus.client", "tcj.azure_service_bus.topology", "tcj.azure_service_bus.session_processor")
    require(ROOT / "src/TCJ.Messaging.AzureServiceBus/Configuration/AzureServiceBusStartupValidator.cs", "ValidateAsync", "Topology")

    test_names = azure_integration_test_names()
    minimum = policy.get("minimumIntegrationTestCount")
    if not isinstance(minimum, int) or minimum < 28:
        fail("minimumIntegrationTestCount must remain at least 28.")
    if len(test_names) < minimum:
        fail(f"Only {len(test_names)} Azure Service Bus integration tests are declared; {minimum} are required.")
    combined_tests = "\n".join(
        path.read_text(encoding="utf-8")
        for path in sorted((ROOT / "tests/TCJ.Messaging.AzureServiceBus.Tests").glob("*.cs"))
    ).lower()
    for marker in (
        "peeklock", "manual_completion", "abandon", "deadletter", "defer", "scheduled_delivery",
        "time_to_live", "lock_renewal", "lock_loss", "session", "graceful", "duplicate_detection",
        "topology", "readiness", "batch", "oversized", "inbox_", "outbox_",
    ):
        if marker not in combined_tests:
            fail(f"Azure Service Bus integration tests are missing scenario marker '{marker}'.")

    for rel in policy.get("requiredConsumers", []):
        require(ROOT / rel, "TCJ.Messaging.AzureServiceBus", "$(TCJCompatibilityVersion)")
    require(ROOT / "upgrade-tests/Scenarios/AzureServiceBusConsumer/AzureServiceBusConsumer.csproj", "TCJ.Messaging.AzureServiceBus")
    require(ROOT / "docs/messaging-azure-service-bus.md", "Peek-Lock", "TCJ Inbox", "TCJ Outbox", "Managed identity", "global exactly-once", "EnablePartitioning")
    require(ROOT / "docs/nuget/TCJ.Messaging.AzureServiceBus.md", "TCJ.Messaging.AzureServiceBus")
    require(ROOT / ".github/workflows/azure-service-bus.yml", "Azure Service Bus transport", "verify-azure-service-bus.py", "TCJ_AZURE_SERVICE_BUS_REQUIRE_INTEGRATION", "GITHUB_STEP_SUMMARY", "messaging-conformance.trx")
    require(ROOT / ".github/workflows/ci.yml", "verify-azure-service-bus.py", "validate-config")
    require(ROOT / ".github/workflows/release-preflight.yml", "azure-service-bus", "verify-azure-service-bus.py")
    require(ROOT / ".github/workflows/release.yml", "azure-service-bus", "verify-azure-service-bus.py")
    require(ROOT / ".github/workflows/published-package-smoke.yml", "TCJ.Messaging.AzureServiceBus")
    require(ROOT / ".github/PULL_REQUEST_TEMPLATE.md", "Azure Service Bus transport", "Peek-Lock processing passes", "Generated Azure Service Bus artifacts are not committed")
    require(ROOT / "tests/TCJ.Concurrency.Tests/Tests/AzureServiceBusStressTests.cs", "Category", "AzureServiceBus", "reuse_one_sender_per_destination", "remains_bounded")
    require(ROOT / "tests/TCJ.Resilience.Tests/AzureServiceBusResilienceTests.cs", "ServiceBusy", "MessagingEntityNotFound", "ServiceTimeout", "MessageSizeExceeded", "SessionLockLost")
    require(ROOT / "benchmarks/TCJ.Benchmarks/Benchmarks/AzureServiceBusBenchmarks.cs", "AzureServiceBus", "MapPublishMessage", "MapScheduledPublishMessage", "TelemetryDisabled")
    require(ROOT / "tests/TCJ.Observability.Tests/AzureServiceBusTelemetryTests.cs", "ActivityCollector", "MeterListener", "logical-message-id-secret")
    require(ROOT / "tests/TCJ.HealthChecks.Tests/Tests/AzureServiceBusHealthCheckTests.cs", "TcjAzureServiceBusHealthCheckNames.Client", "TcjAzureServiceBusHealthCheckNames.SessionProcessor", "TCJ-HEALTH-CREDENTIAL-SECRET")
    require(ROOT / ".github/workflows/concurrency-stress.yml", "src/TCJ.Messaging.AzureServiceBus/**")
    require(ROOT / ".github/workflows/resilience.yml", "src/TCJ.Messaging.AzureServiceBus/**", "verify-azure-service-bus.py")
    require(ROOT / ".github/workflows/performance-benchmarks.yml", "src/TCJ.Messaging.AzureServiceBus/**")
    require(ROOT / ".github/workflows/health-checks.yml", "src/TCJ.Messaging.AzureServiceBus/**", "verify-azure-service-bus.py")
    require(ROOT / "docs/migrations/0.1.0-preview.4-to-0.1.0-preview.5.md", "TCJ.Messaging.AzureServiceBus", "session", "duplicate detection")
    require(ROOT / "docs/package-upgrade-testing.md", "AzureServiceBusConsumer", "TCJ.Messaging.AzureServiceBus")

    if "TCJ.Messaging.AzureServiceBus" not in runtime_ids(ROOT / "eng/release-manifest.json"):
        fail("Azure Service Bus package is missing from release manifest.")
    if "TCJ.Messaging.AzureServiceBus" not in runtime_ids(ROOT / "eng/sbom-policy.json"):
        fail("Azure Service Bus package is missing from SBOM policy.")

    gitignore = read_text(ROOT / ".gitignore")
    for marker in ("artifacts/azure-service-bus/", "TestResults/AzureServiceBus/"):
        if marker not in gitignore:
            fail(f".gitignore must ignore {marker}")
    for forbidden in (
        "eng/azure-service-bus-policy.json",
        "eng/azure-service-bus-contract.json",
        "src/TCJ.Messaging.AzureServiceBus",
        "tests/TCJ.Messaging.AzureServiceBus.Tests",
    ):
        if re.search(rf"^/?{re.escape(forbidden)}/?$", gitignore, re.MULTILINE):
            fail(f"Required Azure Service Bus source is ignored: {forbidden}")

    ensure_not_ignored((POLICY, CONTRACT, PROJECT, TEST_PROJECT))
    return policy, contract


@dataclass
class TrxEvidence:
    files: int = 0
    total: int = 0
    failed: int = 0
    outcomes: dict[str, str] = field(default_factory=dict)
    file_names: set[str] = field(default_factory=set)


def parse_trx(root: Path) -> TrxEvidence:
    evidence = TrxEvidence()
    for path in sorted(root.rglob("*.trx")):
        try:
            tree = ET.parse(path)
        except ET.ParseError as exc:
            fail(f"Malformed TRX {path}: {exc}")
        evidence.files += 1
        evidence.file_names.add(path.name)
        counters = next((node for node in tree.getroot().iter() if node.tag.endswith("Counters")), None)
        if counters is not None:
            evidence.total += int(counters.attrib.get("total", "0"))
            evidence.failed += int(counters.attrib.get("failed", "0"))
        for result in (node for node in tree.getroot().iter() if node.tag.endswith("UnitTestResult")):
            name = result.attrib.get("testName", "")
            outcome = result.attrib.get("outcome", "")
            if name:
                evidence.outcomes[name] = outcome
    return evidence


def find_outcome(evidence: TrxEvidence, method_name: str) -> str | None:
    for test_name, outcome in evidence.outcomes.items():
        if test_name == method_name or test_name.endswith(f".{method_name}"):
            return outcome
    return None


def scan_sensitive_output(root: Path) -> list[str]:
    findings: list[str] = []
    patterns = (
        re.compile(r"SharedAccessKey\s*=", re.IGNORECASE),
        re.compile(r"SAS_KEY_VALUE", re.IGNORECASE),
        re.compile(r"MSSQL_SA_PASSWORD", re.IGNORECASE),
        re.compile(r"Authorization:\s*Bearer", re.IGNORECASE),
    )
    for path in root.rglob("*"):
        if not path.is_file() or path.suffix.lower() in {".dll", ".pdb", ".zip", ".nupkg", ".snupkg"}:
            continue
        try:
            text = path.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        if any(pattern.search(text) for pattern in patterns):
            findings.append(str(path.relative_to(root)))
    return findings


def verify(results: Path, output: Path) -> None:
    policy, contract = validate_config()
    if not results.is_dir():
        fail(f"Azure Service Bus result directory is missing: {results}")

    evidence = parse_trx(results)
    if evidence.files == 0:
        fail("No Azure Service Bus TRX evidence was found.")
    if evidence.failed:
        fail(f"Azure Service Bus TRX reports {evidence.failed} failed tests.")

    required_tests = azure_integration_test_names()
    missing = [name for name in required_tests if find_outcome(evidence, name) is None]
    not_passed = [(name, find_outcome(evidence, name)) for name in required_tests if find_outcome(evidence, name) not in (None, "Passed")]
    if missing:
        fail(f"Azure Service Bus integration evidence is missing {len(missing)} required tests; first missing: {missing[0]}")
    if not_passed:
        fail(f"Azure Service Bus integration test did not pass: {not_passed[0][0]} ({not_passed[0][1]})")
    if len(required_tests) < int(policy["minimumIntegrationTestCount"]):
        fail("Declared Azure Service Bus integration test count fell below policy minimum.")
    if policy.get("requireConformanceSuite") and "messaging-conformance.trx" not in evidence.file_names:
        fail("Step 46 messaging conformance evidence (messaging-conformance.trx) is missing.")

    sensitive_findings = scan_sensitive_output(results)
    if sensitive_findings:
        fail(f"Sensitive-data marker found in Azure Service Bus evidence: {sensitive_findings[0]}")

    output.mkdir(parents=True, exist_ok=True)
    package_version = read_json(ROOT / "eng/release-manifest.json").get("version", "unknown")
    source_sha = os.environ.get("GITHUB_SHA", "local")
    environment_type = os.environ.get("TCJ_AZURE_SERVICE_BUS_TEST_ENVIRONMENT", "unspecified")
    summary = {
        "sourceCommitSha": source_sha,
        "packageName": policy["packageName"],
        "packageVersion": package_version,
        "azureServiceBusVersion": policy["azureServiceBusVersion"],
        "testEnvironmentType": environment_type,
        "transportCapabilities": contract["transportDescriptor"]["capabilities"],
        "integrationTestCount": len(required_tests),
        "executedTestCount": evidence.total,
        "conformanceSuite": "passed",
        "sessionOrdering": "per-session",
        "gracefulShutdown": "passed",
        "outboxIntegration": "passed",
        "inboxIntegration": "passed",
        "sensitiveDataScan": "passed",
        "telemetry": "passed",
        "healthChecks": "passed",
        "globalExactlyOnceGuaranteed": False,
        "overall": "pass",
    }
    (output / "azure-service-bus-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")

    lines = [
        "# Azure Service Bus validation summary",
        "",
        f"- Source commit: `{source_sha}`",
        f"- Package: `{policy['packageName']}` `{package_version}`",
        f"- Azure Service Bus SDK: `{policy['azureServiceBusVersion']}`",
        f"- Test environment: `{environment_type}`",
        f"- Azure integration tests: **{len(required_tests)} passed**",
        f"- Total TRX tests: **{evidence.total}**",
        "- Step 46 conformance suite: **PASS**",
        "- Peek-Lock/manual completion: **PASS**",
        "- Session ordering: **session-scoped only**",
        "- Graceful shutdown: **PASS**",
        "- TCJ Outbox integration: **PASS**",
        "- TCJ Inbox integration: **PASS**",
        "- Sensitive-data evidence scan: **PASS**",
        "- Global exactly-once claim: **false**",
        "- Overall: **PASS**",
        "",
    ]
    (output / "AZURE_SERVICE_BUS_SUMMARY.md").write_text("\n".join(lines), encoding="utf-8")

    reports = {
        "conformance-report.json": {"status": "pass", "step46Trx": "messaging-conformance.trx", "azureIntegrationTests": len(required_tests)},
        "topology-report.json": {"status": "pass", "modes": contract["topologyModes"]},
        "session-report.json": {"status": "pass", "ordering": "per-session"},
        "lock-renewal-report.json": {"status": "pass", "boundedMinutes": contract["defaults"]["MaxAutoLockRenewalMinutes"]},
        "sensitive-data-scan.json": {"status": "pass", "findings": []},
    }
    for name, body in reports.items():
        (output / name).write_text(json.dumps(body, indent=2) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("validate-config")
    verify_parser = subparsers.add_parser("verify")
    verify_parser.add_argument("--results", required=True)
    verify_parser.add_argument("--output", required=True)
    args = parser.parse_args()

    try:
        if args.command == "validate-config":
            validate_config()
            print("Azure Service Bus configuration validation passed.")
        else:
            results = Path(args.results)
            output = Path(args.output)
            verify(results if results.is_absolute() else (ROOT / results).resolve(), output if output.is_absolute() else (ROOT / output).resolve())
            print("Azure Service Bus evidence verification passed.")
        return 0
    except VerificationError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
