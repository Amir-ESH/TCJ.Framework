#!/usr/bin/env python3
"""Validate TCJ observability contracts, tests, workflows, and generated evidence."""

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
POLICY_PATH = ROOT / "eng/observability-policy.json"
CONTRACT_PATH = ROOT / "eng/observability-contract.json"
TEST_PROJECT = ROOT / "tests/TCJ.Observability.Tests/TCJ.Observability.Tests.csproj"
DIAGNOSTIC_NAMES = ROOT / "src/TCJ.Core/Diagnostics/TcjDiagnosticNames.cs"
PACKAGING_PROPS = ROOT / "eng/Packaging.props"


class ObservabilityError(RuntimeError):
    pass


def fail(message: str) -> None:
    raise ObservabilityError(message)


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


def require_schema_one(document: dict[str, Any], name: str) -> None:
    if document.get("schemaVersion") != 1:
        fail(f"{name} schemaVersion must be 1.")


def require_unique_strings(values: Any, field: str) -> list[str]:
    if not isinstance(values, list) or not values:
        fail(f"{field} must be a non-empty array.")
    if any(not isinstance(value, str) or not value.strip() for value in values):
        fail(f"{field} must contain non-empty strings.")
    normalized = [value.strip() for value in values]
    duplicates = sorted({value for value in normalized if normalized.count(value) > 1})
    if duplicates:
        fail(f"{field} contains duplicates: {', '.join(duplicates)}")
    return normalized


def contract_names(contract: dict[str, Any], field: str) -> list[str]:
    entries = contract.get(field)
    if not isinstance(entries, list) or not entries:
        fail(f"observability-contract.json {field} must be a non-empty array.")
    names: list[str] = []
    for entry in entries:
        if not isinstance(entry, dict) or not isinstance(entry.get("name"), str) or not entry["name"].strip():
            fail(f"observability-contract.json {field} contains an invalid entry.")
        names.append(entry["name"].strip())
    if len(names) != len(set(names)):
        fail(f"observability-contract.json {field} contains duplicate names.")
    return names


def tracked_and_not_ignored(path: Path) -> None:
    if not (ROOT / ".git").exists():
        return
    relative = path.relative_to(ROOT).as_posix()
    ignored = subprocess.run(
        ["git", "check-ignore", "--quiet", "--", relative],
        cwd=ROOT,
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        text=True,
    )
    if ignored.returncode == 0:
        fail(f"{relative} is ignored by Git and must remain tracked.")
    if ignored.returncode not in (0, 1):
        fail(f"Unable to inspect Git ignore state for {relative}: {ignored.stderr.strip()}")


def require_text(path: Path, fragments: list[str]) -> str:
    if not path.is_file():
        fail(f"Required file is missing: {path.relative_to(ROOT).as_posix()}")
    text = path.read_text(encoding="utf-8")
    missing = [fragment for fragment in fragments if fragment not in text]
    if missing:
        fail(
            f"{path.relative_to(ROOT).as_posix()} is missing required observability fragments: "
            + ", ".join(missing)
        )
    return text


def validate_name_stability(names: list[str], kind: str) -> None:
    unstable = [
        name for name in names
        if any(token in name for token in ("<", ">", "`", "{id}", "{user}", "{tenant}"))
        or re.search(r"[0-9a-f]{8}-[0-9a-f]{4}-", name, re.IGNORECASE)
    ]
    if unstable:
        fail(f"Unstable {kind} names detected: {', '.join(unstable)}")



def diagnostic_constant_map(source_code: str, class_name: str) -> dict[str, str]:
    start_marker = f"public static class {class_name}"
    start = source_code.find(start_marker)
    if start < 0:
        fail(f"TcjDiagnosticNames is missing the {class_name} constants class.")
    brace = source_code.find("{", start)
    if brace < 0:
        fail(f"Unable to parse TcjDiagnosticNames.{class_name}.")
    depth = 0
    end = -1
    for index in range(brace, len(source_code)):
        if source_code[index] == "{":
            depth += 1
        elif source_code[index] == "}":
            depth -= 1
            if depth == 0:
                end = index
                break
    if end < 0:
        fail(f"Unable to parse TcjDiagnosticNames.{class_name}.")
    block = source_code[brace:end]
    return {
        symbol: value
        for symbol, value in re.findall(
            r'public\s+const\s+string\s+(\w+)\s*=\s*"([^"]+)"\s*;',
            block,
        )
    }


