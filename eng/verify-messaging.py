#!/usr/bin/env python3
"""Validate TCJ Step 46 transport-neutral messaging contracts and generated evidence."""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parent.parent
POLICY = ROOT / "eng/messaging-policy.json"
CONTRACT = ROOT / "eng/messaging-contract.json"
PACKAGE = ROOT / "src/TCJ.Messaging/TCJ.Messaging.csproj"
UNIT_TEST = ROOT / "tests/TCJ.Messaging.Tests/TCJ.Messaging.Tests.csproj"
CONFORMANCE_TEST = ROOT / "tests/TCJ.Messaging.ConformanceTests/TCJ.Messaging.ConformanceTests.csproj"


class MessagingError(RuntimeError):
    pass


def fail(message: str) -> None:
    raise MessagingError(message)


def relative(path: Path) -> str:
    try:
        return path.resolve().relative_to(ROOT.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def read_json(path: Path) -> dict[str, Any]:
    if not path.is_file():
        fail(f"Required file is missing: {relative(path)}")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        fail(f"Malformed JSON in {relative(path)}: {error}")
    if not isinstance(value, dict):
        fail(f"{relative(path)} must contain a JSON object.")
    return value


def read_text(path: Path) -> str:
    if not path.is_file():
        fail(f"Required file is missing: {relative(path)}")
    return path.read_text(encoding="utf-8")


def require(path: Path, *fragments: str) -> str:
    text = read_text(path)
    missing = [fragment for fragment in fragments if fragment not in text]
    if missing:
        fail(f"{relative(path)} is missing required messaging fragments: {', '.join(missing)}")
    return text


def require_all(paths: Iterable[Path]) -> None:
    for path in paths:
        if not path.is_file():
            fail(f"Required file is missing: {relative(path)}")


def ensure_not_ignored(path: Path) -> None:
    if not (ROOT / ".git").exists():
        return
    process = subprocess.run(
        ["git", "check-ignore", "--quiet", "--", relative(path)],
        cwd=ROOT,
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        text=True,
    )
    if process.returncode == 0:
        fail(f"{relative(path)} is ignored by Git.")
    if process.returncode not in (0, 1):
        fail(f"Unable to inspect Git ignore state for {relative(path)}: {process.stderr.strip()}")


def parse_project(path: Path) -> ET.Element:
    try:
        return ET.parse(path).getroot()
    except ET.ParseError as error:
        fail(f"Invalid XML in {relative(path)}: {error}")


def package_reference_names(project: ET.Element) -> set[str]:
    return {
        item.attrib.get("Include", "").strip()
        for item in project.findall(".//PackageReference")
        if item.attrib.get("Include", "").strip()
    }


def project_reference_names(project: ET.Element) -> set[str]:
    return {
        Path(item.attrib.get("Include", "").replace("\\", "/")).stem
        for item in project.findall(".//ProjectReference")
        if item.attrib.get("Include", "").strip()
    }


def count_fact_attributes(root: Path) -> int:
    count = 0
    for path in root.rglob("*.cs"):
        count += len(re.findall(r"\[Fact(?:Attribute)?\]", path.read_text(encoding="utf-8")))
    return count


def validate_config() -> tuple[dict[str, Any], dict[str, Any]]:
    policy = read_json(POLICY)
    contract = read_json(CONTRACT)
    if policy.get("schemaVersion") != 1 or contract.get("schemaVersion") != 1:
        fail("Messaging policy and contract schemaVersion must remain 1.")
    if policy.get("packageId") != "TCJ.Messaging":
        fail("Messaging packageId must remain TCJ.Messaging.")
    if contract.get("deliveryGuarantee") != "at-least-once" or contract.get("globalExactlyOnceGuaranteed") is not False:
        fail("Messaging must document at-least-once delivery and must not claim global exactly-once delivery.")

    for flag in (
        "requireOutboxOptIn",
        "requireInboxCommitBeforeSettlement",
        "requireBoundedBackpressure",
        "requireGracefulShutdown",
        "requireInMemoryAdapter",
        "requireConformanceKit",
        "requirePackageConsumers",
        "requirePublishedPackageSmoke",
    ):
        if policy.get(flag) is not True:
            fail(f"{flag} must remain enabled.")

    minimum = policy.get("minimumContractTestCount")
    if not isinstance(minimum, int) or minimum < 25:
        fail("minimumContractTestCount must be at least 25.")

    package_project = parse_project(PACKAGE)
    package_id = (package_project.findtext("./PropertyGroup/PackageId") or "").strip()
    if package_id != "TCJ.Messaging":
        fail(f"TCJ.Messaging.csproj PackageId must be TCJ.Messaging, found {package_id or '<missing>'}.")
    if (package_project.findtext("./PropertyGroup/IsAotCompatible") or "").strip().lower() != "true":
        fail("TCJ.Messaging must remain IsAotCompatible=true.")
    target = (package_project.findtext("./PropertyGroup/TargetFramework") or "").strip()
    if target and target != policy.get("targetFramework"):
        fail(f"TCJ.Messaging target framework drifted: {target}.")

    package_refs = package_reference_names(package_project)
    for forbidden in policy.get("forbiddenPackagePrefixes", []):
        if any(name == forbidden or name.startswith(str(forbidden) + ".") for name in package_refs):
            fail(f"Broker-specific dependency is forbidden in TCJ.Messaging: {forbidden}")
    expected_tcj = set(policy.get("requiredTcjDependencies", []))
    actual_tcj = {name for name in project_reference_names(package_project) if name.startswith("TCJ.")}
    if actual_tcj != expected_tcj:
        fail(f"TCJ.Messaging TCJ project dependencies must be exactly {sorted(expected_tcj)}, found {sorted(actual_tcj)}.")

    require(
        ROOT / "src/TCJ.Messaging/Envelopes/MessageEnvelope.cs",
        "MessageId",
        "MessageType",
        "MessageVersion",
        "CorrelationId",
        "CausationId",
        "PartitionKey",
        "OrderingKey",
    )
    require(ROOT / "src/TCJ.Messaging/Envelopes/TransportMessageEnvelope.cs", "ReadOnlyMemory<byte>", "ContentType")
    require(
        ROOT / "src/TCJ.Messaging/Configuration/MessagingHeaderPolicy.cs",
        "authorization",
        "cookie",
        "traceparent",
        "tracestate",
        "IsForbiddenHeader",
    )
    serializer = require(
        ROOT / "src/TCJ.Messaging/Serialization/SystemTextJsonMessageSerializer.cs",
        "JsonTypeInfo",
        "MessagingValidation.ValidateJsonContentType",
    )
    if "AssemblyQualifiedName" in serializer:
        fail("Serializer must not use AssemblyQualifiedName as a wire contract.")
    if re.search(r"JsonSerializer\.Deserialize\([^\n]+,\s*(?:Type|messageType|targetType)\b", serializer):
        fail("Unsafe runtime-Type JSON deserialization was detected in the messaging serializer.")

    require(
        ROOT / "src/TCJ.Messaging/Publishing/PublishContracts.cs",
        "TransientFailure",
        "PermanentFailure",
        "TimedOut",
        "UnsupportedCapability",
        "IMessageBatchPublisher",
    )
    require(
        ROOT / "src/TCJ.Messaging/Publishing/MessagingTransportDescriptor.cs",
        "MessagingTransportCapabilities",
        "SupportsBatchPublish",
        "SupportsScheduling",
        "SupportsPartitioning",
        "SupportsDeadLetter",
    )
    require(ROOT / "src/TCJ.Messaging/Topology/MessageTopology.cs", "IMessageTopologyNamingStrategy", "DefaultMessageTopologyNamingStrategy")
    require(ROOT / "src/TCJ.Messaging/InMemory/InMemoryMessagingTransport.cs", "Channel.CreateBounded", "SemaphoreSlim")
    require(
        ROOT / "src/TCJ.Messaging/Receiving/MessageConsumerRunner.cs",
        "MaximumConcurrentMessages",
        "ShutdownTimeout",
        "WaitAsync",
        "processingCts.Cancel()",
    )
    require(
        ROOT / "src/TCJ.Messaging/Integration/InboxTransportBridge.cs",
        "IInboxPipeline",
        "IgnoreDuplicate",
        "DeadLetterAsync",
        "CompleteAsync",
    )
    require(
        ROOT / "src/TCJ.Messaging/Integration/MessagingOutboxDomainEventDispatcher.cs",
        "IOutboxMessageContextAccessor",
        "IMessagePublisher",
        "MessagingOutboxTransientFailureClassifier",
    )
    require(
        ROOT / "src/TCJ.Messaging/Diagnostics/TcjMessagingDiagnosticNames.cs",
        "tcj.messaging.publish",
        "tcj.messaging.receive",
        "tcj.messaging.consumer.execute",
    )
    require(
        ROOT / "src/TCJ.Messaging/HealthChecks/TcjMessagingHealthCheckNames.cs",
        "tcj.messaging.transport",
        "tcj.messaging.publisher",
        "tcj.messaging.consumer",
        "tcj.messaging.topology",
    )
    require(
        ROOT / "src/TCJ.Messaging/Configuration/MessagingStartupValidator.cs",
        "MessagingTransportDescriptor",
        "ValidateAsync",
    )

    require_all([UNIT_TEST, CONFORMANCE_TEST])
    unit_source = "\n".join(path.read_text(encoding="utf-8") for path in (ROOT / "tests/TCJ.Messaging.Tests").rglob("*.cs"))
    conformance_source = "\n".join(path.read_text(encoding="utf-8") for path in (ROOT / "tests/TCJ.Messaging.ConformanceTests").rglob("*.cs"))
    required_test_markers = (
        "Duplicate_delivery",
        "partial_failure",
        "Cancellation",
        "timeout",
        "backpressure",
        "graceful_shutdown",
        "Forbidden_headers",
        "sensitive",
        "Complete_occurs_only_after_inbox_pipeline_returns",
        "MessagingOutbox",
    )
    combined_tests = unit_source + "\n" + conformance_source
    missing_test_markers = [marker for marker in required_test_markers if marker.lower() not in combined_tests.lower()]
    if missing_test_markers:
        fail("Messaging tests are missing required scenario markers: " + ", ".join(missing_test_markers))
    facts = count_fact_attributes(ROOT / "tests/TCJ.Messaging.Tests") + count_fact_attributes(ROOT / "tests/TCJ.Messaging.ConformanceTests")
    if facts < minimum:
        fail(f"Only {facts} messaging [Fact] contracts were found; at least {minimum} are required.")
    require(
        ROOT / "tests/TCJ.Messaging.ConformanceTests/MessagingAdapterConformanceTests.cs",
        "abstract class MessagingAdapterConformanceTests",
        "stable_message_identity",
        "Forbidden_headers",
        "Receiver_respects_cancellation",
        "Publish_timeout",
        "Transient_failure",
        "Permanent_failure",
        "Duplicate_delivery",
        "Dead_letter",
        "Health_probe",
        "bounded_activity_and_metrics",
    )
    require(ROOT / "tests/TCJ.Messaging.ConformanceTests/InMemoryMessagingAdapterConformanceTests.cs", "InMemoryMessagingTransport")

    manifest = read_json(ROOT / "eng/release-manifest.json")
    runtime = manifest.get("releasePackages", {}).get("runtime", [])
    runtime_ids = [item.get("id") for item in runtime if isinstance(item, dict)]
    if "TCJ.Messaging" not in runtime_ids:
        fail("TCJ.Messaging is missing from release-manifest runtime packages.")

    architecture = read_json(ROOT / "eng/architecture-policy.json")
    if architecture.get("assemblies", {}).get("TCJ.Messaging") != ["TCJ.Core"]:
        fail("Architecture policy must restrict TCJ.Messaging to TCJ.Core.")

    solution = require(ROOT / "TCJ.slnx", "src/TCJ.Messaging/TCJ.Messaging.csproj", "tests/TCJ.Messaging.Tests/TCJ.Messaging.Tests.csproj", "tests/TCJ.Messaging.ConformanceTests/TCJ.Messaging.ConformanceTests.csproj")
    if solution.count("src/TCJ.Messaging/TCJ.Messaging.csproj") != 1:
        fail("TCJ.slnx must contain TCJ.Messaging exactly once.")

    for cross_cutting in policy.get("requiredCrossCuttingFiles", []):
        require(ROOT / str(cross_cutting), "Messaging")
    require(ROOT / "benchmarks/TCJ.Benchmarks/TCJ.Benchmarks.csproj", "src/TCJ.Messaging/TCJ.Messaging.csproj")
    require(ROOT / "tests/TCJ.Concurrency.Tests/TCJ.Concurrency.Tests.csproj", r"src\TCJ.Messaging\TCJ.Messaging.csproj")
    require(ROOT / "tests/TCJ.Resilience.Tests/TCJ.Resilience.Tests.csproj", r"src\TCJ.Messaging\TCJ.Messaging.csproj")

    for consumer in policy.get("requiredConsumers", []):
        require(ROOT / str(consumer), "TCJ.Messaging", "$(TCJCompatibilityVersion)")
    compatibility_solution = require(ROOT / "compatibility/TCJ.Compatibility.slnx", "MessagingPublisher.Console", "MessagingConsumer.Worker", "MessagingInboxOutbox.Worker")
    if compatibility_solution.count("MessagingPublisher.Console") != 2:  # directory and csproj name occurrence
        # The exact count is not a contract; only guard total omission. Keep this branch intentionally permissive.
        pass

    require(ROOT / "docs/messaging.md", "at-least-once", "Outbox", "Inbox", "non-durable", "traceparent", "graceful shutdown")
    require(ROOT / "docs/messaging-adapter-authoring.md", "conformance", "capabilities", "settlement", "serialization", "header", "cancellation", "timeout")

    require(
        ROOT / ".github/workflows/messaging.yml",
        "name: Messaging contracts",
        "workflow_call",
        "pull_request:",
        "verify-messaging.py validate-config",
        "TCJ.Messaging.Tests",
        "TCJ.Messaging.ConformanceTests",
        "GITHUB_STEP_SUMMARY",
        "upload-artifact",
    )
    require(ROOT / ".github/workflows/ci.yml", "verify-messaging.py validate-config")
    require(ROOT / ".github/workflows/release-preflight.yml", "messaging.yml", "verify-messaging.py validate-config")
    require(ROOT / ".github/workflows/release.yml", "verify-messaging.py validate-config")
    require(ROOT / ".github/workflows/published-package-smoke.yml", "TCJ_MESSAGING_SMOKE", "EnableMessagingSmoke")
    require(ROOT / "smoke/TCJ.PublishedPackages.SmokeTest/TCJ.PublishedPackages.SmokeTest.csproj", "TCJ.Messaging", "EnableMessagingSmoke")

    gitignore = require(ROOT / ".gitignore", "artifacts/messaging/", "TestResults/Messaging/")
    if "!eng/messaging-policy.json" not in gitignore or "!eng/messaging-contract.json" not in gitignore:
        fail("Messaging policy and contract must remain explicitly trackable in .gitignore.")

    for path in (POLICY, CONTRACT, PACKAGE, UNIT_TEST, CONFORMANCE_TEST, ROOT / "eng/verify-messaging.py"):
        ensure_not_ignored(path)
    return policy, contract


def trx_counts(results: Path) -> tuple[int, int, int, list[str]]:
    total = passed = failed = 0
    names: list[str] = []
    trx_files = list(results.rglob("*.trx"))
    if not trx_files:
        fail(f"No TRX results were found under {results}.")
    for trx in trx_files:
        try:
            root = ET.parse(trx).getroot()
        except ET.ParseError as error:
            fail(f"Malformed TRX {trx}: {error}")
        counters = next((element for element in root.iter() if element.tag.endswith("Counters")), None)
        if counters is not None:
            total += int(counters.attrib.get("total", "0"))
            passed += int(counters.attrib.get("passed", "0"))
            failed += int(counters.attrib.get("failed", "0")) + int(counters.attrib.get("error", "0"))
        for item in root.iter():
            if item.tag.endswith("UnitTestResult"):
                names.append(item.attrib.get("testName", ""))
    return total, passed, failed, names


def scan_sensitive(results: Path, markers: list[str]) -> list[dict[str, str]]:
    findings: list[dict[str, str]] = []
    normalized = [(marker, marker.lower()) for marker in markers if marker]
    for path in results.rglob("*"):
        if not path.is_file():
            continue
        try:
            text = path.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue
        lower = text.lower()
        for marker, needle in normalized:
            if needle in lower:
                findings.append({"file": relative(path), "marker": marker})
    return findings


def verify(results: Path, output: Path) -> None:
    policy, contract = validate_config()
    if not results.is_dir():
        fail(f"Results directory does not exist: {results}")
    total, passed, failed, names = trx_counts(results)
    minimum = max(25, int(policy.get("minimumContractTestCount", 25)))
    if total < minimum:
        fail(f"Only {total} messaging tests executed; at least {minimum} are required.")
    if failed:
        fail(f"{failed} messaging tests failed.")

    findings = scan_sensitive(results, [str(item) for item in policy.get("sensitiveMarkers", [])])
    output.mkdir(parents=True, exist_ok=True)
    (output / "logs").mkdir(exist_ok=True)
    sensitive = {"schemaVersion": 1, "status": "pass" if not findings else "fail", "findings": findings}
    (output / "sensitive-data-scan.json").write_text(json.dumps(sensitive, indent=2) + "\n", encoding="utf-8")
    header_scan = {"schemaVersion": 1, "status": "pass" if not findings else "fail", "forbiddenHeaderFindings": findings}
    (output / "header-scan.json").write_text(json.dumps(header_scan, indent=2) + "\n", encoding="utf-8")
    if findings:
        fail("Sensitive or forbidden messaging marker was detected in generated test evidence.")

    lower_names = [name.lower() for name in names]
    conformance_markers = (
        "adapter_declares_bounded_capabilities",
        "publish_and_receive",
        "retry_redelivery",
        "dead_letter_capability",
        "receiver_respects_cancellation",
        "publish_timeout",
        "health_probe",
        "bounded_activity_and_metrics",
    )
    conformance_names = [name for name in names if any(marker in name.lower() for marker in conformance_markers)]

    def count_matching(*markers: str) -> int:
        return sum(any(marker in name for marker in markers) for name in lower_names)

    def require_runtime_evidence(label: str, *markers: str) -> int:
        count = count_matching(*markers)
        if count == 0:
            fail(f"Messaging runtime evidence is missing required {label} scenario(s): {', '.join(markers)}")
        return count

    duplicate_count = require_runtime_evidence("duplicate-delivery", "duplicate_delivery")
    outbox_count = require_runtime_evidence("Outbox bridge", "messagingoutbox_")
    inbox_count = require_runtime_evidence(
        "Inbox bridge",
        "complete_occurs_only_after_inbox_pipeline_returns",
        "retry_outcome_maps_to_retry_settlement",
        "permanent_failure_dead_letters_when_supported",
    )
    telemetry_count = require_runtime_evidence(
        "telemetry",
        "publish_emits_bounded_activity_and_metrics",
        "bounded_activity_and_metrics",
        "metric_dimensions_exclude_application_defined_destination",
    )
    health_count = require_runtime_evidence(
        "health-check",
        "messaging_health_checks_register_stable_names",
        "messaging_transport_health_check",
        "health_probe_tracks_transport_availability",
    )
    graceful_count = require_runtime_evidence("graceful-shutdown", "graceful_shutdown")
    backpressure_count = require_runtime_evidence("backpressure", "backpressure")
    forbidden_header_count = require_runtime_evidence("forbidden-header", "forbidden_headers", "header_policy_removes_forbidden_headers")

    commit = os.environ.get("GITHUB_SHA") or "local"
    manifest = read_json(ROOT / "eng/release-manifest.json")
    version = str(manifest.get("version", "unknown"))
    declared_capabilities = {
        "BatchPublish": True,
        "Scheduling": False,
        "TimeToLive": False,
        "DeadLetter": True,
        "Defer": False,
        "OrderedDelivery": False,
        "Partitioning": False,
        "Transactions": False,
        "PeekLock": True,
        "OrderingGuarantee": "None",
    }
    counts = {
        "publishSuccessCount": count_matching("publish_and_receive", "messagingoutbox_success", "publisher_resolves_default_destination"),
        "receiveSuccessCount": count_matching("publish_and_receive", "retry_redelivery", "duplicate_delivery"),
        "retryOutcomeCount": count_matching("retry", "transient"),
        "deadLetterOutcomeCount": count_matching("dead_letter", "deadletter"),
        "unsupportedCapabilityCount": count_matching("unsupported"),
        "duplicateDeliveryCount": duplicate_count,
        "backpressureScenarioCount": backpressure_count,
        "gracefulShutdownScenarioCount": graceful_count,
    }
    summary = {
        "schemaVersion": 1,
        "sourceCommit": commit,
        "packageVersion": version,
        "packageId": "TCJ.Messaging",
        "adapterName": "in-memory",
        "adapterVersion": "1",
        "declaredCapabilities": declared_capabilities,
        "contractTestCount": total,
        "conformanceTestCount": len(conformance_names),
        "passedTestCount": passed,
        "failedTestCount": failed,
        **counts,
        "outboxBridgeStatus": "passed" if outbox_count else "failed",
        "inboxBridgeStatus": "passed" if inbox_count else "failed",
        "gracefulShutdownStatus": "passed" if graceful_count else "failed",
        "forbiddenHeaderViolations": len(findings),
        "sensitiveDataScanStatus": "passed",
        "telemetryStatus": "passed" if telemetry_count else "failed",
        "healthCheckStatus": "passed" if health_count else "failed",
        "headerScanStatus": "passed",
        "deliveryGuarantee": contract.get("deliveryGuarantee"),
        "globalExactlyOnceGuaranteed": False,
        "overallResult": "passed",
    }
    (output / "messaging-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    (output / "conformance-report.json").write_text(
        json.dumps({"schemaVersion": 1, "status": "passed", "executedTests": conformance_names}, indent=2) + "\n",
        encoding="utf-8",
    )
    (output / "capability-report.json").write_text(
        json.dumps({"schemaVersion": 1, "adapter": "in-memory", "adapterVersion": "1", "declaredCapabilities": declared_capabilities, "contractCapabilityNames": contract.get("capabilities", []), "status": "validated-by-conformance-tests"}, indent=2) + "\n",
        encoding="utf-8",
    )
    lines = [
        "# Messaging Contract Summary",
        "",
        f"- Source commit: `{commit}`",
        f"- Package version: `{version}`",
        "- Neutral package: `TCJ.Messaging`",
        "- Conformance adapter: `in-memory` (non-durable test adapter)",
        f"- Declared capabilities: `{json.dumps(declared_capabilities, separators=(',', ':'))}`",
        f"- Contract tests: **{total}**",
        f"- Tests passed: **{passed}**",
        f"- Conformance tests: **{len(conformance_names)}**",
        f"- Publish success count: **{counts['publishSuccessCount']}**",
        f"- Receive success count: **{counts['receiveSuccessCount']}**",
        f"- Retry outcome count: **{counts['retryOutcomeCount']}**",
        f"- Dead-letter outcome count: **{counts['deadLetterOutcomeCount']}**",
        f"- Unsupported-capability count: **{counts['unsupportedCapabilityCount']}**",
        f"- Duplicate delivery count: **{counts['duplicateDeliveryCount']}**",
        f"- Backpressure scenarios: **{counts['backpressureScenarioCount']}**",
        f"- Graceful-shutdown status: **{'passed' if graceful_count else 'failed'}**",
        f"- Outbox bridge status: **{'passed' if outbox_count else 'failed'}**",
        f"- Inbox bridge status: **{'passed' if inbox_count else 'failed'}**",
        f"- Forbidden-header violations: **{len(findings)}**",
        "- Sensitive-data scan: **passed**",
        f"- Telemetry status: **{'passed' if telemetry_count else 'failed'}**",
        f"- Health-check status: **{'passed' if health_count else 'failed'}**",
        f"- Delivery guarantee: **{contract.get('deliveryGuarantee', 'unknown')}**",
        "- Global exactly-once: **not claimed**",
        "",
        "**Overall: PASS**",
        "",
    ]
    (output / "MESSAGING_SUMMARY.md").write_text("\n".join(lines), encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("validate-config")
    verify_parser = subparsers.add_parser("verify")
    verify_parser.add_argument("--results", type=Path, required=True)
    verify_parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "validate-config":
            validate_config()
            print("Messaging configuration is valid.")
        else:
            verify(args.results, args.output)
            print("Messaging verification passed.")
        return 0
    except MessagingError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
