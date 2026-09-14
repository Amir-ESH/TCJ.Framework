#!/usr/bin/env python3
"""Fail-closed aggregation of current-source messaging evidence; no broker diagnostics in reports."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
POLICY = "eng/messaging-compatibility-policy.json"
CONTRACT = "eng/messaging-compatibility-contract.json"
STATUSES = {"Supported", "Unsupported", "Emulated", "ConventionBased", "TransportSpecific", "NotApplicable", "Blocked"}
TRANSPORTS = {"InMemory", "RabbitMQ", "AzureServiceBus", "Kafka"}


class VerificationError(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise VerificationError(message)


def read_json(path):
    try:
        result = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        raise VerificationError(f"Missing or malformed JSON: {Path(path).name}") from None
    require(isinstance(result, dict), "Expected JSON object.")
    return result


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def command(args):
    result = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, check=False)
    require(result.returncode == 0, f"Required command failed: {Path(args[0]).name} {' '.join(args[1:3])}")
    return result.stdout.strip()


def source_digest():
    hasher = hashlib.sha256()
    paths = []
    for folder in ["src", "tests", "compatibility", "eng", ".github/workflows"]:
        paths.extend((ROOT / folder).rglob("*"))
    paths.extend(ROOT.glob("*.props"))
    paths.extend([ROOT / "global.json", ROOT / "TCJ.slnx"])
    for path in sorted(set(paths)):
        if not path.is_file() or any(p.lower() in {"bin", "obj", "artifacts", "testresults", "__pycache__"} for p in path.parts):
            continue
        if path.suffix not in {".cs", ".csproj", ".json", ".py", ".yml", ".props", ".slnx"}:
            continue
        hasher.update(path.relative_to(ROOT).as_posix().encode())
        # Git checkouts may normalize CRLF; source identity must be platform-independent.
        hasher.update(path.read_bytes().replace(b"\r\n", b"\n"))
    return hasher.hexdigest()


def context():
    return {"sourceSha": command(["git", "rev-parse", "HEAD"]),
            "runId": os.environ.get("GITHUB_RUN_ID", "local"),
            "runAttempt": os.environ.get("GITHUB_RUN_ATTEMPT", "local"),
            "sourceDigest": source_digest()}


def adapter_capabilities(name, spec):
    raw = read_json(ROOT / spec["adapterContract"])
    if name == "InMemory":
        # InMemory has no separate adapter contract. Its runtime declaration is checked below.
        return spec["descriptorCapabilities"]
    caps = raw.get("capabilities", raw.get("transportDescriptor", {}).get("capabilities", {}))
    return {k[0].upper() + k[1:]: v for k, v in caps.items()}


def validate_config(run_adapters=True):
    policy, contract = read_json(ROOT / POLICY), read_json(ROOT / CONTRACT)
    require(policy.get("schemaVersion") == contract.get("schemaVersion") == contract.get("matrixSchemaVersion") == 1, "Matrix schema drift.")
    require(set(policy.get("supportedTransports", [])) == set(contract.get("transports", {})) >= TRANSPORTS, "Missing supported transport.")
    require(set(contract.get("statuses", [])) == STATUSES, "Compatibility status drift.")
    for flag in "ExistingConformanceSuite AdapterSpecificValidation CapabilityTruthfulness UnsupportedCapabilityTests InboxScenarios OutboxScenarios SettlementScenarios OrderingScopeTests FailureCategoryTests ObservabilityScenarios HealthScenarios SensitiveDataScan MachineReadableMatrix MarkdownMatrix PublishedPackageEvidence".split():
        require(policy.get("require" + flag) is True, f"Required policy flag disabled: {flag}")
    neutral = read_json(ROOT / "eng/messaging-contract.json")
    for key in ["failureCategories", "activities", "metrics", "healthChecks"]:
        require(contract.get(key) == neutral[key], f"Neutral {key} drift.")
    require(contract.get("normalizedEnvelopeFields") == neutral["rawEnvelopeFields"], "Envelope normalization drift.")
    require(set(policy.get("requiredScenarios", [])) >= {"identity", "headers", "inbox-order", "inbox-failure", "ordering", "publish-telemetry", "readiness", "settlement", "unsupported-options", "batch"}, "Required scenario missing.")
    packages = {p.stem for p in (ROOT / "src").glob("TCJ.Messaging.*/*.csproj")}
    required_packages = {Path(s["testProject"]).stem.removesuffix(".Tests") for n, s in contract["transports"].items() if n != "InMemory"}
    require(packages == required_packages, "Every production adapter must join the compatibility matrix before release.")
    for name, spec in contract["transports"].items():
        require((ROOT / spec["adapterContract"]).is_file(), "Adapter contract missing from matrix inputs.")
        require(spec["descriptorCapabilities"] == adapter_capabilities(name, spec), f"{name}: adapter contract mismatch.")
        require(set(spec.get("capabilities", {})) == set(neutral["capabilities"]), f"{name}: capability mapping incomplete.")
        require(set(spec.get("settlements", {})) == set(neutral["settlements"]), f"{name}: settlement mapping incomplete.")
        require(spec.get("orderingScope") == spec["descriptorCapabilities"]["OrderingGuarantee"], "Ordering scope overstated.")
        for cap, entry in spec["capabilities"].items():
            declared = spec["descriptorCapabilities"]["Supports" + cap]
            require(type(declared) is bool, "Capability declaration must be Boolean.")
            require(entry.get("status") in STATUSES - {"Blocked"}, "Blocked or invalid capability status.")
            require(declared == (entry["status"] not in {"Unsupported", "NotApplicable"}), "Capability/status disagreement.")
        for entry in list(spec["capabilities"].values()) + list(spec["settlements"].values()):
            require(entry.get("status") in STATUSES - {"Blocked"} and entry.get("evidence"), "Missing capability/settlement evidence mapping.")
            for proof in entry["evidence"]:
                require(isinstance(proof, str) and re.fullmatch(r"(matrix|adapter|neutral|conformance):[A-Za-z0-9_-]+", proof), "Invalid evidence reference.")
        if run_adapters:
            command([sys.executable, spec["validator"], "validate-config"])
    project = (ROOT / policy["testProject"]).read_text(encoding="utf-8")
    require("TestProject.props" in project and "net10.0" in project and "TCJ.Messaging.ConformanceTests" in project, "Compatibility project must remain test-only and reuse conformance.")
    required_files = [POLICY, CONTRACT, "eng/verify-messaging-compatibility.py", policy["testProject"], "docs/messaging-transport-matrix.md", ".github/workflows/messaging-compatibility.yml"]
    for relative in required_files:
        require((ROOT / relative).is_file(), f"Required file missing: {relative}")
        result = subprocess.run(["git", "check-ignore", "-q", "--", relative], cwd=ROOT, check=False)
        require(result.returncode == 1, f"Required source ignored by Git: {relative}")
    for path in [".github/workflows/release-preflight.yml", ".github/workflows/release.yml", ".github/workflows/required-pr-gate.yml"]:
        require("messaging-compatibility.yml" in (ROOT / path).read_text(), "CI/release integration missing.")
    return policy, contract


def trx_tests(directory):
    """Read executed test identities, not just optimistic counters or substrings."""
    found = set()
    files = sorted(Path(directory).rglob("*.trx"))
    require(files, "Required adapter/neutral TRX evidence missing.")
    for path in files:
        try:
            root = ET.parse(path).getroot()
        except (OSError, ET.ParseError):
            raise VerificationError("Malformed TRX evidence.") from None
        results = [n for n in root.iter() if n.tag.endswith("}UnitTestResult") or n.tag == "UnitTestResult"]
        require(results, "Empty TRX evidence.")
        for node in results:
            require(node.get("outcome") == "Passed", "Failed/skipped adapter evidence cannot satisfy compatibility.")
            name = node.get("testName", "").split("(")[0].rsplit(".", 1)[-1]
            require(bool(re.fullmatch(r"[A-Za-z0-9_]+", name)), "Invalid test identity.")
            found.add(name)
    return found


def scan_sensitive(directory, policy):
    patterns = [r"(?i)amqps?://[^\s/]+:[^\s/]+@", r"(?i)SharedAccessKey\s*=\s*[^;\s]+", r"(?i)Bearer\s+[A-Za-z0-9_.-]{8,}", r"(?i)(password|access.token|api.key)\s*[:=]\s*(?!<redacted>)[^\s;\"<]{4,}"]
    count = 0
    for path in Path(directory).rglob("*"):
        if not path.is_file():
            continue
        require(not path.is_symlink(), "Evidence symlinks are not allowed.")
        require(path.stat().st_size <= policy["maximumEvidenceFileBytes"], "Evidence file exceeds bounded scan size.")
        value = path.read_bytes().decode("utf-8", errors="replace")
        require(not any(marker.lower() in value.lower() for marker in policy["sensitiveMarkers"]), "Sensitive test marker leaked into evidence.")
        require(not any(re.search(pattern, value) for pattern in patterns), "Credential-shaped data leaked into evidence.")
        count += 1
    require(count > 0, "Sensitive scan cannot pass without files.")
    return count


def evidence_bundle(results, expected_context, policy):
    require(read_json(results / "context.json") == expected_context, "Stale or cross-run evidence context.")
    scan_sensitive(results, policy)
    return trx_tests(results)


def verify(results, output, expected_context=None, run_adapters=True):
    policy, contract = validate_config(run_adapters)
    expected_context = expected_context or context()
    scan_sensitive(results, policy)
    neutral = evidence_bundle(results / "neutral", expected_context, policy)
    conformance = evidence_bundle(results / "conformance", expected_context, policy)
    require(set(contract["requiredNeutralTests"]) <= neutral, "Missing neutral Inbox/Outbox/serializer/health evidence.")
    require(set(contract["requiredConformanceTests"]) <= conformance, "Missing existing conformance evidence.")
    rows = []
    for name, spec in contract["transports"].items():
        report = read_json(results / name / (name + ".json"))
        matrix_tests = evidence_bundle(results / name, expected_context, policy)
        require(name in matrix_tests, "Matrix scenario report has no matching executed test.")
        require(report.get("schemaVersion") == 1 and report.get("transport") == name, "Scenario schema/transport mismatch.")
        require(report.get("context") == expected_context, "Stale or cross-run scenario results.")
        require(report.get("overall") == "pass", f"{name}: Blocked scenario.")
        descriptor = report.get("descriptor") or {}
        require(descriptor.get("name") == spec["runtimeName"] and isinstance(descriptor.get("version"), str) and re.fullmatch(r"[0-9A-Za-z.+-]{1,64}", descriptor["version"]), "Runtime descriptor identity mismatch.")
        caps = descriptor.get("capabilities", {})
        for key, value in spec["descriptorCapabilities"].items():
            actual = caps.get(key[0].lower() + key[1:])
            require(type(actual) is type(value) and actual == value, f"{name}: runtime descriptor/contract mismatch for {key}.")
        scenarios = report.get("scenarios", [])
        require(isinstance(scenarios, list), "Malformed scenarios.")
        executed = {s.get("scenario") for s in scenarios}
        require(len(executed) == len(scenarios) and executed == set(policy["requiredScenarios"]), "Missing/duplicate/unexpected scenario evidence.")
        require(all(s.get("outcome") == "Passed" and s.get("actualStatus") == "Supported" and type(s.get("durationMs")) is int and 0 <= s["durationMs"] < 180000 for s in scenarios), "Blocked or malformed scenario result.")
        adapter = neutral if name == "InMemory" else evidence_bundle(results / (name + "-adapter"), expected_context, policy)
        if name != "InMemory":
            adapter_policy = read_json(ROOT / spec["adapterContract"].replace("-contract.json", "-policy.json"))
            require(len(adapter) >= adapter_policy["minimumIntegrationTestCount"], f"{name}: insufficient independent adapter evidence.")
        require(set(spec["requiredAdapterTests"]) <= adapter, f"{name}: required adapter-specific evidence missing.")
        sources = {"matrix": executed, "adapter": adapter, "neutral": neutral, "conformance": conformance}
        for entry in list(spec["capabilities"].values()) + list(spec["settlements"].values()):
            for proof in entry["evidence"]:
                source, identity = proof.split(":", 1)
                require(identity in sources[source], f"{name}: missing declared capability scenario {proof}.")
        package = read_json(results / "packages" / (name + ".json"))
        require(package.get("context") == expected_context and package.get("overall") == "pass", "Missing/stale package-only evidence.")
        require(package.get("descriptor") == descriptor, "Packed/source descriptor mismatch.")
        require(package.get("packageOnly") is True, "Package-only evidence missing.")
        require(package.get("version") == read_json(ROOT / "eng/release-manifest.json")["version"], "Package evidence version differs from release candidate.")
        rows.append({"transport": name, "descriptor": descriptor, "adapterContract": spec["adapterContract"], "adapterContractHash": digest(ROOT / spec["adapterContract"]), "capabilities": spec["capabilities"], "settlements": spec["settlements"], "orderingScope": spec["orderingScope"], "retryOwnership": spec["retryOwnership"], "scenarios": scenarios, "adapterTests": sorted(adapter), "packageEvidence": "pass", "packageVersion": package["version"]})
    matrix = {"schemaVersion": 1, **expected_context, "transports": rows, "mismatches": [], "overall": "pass"}
    output.mkdir(parents=True, exist_ok=True)
    target = output / "compatibility-matrix.json"
    target.write_text(json.dumps(matrix, indent=2) + "\n", encoding="utf-8")
    lines = ["# Messaging compatibility", "", f"Source: `{expected_context['sourceSha']}`", "", "| Transport | Ordering scope | Capabilities | Result |", "| --- | --- | --- | --- |"]
    for row in rows:
        summary = "; ".join(f"{k}: {v['status']}" for k, v in row["capabilities"].items())
        lines.append(f"| {row['transport']} | {row['orderingScope']} | {summary} | pass |")
    (output / "MESSAGING_COMPATIBILITY_SUMMARY.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    require(read_json(target) == matrix, "Generated matrix missing or schema drifted.")
    scan_sensitive(output, policy)
    return matrix


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["validate-config", "record-context", "verify"])
    parser.add_argument("--results", type=Path, default=ROOT / "TestResults/MessagingCompatibility")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/messaging-compatibility")
    args = parser.parse_args()
    try:
        if args.command == "validate-config":
            validate_config()
        elif args.command == "record-context":
            args.results.mkdir(parents=True, exist_ok=True)
            (args.results / "context.json").write_text(json.dumps(context(), indent=2) + "\n", encoding="utf-8")
        else:
            verify(args.results, args.output)
        print("Messaging compatibility " + args.command + " passed.")
        return 0
    except (VerificationError, KeyError, TypeError, ValueError, OSError):
        # Never echo raw evidence or credentials from exception text.
        if args.command == "verify":
            args.output.mkdir(parents=True, exist_ok=True)
            failure = {"schemaVersion": 1, "overall": "fail", "mismatches": ["InvalidOrIncompleteEvidence"],
                       "transports": [{"transport": name, "status": "Blocked"} for name in sorted(TRANSPORTS)]}
            try:
                failure.update(context())
            except VerificationError:
                pass
            (args.output / "compatibility-matrix.json").write_text(json.dumps(failure, indent=2) + "\n", encoding="utf-8")
            (args.output / "MESSAGING_COMPATIBILITY_SUMMARY.md").write_text("# Messaging compatibility\n\n**Blocked**: current evidence is invalid, incomplete, inconsistent or contains sensitive output. No passing release matrix was produced.\n", encoding="utf-8")
        print("Messaging compatibility failed: invalid configuration, incomplete evidence, semantic mismatch or sensitive output.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