def validate_production_contract_usage(source_code: str, contract: dict[str, Any]) -> None:
    activity_constants = diagnostic_constant_map(source_code, "Activities")
    metric_constants = diagnostic_constant_map(source_code, "Metrics")

    production_text = "\n".join(
        path.read_text(encoding="utf-8")
        for path in sorted((ROOT / "src").rglob("*.cs"))
        if path.resolve() != DIAGNOSTIC_NAMES.resolve()
    )

    missing_activity_usage = [
        value
        for symbol, value in activity_constants.items()
        if value in contract_names(contract, "activities")
        and f"TcjDiagnosticNames.Activities.{symbol}" not in production_text
    ]
    if missing_activity_usage:
        fail(
            "Contract activities are declared but never emitted by production code: "
            + ", ".join(sorted(missing_activity_usage))
        )

    metric_contract = {entry["name"]: entry for entry in contract["metrics"]}
    instrument_pattern = re.compile(
        r"Create(?P<kind>Counter|Histogram)<[^>]+>\(\s*"
        r"TcjDiagnosticNames\.Metrics\.(?P<symbol>\w+)\s*,\s*"
        r"unit:\s*\"(?P<unit>[^\"]+)\"",
        re.MULTILINE,
    )
    definitions: dict[str, list[tuple[str, str]]] = {}
    for match in instrument_pattern.finditer(production_text):
        symbol = match.group("symbol")
        name = metric_constants.get(symbol)
        if name is None:
            fail(f"Unknown metric constant referenced by production code: {symbol}")
        definitions.setdefault(name, []).append((match.group("kind").lower(), match.group("unit")))

    for name, entry in metric_contract.items():
        actual = definitions.get(name, [])
        if len(actual) != 1:
            fail(f"Metric {name} must have exactly one production instrument definition; found {len(actual)}.")
        kind, unit = actual[0]
        if kind != entry.get("type") or unit != entry.get("unit"):
            fail(
                f"Metric {name} metadata drift: contract={entry.get('type')}/{entry.get('unit')} "
                f"production={kind}/{unit}."
            )

    extras = sorted(set(definitions) - set(metric_contract))
    if extras:
        fail("Production metrics are missing from the committed contract: " + ", ".join(extras))


def validate_metric_dimensions(source_code: str, allowed_dimensions: list[str]) -> None:
    tag_constants = diagnostic_constant_map(source_code, "Tags")
    production_text = "\n".join(
        path.read_text(encoding="utf-8")
        for path in sorted((ROOT / "src").rglob("*.cs"))
        if path.resolve() != DIAGNOSTIC_NAMES.resolve()
    )

    metric_tag_symbols: set[str] = set()
    for match in re.finditer(
        r"TagList\s+\w+\s*=\s*(?:new\(\)|CreateOutcomeTags\([^;]+\))(?P<body>.*?)(?:;|\};)",
        production_text,
        re.DOTALL,
    ):
        metric_tag_symbols.update(
            re.findall(r"TcjDiagnosticNames\.Tags\.(\w+)", match.group(0))
        )

    # Domain-event metric tags are constructed by this helper and then passed to instruments.
    helper = re.search(
        r"private\s+static\s+TagList\s+CreateOutcomeTags\([^)]*\)\s*=>\s*new\(\)\s*\{(?P<body>.*?)\};",
        production_text,
        re.DOTALL,
    )
    if helper:
        metric_tag_symbols.update(
            re.findall(r"TcjDiagnosticNames\.Tags\.(\w+)", helper.group("body"))
        )

    # Include post-initialization additions such as the bounded HTTP status dimension.
    metric_tag_symbols.update(
        re.findall(r"tags\.Add\(TcjDiagnosticNames\.Tags\.(\w+)", production_text)
    )

    unknown_symbols = sorted(symbol for symbol in metric_tag_symbols if symbol not in tag_constants)
    if unknown_symbols:
        fail("Unknown metric tag constants used by production code: " + ", ".join(unknown_symbols))

    actual_dimensions = {tag_constants[symbol] for symbol in metric_tag_symbols}
    forbidden_dimensions = sorted(actual_dimensions - set(allowed_dimensions))
    if forbidden_dimensions:
        fail(
            "Production metrics use dimensions outside allowedMetricDimensions: "
            + ", ".join(forbidden_dimensions)
        )


