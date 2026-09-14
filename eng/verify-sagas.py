#!/usr/bin/env python3
"""Validate TCJ Step 51 durable Saga contracts, repository wiring, and test evidence."""
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
POLICY = ROOT / "eng/sagas-policy.json"
CONTRACT = ROOT / "eng/sagas-contract.json"
PACKAGE_IDS = (
    "TCJ.Messaging.Sagas",
    "TCJ.Messaging.Sagas.EntityFrameworkCore",
    "TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer",
)
EXPECTED_PROJECT_REFERENCES = {
    "TCJ.Messaging.Sagas": {"TCJ.Core", "TCJ.Messaging"},
    "TCJ.Messaging.Sagas.EntityFrameworkCore": {"TCJ.Messaging.Sagas", "TCJ.EntityFrameworkCore"},
    "TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer": {"TCJ.Messaging.Sagas.EntityFrameworkCore", "TCJ.EntityFrameworkCore.SqlServer"},
}


class SagaVerificationError(RuntimeError):
    pass


def fail(message: str) -> None:
    raise SagaVerificationError(message)


def read_json(path: Path) -> dict[str, Any]:
    if not path.is_file():
        fail(f"Required file is missing: {path.relative_to(ROOT)}")
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as error:
        fail(f"Malformed JSON in {path.relative_to(ROOT)}: {error}")
    if not isinstance(value, dict):
        fail(f"{path.relative_to(ROOT)} must contain a JSON object.")
    return value


def require_text(path: Path, *fragments: str) -> str:
    if not path.is_file():
        fail(f"Required file is missing: {path.relative_to(ROOT)}")
    text = path.read_text(encoding="utf-8-sig")
    missing = [fragment for fragment in fragments if fragment not in text]
    if missing:
        fail(f"{path.relative_to(ROOT)} is missing required evidence: {', '.join(missing)}")
    return text


def ensure_not_ignored(path: Path) -> None:
    if not (ROOT / ".git").exists():
        return
    result = subprocess.run(
        ["git", "check-ignore", "--quiet", "--", path.relative_to(ROOT).as_posix()],
        cwd=ROOT,
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    if result.returncode == 0:
        fail(f"Required Saga source is ignored by Git: {path.relative_to(ROOT)}")
    if result.returncode not in (1,):
        fail(f"Unable to inspect Git ignore state for {path.relative_to(ROOT)}")


def project_references(path: Path) -> set[str]:
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, FileNotFoundError) as error:
        fail(f"Invalid Saga project {path.relative_to(ROOT)}: {error}")
    result: set[str] = set()
    for node in root.iter():
        if node.tag.rsplit("}", 1)[-1] != "ProjectReference":
            continue
        include = node.attrib.get("Include", "").replace("\\", "/")
        result.add(Path(include).stem)
    return result


def package_references(path: Path) -> set[str]:
    root = ET.parse(path).getroot()
    return {
        (node.attrib.get("Include") or node.attrib.get("Update") or "")
        for node in root.iter()
        if node.tag.rsplit("}", 1)[-1] == "PackageReference"
    } - {""}


def runtime_release_packages(path: Path) -> set[str]:
    manifest = read_json(path)
    try:
        return {entry["id"] for entry in manifest["releasePackages"]["runtime"]}
    except (KeyError, TypeError):
        fail(f"Invalid runtime release package manifest: {path.relative_to(ROOT)}")


def aot_packages(path: Path) -> set[str]:
    data = read_json(path)
    packages = data.get("packages")
    if not isinstance(packages, list):
        fail("AOT policy packages must be an array.")
    return {item.get("packageId") for item in packages if isinstance(item, dict)}


