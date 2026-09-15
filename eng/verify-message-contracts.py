#!/usr/bin/env python3
"""Fail-closed repository verification for Step 52 message-contract governance."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
POLICY_PATH = Path("eng/message-contract-policy.json")
CONTRACT_PATH = Path("eng/message-contract-governance-contract.json")
BANNED_PACKAGE_REFERENCES = {
    "Microsoft.EntityFrameworkCore", "Microsoft.Data.SqlClient", "Microsoft.AspNetCore",
    "RabbitMQ.Client", "Azure.Messaging.ServiceBus", "Confluent.Kafka"
}
MODES = {"None", "Backward", "Forward", "Full", "BackwardTransitive", "ForwardTransitive", "FullTransitive"}
RESULTS = {"Compatible", "Breaking", "ReviewRequired"}
CLASSIFICATIONS = {"Public", "Internal", "Personal", "Confidential", "SecretProhibited"}
SECRET_MARKERS = ("password=", "sharedaccesskey=", "bearer ", "private_key", "client_secret")

class VerificationError(RuntimeError):
    pass

def require(condition: bool, message: str) -> None:
    if not condition:
        raise VerificationError(message)

def read_json(relative: str | Path) -> dict:
    path = ROOT / relative
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        raise VerificationError(f"Missing or malformed JSON: {relative}") from None
    require(isinstance(value, dict), f"Expected JSON object: {relative}")
    return value

def safe_relative_path(value: str) -> bool:
    if not value or len(value) > 512 or "\\" in value or "\x00" in value:
        return False
    path = Path(value)
    return not path.is_absolute() and ".." not in path.parts

def contains_secret_marker(data: bytes) -> bool:
    text = data.decode("utf-8", errors="ignore").lower()
    return any(marker in text for marker in SECRET_MARKERS)

def validate_config() -> tuple[dict, dict]:
    policy = read_json(POLICY_PATH)
    contract = read_json(CONTRACT_PATH)
    require(policy.get("schemaVersion") == contract.get("schemaVersion") == 1, "Governance schema version drift.")
    require(policy.get("manifestSchemaVersion") == contract.get("manifestSchemaVersion") == 1, "Manifest schema version drift.")
    require(policy.get("jsonSchemaDraft") == contract.get("schemaDraft") == "https://json-schema.org/draft/2020-12/schema", "JSON Schema draft drift.")
    require(policy.get("canonicalizationVersion") == contract.get("canonicalization", {}).get("version") == 1, "Canonicalization version drift.")
    require(policy.get("fingerprintAlgorithm") == contract.get("fingerprint", {}).get("algorithm") == "SHA-256", "Fingerprint algorithm drift.")
    require(set(policy.get("supportedCompatibilityModes", [])) == set(contract.get("compatibilityModes", {})) == MODES, "Compatibility mode drift.")
    require(set(policy.get("compatibilityResults", [])) == set(contract.get("compatibilityResults", [])) == RESULTS, "Compatibility result drift.")
    require(set(policy.get("dataClassifications", [])) == set(contract.get("dataClassifications", [])) == CLASSIFICATIONS, "Classification drift.")
    require(policy.get("reviewRequiredBehavior") == "FailUnlessExplicitlyReviewed", "ReviewRequired must fail unless explicitly reviewed.")
    require(policy.get("publishedVersionImmutability") is True, "Published-version immutability must remain enabled.")
    require(policy.get("secretProhibitedBehavior") == "Fail", "SecretProhibited must fail governance.")
    runtime = contract.get("runtimeSemantics", {})
    require(runtime.get("runtimeRegistry") == "TCJ.Messaging.IMessageContractRegistry", "Runtime registry ownership drift.")
    require(runtime.get("upcaster") == "TCJ.Messaging.IMessageUpcaster" and runtime.get("downcastingSupported") is False, "Upcaster/downcaster contract drift.")
    require(runtime.get("externalRegistryRequired") is False and runtime.get("runtimeJsonSchemaValidationRequired") is False, "Runtime governance scope drift.")

    for relative in policy.get("requiredPaths", []):
        require(safe_relative_path(relative) and (ROOT / relative).is_file(), f"Required message-contract file missing: {relative}")

    governance_project = (ROOT / "src/TCJ.Messaging.Contracts/TCJ.Messaging.Contracts.csproj").read_text(encoding="utf-8")
    require("../TCJ.Messaging/TCJ.Messaging.csproj" in governance_project.replace("\\", "/"), "Governance package must depend on TCJ.Messaging.")
    for package in BANNED_PACKAGE_REFERENCES:
        require(package not in governance_project, f"Governance package must not depend on provider/runtime SDK: {package}")
    messaging_project = (ROOT / "src/TCJ.Messaging/TCJ.Messaging.csproj").read_text(encoding="utf-8")
    require("TCJ.Messaging.Contracts" not in messaging_project, "TCJ.Messaging must not depend on governance package.")
    for project in ["TCJ.EntityFrameworkCore", "TCJ.AspNetCore", "TCJ.Messaging.RabbitMQ", "TCJ.Messaging.AzureServiceBus", "TCJ.Messaging.Kafka"]:
        text = (ROOT / f"src/{project}/{project}.csproj").read_text(encoding="utf-8")
        require("TCJ.Messaging.Contracts" not in text, f"{project} must not depend on governance package.")

    architecture = read_json("eng/architecture-policy.json")
    require(architecture.get("assemblies", {}).get("TCJ.Messaging.Contracts") == ["TCJ.Messaging"], "Architecture policy must constrain governance package to TCJ.Messaging.")
    release = read_json("eng/release-manifest.json")
    runtime_packages = {item["id"] for item in release.get("releasePackages", {}).get("runtime", [])}
    require("TCJ.Messaging.Contracts" in runtime_packages, "Governance package missing from release manifest.")
    sbom = read_json("eng/sbom-policy.json")
    require("TCJ.Messaging.Contracts" in {item["id"] for item in sbom.get("releasePackages", {}).get("runtime", [])}, "Governance package missing from SBOM policy.")
    require("TCJ.Messaging.Contracts" in read_json("eng/reproducibility-policy.json").get("requiredPackages", []), "Governance package missing from reproducibility policy.")
    require("TCJ.Messaging.Contracts" in read_json("eng/compatibility-policy.json").get("requiredPackages", []), "Governance package missing from compatibility policy.")
    upgrade = read_json("eng/upgrade-compatibility-policy.json")
    require("TCJ.Messaging.Contracts" in upgrade.get("targetOnlyPackages", []), "New governance package must be target-only in upgrade policy.")
    aot = read_json("eng/aot-policy.json")
    require(any(item.get("packageId") == "TCJ.Messaging.Contracts" for item in aot.get("packages", [])), "Governance package missing from AOT policy.")

    for workflow in policy.get("requiredWorkflowIntegrations", []):
        text = (ROOT / workflow).read_text(encoding="utf-8")
        require("message-contract" in text.lower(), f"Message-contract workflow integration missing: {workflow}")
    workflow_text = (ROOT / ".github/workflows/message-contracts.yml").read_text(encoding="utf-8")
    require("verify-message-contracts.py" in workflow_text and "TCJ.Messaging.Contracts.Tests" in workflow_text, "Dedicated workflow is incomplete.")
    gitignore = (ROOT / ".gitignore").read_text(encoding="utf-8")
    require("artifacts/message-contracts" in gitignore and "TestResults/MessageContracts" in gitignore, "Generated message-contract outputs must be ignored.")
    return policy, contract

def verify(results: Path, output: Path, source_commit: str, package_consumer_evidence: Path) -> Path:
    validate_config()
    require(re.fullmatch(r"[0-9a-fA-F]{40}", source_commit) is not None, "Verification requires a full commit SHA.")
    try:
        package_evidence = json.loads(package_consumer_evidence.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        raise VerificationError("Package-consumer evidence is missing or malformed.") from None
    require(isinstance(package_evidence, dict), "Package-consumer evidence must be a JSON object.")
    require(package_evidence.get("schemaVersion") == 1, "Package-consumer evidence schema version is invalid.")
    require(package_evidence.get("sourceCommit") == source_commit.lower(), "Package-consumer evidence does not match the verified commit.")
    require(package_evidence.get("status") == "passed", "Package-consumer evidence did not pass.")
    require(package_evidence.get("packageId") == "TCJ.Messaging.Contracts", "Package-consumer evidence package identity is invalid.")
    require(package_evidence.get("packageVersion") == read_json("eng/release-manifest.json").get("version"), "Package-consumer evidence version does not match the release manifest.")
    trx = sorted(results.rglob("*.trx"))
    require(trx, "Message-contract test TRX evidence is missing.")
    manifests = sorted(output.rglob("manifest.json"))
    require(len(manifests) >= 2, "Deterministic generation evidence requires at least two manifests.")
    hashes = [hashlib.sha256(path.read_bytes()).hexdigest() for path in manifests if path.parts[-2] in {"run-a", "run-b"}]
    require(len(hashes) >= 2 and len(set(hashes)) == 1, "Repeated generated manifests are not byte-identical.")
    for path in output.rglob("*"):
        if path.is_file():
            require(path.stat().st_size <= 16 * 1024 * 1024, f"Generated evidence exceeds bounded size: {path.name}")
            require(not contains_secret_marker(path.read_bytes()), f"Sensitive marker found in generated message-contract evidence: {path.name}")
    output.mkdir(parents=True, exist_ok=True)
    evidence = {
        "schemaVersion": 1,
        "sourceCommit": source_commit.lower(),
        "testResultCount": len(trx),
        "deterministicManifestSha256": hashes[0],
        "packageConsumerEvidenceSha256": hashlib.sha256(package_consumer_evidence.read_bytes()).hexdigest(),
        "packageConsumerStatus": "passed",
        "status": "passed"
    }
    evidence_path = output / "message-contract-evidence.json"
    evidence_path.write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
    return evidence_path

def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("validate-config")
    verify_parser = sub.add_parser("verify")
    verify_parser.add_argument("--results", required=True)
    verify_parser.add_argument("--output", required=True)
    verify_parser.add_argument("--source-commit", default=os.environ.get("GITHUB_SHA", ""))
    verify_parser.add_argument("--package-consumer-evidence", required=True)
    args = parser.parse_args()
    try:
        if args.command == "validate-config":
            validate_config()
            print("Message-contract governance configuration is valid.")
        else:
            path = verify(Path(args.results), Path(args.output), args.source_commit, Path(args.package_consumer_evidence))
            print(path)
        return 0
    except VerificationError as error:
        print(f"message-contract verification failed: {error}", file=sys.stderr)
        return 1

if __name__ == "__main__":
    raise SystemExit(main())