def validate_configuration() -> tuple[dict[str, Any], dict[str, Any]]:
    policy = read_json(POLICY_PATH)
    contract = read_json(CONTRACT_PATH)
    require_schema_one(policy, "Observability policy")
    require_schema_one(contract, "Observability contract")
    minimum_test_count = policy.get("minimumTestCount")
    if not isinstance(minimum_test_count, int) or minimum_test_count < 10:
        fail("minimumTestCount must be an integer of at least 10.")

    tracked_and_not_ignored(POLICY_PATH)
    tracked_and_not_ignored(CONTRACT_PATH)
    tracked_and_not_ignored(TEST_PROJECT)

    required_sources = require_unique_strings(policy.get("requiredActivitySources"), "requiredActivitySources")
    required_meters = require_unique_strings(policy.get("requiredMeters"), "requiredMeters")
    required_activities = require_unique_strings(policy.get("requiredActivities"), "requiredActivities")
    required_metrics = require_unique_strings(policy.get("requiredMetrics"), "requiredMetrics")
    forbidden_patterns = [value.lower() for value in require_unique_strings(policy.get("forbiddenTagPatterns"), "forbiddenTagPatterns")]
    allowed_metric_dimensions = require_unique_strings(
        policy.get("allowedMetricDimensions"),
        "allowedMetricDimensions",
    )

    sources = contract_names(contract, "activitySources")
    meters = contract_names(contract, "meters")
    activities = contract_names(contract, "activities")
    metrics = contract_names(contract, "metrics")
    tags = require_unique_strings(contract.get("tags"), "contract.tags")
    opt_in_sensitive_tags = require_unique_strings(
        contract.get("optInSensitiveTags"),
        "contract.optInSensitiveTags",
    )
    overlap = sorted(set(tags).intersection(opt_in_sensitive_tags))
    if overlap:
        fail("Opt-in sensitive tags must not be part of the default tag contract: " + ", ".join(overlap))

    for label, required, actual in (
        ("ActivitySource", required_sources, sources),
        ("Meter", required_meters, meters),
        ("activity", required_activities, activities),
        ("metric", required_metrics, metrics),
    ):
        missing = sorted(set(required) - set(actual))
        if missing:
            fail(f"Observability contract is missing required {label} names: {', '.join(missing)}")

    validate_name_stability(sources + meters + activities + metrics, "telemetry")

    forbidden_tags = sorted(
        tag for tag in tags
        if any(pattern in tag.lower() for pattern in forbidden_patterns)
    )
    if forbidden_tags:
        fail("Observability contract contains forbidden tag names: " + ", ".join(forbidden_tags))

    source_code = require_text(
        DIAGNOSTIC_NAMES,
        required_sources + required_activities + required_metrics + tags + opt_in_sensitive_tags,
    )
    validate_production_contract_usage(source_code, contract)
    validate_metric_dimensions(source_code, allowed_metric_dimensions)
    if opt_in_sensitive_tags != ["tcj.exception.message"]:
        fail("The only approved opt-in sensitive telemetry tag is tcj.exception.message.")
    telemetry_code = require_text(
        ROOT / "src/TCJ.Core/Diagnostics/TcjTelemetry.cs",
        ["RecordExceptionMessages", "TcjDiagnosticNames.Tags.ExceptionMessage"],
    )
    if "if (RecordExceptionMessages)" not in telemetry_code:
        fail("Exception-message telemetry must remain guarded by the explicit opt-in option.")

    require_text(PACKAGING_PROPS, ['AssemblyMetadata Include="PackageVersion" Value="$(Version)"'])

    diagnostics_files = [
        ROOT / "src/TCJ.Core/Diagnostics/CoreTelemetryDiagnostics.cs",
        ROOT / "src/TCJ.DependencyInjection/Diagnostics/DependencyInjectionTelemetryDiagnostics.cs",
        ROOT / "src/TCJ.EntityFrameworkCore/Diagnostics/EntityFrameworkCoreTelemetryDiagnostics.cs",
        ROOT / "src/TCJ.EntityFrameworkCore.SqlServer/Diagnostics/SqlServerTelemetryDiagnostics.cs",
        ROOT / "src/TCJ.AspNetCore/Diagnostics/AspNetCoreTelemetryDiagnostics.cs",
    ]
    for path in diagnostics_files:
        require_text(path, ["ActivitySource", "Meter", "PackageVersion"])

    if policy.get("requireVersionedSources") is not True:
        fail("requireVersionedSources must remain enabled.")

    project = ET.parse(TEST_PROJECT).getroot()
    target_framework = (project.findtext("./PropertyGroup/TargetFramework") or "").strip()
    if target_framework != "net10.0":
        fail("TCJ.Observability.Tests must target net10.0.")

    test_text = "\n".join(path.read_text(encoding="utf-8") for path in TEST_PROJECT.parent.glob("*.cs"))
    required_test_fragments = [
        "No_listener",
        "ActivityStatusCode.Error",
        "OperationCanceledException",
        "ParentSpanId",
        "TCJ_TEST_PASSWORD_MARKER",
        "TCJ_TEST_TOKEN_MARKER",
        "TCJ_TEST_CONNECTION_STRING_MARKER",
    ]
    missing_tests = [fragment for fragment in required_test_fragments if fragment not in test_text]
    if missing_tests:
        fail("Observability tests are missing required scenarios: " + ", ".join(missing_tests))

    benchmark = require_text(
        ROOT / "benchmarks/TCJ.Benchmarks/Benchmarks/ObservabilityBenchmarks.cs",
        [
            "TelemetryDisabled",
            "TracingListenerEnabled",
            "MetricsListenerEnabled",
            "TracingAndMetricsEnabled",
        ],
    )
    if policy.get("requireDisabledOverheadBenchmark") is not True or "MemoryDiagnoser" not in benchmark:
        fail("Telemetry-disabled allocation/overhead benchmark evidence is required.")

    for project_file in (ROOT / "src").glob("*/TCJ.*.csproj"):
        text = project_file.read_text(encoding="utf-8")
        forbidden_dependencies = (
            "OpenTelemetry",
            "Datadog",
            "NewRelic",
            "ApplicationInsights",
            "Honeycomb",
            "Sentry",
        )
        found = [dependency for dependency in forbidden_dependencies if dependency.lower() in text.lower()]
        if found:
            fail(
                f"Production project {project_file.relative_to(ROOT).as_posix()} references forbidden telemetry dependencies: "
                + ", ".join(found)
            )

    workflow_fragments = [
        "python3 eng/verify-observability.py validate-config",
        "tests/TCJ.Observability.Tests/TCJ.Observability.Tests.csproj",
        "python3 eng/verify-observability.py verify",
    ]
    for workflow in (
        ROOT / ".github/workflows/ci.yml",
        ROOT / ".github/workflows/release-preflight.yml",
        ROOT / ".github/workflows/release.yml",
    ):
        require_text(workflow, workflow_fragments)

    require_text(
        ROOT / ".github/workflows/performance-benchmarks.yml",
        ["workflow_call:", '"benchmarks/**"', "python3 eng/verify-performance-results.py verify"],
    )
    for workflow in (
        ROOT / ".github/workflows/release-preflight.yml",
        ROOT / ".github/workflows/release.yml",
    ):
        require_text(workflow, ["Performance and observability overhead gate", "performance-benchmarks.yml"])
    require_text(
        ROOT / ".github/workflows/release.yml",
        ["OBSERVABILITY_SUMMARY.md", "observability-summary.json"],
    )
    require_text(
        ROOT / "samples/TCJ.Empty/Program.cs",
        ["AddOpenTelemetry()", "AddSource(", "AddMeter(", "AddOtlpExporter()", "TcjTelemetry.FrameworkVersion"],
    )
    require_text(
        ROOT / "docs/observability.md",
        [
            "ActivitySource",
            "Meter",
            "cardinality",
            "sensitive",
            "OTLP",
            "compatibility",
            *sources,
            *activities,
            *metrics,
        ],
    )
    require_text(ROOT / "eng/tests/test_verify_observability.py", ["ObservabilityVerifierTests"])

    require_text(
        ROOT / ".github/PULL_REQUEST_TEMPLATE.md",
        [
            "ActivitySource names remain stable",
            "Meter names remain stable",
            "No sensitive telemetry tags are emitted",
            "Telemetry-disabled overhead is measured",
            "Observability tests pass",
        ],
    )

    gitignore = require_text(
        ROOT / ".gitignore",
        [
            "TestResults/Observability/",
            "artifacts/observability/",
            "tests/TCJ.Observability.Tests/bin/",
            "tests/TCJ.Observability.Tests/obj/",
            "!eng/observability-policy.json",
            "!eng/observability-contract.json",
        ],
    )
    if "!tests/TCJ.Observability.Tests/**/*.cs" not in gitignore:
        fail("Observability test source files must be explicitly kept trackable in .gitignore.")

    return policy, contract