def validate_transport_neutral_sources() -> None:
    forbidden = {
        "src/TCJ.Messaging.Sagas": (
            "Microsoft.EntityFrameworkCore",
            "Microsoft.Data.SqlClient",
            "TCJ.EntityFrameworkCore",
            "TCJ.AspNetCore",
            "RabbitMQ.Client",
            "Azure.Messaging.ServiceBus",
            "Confluent.Kafka",
        ),
        "src/TCJ.Messaging.Sagas.EntityFrameworkCore": (
            "Microsoft.EntityFrameworkCore.SqlServer",
            "Microsoft.Data.SqlClient",
            "TCJ.EntityFrameworkCore.SqlServer",
            "RabbitMQ.Client",
            "Azure.Messaging.ServiceBus",
            "Confluent.Kafka",
        ),
        "src/TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer": (
            "TCJ.AspNetCore",
            "RabbitMQ.Client",
            "Azure.Messaging.ServiceBus",
            "Confluent.Kafka",
        ),
    }
    for folder, prefixes in forbidden.items():
        for source in (ROOT / folder).rglob("*.cs"):
            text = source.read_text(encoding="utf-8-sig")
            found = [prefix for prefix in prefixes if prefix in text]
            if found:
                fail(f"{source.relative_to(ROOT)} contains forbidden dependency/API evidence: {', '.join(found)}")


def validate_no_reflection_registration() -> None:
    source = "\n".join(
        path.read_text(encoding="utf-8-sig")
        for path in (ROOT / "src/TCJ.Messaging.Sagas.EntityFrameworkCore/Registration").glob("*.cs")
    )
    forbidden = ("Assembly.GetTypes", "GetExportedTypes", "AppDomain.CurrentDomain.GetAssemblies", "Type.GetType(")
    found = [item for item in forbidden if item in source]
    if found:
        fail(f"Saga registration must remain explicit and trimming-safe; reflection scanning found: {', '.join(found)}")
    for required in ("JsonTypeInfo<TState>", "stable", "SagaType", "DefinitionVersion", "StateSchemaVersion"):
        if required not in source:
            fail(f"Saga explicit registration evidence is missing: {required}")


