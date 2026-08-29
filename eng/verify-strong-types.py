#!/usr/bin/env python3
"""Validate Strong Types release contracts and packed-package evidence."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import statistics
import subprocess
import sys
import time
import zipfile
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any
from urllib.parse import unquote, urlparse
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
POLICY = ROOT / "eng/strong-types-policy.json"
DEFAULT_PACKAGES = ROOT / "artifacts/packages"
DEFAULT_OUTPUT = ROOT / "artifacts/strong-types"
TCJ_LIBRARY_RE = re.compile(r"^(TCJ\.[^/]+)/(.+)$", re.IGNORECASE)


class StrongTypesError(RuntimeError):
    pass


@dataclass(frozen=True)
class CommandResult:
    command: str
    milliseconds: int
    log: str


@dataclass(frozen=True)
class GeneratedSnapshot:
    files: dict[str, str]
    count: int


def fail(message: str) -> None:
    raise StrongTypesError(message)


def tag_name(element: ET.Element) -> str:
    return element.tag.rsplit("}", 1)[-1]


def read_policy(root: Path = ROOT) -> dict[str, Any]:
    path = root / "eng/strong-types-policy.json"
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"Unable to read Strong Types policy {path}: {error}")
    if not isinstance(data, dict) or data.get("schemaVersion") != 1:
        fail("Strong Types policy must be a schemaVersion 1 JSON object.")
    for key in ("packageConsumer", "generatorPackage", "determinism", "performance", "incrementalTrackingNames", "diagnostics"):
        if key not in data:
            fail(f"Strong Types policy is missing {key!r}.")
    return data


def parse_package_references(project: Path) -> tuple[dict[str, tuple[str, str]], list[str]]:
    try:
        xml_root = ET.parse(project).getroot()
    except (OSError, ET.ParseError) as error:
        fail(f"Invalid Strong Types consumer project {project}: {error}")
    packages: dict[str, tuple[str, str]] = {}
    project_refs: list[str] = []
    for element in xml_root.iter():
        name = tag_name(element)
        if name == "ProjectReference":
            project_refs.append((element.attrib.get("Include") or "").strip())
            continue
        if name != "PackageReference":
            continue
        package_id = (element.attrib.get("Include") or element.attrib.get("Update") or "").strip()
        if not package_id.startswith("TCJ."):
            continue
        version = (element.attrib.get("Version") or "").strip()
        if not version:
            version_node = next((child for child in element if tag_name(child) == "Version"), None)
            version = ((version_node.text if version_node is not None else "") or "").strip()
        private_assets = (element.attrib.get("PrivateAssets") or "").strip()
        if not private_assets:
            node = next((child for child in element if tag_name(child) == "PrivateAssets"), None)
            private_assets = ((node.text if node is not None else "") or "").strip()
        packages[package_id] = (version, private_assets)
    return packages, project_refs


def require_text(path: Path, fragments: list[str]) -> None:
    if not path.is_file():
        fail(f"Required Strong Types file is missing: {path}")
    text = path.read_text(encoding="utf-8")
    missing = [fragment for fragment in fragments if fragment not in text]
    if missing:
        fail(f"{path} is missing required Strong Types contract fragment(s): {missing}")


def validate_config(root: Path = ROOT) -> dict[str, Any]:
    policy = read_policy(root)
    consumer = policy["packageConsumer"]
    required_packages = consumer.get("requiredPackages")
    expected_packages = ["TCJ.Core", "TCJ.Generators", "TCJ.EntityFrameworkCore", "TCJ.AspNetCore"]
    if required_packages != expected_packages:
        fail(f"Strong Types packed consumer requiredPackages must be exactly {expected_packages}.")

    project = root / str(consumer.get("project", ""))
    program = root / str(consumer.get("program", ""))
    references, project_refs = parse_package_references(project)
    if project_refs:
        fail(f"Strong Types packed consumer must not contain ProjectReference: {project_refs}")
    if set(references) != set(expected_packages):
        fail(f"Strong Types packed consumer TCJ PackageReferences must be exactly {expected_packages}; found {sorted(references)}.")
    for package_id, (version, _) in references.items():
        if version != "$(TCJStrongTypesPackageVersion)":
            fail(f"{package_id} must use $(TCJStrongTypesPackageVersion) in the Strong Types packed consumer.")
    if references["TCJ.Generators"][1].casefold() != "all":
        fail("TCJ.Generators must be PrivateAssets=all in the Strong Types packed consumer.")

    require_text(project, [
        "Microsoft.NET.Sdk.Web",
        "EnableRequestDelegateGenerator>true",
        "JsonSerializerIsReflectionEnabledByDefault>false",
        "TreatWarningsAsErrors>true",
        "ManagePackageVersionsCentrally>false",
        "TCJStrongTypesPackageVersion",
    ])
    require_text(program, [
        "[StronglyTypedId<Guid>]",
        "[ValueObject<string>]",
        "OrderId.StrongIdJsonConverter",
        "EmailAddress.ValueObjectJsonConverter",
        "ParseStrongTypes(OrderId id, EmailAddress email)",
        "StrongIdConversionRegistry",
        "ValueObjectConversionRegistry",
        "ApplyStrongIdConversions",
        "ApplyValueObjectConversions",
        str(consumer.get("expectedOutput", "")),
    ])

    generator = policy["generatorPackage"]
    if generator.get("id") != "TCJ.Generators" or generator.get("asset") != "analyzers/dotnet/cs/TCJ.Generators.dll":
        fail("Strong Types policy must pin TCJ.Generators to its analyzer-only package asset path.")
    if generator.get("forbiddenRuntimePrefixes") != ["lib/", "runtime/"]:
        fail("Strong Types policy must forbid TCJ.Generators lib/ and runtime/ package assets.")

    determinism = policy["determinism"]
    strong_count = int(determinism.get("strongIdCount", 0))
    value_count = int(determinism.get("valueObjectCount", 0))
    if strong_count < 32 or value_count < 32 or int(determinism.get("expectedGeneratedFileCount", 0)) != strong_count + value_count:
        fail("Determinism fixture must contain at least 32 Strong IDs and 32 Value Objects and require one generated file per type.")

    performance = policy["performance"]
    perf_strong = int(performance.get("strongIdCount", 0))
    perf_value = int(performance.get("valueObjectCount", 0))
    warmups = int(performance.get("warmupRuns", 0))
    measurements = int(performance.get("measurementRuns", 0))
    max_median = int(performance.get("maxMedianMilliseconds", 0))
    if perf_strong < 100 or perf_value < 100:
        fail("Generator performance fixture must contain at least 100 Strong IDs and 100 Value Objects.")
    if warmups < 1 or measurements < 3:
        fail("Generator performance budget must include at least one warmup and three measured rebuilds.")
    if not 5_000 <= max_median <= 120_000:
        fail("Generator performance maxMedianMilliseconds must be a coarse CI-stable budget between 5s and 120s.")

    tracking_names = policy["incrementalTrackingNames"]
    expected_tracking = ["TCJ.StrongTypes.StrongIdModels", "TCJ.StrongTypes.ValueObjectModels"]
    if tracking_names != expected_tracking:
        fail(f"Incremental tracking names must be exactly {expected_tracking}.")
    require_text(root / "src/TCJ.Generators/StrongTypeGenerator.cs", [f'.WithTrackingName("{name}")' for name in expected_tracking])
    require_text(root / "tests/TCJ.Generators.Tests/StrongTypeGeneratorTests.cs", [
        "Generator_UnrelatedSyntaxChange_ReusesStrongTypeModels",
        "trackIncrementalGeneratorSteps: true",
        "IncrementalStepRunReason.Cached",
        "IncrementalStepRunReason.Unchanged",
        *expected_tracking,
    ])

    diagnostics = policy["diagnostics"]
    ids = diagnostics.get("diagnosticIds")
    expected_ids = [f"TCJ400{i}" for i in range(8)]
    if ids != expected_ids:
        fail(f"Strong Types diagnostics policy must track exactly {expected_ids}.")
    tracking_files = [root / relative for relative in diagnostics.get("releaseTrackingFiles", [])]
    if len(tracking_files) != 2 or any(not path.is_file() for path in tracking_files):
        fail("Strong Types diagnostics must use both shipped and unshipped analyzer release tracking files.")
    tracking_text = "\n".join(path.read_text(encoding="utf-8") for path in tracking_files)
    missing_tracked_ids = [diagnostic_id for diagnostic_id in expected_ids if diagnostic_id not in tracking_text]
    if missing_tracked_ids:
        fail(f"Strong Types diagnostics are missing from analyzer release tracking: {missing_tracked_ids}")
    require_text(root / "src/TCJ.Generators/TCJ.Generators.csproj", [
        'AdditionalFiles Include="AnalyzerReleases.Shipped.md"',
        'AdditionalFiles Include="AnalyzerReleases.Unshipped.md"',
    ])
    for diagnostic_id in expected_ids:
        require_text(root / f"docs/analyzers/{diagnostic_id}.md", [diagnostic_id])

    require_text(root / "docs/guides/strong-types.md", [
        "Strongly Typed IDs",
        "Primitive-backed Value Objects",
        "System.Text.Json",
        "Minimal API",
        "EF Core",
        "Native AOT",
        "diagnostic",
        "default",
    ])
    require_text(root / ".github/workflows/ci.yml", ["eng/verify-strong-types.py validate-config", "eng/verify-strong-types.py verify-packed"])
    require_text(root / ".github/workflows/release-preflight.yml", ["eng/verify-strong-types.py validate-config", "eng/verify-strong-types.py verify-packed"])
    require_text(root / ".github/workflows/release.yml", ["eng/verify-strong-types.py validate-config", "eng/verify-strong-types.py verify-packed"])
    require_text(root / "eng/run-native-aot-smoke.py", ["TCJ.Generators.dll", "publishOutputToolingStatus"])
    require_text(root / "smoke/TCJ.NativeAot.SmokeTest/TCJ.NativeAot.SmokeTest.csproj", [
        'PackageReference Include="TCJ.Generators"',
        'PrivateAssets="all"',
    ])
    return policy


def write_json(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")


def run(command: list[str], *, cwd: Path, env: dict[str, str], log: Path) -> CommandResult:
    log.parent.mkdir(parents=True, exist_ok=True)
    start = time.perf_counter()
    process = subprocess.run(
        command,
        cwd=cwd,
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    elapsed = int(round((time.perf_counter() - start) * 1000))
    output = process.stdout or ""
    log.write_text(output, encoding="utf-8", newline="\n")
    if process.returncode != 0:
        fail(f"Command failed with exit code {process.returncode}: {' '.join(command)}. See {log}.")
    return CommandResult(" ".join(command), elapsed, str(log))


def safe_package_entries(path: Path) -> list[str]:
    try:
        with zipfile.ZipFile(path, "r") as archive:
            names: list[str] = []
            seen: set[str] = set()
            for info in archive.infolist():
                if info.is_dir():
                    continue
                normalized = str(PurePosixPath(info.filename))
                parts = PurePosixPath(normalized).parts
                if normalized.startswith("/") or ".." in parts:
                    fail(f"{path.name} contains unsafe archive entry {info.filename!r}.")
                folded = normalized.casefold()
                if folded in seen:
                    fail(f"{path.name} contains duplicate archive entry {normalized!r}.")
                seen.add(folded)
                names.append(normalized)
            return names
    except zipfile.BadZipFile as error:
        fail(f"Invalid NuGet package {path}: {error}")


def verify_generator_package(packages: Path, version: str, policy: dict[str, Any]) -> dict[str, Any]:
    generator = policy["generatorPackage"]
    package_id = generator["id"]
    package = packages / f"{package_id}.{version}.nupkg"
    if not package.is_file():
        fail(f"Packed Strong Types verification is missing {package.name}.")
    entries = safe_package_entries(package)
    asset = generator["asset"]
    if asset not in entries:
        fail(f"{package.name} must contain generator implementation at {asset}.")
    forbidden = [entry for entry in entries if any(entry.casefold().startswith(prefix.casefold()) for prefix in generator["forbiddenRuntimePrefixes"])]
    if forbidden:
        fail(f"{package.name} exposes forbidden runtime assets: {forbidden}")
    generator_dlls = [entry for entry in entries if entry.casefold().endswith("tcj.generators.dll")]
    if generator_dlls != [asset]:
        fail(f"{package.name} must contain exactly one TCJ.Generators.dll analyzer asset; found {generator_dlls}.")
    return {"package": package.name, "asset": asset, "forbiddenRuntimeAssets": []}


def create_nuget_config(path: Path, packages: Path) -> None:
    escaped = str(packages.resolve()).replace("&", "&amp;").replace('"', "&quot;")
    path.write_text(
        f'''<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <packageSources>\n    <clear />\n    <add key="tcj-local" value="{escaped}" />\n    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />\n  </packageSources>\n  <packageSourceMapping>\n    <packageSource key="tcj-local"><package pattern="TCJ.*" /></packageSource>\n    <packageSource key="nuget.org"><package pattern="*" /></packageSource>\n  </packageSourceMapping>\n</configuration>\n''',
        encoding="utf-8",
        newline="\n",
    )


def source_path_candidates(value: str) -> set[str]:
    """Return normalized filesystem candidates for a NuGet source path or file URI."""
    candidates: set[str] = set()
    raw = value.strip().rstrip("/\\")
    if not raw:
        return candidates
    parsed = urlparse(raw)
    values = [raw]
    if parsed.scheme.casefold() == "file":
        path = unquote(parsed.path)
        if parsed.netloc:
            path = f"//{parsed.netloc}{path}"
        if os.name == "nt" and re.match(r"^/[A-Za-z]:/", path):
            path = path[1:]
        values.append(path)
    for candidate in values:
        try:
            normalized = os.path.normcase(os.path.normpath(str(Path(candidate).resolve())))
        except (OSError, RuntimeError):
            normalized = os.path.normcase(os.path.normpath(candidate))
        candidates.add(normalized)
    return candidates


def tcj_assets(project: Path, expected_version: str, expected_packages: list[str], package_cache: Path, package_source: Path) -> dict[str, str]:
    assets = project.parent / "obj/project.assets.json"
    if not assets.is_file():
        fail(f"Packed Strong Types consumer restore did not create {assets}.")
    data = json.loads(assets.read_text(encoding="utf-8"))
    resolved: dict[str, str] = {}
    for name, metadata in data.get("libraries", {}).items():
        match = TCJ_LIBRARY_RE.match(name)
        if not match:
            continue
        package_id, version = match.groups()
        if not isinstance(metadata, dict) or metadata.get("type") != "package":
            fail(f"Packed Strong Types consumer resolved {package_id} as a non-package reference.")
        resolved[package_id] = version
    if set(resolved) != set(expected_packages):
        fail(f"Packed Strong Types consumer TCJ closure mismatch: expected {sorted(expected_packages)}, found {sorted(resolved)}.")
    wrong = {package_id: version for package_id, version in resolved.items() if version != expected_version}
    if wrong:
        fail(f"Packed Strong Types consumer resolved wrong TCJ versions: {wrong}; expected {expected_version}.")
    project_refs = data.get("project", {}).get("restore", {}).get("projectReferences") or {}
    if project_refs:
        fail(f"Packed Strong Types consumer restored repository project references: {sorted(project_refs)}")
    package_folders = {os.path.normcase(os.path.normpath(path.rstrip('/\\'))) for path in data.get("packageFolders", {})}
    expected_cache = os.path.normcase(os.path.normpath(str(package_cache.resolve())))
    if expected_cache not in package_folders:
        fail(f"Packed Strong Types consumer did not use isolated NUGET_PACKAGES {package_cache}.")
    expected_source = os.path.normcase(os.path.normpath(str(package_source.resolve())))
    for package_id in expected_packages:
        metadata_path = package_cache / package_id.casefold() / expected_version.casefold() / ".nupkg.metadata"
        if not metadata_path.is_file():
            fail(f"Missing source metadata for {package_id} {expected_version}: {metadata_path}")
        metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        source_value = str(metadata.get("source", ""))
        if expected_source not in source_path_candidates(source_value):
            fail(f"{package_id} {expected_version} restored from {source_value!r}; expected {package_source.resolve()}.")
    return dict(sorted(resolved.items()))


def verify_consumer(version: str, packages: Path, output: Path, policy: dict[str, Any], env: dict[str, str], nuget_config: Path, package_cache: Path) -> dict[str, Any]:
    consumer = policy["packageConsumer"]
    project = ROOT / consumer["project"]
    for directory in (project.parent / "bin", project.parent / "obj"):
        if directory.exists():
            shutil.rmtree(directory)
    common = [f"-p:TCJStrongTypesPackageVersion={version}", f"-p:RestorePackagesPath={package_cache}"]
    restore = run(
        ["dotnet", "restore", str(project), "--configfile", str(nuget_config), "--force", "--no-cache", *common],
        cwd=ROOT,
        env=env,
        log=output / "logs/package-consumer-restore.log",
    )
    resolved = tcj_assets(project, version, list(consumer["requiredPackages"]), package_cache, packages)
    build = run(
        ["dotnet", "build", str(project), "--configuration", "Release", "--no-restore", "-p:TreatWarningsAsErrors=true", *common],
        cwd=ROOT,
        env=env,
        log=output / "logs/package-consumer-build.log",
    )
    runtime = run(
        ["dotnet", "run", "--project", str(project), "--configuration", "Release", "--no-build", "--no-restore", *common],
        cwd=ROOT,
        env=env,
        log=output / "logs/package-consumer-runtime.log",
    )
    runtime_text = (output / "logs/package-consumer-runtime.log").read_text(encoding="utf-8")
    if consumer["expectedOutput"] not in runtime_text:
        fail(f"Packed Strong Types consumer did not emit expected output {consumer['expectedOutput']!r}.")
    runtime_generator = list((project.parent / "bin/Release/net10.0").rglob("TCJ.Generators.dll"))
    if runtime_generator:
        fail(f"TCJ.Generators.dll leaked into packed consumer runtime output: {runtime_generator}")
    return {
        "project": consumer["project"],
        "expectedPackages": consumer["requiredPackages"],
        "resolvedPackages": resolved,
        "restoreMilliseconds": restore.milliseconds,
        "buildMilliseconds": build.milliseconds,
        "runtimeMilliseconds": runtime.milliseconds,
        "generatorDllInRuntimeOutput": False,
        "status": "pass",
    }


def fixture_source(strong_ids: int, value_objects: int) -> str:
    lines = [
        "using TCJ.Core.Results;",
        "using TCJ.Core.StrongTypes;",
        "",
        "namespace TcjStrongTypesGeneratedFixture;",
        "",
    ]
    for index in range(strong_ids):
        lines.extend([
            "[StronglyTypedId<long>]",
            f"public readonly partial record struct GeneratedId{index:04d};",
            "",
        ])
    for index in range(value_objects):
        lines.extend([
            "[ValueObject<int>]",
            f"public readonly partial record struct GeneratedValue{index:04d}",
            "{",
            "    private static Result Validate(int value) => Result.Success();",
            "}",
            "",
        ])
    return "\n".join(lines)


def create_generated_fixture(directory: Path, version: str, strong_ids: int, value_objects: int) -> Path:
    directory.mkdir(parents=True, exist_ok=True)
    project = directory / "GeneratedFixture.csproj"
    project.write_text(
        f'''<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <Nullable>enable</Nullable>\n    <ImplicitUsings>enable</ImplicitUsings>\n    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include="TCJ.Core" Version="{version}" />\n    <PackageReference Include="TCJ.Generators" Version="{version}" PrivateAssets="all" />\n  </ItemGroup>\n</Project>\n''',
        encoding="utf-8",
        newline="\n",
    )
    (directory / "GeneratedTypes.cs").write_text(fixture_source(strong_ids, value_objects), encoding="utf-8", newline="\n")
    return project


def collect_generated(path: Path) -> GeneratedSnapshot:
    files: dict[str, str] = {}
    for file in sorted(path.rglob("*.g.cs")):
        name = file.name
        if not (name.startswith("TCJ.StronglyTypedId.") or name.startswith("TCJ.ValueObject.")):
            continue
        relative = file.relative_to(path).as_posix()
        digest = hashlib.sha256(file.read_bytes()).hexdigest()
        files[relative] = digest
    return GeneratedSnapshot(files=files, count=len(files))


def clean_fixture(project: Path, env: dict[str, str], log: Path, package_cache: Path) -> None:
    run(
        ["dotnet", "clean", str(project), "--configuration", "Release", f"-p:RestorePackagesPath={package_cache}"],
        cwd=ROOT,
        env=env,
        log=log,
    )


def build_generated_fixture(project: Path, generated: Path, env: dict[str, str], log: Path, package_cache: Path) -> CommandResult:
    if generated.exists():
        shutil.rmtree(generated)
    generated.mkdir(parents=True, exist_ok=True)
    return run(
        [
            "dotnet", "build", str(project), "--configuration", "Release", "--no-restore",
            "-p:TreatWarningsAsErrors=true",
            "-p:EmitCompilerGeneratedFiles=true",
            f"-p:CompilerGeneratedFilesOutputPath={generated}",
            f"-p:RestorePackagesPath={package_cache}",
        ],
        cwd=ROOT,
        env=env,
        log=log,
    )


def restore_generated_fixture(project: Path, nuget_config: Path, env: dict[str, str], log: Path, package_cache: Path) -> None:
    run(
        ["dotnet", "restore", str(project), "--configfile", str(nuget_config), "--force", "--no-cache", f"-p:RestorePackagesPath={package_cache}"],
        cwd=ROOT,
        env=env,
        log=log,
    )


def verify_determinism(version: str, output: Path, policy: dict[str, Any], env: dict[str, str], nuget_config: Path, package_cache: Path) -> dict[str, Any]:
    config = policy["determinism"]
    fixture = output / "work/determinism"
    project = create_generated_fixture(fixture, version, int(config["strongIdCount"]), int(config["valueObjectCount"]))
    restore_generated_fixture(project, nuget_config, env, output / "logs/determinism-restore.log", package_cache)
    generated_one = output / "work/determinism-generated-1"
    generated_two = output / "work/determinism-generated-2"
    clean_fixture(project, env, output / "logs/determinism-clean-1.log", package_cache)
    first_build = build_generated_fixture(project, generated_one, env, output / "logs/determinism-build-1.log", package_cache)
    first = collect_generated(generated_one)
    clean_fixture(project, env, output / "logs/determinism-clean-2.log", package_cache)
    second_build = build_generated_fixture(project, generated_two, env, output / "logs/determinism-build-2.log", package_cache)
    second = collect_generated(generated_two)
    expected_count = int(config["expectedGeneratedFileCount"])
    if first.count != expected_count or second.count != expected_count:
        fail(f"Strong Types determinism fixture expected {expected_count} TCJ generated files; found {first.count} and {second.count}.")
    if first.files != second.files:
        changed = sorted(set(first.files) ^ set(second.files) | {name for name in first.files.keys() & second.files.keys() if first.files[name] != second.files[name]})
        fail(f"Strong Types generated output is not byte-for-byte deterministic across clean rebuilds: {changed[:20]}")
    aggregate = hashlib.sha256("\n".join(f"{name}:{digest}" for name, digest in sorted(first.files.items())).encode("utf-8")).hexdigest()
    return {
        "strongIdCount": config["strongIdCount"],
        "valueObjectCount": config["valueObjectCount"],
        "generatedFileCount": first.count,
        "firstBuildMilliseconds": first_build.milliseconds,
        "secondBuildMilliseconds": second_build.milliseconds,
        "aggregateSha256": aggregate,
        "status": "pass",
    }


def verify_performance(version: str, output: Path, policy: dict[str, Any], env: dict[str, str], nuget_config: Path, package_cache: Path) -> dict[str, Any]:
    config = policy["performance"]
    fixture = output / "work/performance"
    project = create_generated_fixture(fixture, version, int(config["strongIdCount"]), int(config["valueObjectCount"]))
    restore_generated_fixture(project, nuget_config, env, output / "logs/performance-restore.log", package_cache)
    warmups = int(config["warmupRuns"])
    measurements = int(config["measurementRuns"])
    samples: list[int] = []
    for index in range(warmups + measurements):
        clean_fixture(project, env, output / f"logs/performance-clean-{index + 1}.log", package_cache)
        result = build_generated_fixture(
            project,
            output / f"work/performance-generated-{index + 1}",
            env,
            output / f"logs/performance-build-{index + 1}.log",
            package_cache,
        )
        if index >= warmups:
            samples.append(result.milliseconds)
    median = int(round(statistics.median(samples)))
    budget = int(config["maxMedianMilliseconds"])
    if median > budget:
        fail(f"Strong Types generator performance budget exceeded: median {median} ms > {budget} ms for {config['strongIdCount']} Strong IDs + {config['valueObjectCount']} Value Objects.")
    return {
        "strongIdCount": config["strongIdCount"],
        "valueObjectCount": config["valueObjectCount"],
        "warmupRuns": warmups,
        "measurementRuns": measurements,
        "samplesMilliseconds": samples,
        "medianMilliseconds": median,
        "maxMedianMilliseconds": budget,
        "status": "pass",
    }


def write_summary(output: Path, payload: dict[str, Any]) -> None:
    lines = [
        "# Strong Types release verification",
        "",
        "| Contract | Result |",
        "|---|---|",
        "| Packed NuGet consumer | PASS |",
        "| Generator package layout | PASS |",
        "| Byte-for-byte generated source determinism | PASS |",
        "| Generator performance budget | PASS |",
        "| Generator DLL absent from consumer runtime output | PASS |",
        "",
        f"Package version: `{payload['packageVersion']}`",
        f"Determinism digest: `{payload['determinism']['aggregateSha256']}`",
        f"Performance median: `{payload['performance']['medianMilliseconds']} ms` / `{payload['performance']['maxMedianMilliseconds']} ms` budget",
        "",
    ]
    (output / "STRONG_TYPES_SUMMARY.md").write_text("\n".join(lines), encoding="utf-8", newline="\n")


def verify_packed(version: str, packages: Path, output: Path) -> dict[str, Any]:
    version = version.strip()
    if not version:
        fail("Strong Types packed verification requires a non-empty package version.")
    policy = validate_config(ROOT)
    packages = packages.resolve()
    if not packages.is_dir():
        fail(f"Packed package directory does not exist: {packages}")
    output = output.resolve()
    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True, exist_ok=True)

    required = list(policy["packageConsumer"]["requiredPackages"])
    missing = [package_id for package_id in required if not (packages / f"{package_id}.{version}.nupkg").is_file()]
    if missing:
        fail(f"Strong Types packed verification is missing required package(s): {', '.join(missing)}")

    isolated = output / "nuget"
    package_cache = isolated / "packages"
    http_cache = isolated / "http-cache"
    cli_home = isolated / "home"
    for directory in (package_cache, http_cache, cli_home):
        directory.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env.update({
        "NUGET_PACKAGES": str(package_cache),
        "NUGET_HTTP_CACHE_PATH": str(http_cache),
        "DOTNET_CLI_HOME": str(cli_home),
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "true",
    })
    nuget_config = output / "NuGet.Config"
    create_nuget_config(nuget_config, packages)

    generator_package = verify_generator_package(packages, version, policy)
    consumer = verify_consumer(version, packages, output, policy, env, nuget_config, package_cache)
    determinism = verify_determinism(version, output, policy, env, nuget_config, package_cache)
    performance = verify_performance(version, output, policy, env, nuget_config, package_cache)
    payload = {
        "schemaVersion": 1,
        "status": "passed",
        "packageVersion": version,
        "packageDirectory": str(packages),
        "generatorPackage": generator_package,
        "consumer": consumer,
        "determinism": determinism,
        "performance": performance,
    }
    write_json(output / "strong-types-result.json", payload)
    write_summary(output, payload)
    shutil.rmtree(output / "work", ignore_errors=True)
    print(
        "Strong Types packed verification passed: package consumer, generator layout, "
        "determinism, and performance budget are clean."
    )
    return payload


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("validate-config")
    packed = sub.add_parser("verify-packed")
    packed.add_argument("--version", required=True)
    packed.add_argument("--packages", type=Path, default=DEFAULT_PACKAGES)
    packed.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()
    try:
        if args.command == "validate-config":
            policy = validate_config(ROOT)
            print(
                "Strong Types configuration is valid: "
                f"consumer={policy['packageConsumer']['project']}, "
                f"determinismTypes={policy['determinism']['expectedGeneratedFileCount']}, "
                f"performanceTypes={policy['performance']['strongIdCount'] + policy['performance']['valueObjectCount']}."
            )
            return 0
        verify_packed(args.version, args.packages, args.output)
        return 0
    except StrongTypesError as error:
        print(f"Strong Types verification failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