def parse_trx_results(results_dir: Path) -> tuple[int, int]:
    files = sorted(results_dir.rglob("*.trx")) if results_dir.is_dir() else []
    if not files:
        fail(f"No observability TRX files found under {results_dir.as_posix()}.")

    total = 0
    failed = 0
    for path in files:
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError as error:
            fail(f"Malformed TRX file {path.as_posix()}: {error}")
        for counters in root.iter():
            if counters.tag.endswith("Counters"):
                total += int(counters.attrib.get("total", "0"))
                failed += int(counters.attrib.get("failed", "0"))
                break

    if total <= 0:
        fail("Observability test results do not contain executed tests.")
    if failed:
        fail(f"Observability test results contain {failed} failed test(s).")
    return total, failed


def package_version() -> str:
    text = PACKAGING_PROPS.read_text(encoding="utf-8")
    match = re.search(r"<Version>([^<]+)</Version>", text)
    if not match:
        fail("Unable to read package version from eng/Packaging.props.")
    return match.group(1).strip()


def commit_sha() -> str:
    try:
        process = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=ROOT,
            check=True,
            capture_output=True,
            text=True,
        )
        return process.stdout.strip()
    except (subprocess.CalledProcessError, FileNotFoundError):
        return "unknown"


def scan_sensitive_markers(results_dir: Path, markers: list[str]) -> dict[str, Any]:
    hits: list[dict[str, str]] = []
    if results_dir.is_dir():
        for path in results_dir.rglob("*"):
            if not path.is_file() or path.suffix.lower() not in {".trx", ".xml", ".json", ".txt", ".log"}:
                continue
            text = path.read_text(encoding="utf-8", errors="replace")
            for marker in markers:
                if marker in text:
                    hits.append({"file": path.as_posix(), "marker": marker})
    return {"status": "pass" if not hits else "fail", "hits": hits}