def validate_config() -> tuple[dict[str, Any], dict[str, Any]]:
    policy = read_json(POLICY)
    contract = read_json(CONTRACT)
    if policy.get("schemaVersion") != 1 or contract.get("schemaVersion") != 1:
        fail("Saga policy and contract schemaVersion must remain 1.")
    if tuple(contract.get("packageIds", [])) != PACKAGE_IDS:
        fail("Saga contract package IDs drifted.")
    if set(policy.get("packages", {})) != set(PACKAGE_IDS):
        fail("Saga policy must describe exactly the three Step 51 runtime packages.")
    if policy.get("supportedProviders") != ["SqlServer"]:
        fail("The initial Saga provider contract must remain SQL Server only.")

    required_true = (
        "requireSameInboxSagaDbContext",
        "requireExistingOutbox",
        "requireRealSqlServerConcurrencyTests",
        "requireRealSqlServerTimerTests",
        "requireCrossTransportSagaSmoke",
        "requireSensitiveDataScan",
        "requirePackageConsumer",
        "requireUpgradeScenario",
        "requireCommitMatchedReleaseEvidence",
    )
    for name in required_true:
        if policy.get(name) is not True:
            fail(f"Saga policy guarantee is disabled: {name}")

    transaction = contract.get("transactionOwnership", {})
    if transaction != {
        "externalMessages": "existing-transactional-inbox",
        "requiresSameDbContext": True,
        "sagaCreatesNestedTransaction": False,
        "durableOutput": "existing-outbox-compatible-domain-events",
        "directBrokerPublishDefault": False,
    }:
        fail("Saga transaction ownership contract drifted.")
    claims = contract.get("claims", {})
    if any(claims.get(name) is not False for name in ("distributedAcid", "globalExactlyOnce", "automaticRemoteRollback", "generalPurposeScheduler", "eventSourcingReplay")):
        fail("Saga contract must not overclaim distributed transaction, exactly-once, rollback, scheduler, or replay guarantees.")
    if contract.get("concurrency", {}).get("sqlServerToken") != "rowversion":
        fail("SQL Server Saga concurrency must use rowversion.")
    if contract.get("correlation", {}).get("persistRawValue") is not False:
        fail("Raw Saga correlation values must not be persisted by the framework correlation table.")
    telemetry = contract.get("telemetry", {})
    if any(telemetry.get(name) is not False for name in ("allowsSagaIdDimension", "allowsStatePayload", "allowsCorrelationValue")):
        fail("Saga telemetry must exclude unbounded/sensitive Saga identity, state, and correlation dimensions.")

    for package_id, rel in policy["packages"].items():
        project = ROOT / rel
        require_text(project, f"<PackageId>{package_id}</PackageId>", "eng/Packaging.props", "<PackageValidationBaselineVersion>")
        actual_refs = project_references(project)
        if actual_refs != EXPECTED_PROJECT_REFERENCES[package_id]:
            fail(f"{package_id} ProjectReference direction drifted: {sorted(actual_refs)}")
        if package_references(project):
            fail(f"{package_id} must not add direct NuGet PackageReference dependencies; use existing TCJ project/package dependency graph.")

    validate_transport_neutral_sources()
    validate_no_reflection_registration()

    require_text(
        ROOT / "src/TCJ.Messaging.Sagas/SagaCorrelationKey.cs",
        "MaximumStringLength = 256",
        "case-sensitive ordinal string correlation key without implicit trimming",
        "[redacted-correlation]",
    )
    require_text(
        ROOT / "src/TCJ.Messaging.Sagas/Configuration/TcjSagaOptions.cs",
        "MaximumStatePayloadBytes",
        "MaxTimerAttempts",
        "MaxCompensationAttempts",
        "TerminalRetentionPeriod",
        "CleanupBatchSize",
    )
    require_text(
        ROOT / "src/TCJ.Messaging.Sagas.EntityFrameworkCore/Registration/SagaServiceCollectionExtensions.cs",
        "JsonTypeInfo<TState>",
        "Call AddTcjSagas<TDbContext>()",
        "SagaType values must be globally unique",
    )
    require_text(
        ROOT / "src/TCJ.Messaging.Sagas.EntityFrameworkCore/Processing/SagaStartupValidator.cs",
        "InboxContextRegistration",
        "OutboxContextRegistration",
        "same",
        "ConcurrencyToken",
    )
    require_text(
        ROOT / "src/TCJ.Messaging.Sagas.EntityFrameworkCore/Processing/SagaStateRuntime.cs",
        "SHA256",
        "MaximumStatePayloadBytes",
        "CancelAllActiveTimersAsync",
        "RetireActiveCorrelationsAsync",
    )
    require_text(
        ROOT / "src/TCJ.EntityFrameworkCore/Inbox/Processing/InboxCoordinator.cs",
        "DbUpdateConcurrencyException",
        "InboxFailureType.ConcurrencyConflict",
    )
    require_text(
        ROOT / "src/TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer/Extensions/SqlServerSagaModelBuilderExtensions.cs",
        "IsRowVersion()",
        "IsConcurrencyToken()",
        "[RemovedAtUtc] IS NULL",
    )
    require_text(
        ROOT / "src/TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer/Processing/SqlServerSagaProviderStorage.cs",
        "UPDLOCK",
        "READPAST",
        "ROWLOCK",
        "LockExpiresAtUtc",
        "2601",
        "2627",
    )

    unit_test = ROOT / policy["unitTestProject"]
    sql_test = ROOT / policy["sqlServerTestProject"]
    unit_source = "\n".join(p.read_text(encoding="utf-8-sig") for p in unit_test.parent.glob("*.cs"))
    sql_source = "\n".join(p.read_text(encoding="utf-8-sig") for p in sql_test.parent.glob("*.cs"))
    for name in policy["requiredUnitTests"]:
        if name not in unit_source:
            fail(f"Required Saga unit test is missing: {name}")
    for name in policy["requiredSqlServerTests"]:
        if name not in sql_source:
            fail(f"Required real SQL Server Saga test is missing: {name}")
    require_text(sql_test, "Testcontainers.MsSql")
    compatibility_source = require_text(
        ROOT / "tests/TCJ.Messaging.CompatibilityTests/CompatibilityTests.cs",
        "saga-orchestration",
        "SagaTransportCompatibilityTests",
        "SagaStatus.Completed",
        "InboxHandlingOutcome.IgnoreDuplicate",
        "DequeueDurableOutbox",
    )
    for transport in policy["requiredTransportScenarios"]:
        if f'Trait("Transport", "{transport}")' not in compatibility_source:
            fail(f"Focused Saga transport compatibility scenario is missing for {transport}.")

    package_set = set(PACKAGE_IDS)
    if not package_set <= runtime_release_packages(ROOT / "eng/release-manifest.json"):
        fail("Release manifest is missing Saga runtime packages.")
    architecture = read_json(ROOT / "eng/architecture-policy.json")
    if not package_set <= set(architecture.get("assemblies", {})):
        fail("Architecture policy is missing Saga assemblies.")
    sbom = read_json(ROOT / "eng/sbom-policy.json")
    sbom_ids = {entry.get("id") for entry in sbom.get("releasePackages", {}).get("runtime", []) if isinstance(entry, dict)}
    if not package_set <= sbom_ids:
        fail("SBOM policy is missing Saga packages.")
    if not package_set <= aot_packages(ROOT / "eng/aot-policy.json"):
        fail("AOT policy is missing Saga packages.")
    for rel in ("eng/reproducibility-policy.json", "eng/compatibility-policy.json", "eng/upgrade-compatibility-policy.json"):
        data = read_json(ROOT / rel)
        if not package_set <= set(data.get("requiredPackages", [])):
            fail(f"{rel} is missing Saga packages.")

    compatibility = read_json(ROOT / "eng/compatibility-policy.json")
    if policy["packageConsumer"] not in {item.get("name") for item in compatibility.get("consumers", []) if isinstance(item, dict)}:
        fail("Package-only Saga consumer is missing from compatibility policy.")
    upgrade = read_json(ROOT / "eng/upgrade-compatibility-policy.json")
    if policy["upgradeScenario"] not in {item.get("name") for item in upgrade.get("targetOnlyScenarios", []) if isinstance(item, dict)}:
        fail("Saga target-only upgrade scenario is missing.")

    require_text(ROOT / policy["documentation"], "existing transactional Inbox", "existing Outbox", "rowversion", "compensation", "not a database rollback", "global exactly-once", "distributed ACID", "Consumer-controlled migrations", "case-sensitive")
    require_text(ROOT / policy["workflow"], "name: Saga orchestration", "verify-sagas.py", "TCJ.Messaging.Sagas.SqlServer.Tests", "SagaTransportCompatibilityTests", "upload-artifact")
    require_text(ROOT / ".github/workflows/ci.yml", "verify-sagas.py validate-config")
    require_text(ROOT / ".github/workflows/release-preflight.yml", "verify-sagas.py", "saga-summary.json", "sourceCommit")
    require_text(ROOT / ".github/workflows/release.yml", "verify-sagas.py validate-config")
    require_text(ROOT / ".github/workflows/required-pr-gate.yml", "sagas.yml")
    require_text(ROOT / ".github/workflows/concurrency-stress.yml", "TCJ.Messaging.Sagas.SqlServer.Tests")
    require_text(ROOT / ".github/workflows/resilience.yml", "TCJ.Messaging.Sagas.SqlServer.Tests")
    require_text(ROOT / ".github/workflows/published-package-smoke.yml", "saga-smoke", "EnableSagaSmoke")
    require_text(ROOT / "smoke/TCJ.PublishedPackages.SmokeTest/TCJ.PublishedPackages.SmokeTest.csproj", "EnableSagaSmoke", "TCJ.Messaging.Sagas")
    require_text(ROOT / "smoke/TCJ.PublishedPackages.SmokeTest/Program.cs", "TCJ_SAGA_SMOKE", "SagaCorrelationKey", "TcjSagaOptions")

    gitignore = require_text(ROOT / ".gitignore", "artifacts/")
    if policy["generatedArtifactDirectory"] not in gitignore and "artifacts/" not in gitignore:
        fail("Generated Saga artifacts must be ignored by Git.")

    critical = [
        POLICY,
        CONTRACT,
        ROOT / "eng/verify-sagas.py",
        unit_test,
        sql_test,
        ROOT / policy["workflow"],
        ROOT / policy["documentation"],
    ]
    for path in critical:
        ensure_not_ignored(path)
    return policy, contract


