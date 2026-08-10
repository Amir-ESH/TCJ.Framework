#!/usr/bin/env python3
"""Validate TCJ performance configuration and BenchmarkDotNet reports."""

from __future__ import annotations

import argparse
import json
import math
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_POLICY = ROOT / "eng/performance-policy.json"
DEFAULT_REPORTS = ROOT / "artifacts/performance/reports"
DEFAULT_SUMMARY = ROOT / "artifacts/performance/PERFORMANCE_SUMMARY.md"
DEFAULT_JSON = ROOT / "artifacts/performance/performance-summary.json"
DEFAULT_MANIFEST_NAME = "benchmark-manifest.json"

REQUIRED_CATEGORIES = {"TCJ.Core", "TCJ.DependencyInjection"}
REQUIRED_BENCHMARKDOTNET_VERSION = "0.15.8"


class PerformanceError(RuntimeError):
    pass


@dataclass(frozen=True)
class Policy:
    schema_version: int
    minimum_benchmark_count: int
    maximum_relative_mean_ratio: float
    maximum_relative_allocation_ratio: float
    maximum_unexplained_allocated_bytes: int
    required_categories: tuple[str, ...]


@dataclass(frozen=True)
class ManifestBenchmark:
    type_name: str
    method: str
    categories: tuple[str, ...]
    comparison_group: str | None
    baseline: bool


@dataclass(frozen=True)
class BenchmarkResult:
    type_name: str
    method: str
    mean: float
    standard_error: float
    standard_deviation: float
    allocated_bytes: float
    source_report: str


@dataclass(frozen=True)
class EvaluatedBenchmark:
    type_name: str
    method: str
    categories: tuple[str, ...]
    comparison_group: str | None
    baseline: bool
    mean: float
    standard_error: float
    standard_deviation: float
    mean_ratio: float | None
    allocated_bytes: float
    allocation_ratio: float | None
    status: str


def fail(message: str) -> None:
    raise PerformanceError(message)