def verify(results_dir: Path, output_dir: Path) -> None:
    policy, contract = validate_configuration()
    total, failed = parse_trx_results(results_dir)
    minimum_test_count = int(policy["minimumTestCount"])
    if total < minimum_test_count:
        fail(
            f"Observability test results contain {total} test(s); "
            f"at least {minimum_test_count} are required."
        )
    markers = require_unique_strings(policy.get("sensitiveTestMarkers"), "sensitiveTestMarkers")
    sensitive_scan = scan_sensitive_markers(results_dir, markers)
    if sensitive_scan["status"] != "pass":
        fail("Sensitive-data markers appeared in observability test artifacts.")

    output_dir.mkdir(parents=True, exist_ok=True)
    activities = contract.get("activities", [])
    metrics = contract.get("metrics", [])
    sources = contract.get("activitySources", [])
    meters = contract.get("meters", [])

    (output_dir / "activities.json").write_text(
        json.dumps({"activitySources": sources, "activities": activities}, indent=2) + "\n",
        encoding="utf-8",
    )
    (output_dir / "metrics.json").write_text(
        json.dumps({"meters": meters, "metrics": metrics}, indent=2) + "\n",
        encoding="utf-8",
    )
    (output_dir / "sensitive-data-scan.json").write_text(
        json.dumps(sensitive_scan, indent=2) + "\n",
        encoding="utf-8",
    )

    summary = {
        "schemaVersion": 1,
        "sourceCommitSha": commit_sha(),
        "packageVersion": package_version(),
        "activitySourceCount": len(sources),
        "meterCount": len(meters),
        "activityCount": len(activities),
        "metricCount": len(metrics),
        "missingContractEntries": [],
        "conflictingContractEntries": [],
        "sensitiveDataScanStatus": "pass",
        "disabledListenerBehaviorStatus": "covered-by-tests",
        "tracePropagationStatus": "covered-by-tests",
        "metricCardinalityStatus": "policy-validated-and-tested",
        "overheadBenchmarkStatus": "configured-in-performance-gate",
        "executedObservabilityTests": total,
        "failedObservabilityTests": failed,
        "overallStatus": "pass",
    }
    (output_dir / "observability-summary.json").write_text(
        json.dumps(summary, indent=2) + "\n",
        encoding="utf-8",
    )

    markdown = f"""# TCJ observability summary

- Source commit: `{summary['sourceCommitSha']}`
- Package version: `{summary['packageVersion']}`
- ActivitySources: {summary['activitySourceCount']}
- Meters: {summary['meterCount']}
- Activities: {summary['activityCount']}
- Metrics: {summary['metricCount']}
- Observability tests: {total} passed, {failed} failed
- Sensitive-data scan: **PASS**
- Disabled-listener behavior: **covered by tests**
- Trace propagation: **covered by tests**
- Metric cardinality: **policy validated and tested**
- Disabled-overhead benchmark: **configured in the performance gate**

Overall result: **PASS**
"""
    (output_dir / "OBSERVABILITY_SUMMARY.md").write_text(markdown, encoding="utf-8")
    print(markdown, end="")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("validate-config")
    verify_parser = subparsers.add_parser("verify")
    verify_parser.add_argument("--results", type=Path, required=True)
    verify_parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    try:
        if args.command == "validate-config":
            validate_configuration()
            print("Observability configuration is valid.")
        else:
            verify(args.results, args.output)
        return 0
    except ObservabilityError as error:
        print(f"Observability verification failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