def trx_test_names(results: Path) -> tuple[int, int, list[str]]:
    total = failed = 0
    names: list[str] = []
    trx_files = sorted(results.rglob("*.trx"))
    if not trx_files:
        fail(f"No Saga TRX evidence found under {results}.")
    for trx in trx_files:
        try:
            root = ET.parse(trx).getroot()
        except ET.ParseError as error:
            fail(f"Malformed TRX evidence {trx}: {error}")
        for node in root.iter():
            if not node.tag.endswith("UnitTestResult"):
                continue
            total += 1
            name = node.attrib.get("testName", "")
            names.append(name)
            if node.attrib.get("outcome") != "Passed":
                failed += 1
    return total, failed, names


def scan_sensitive(paths: Iterable[Path], markers: Iterable[str]) -> list[dict[str, str]]:
    findings: list[dict[str, str]] = []
    normalized = tuple(marker for marker in markers if marker)
    for root in paths:
        if not root.exists():
            continue
        files = [root] if root.is_file() else root.rglob("*")
        for file in files:
            if not file.is_file():
                continue
            text = file.read_text(encoding="utf-8", errors="ignore")
            for marker in normalized:
                if marker in text:
                    findings.append({"file": file.as_posix(), "marker": marker})
    return findings


def verify(results: Path, output: Path) -> None:
    policy, _ = validate_config()
    total, failed, names = trx_test_names(results)
    if failed:
        fail(f"{failed} Saga tests failed or were not executed successfully.")
    required = set(policy["requiredUnitTests"]) | set(policy["requiredSqlServerTests"])
    executed = {name.rsplit(".", 1)[-1].split("(", 1)[0] for name in names}
    missing = sorted(required - executed)
    if missing:
        fail(f"Required Saga test evidence is missing: {', '.join(missing)}")
    transport_executed = {transport for transport in policy["requiredTransportScenarios"] if any(f"SagaTransportCompatibilityTests.{transport}" in name for name in names)}
    if transport_executed != set(policy["requiredTransportScenarios"]):
        fail("Focused cross-transport Saga evidence is incomplete.")

    output.mkdir(parents=True, exist_ok=True)
    findings = scan_sensitive([results, output], policy.get("sensitiveMarkers", []))
    sensitive = {"schemaVersion": 1, "status": "pass" if not findings else "fail", "findings": findings}
    (output / "sensitive-data-scan.json").write_text(json.dumps(sensitive, indent=2) + "\n", encoding="utf-8")
    if findings:
        fail("Sensitive Saga marker detected in generated evidence.")

    commit = os.environ.get("GITHUB_SHA") or "local"
    version = str(read_json(ROOT / "eng/release-manifest.json").get("version", "unknown"))
    summary = {
        "schemaVersion": 1,
        "sourceCommit": commit,
        "packageVersion": version,
        "executedTestCount": total,
        "failedTestCount": failed,
        "requiredSqlServerEvidence": "passed",
        "crossTransportEvidence": sorted(transport_executed),
        "sensitiveDataScanStatus": "passed",
        "overallResult": "passed",
    }
    (output / "saga-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    lines = [
        "# Durable Saga orchestration summary",
        "",
        f"- Source commit: `{commit}`",
        f"- Package version: `{version}`",
        f"- Executed tests: **{total}**",
        "- SQL Server concurrency/timer evidence: **passed**",
        "- Focused transport scenarios: **" + ", ".join(sorted(transport_executed)) + "**",
        "- Sensitive-data scan: **passed**",
        "- Distributed ACID: **not claimed**",
        "- Global exactly-once: **not claimed**",
        "",
        "**Overall: PASS**",
        "",
    ]
    (output / "SAGAS_SUMMARY.md").write_text("\n".join(lines), encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("validate-config")
    verify_parser = sub.add_parser("verify")
    verify_parser.add_argument("--results", type=Path, required=True)
    verify_parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "validate-config":
            validate_config()
            print("Durable Saga configuration is valid.")
        else:
            verify(args.results, args.output)
            print("Durable Saga verification passed.")
        return 0
    except SagaVerificationError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