def read_json(path: Path) -> Any:
    if not path.is_file():
        fail(f"Required JSON file is missing: {relative(path)}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        fail(f"Invalid JSON in {relative(path)}: {error}")


def relative(path: Path, root: Path = ROOT) -> str:
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def finite_number(value: Any, description: str, *, positive: bool = False) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        fail(f"{description} must be numeric.")
    result = float(value)
    if not math.isfinite(result):
        fail(f"{description} must be finite.")
    if positive and result <= 0:
        fail(f"{description} must be greater than zero.")
    if not positive and result < 0:
        fail(f"{description} cannot be negative.")
    return result


def load_policy(path: Path = DEFAULT_POLICY) -> Policy:
    raw = read_json(path)
    if not isinstance(raw, dict):
        fail("Performance policy must be a JSON object.")
    if raw.get("schemaVersion") != 1:
        fail("Performance policy schemaVersion must be 1.")

    minimum_count = raw.get("minimumBenchmarkCount")
    if isinstance(minimum_count, bool) or not isinstance(minimum_count, int) or minimum_count < 1:
        fail("minimumBenchmarkCount must be a positive integer.")

    maximum_mean = finite_number(
        raw.get("maximumRelativeMeanRatio"),
        "maximumRelativeMeanRatio",
        positive=True,
    )
    maximum_allocation = finite_number(
        raw.get("maximumRelativeAllocationRatio"),
        "maximumRelativeAllocationRatio",
        positive=True,
    )
    unexplained = raw.get("maximumUnexplainedAllocatedBytes")
    if isinstance(unexplained, bool) or not isinstance(unexplained, int) or unexplained < 0:
        fail("maximumUnexplainedAllocatedBytes must be a non-negative integer.")

    categories = raw.get("requiredBenchmarkCategories")
    if not isinstance(categories, list) or not categories:
        fail("requiredBenchmarkCategories must be a non-empty array.")
    if any(not isinstance(item, str) or not item.strip() for item in categories):
        fail("requiredBenchmarkCategories must contain non-empty strings.")
    normalized = tuple(dict.fromkeys(item.strip() for item in categories))
    missing = sorted(REQUIRED_CATEGORIES.difference(normalized))
    if missing:
        fail("Performance policy is missing required categories: " + ", ".join(missing))

    return Policy(
        schema_version=1,
        minimum_benchmark_count=minimum_count,
        maximum_relative_mean_ratio=maximum_mean,
        maximum_relative_allocation_ratio=maximum_allocation,
        maximum_unexplained_allocated_bytes=unexplained,
        required_categories=normalized,
    )


def ensure_policy_tracked(root: Path, policy_path: Path) -> None:
    if not (root / ".git").exists():
        return
    try:
        relative_policy = policy_path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        fail("Performance policy must be inside the repository root.")

    process = subprocess.run(
        ["git", "check-ignore", "--quiet", "--", relative_policy],
        cwd=root,
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        text=True,
    )
    if process.returncode == 0:
        fail(f"{relative_policy} is ignored by Git and must remain tracked.")
    if process.returncode not in (0, 1):
        fail("Unable to verify whether the performance policy is ignored by Git: " + process.stderr.strip())


def parse_xml(path: Path, root: Path) -> ET.Element:
    if not path.is_file():
        fail(f"Required file is missing: {relative(path, root)}")
    try:
        return ET.parse(path).getroot()
    except ET.ParseError as error:
        fail(f"Invalid XML in {relative(path, root)}: {error}")


def require_text(path: Path, fragments: Iterable[str], root: Path) -> str:
    if not path.is_file():
        fail(f"Required file is missing: {relative(path, root)}")
    content = path.read_text(encoding="utf-8")
    missing = [fragment for fragment in fragments if fragment not in content]
    if missing:
        fail(
            f"{relative(path, root)} is missing required fragments: "
            + ", ".join(missing)
        )
    return content


def validate_configuration(
    root: Path = ROOT,
    policy_path: Path = DEFAULT_POLICY,
    *,
    check_git: bool = True,
) -> Policy:
    policy = load_policy(policy_path)
    if check_git:
        ensure_policy_tracked(root, policy_path)

    packages = parse_xml(root / "Directory.Packages.props", root)
    versions = {
        item.attrib.get("Include"): item.attrib.get("Version")
        for item in packages.findall(".//PackageVersion")
    }
    if versions.get("BenchmarkDotNet") != REQUIRED_BENCHMARKDOTNET_VERSION:
        fail(
            "Directory.Packages.props must centrally pin BenchmarkDotNet "
            f"{REQUIRED_BENCHMARKDOTNET_VERSION}."
        )

    project_path = root / "benchmarks/TCJ.Benchmarks/TCJ.Benchmarks.csproj"
    project = parse_xml(project_path, root)
    if (project.findtext("./PropertyGroup/TargetFramework") or "").strip() != "net10.0":
        fail("The benchmark project must target net10.0.")
    if (project.findtext("./PropertyGroup/OutputType") or "").strip() != "Exe":
        fail("The benchmark project must be executable.")

    package_references = {
        item.attrib.get("Include"): item.attrib.get("Version")
        for item in project.findall(".//PackageReference")
    }
    if "BenchmarkDotNet" not in package_references:
        fail("The benchmark project must reference BenchmarkDotNet.")
    if package_references["BenchmarkDotNet"]:
        fail("BenchmarkDotNet must use central package version management.")
    forbidden_test_packages = {
        "Microsoft.NET.Test.Sdk", "xunit", "xunit.v3", "NUnit", "MSTest.TestFramework"
    }
    found_test_packages = sorted(forbidden_test_packages.intersection(package_references))
    if found_test_packages:
        fail("The benchmark project must not reference test frameworks: " + ", ".join(found_test_packages))

    project_references = {
        item.attrib.get("Include", "").replace("\\", "/")
        for item in project.findall(".//ProjectReference")
    }
    required_projects = {
        "../../src/TCJ.Core/TCJ.Core.csproj",
        "../../src/TCJ.DependencyInjection/TCJ.DependencyInjection.csproj",
    }
    if not required_projects.issubset(project_references):
        fail("The benchmark project must directly reference TCJ.Core and TCJ.DependencyInjection.")

    solution = require_text(
        root / "TCJ.slnx",
        ("/benchmarks/", "benchmarks/TCJ.Benchmarks/TCJ.Benchmarks.csproj"),
        root,
    )
    if solution.count("benchmarks/TCJ.Benchmarks/TCJ.Benchmarks.csproj") != 1:
        fail("TCJ.slnx must contain the benchmark project exactly once.")

    benchmark_sources = sorted((root / "benchmarks/TCJ.Benchmarks/Benchmarks").glob("*.cs"))
    if not benchmark_sources:
        fail("No benchmark classes were found.")
    benchmark_count = 0
    category_text = ""
    source_benchmarks: set[tuple[str, str]] = set()
    benchmark_pattern = re.compile(
        r"\[Benchmark(?:\([^]]*\))?\]\s+"
        r"public\s+(?:[\w<>,.?\[\]]+\s+)+(?P<method>\w+)\s*\(",
        re.MULTILINE,
    )
    class_pattern = re.compile(r"public\s+class\s+(?P<type>\w+)")
    for source in benchmark_sources:
        content = source.read_text(encoding="utf-8")
        methods = benchmark_pattern.findall(content)
        if not methods:
            continue
        class_match = class_pattern.search(content)
        if class_match is None:
            fail(f"{relative(source, root)} must declare a public benchmark class.")
        type_name = class_match.group("type")
        benchmark_count += len(methods)
        source_benchmarks.update((type_name, method) for method in methods)
        category_text += content
        for fragment in ("[MemoryDiagnoser]", "[BenchmarkCategory(", "Baseline = true"):
            if fragment not in content:
                fail(f"{relative(source, root)} must include {fragment}.")

    catalog_path = root / "benchmarks/TCJ.Benchmarks/Configuration/BenchmarkCatalog.cs"
    catalog = require_text(
        catalog_path,
        ("benchmark-manifest.json", "comparisonGroup", "baseline"),
        root,
    )
    catalog_benchmarks = set(
        re.findall(
            r"(?:Core|DependencyInjection|Observability|Resilience|HealthChecks)\(\s*\"([^\"]+)\"\s*,\s*\"([^\"]+)\"",
            catalog,
        )
    )
    if source_benchmarks != catalog_benchmarks:
        missing_catalog = sorted(source_benchmarks.difference(catalog_benchmarks))
        stale_catalog = sorted(catalog_benchmarks.difference(source_benchmarks))
        details: list[str] = []
        if missing_catalog:
            details.append(
                "missing from catalog: "
                + ", ".join(f"{type_name}.{method}" for type_name, method in missing_catalog)
            )
        if stale_catalog:
            details.append(
                "not found in source: "
                + ", ".join(f"{type_name}.{method}" for type_name, method in stale_catalog)
            )
        fail("Benchmark catalog and source methods differ; " + "; ".join(details))

    require_text(
        root / "benchmarks/TCJ.Benchmarks/Program.cs",
        ("BenchmarkCatalog.WriteManifest();", "BenchmarkSwitcher", "TcjBenchmarkConfig.Create()"),
        root,
    )
    require_text(
        root / "benchmarks/TCJ.Benchmarks/Configuration/TcjBenchmarkConfig.cs",
        ("Job.ShortRun", "JsonExporter.Full", "MarkdownExporter.GitHub", "WithArtifactsPath"),
        root,
    )

    if benchmark_count < policy.minimum_benchmark_count:
        fail(
            f"Benchmark sources define {benchmark_count} methods; "
            f"at least {policy.minimum_benchmark_count} are required."
        )
    for category in policy.required_categories:
        if f'"{category}"' not in category_text:
            fail(f"Benchmark sources do not declare category '{category}'.")

    require_text(
        root / ".github/workflows/performance-benchmarks.yml",
        (
            "name: Performance benchmarks",
            "workflow_dispatch:",
            "schedule:",
            "pull_request:",
            "push:",
            "name: Run benchmarks",
            "python3 eng/verify-dependency-security.py",
            "python3 eng/verify-performance-results.py validate-config",
            "dotnet run",
            "python3 eng/verify-performance-results.py verify",
            "artifacts/performance/PERFORMANCE_SUMMARY.md",
            "actions/upload-artifact@v7",
        ),
        root,
    )
    require_text(
        root / ".github/workflows/ci.yml",
        ("python3 eng/verify-performance-results.py validate-config",),
        root,
    )
    require_text(
        root / ".gitignore",
        (
            "BenchmarkDotNet.Artifacts/",
            "artifacts/performance/",
            "!eng/performance-policy.json",
            "!eng/verify-performance-results.py",
        ),
        root,
    )

    print(
        "Performance configuration is valid: "
        f"benchmarks >= {policy.minimum_benchmark_count}, "
        f"mean ratio <= {policy.maximum_relative_mean_ratio:.2f}, "
        f"allocation ratio <= {policy.maximum_relative_allocation_ratio:.2f}."
    )
    return policy


def value_case_insensitive(mapping: dict[str, Any], name: str) -> Any:
    if name in mapping:
        return mapping[name]
    lowered = name.lower()
    for key, value in mapping.items():
        if key.lower() == lowered:
            return value
    return None


def load_manifest(path: Path) -> list[ManifestBenchmark]:
    raw = read_json(path)
    if not isinstance(raw, dict) or raw.get("schemaVersion") != 1:
        fail("Benchmark manifest schemaVersion must be 1.")
    items = raw.get("benchmarks")
    if not isinstance(items, list) or not items:
        fail("Benchmark manifest must contain benchmark definitions.")

    result: list[ManifestBenchmark] = []
    seen: set[tuple[str, str]] = set()
    for index, item in enumerate(items):
        if not isinstance(item, dict):
            fail(f"Benchmark manifest entry {index} must be an object.")
        type_name = value_case_insensitive(item, "type")
        method = value_case_insensitive(item, "method")
        categories = value_case_insensitive(item, "categories")
        group = value_case_insensitive(item, "comparisonGroup")
        baseline = value_case_insensitive(item, "baseline")
        if not isinstance(type_name, str) or not type_name:
            fail(f"Benchmark manifest entry {index} has no type.")
        if not isinstance(method, str) or not method:
            fail(f"Benchmark manifest entry {index} has no method.")
        if not isinstance(categories, list) or not categories or any(
            not isinstance(category, str) or not category for category in categories
        ):
            fail(f"Benchmark manifest entry {index} has invalid categories.")
        if group is not None and (not isinstance(group, str) or not group):
            fail(f"Benchmark manifest entry {index} has an invalid comparisonGroup.")
        if not isinstance(baseline, bool):
            fail(f"Benchmark manifest entry {index} has an invalid baseline flag.")
        key = (type_name, method)
        if key in seen:
            fail(f"Benchmark manifest contains duplicate entry {type_name}.{method}.")
        seen.add(key)
        result.append(
            ManifestBenchmark(
                type_name=type_name,
                method=method,
                categories=tuple(categories),
                comparison_group=group,
                baseline=baseline,
            )
        )

    groups = {item.comparison_group for item in result if item.comparison_group}
    for group in groups:
        group_items = [item for item in result if item.comparison_group == group]
        baselines = [item for item in group_items if item.baseline]
        if len(baselines) != 1:
            fail(f"Comparison group '{group}' must define exactly one baseline.")
        if len(group_items) < 2:
            fail(f"Comparison group '{group}' must contain at least two benchmarks.")
    return result


def report_paths(reports_directory: Path) -> list[Path]:
    full = sorted(reports_directory.rglob("*-report-full.json"))
    if full:
        return full
    return sorted(
        path
        for path in reports_directory.rglob("*-report*.json")
        if path.name not in {DEFAULT_MANIFEST_NAME, "performance-summary.json"}
    )


def load_report_results(reports_directory: Path) -> tuple[dict[tuple[str, str], BenchmarkResult], dict[str, Any]]:
    paths = report_paths(reports_directory)
    if not paths:
        fail(f"No BenchmarkDotNet JSON reports were found under {relative(reports_directory)}.")

    results: dict[tuple[str, str], BenchmarkResult] = {}
    environment: dict[str, Any] = {}
    for path in paths:
        raw = read_json(path)
        if not isinstance(raw, dict):
            fail(f"Benchmark report {path.name} must be a JSON object.")
        host = value_case_insensitive(raw, "HostEnvironmentInfo")
        if not environment and isinstance(host, dict):
            environment = host
        benchmarks = value_case_insensitive(raw, "Benchmarks")
        if not isinstance(benchmarks, list) or not benchmarks:
            fail(f"Benchmark report {path.name} contains no benchmark results.")

        for index, item in enumerate(benchmarks):
            if not isinstance(item, dict):
                fail(f"Benchmark {index} in {path.name} must be an object.")
            success = value_case_insensitive(item, "Success")
            if success is False:
                fail(f"Benchmark {index} in {path.name} was not successful.")
            type_name = value_case_insensitive(item, "Type")
            method = value_case_insensitive(item, "Method")
            if not isinstance(type_name, str) or not type_name:
                fail(f"Benchmark {index} in {path.name} has no Type.")
            if not isinstance(method, str) or not method:
                fail(f"Benchmark {index} in {path.name} has no Method.")

            statistics = value_case_insensitive(item, "Statistics")
            if not isinstance(statistics, dict):
                fail(f"Benchmark {type_name}.{method} has no Statistics; it may have failed.")
            mean = finite_number(
                value_case_insensitive(statistics, "Mean"),
                f"{type_name}.{method} mean",
                positive=True,
            )
            standard_error = finite_number(
                value_case_insensitive(statistics, "StandardError"),
                f"{type_name}.{method} standard error",
            )
            standard_deviation = finite_number(
                value_case_insensitive(statistics, "StandardDeviation"),
                f"{type_name}.{method} standard deviation",
            )

            memory = value_case_insensitive(item, "Memory")
            if not isinstance(memory, dict):
                fail(f"Benchmark {type_name}.{method} has no memory-allocation measurement.")
            allocated = value_case_insensitive(memory, "BytesAllocatedPerOperation")
            if allocated is None:
                allocated = value_case_insensitive(memory, "AllocatedBytes")
            allocated_bytes = finite_number(
                allocated,
                f"{type_name}.{method} allocated bytes",
            )

            key = (type_name, method)
            if key in results:
                fail(
                    f"Duplicate benchmark result {type_name}.{method}; "
                    "use a single job and avoid untracked parameter variants."
                )
            results[key] = BenchmarkResult(
                type_name=type_name,
                method=method,
                mean=mean,
                standard_error=standard_error,
                standard_deviation=standard_deviation,
                allocated_bytes=allocated_bytes,
                source_report=path.name,
            )

    return results, environment


def allocation_ratio(current: float, baseline: float, allowed_unexplained: int) -> float:
    if baseline > 0:
        return current / baseline
    if current <= allowed_unexplained:
        return 1.0
    return math.inf


def verify_reports(
    policy: Policy,
    reports_directory: Path = DEFAULT_REPORTS,
) -> tuple[list[EvaluatedBenchmark], list[str], list[str], dict[str, Any]]:
    manifest = load_manifest(reports_directory / DEFAULT_MANIFEST_NAME)
    reports, environment = load_report_results(reports_directory)

    failures: list[str] = []
    warnings: list[str] = []
    manifest_keys = {(item.type_name, item.method) for item in manifest}
    missing_results = sorted(manifest_keys.difference(reports))
    if missing_results:
        failures.append(
            "Missing benchmark results: "
            + ", ".join(f"{type_name}.{method}" for type_name, method in missing_results)
        )
    unexpected_results = sorted(set(reports).difference(manifest_keys))
    if unexpected_results:
        failures.append(
            "Unexpected benchmark results not declared in the manifest: "
            + ", ".join(f"{type_name}.{method}" for type_name, method in unexpected_results)
        )

    if len(reports) < policy.minimum_benchmark_count:
        failures.append(
            f"Only {len(reports)} benchmarks ran; "
            f"at least {policy.minimum_benchmark_count} are required."
        )

    present_categories = {
        category
        for item in manifest
        if (item.type_name, item.method) in reports
        for category in item.categories
    }
    missing_categories = sorted(set(policy.required_categories).difference(present_categories))
    if missing_categories:
        failures.append("Missing required benchmark categories: " + ", ".join(missing_categories))

    groups: dict[str, list[ManifestBenchmark]] = {}
    for item in manifest:
        if item.comparison_group:
            groups.setdefault(item.comparison_group, []).append(item)

    ratios: dict[tuple[str, str], tuple[float | None, float | None, str]] = {}
    for group_name, group_items in sorted(groups.items()):
        baselines = [item for item in group_items if item.baseline]
        if len(baselines) != 1:
            failures.append(f"Comparison group '{group_name}' must define exactly one baseline.")
            continue
        baseline_item = baselines[0]
        baseline_result = reports.get((baseline_item.type_name, baseline_item.method))
        if baseline_result is None:
            failures.append(
                f"Comparison group '{group_name}' is missing baseline result "
                f"{baseline_item.type_name}.{baseline_item.method}."
            )
            continue

        for item in group_items:
            result = reports.get((item.type_name, item.method))
            if result is None:
                continue
            mean_ratio = result.mean / baseline_result.mean
            allocated_ratio = allocation_ratio(
                result.allocated_bytes,
                baseline_result.allocated_bytes,
                policy.maximum_unexplained_allocated_bytes,
            )
            status = "PASS"
            if not item.baseline and mean_ratio > policy.maximum_relative_mean_ratio:
                status = "FAIL"
                failures.append(
                    f"{item.type_name}.{item.method} mean ratio {mean_ratio:.3f} "
                    f"exceeds {policy.maximum_relative_mean_ratio:.3f} in group '{group_name}'."
                )
            if not item.baseline and allocated_ratio > policy.maximum_relative_allocation_ratio:
                status = "FAIL"
                if math.isinf(allocated_ratio):
                    failures.append(
                        f"{item.type_name}.{item.method} allocated "
                        f"{result.allocated_bytes:.0f} bytes while its baseline allocates none; "
                        f"the unexplained allowance is {policy.maximum_unexplained_allocated_bytes} bytes."
                    )
                else:
                    failures.append(
                        f"{item.type_name}.{item.method} allocation ratio {allocated_ratio:.3f} "
                        f"exceeds {policy.maximum_relative_allocation_ratio:.3f} in group '{group_name}'."
                    )
            ratios[(item.type_name, item.method)] = (mean_ratio, allocated_ratio, status)

    evaluated: list[EvaluatedBenchmark] = []
    for item in manifest:
        result = reports.get((item.type_name, item.method))
        if result is None:
            continue
        mean_ratio, allocated_ratio, status = ratios.get(
            (item.type_name, item.method),
            (None, None, "PASS"),
        )
        if item.comparison_group is None and result.allocated_bytes > policy.maximum_unexplained_allocated_bytes:
            warnings.append(
                f"{item.type_name}.{item.method} allocates {result.allocated_bytes:.0f} bytes; "
                "this is recorded for review but has no within-run comparison baseline."
            )
        evaluated.append(
            EvaluatedBenchmark(
                type_name=item.type_name,
                method=item.method,
                categories=item.categories,
                comparison_group=item.comparison_group,
                baseline=item.baseline,
                mean=result.mean,
                standard_error=result.standard_error,
                standard_deviation=result.standard_deviation,
                mean_ratio=mean_ratio,
                allocated_bytes=result.allocated_bytes,
                allocation_ratio=allocated_ratio,
                status=status,
            )
        )

    return evaluated, failures, warnings, environment


def display_environment(environment: dict[str, Any]) -> dict[str, str]:
    processor = value_case_insensitive(environment, "ProcessorName")
    if isinstance(processor, dict):
        processor = value_case_insensitive(processor, "Value")
    return {
        "benchmarkDotNet": str(value_case_insensitive(environment, "BenchmarkDotNetCaption") or "unknown"),
        "runtime": str(
            value_case_insensitive(environment, "RuntimeVersion")
            or value_case_insensitive(environment, "ClrVersion")
            or "unknown"
        ),
        "sdk": str(
            value_case_insensitive(environment, "DotNetSdkVersion")
            or value_case_insensitive(environment, "DotNetCliVersion")
            or "unknown"
        ),
        "os": str(value_case_insensitive(environment, "OsVersion") or "unknown"),
        "architecture": str(value_case_insensitive(environment, "Architecture") or "unknown"),
        "processor": str(processor or "unknown"),
    }


def ratio_text(value: float | None) -> str:
    if value is None:
        return "—"
    if math.isinf(value):
        return "∞"
    return f"{value:.3f}"


def write_outputs(
    policy: Policy,
    evaluated: list[EvaluatedBenchmark],
    failures: list[str],
    warnings: list[str],
    environment: dict[str, Any],
    summary_path: Path,
    json_path: Path,
) -> None:
    summary_path.parent.mkdir(parents=True, exist_ok=True)
    json_path.parent.mkdir(parents=True, exist_ok=True)
    status = "FAIL" if failures else "PASS"
    env = display_environment(environment)

    lines = [
        "# TCJ performance benchmarks",
        "",
        f"**Overall status:** {status}",
        "",
        "## Environment",
        "",
        f"- BenchmarkDotNet: `{env['benchmarkDotNet']}`",
        f"- Runtime: `{env['runtime']}`",
        f"- SDK: `{env['sdk']}`",
        f"- Operating system: `{env['os']}`",
        f"- Architecture: `{env['architecture']}`",
        f"- Processor: `{env['processor']}`",
        "",
        "## Policy",
        "",
        f"- Minimum benchmark count: `{policy.minimum_benchmark_count}`",
        f"- Maximum relative mean ratio: `{policy.maximum_relative_mean_ratio:.2f}`",
        f"- Maximum relative allocation ratio: `{policy.maximum_relative_allocation_ratio:.2f}`",
        f"- Maximum unexplained allocation: `{policy.maximum_unexplained_allocated_bytes} B`",
        f"- Required categories: `{', '.join(policy.required_categories)}`",
        "",
        f"## Results ({len(evaluated)} benchmarks)",
        "",
        "| Category | Benchmark | Baseline | Mean (ns) | Error | StdDev | Mean ratio | Allocated (B) | Allocation ratio | Status |",
        "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |",
    ]
    for item in evaluated:
        lines.append(
            "| "
            + ", ".join(item.categories)
            + f" | `{item.type_name}.{item.method}` | {'yes' if item.baseline else 'no'}"
            + f" | {item.mean:.3f} | {item.standard_error:.3f} | {item.standard_deviation:.3f}"
            + f" | {ratio_text(item.mean_ratio)} | {item.allocated_bytes:.0f}"
            + f" | {ratio_text(item.allocation_ratio)} | {item.status} |"
        )

    if warnings:
        lines.extend(["", "## Allocation review notes", ""])
        lines.extend(f"- {warning}" for warning in warnings)
    if failures:
        lines.extend(["", "## Blocking failures", ""])
        lines.extend(f"- {failure}" for failure in failures)
    lines.append("")
    summary_path.write_text("\n".join(lines), encoding="utf-8")

    payload = {
        "schemaVersion": 1,
        "status": status.lower(),
        "environment": env,
        "policy": {
            "minimumBenchmarkCount": policy.minimum_benchmark_count,
            "maximumRelativeMeanRatio": policy.maximum_relative_mean_ratio,
            "maximumRelativeAllocationRatio": policy.maximum_relative_allocation_ratio,
            "maximumUnexplainedAllocatedBytes": policy.maximum_unexplained_allocated_bytes,
            "requiredBenchmarkCategories": list(policy.required_categories),
        },
        "benchmarkCount": len(evaluated),
        "categories": sorted({category for item in evaluated for category in item.categories}),
        "failures": failures,
        "warnings": warnings,
        "benchmarks": [asdict(item) for item in evaluated],
    }
    json_path.write_text(json.dumps(payload, indent=2, allow_nan=False), encoding="utf-8")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("validate-config", "verify"))
    parser.add_argument("--policy", type=Path, default=DEFAULT_POLICY)
    parser.add_argument("--reports", type=Path, default=DEFAULT_REPORTS)
    parser.add_argument("--summary", type=Path, default=DEFAULT_SUMMARY)
    parser.add_argument("--json", type=Path, default=DEFAULT_JSON)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.mode == "validate-config":
        validate_configuration(ROOT, args.policy)
        return 0

    policy = load_policy(args.policy)
    try:
        evaluated, failures, warnings, environment = verify_reports(policy, args.reports)
    except PerformanceError as error:
        evaluated = []
        failures = [str(error)]
        warnings = []
        environment = {}
    write_outputs(
        policy,
        evaluated,
        failures,
        warnings,
        environment,
        args.summary,
        args.json,
    )
    if failures:
        raise PerformanceError("; ".join(failures))
    print(f"Performance verification passed for {len(evaluated)} benchmarks.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, PerformanceError, subprocess.SubprocessError) as error:
        print(f"Performance verification failed: {error}", file=sys.stderr)
        raise SystemExit(1)
