#!/usr/bin/env python3
"""Verify Native AOT/trimming policy against production package project settings."""

from __future__ import annotations

import argparse
import importlib.util
import json
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_POLICY = ROOT / "eng/aot-policy.json"
DEFAULT_OUTPUT = ROOT / "artifacts/aot/aot-verification.json"
POLICY_MODULE_PATH = ROOT / "eng/verify-aot-policy.py"

SPEC = importlib.util.spec_from_file_location("verify_aot_policy_for_aot_verifier", POLICY_MODULE_PATH)
assert SPEC and SPEC.loader
POLICY = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = POLICY
SPEC.loader.exec_module(POLICY)

DIAGNOSTIC_RE = re.compile(r"^IL[23][0-9]{3}$", re.IGNORECASE)
BROAD_DIAGNOSTIC_RE = re.compile(r"^IL[23].*(?:\*|\?|X)", re.IGNORECASE)
SUPPRESSION_LIST_PROPERTIES = ("NoWarn", "WarningsNotAsErrors")
BROAD_BOOLEAN_PROPERTIES = (
    "SuppressTrimAnalysisWarnings",
    "SuppressAotAnalysisWarnings",
)
ANALYZER_PROPERTIES = (
    "EnableTrimAnalyzer",
    "EnableAotAnalyzer",
)
REPORT_SCHEMA_VERSION = 1
EF_NATIVEAOT_FIXTURE = "tests/TCJ.EntityFrameworkCore.NativeAotExperimental/TCJ.EntityFrameworkCore.NativeAotExperimental.csproj"
EF_NATIVEAOT_PROGRAM = "tests/TCJ.EntityFrameworkCore.NativeAotExperimental/Program.cs"
EF_NATIVEAOT_PROJECT_REFERENCES = (
    "../../src/TCJ.Core/TCJ.Core.csproj",
    "../../src/TCJ.EntityFrameworkCore/TCJ.EntityFrameworkCore.csproj",
    "../../src/TCJ.EntityFrameworkCore.SqlServer/TCJ.EntityFrameworkCore.SqlServer.csproj",
)

PACKED_AOT_FIXTURE = "smoke/TCJ.NativeAot.SmokeTest/TCJ.NativeAot.SmokeTest.csproj"
PACKED_AOT_PROGRAM = "smoke/TCJ.NativeAot.SmokeTest/Program.cs"
PACKED_AOT_NUGET_CONFIG = "smoke/NuGet.Config"
PACKED_AOT_RUNNER = "eng/run-native-aot-smoke.py"
PACKED_AOT_RID = "linux-x64"
PACKED_AOT_RESULT = "artifacts/aot/native-aot-smoke/native-aot-result.json"
PACKED_AOT_PACKAGES = ("TCJ.Core", "TCJ.DependencyInjection", "TCJ.AspNetCore")
BLOCKING_AOT_WORKFLOWS = (
    ".github/workflows/ci.yml",
    ".github/workflows/release-preflight.yml",
    ".github/workflows/release.yml",
)

ANALYZER_FIXTURES = {
    "TCJ.Core": (
        "compatibility/Consumers/Core.Console/Core.Console.csproj",
        ("TCJ.Core",),
    ),
    "TCJ.DependencyInjection": (
        "compatibility/Consumers/DependencyInjection.AotSafe.Console/DependencyInjection.AotSafe.Console.csproj",
        ("TCJ.Core", "TCJ.DependencyInjection"),
    ),
    "TCJ.AspNetCore": (
        "compatibility/Consumers/AspNetCore.MinimalApi/AspNetCore.MinimalApi.csproj",
        ("TCJ.Core", "TCJ.DependencyInjection", "TCJ.AspNetCore"),
    ),
}


@dataclass(frozen=True, order=True)
class Finding:
    package: str
    rule: str
    project: str
    property: str
    value: str
    message: str


@dataclass(frozen=True)
class PackageSnapshot:
    packageId: str
    project: str
    tier: str
    declaredAotProperties: dict[str, str | None]


def repo_relative(path: Path, root: Path) -> str:
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def _tag_name(element: ET.Element) -> str:
    return element.tag.rsplit("}", 1)[-1]


def _property_values(path: Path, property_name: str) -> list[tuple[str, str]]:
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as error:
        raise POLICY.AotPolicyError(f"Invalid project XML in {path.as_posix()}: {error}") from error

    result: list[tuple[str, str]] = []
    for element in root.iter():
        if _tag_name(element) != property_name:
            continue
        value = (element.text or "").strip()
        condition = (element.attrib.get("Condition") or "").strip()
        result.append((value, condition))
    return result


def _resolve_import(importing_file: Path, import_value: str, root: Path) -> Path | None:
    value = import_value.replace("\\", "/")
    this_dir = importing_file.parent.resolve().as_posix().rstrip("/") + "/"
    value = value.replace("$(MSBuildThisFileDirectory)", this_dir)
    if "$(" in value or "*" in value or "?" in value:
        return None
    candidate = Path(value)
    if not candidate.is_absolute():
        candidate = importing_file.parent / candidate
    try:
        candidate.resolve().relative_to(root.resolve())
    except ValueError:
        return None
    return candidate.resolve()


def _project_evaluation_files(project: Path, root: Path) -> tuple[Path, ...]:
    pending: list[Path] = []
    directory_props = root / "Directory.Build.props"
    directory_targets = root / "Directory.Build.targets"
    if directory_props.is_file():
        pending.append(directory_props.resolve())
    pending.append(project.resolve())
    if directory_targets.is_file():
        pending.append(directory_targets.resolve())

    seen: set[Path] = set()
    ordered: list[Path] = []
    while pending:
        current = pending.pop(0)
        if current in seen or not current.is_file():
            continue
        seen.add(current)
        ordered.append(current)
        try:
            xml_root = ET.parse(current).getroot()
        except ET.ParseError as error:
            raise POLICY.AotPolicyError(
                f"Invalid project XML in {repo_relative(current, root)}: {error}"
            ) from error
        imports: list[Path] = []
        for element in xml_root.iter():
            if _tag_name(element) != "Import":
                continue
            import_value = (element.attrib.get("Project") or "").strip()
            if not import_value:
                continue
            resolved = _resolve_import(current, import_value, root)
            if resolved is not None and resolved.is_file():
                imports.append(resolved)
        for imported in sorted(imports, key=lambda item: repo_relative(item, root)):
            if imported not in seen:
                pending.append(imported)
    return tuple(ordered)


def _package_projects(root: Path, policy: Any) -> dict[str, Path]:
    result: dict[str, Path] = {}
    for project in sorted((root / "src").glob("*/*.csproj")):
        try:
            xml_root = ET.parse(project).getroot()
        except ET.ParseError as error:
            raise POLICY.AotPolicyError(
                f"Invalid project XML in {repo_relative(project, root)}: {error}"
            ) from error
        package_ids = [
            (node.text or "").strip()
            for node in xml_root.iter()
            if _tag_name(node) == "PackageId" and (node.text or "").strip()
        ]
        if len(package_ids) == 1:
            result[package_ids[0]] = project

    for package in policy.packages:
        if package.package_id not in result:
            raise POLICY.AotPolicyError(
                f"Production package '{package.package_id}' has no uniquely identifiable project file."
            )
    return result


def _allowed_suppressions(policy: Any) -> set[tuple[str, str, str, str]]:
    raw = policy.warning_policy.get("suppressions", {}).get("allowed", [])
    allowed: set[tuple[str, str, str, str]] = set()
    for entry in raw:
        allowed.add(
            (
                entry["packageId"],
                entry["project"].replace("\\", "/"),
                entry["property"],
                entry["diagnostic"].upper(),
            )
        )
    return allowed


def _split_warning_tokens(value: str) -> tuple[str, ...]:
    tokens = re.split(r"[;,\s]+", value)
    return tuple(token.strip() for token in tokens if token.strip() and not token.strip().startswith("$("))


def _detect_suppressions(
    package_id: str,
    files: Iterable[Path],
    root: Path,
    allowed: set[tuple[str, str, str, str]],
) -> list[Finding]:
    findings: list[Finding] = []
    for path in files:
        project_name = repo_relative(path, root)
        for property_name in SUPPRESSION_LIST_PROPERTIES:
            for value, condition in _property_values(path, property_name):
                rendered_value = value if not condition else f"{value} [Condition: {condition}]"
                for token in _split_warning_tokens(value):
                    upper = token.upper()
                    if upper.startswith(("IL2", "IL3")) and not DIAGNOSTIC_RE.fullmatch(upper):
                        if BROAD_DIAGNOSTIC_RE.fullmatch(upper) or len(upper) < 6:
                            findings.append(
                                Finding(
                                    package_id,
                                    "AOT004",
                                    project_name,
                                    property_name,
                                    rendered_value,
                                    f"Broad trim/AOT diagnostic suppression '{token}' is not allowed.",
                                )
                            )
                    elif DIAGNOSTIC_RE.fullmatch(upper):
                        key = (package_id, project_name, property_name, upper)
                        if key not in allowed:
                            findings.append(
                                Finding(
                                    package_id,
                                    "AOT005",
                                    project_name,
                                    property_name,
                                    rendered_value,
                                    f"Suppression '{upper}' must be narrow, documented, and explicitly listed in eng/aot-policy.json.",
                                )
                            )

        for property_name in BROAD_BOOLEAN_PROPERTIES:
            for value, condition in _property_values(path, property_name):
                if value.lower() == "true":
                    rendered_value = value if not condition else f"{value} [Condition: {condition}]"
                    findings.append(
                        Finding(
                            package_id,
                            "AOT004",
                            project_name,
                            property_name,
                            rendered_value,
                            f"{property_name}=true broadly suppresses trim/AOT diagnostics.",
                        )
                    )

        for property_name in ANALYZER_PROPERTIES:
            for value, condition in _property_values(path, property_name):
                if value.lower() == "false":
                    rendered_value = value if not condition else f"{value} [Condition: {condition}]"
                    findings.append(
                        Finding(
                            package_id,
                            "AOT004",
                            project_name,
                            property_name,
                            rendered_value,
                            f"{property_name}=false disables a trim/AOT analyzer for a production package.",
                        )
                    )
    return findings


def _validate_full_project_contract(
    package: Any, files: Iterable[Path], root: Path
) -> list[Finding]:
    findings: list[Finding] = []
    if package.tier != "Full":
        return findings
    for path in files:
        for value, condition in _property_values(path, "IsAotCompatible"):
            if value.lower() != "false":
                continue
            rendered_value = value if not condition else f"{value} [Condition: {condition}]"
            findings.append(
                Finding(
                    package.package_id,
                    "AOT003",
                    repo_relative(path, root),
                    "IsAotCompatible",
                    rendered_value,
                    "A package declared Full must not explicitly disable IsAotCompatible.",
                )
            )
    return findings


def _validate_analyzer_fixture(package_id: str, project: Path, root: Path) -> list[Finding]:
    fixture_definition = ANALYZER_FIXTURES.get(package_id)
    if fixture_definition is None:
        return []

    relative_fixture, expected_tcj_packages = fixture_definition
    findings: list[Finding] = []

    project_aot_values = _property_values(project, "IsAotCompatible")
    if not any(value.lower() == "true" and not condition for value, condition in project_aot_values):
        findings.append(
            Finding(
                package_id,
                "AOT006",
                repo_relative(project, root),
                "IsAotCompatible",
                " | ".join(value for value, _ in project_aot_values) or "<missing>",
                "A package with an analyzer fixture must unconditionally declare IsAotCompatible=true so its own SDK AOT/trim analyzers run.",
            )
        )

    fixture = root / relative_fixture
    if not fixture.is_file():
        findings.append(
            Finding(
                package_id,
                "AOT006",
                relative_fixture,
                "analyzerFixture",
                "missing",
                "The package-level AOT/trim analyzer fixture is missing.",
            )
        )
        return findings

    try:
        xml_root = ET.parse(fixture).getroot()
    except ET.ParseError as error:
        findings.append(
            Finding(
                package_id,
                "AOT006",
                relative_fixture,
                "analyzerFixture",
                "invalid XML",
                f"The package-level analyzer fixture is invalid XML: {error}",
            )
        )
        return findings

    aot_values = _property_values(fixture, "IsAotCompatible")
    if not any(value.lower() == "true" and not condition for value, condition in aot_values):
        findings.append(
            Finding(
                package_id,
                "AOT006",
                relative_fixture,
                "IsAotCompatible",
                " | ".join(value for value, _ in aot_values) or "<missing>",
                "The package-level compile fixture must enable SDK AOT/trim analyzers with IsAotCompatible=true.",
            )
        )

    package_references = [
        (element.attrib.get("Include") or "").strip()
        for element in xml_root.iter()
        if _tag_name(element) == "PackageReference"
    ]
    tcj_package_references = sorted(
        value for value in package_references if value.startswith("TCJ.")
    )
    expected_references = sorted(expected_tcj_packages)
    if tcj_package_references != expected_references:
        findings.append(
            Finding(
                package_id,
                "AOT006",
                relative_fixture,
                "PackageReference",
                ", ".join(tcj_package_references) or "<missing>",
                "The analyzer fixture must consume exactly the expected packed TCJ package closure: "
                + ", ".join(expected_references)
                + ".",
            )
        )

    project_references = [
        (element.attrib.get("Include") or "").strip()
        for element in xml_root.iter()
        if _tag_name(element) == "ProjectReference"
    ]
    if project_references:
        findings.append(
            Finding(
                package_id,
                "AOT006",
                relative_fixture,
                "ProjectReference",
                ", ".join(sorted(project_references)),
                "The package-level analyzer fixture must not use repository project references.",
            )
        )

    for property_name in ("PublishAot", "PublishTrimmed"):
        enabled = [
            value
            for value, _ in _property_values(fixture, property_name)
            if value.lower() == "true"
        ]
        if enabled:
            findings.append(
                Finding(
                    package_id,
                    "AOT006",
                    relative_fixture,
                    property_name,
                    "true",
                    f"{property_name}=true is outside this compile-only analyzer fixture; packaged Native AOT publish/run belongs to Important 8.",
                )
            )

    return findings



def _validate_ef_nativeaot_fixture(root: Path) -> list[Finding]:
    findings: list[Finding] = []
    fixture = root / EF_NATIVEAOT_FIXTURE
    package_id = "TCJ.EntityFrameworkCore"

    def add(property_name: str, value: str, message: str) -> None:
        findings.append(Finding(package_id, "AOT007", EF_NATIVEAOT_FIXTURE, property_name, value, message))

    if not fixture.is_file():
        add(
            "experimentalFixture",
            "missing",
            "The experimental EF NativeAOT fixture is missing. Add the project-reference fixture with PublishAot=true and EF compiled-model/query-precompile tooling.",
        )
        return findings

    try:
        xml_root = ET.parse(fixture).getroot()
    except ET.ParseError as error:
        add("experimentalFixture", "invalid XML", f"The experimental EF NativeAOT fixture is invalid XML: {error}")
        return findings

    required_properties = {
        "PublishAot": "true",
        "IsAotCompatible": "true",
        "EFOptimizeContext": "true",
        "EFScaffoldModelStage": "publish",
        "EFPrecompileQueriesStage": "publish",
    }
    for property_name, expected in required_properties.items():
        values = _property_values(fixture, property_name)
        if not any(value.lower() == expected and not condition for value, condition in values):
            add(
                property_name,
                " | ".join(value for value, _ in values) or "<missing>",
                f"The experimental EF NativeAOT fixture must set {property_name}={expected} unconditionally so EF Core generates the compiled model and precompiled queries during publish.",
            )

    runtime_values = _property_values(fixture, "RuntimeIdentifier")
    if not any(value and "$(" not in value and not condition for value, condition in runtime_values):
        add(
            "RuntimeIdentifier",
            " | ".join(value for value, _ in runtime_values) or "<missing>",
            "The EF MSBuild publish integration requires an explicit RuntimeIdentifier in the startup project; set a concrete RID such as linux-x64.",
        )

    interceptors = _property_values(fixture, "InterceptorsNamespaces")
    if not any("Microsoft.EntityFrameworkCore.GeneratedInterceptors" in value and not condition for value, condition in interceptors):
        add(
            "InterceptorsNamespaces",
            " | ".join(value for value, _ in interceptors) or "<missing>",
            "EF NativeAOT query precompilation requires Microsoft.EntityFrameworkCore.GeneratedInterceptors in InterceptorsNamespaces.",
        )

    task_references = [
        element for element in xml_root.iter()
        if _tag_name(element) == "PackageReference"
        and (element.attrib.get("Include") or "").strip() == "Microsoft.EntityFrameworkCore.Tasks"
    ]
    if len(task_references) != 1:
        add(
            "Microsoft.EntityFrameworkCore.Tasks",
            str(len(task_references)),
            "The experimental EF NativeAOT fixture must reference Microsoft.EntityFrameworkCore.Tasks exactly once; the EF compiled-model/query-precompile MSBuild integration is not transitive.",
        )
    else:
        task = task_references[0]
        child_values = {
            _tag_name(child): (child.text or "").strip()
            for child in task
        }
        if child_values.get("PrivateAssets", "").lower() != "all":
            add(
                "Microsoft.EntityFrameworkCore.Tasks.PrivateAssets",
                child_values.get("PrivateAssets", "<missing>"),
                "Microsoft.EntityFrameworkCore.Tasks must use PrivateAssets=all in the experimental fixture.",
            )
        include_assets = {token.strip().lower() for token in child_values.get("IncludeAssets", "").split(";") if token.strip()}
        required_assets = {"build", "analyzers", "buildtransitive"}
        if not required_assets.issubset(include_assets):
            add(
                "Microsoft.EntityFrameworkCore.Tasks.IncludeAssets",
                child_values.get("IncludeAssets", "<missing>"),
                "Microsoft.EntityFrameworkCore.Tasks must expose its build, analyzers, and buildtransitive assets to the experimental fixture.",
            )

    project_references = sorted(
        (element.attrib.get("Include") or "").strip().replace("\\", "/")
        for element in xml_root.iter()
        if _tag_name(element) == "ProjectReference"
    )
    expected_references = sorted(EF_NATIVEAOT_PROJECT_REFERENCES)
    if project_references != expected_references:
        add(
            "ProjectReference",
            ", ".join(project_references) or "<missing>",
            "The experimental fixture must exercise exactly TCJ.Core, TCJ.EntityFrameworkCore, and TCJ.EntityFrameworkCore.SqlServer by project reference. Packed-package publish/execute evidence belongs to Important 8.",
        )

    program = root / EF_NATIVEAOT_PROGRAM
    if not program.is_file():
        add("Program.cs", "missing", "The experimental EF NativeAOT fixture must include a self-contained startup DbContext and representative static LINQ query.")
        return findings

    source = program.read_text(encoding="utf-8")
    required_fragments = (
        "UseSqlServer(",
        "ApplyTcjSqlServerConventions()",
        "ToListAsync()",
        'args.Contains("--execute-query", StringComparer.Ordinal)',
    )
    for fragment in required_fragments:
        if fragment not in source:
            add(
                "Program.cs",
                fragment,
                f"The experimental EF NativeAOT fixture must include '{fragment}' so provider setup, TCJ SQL Server conventions, and a statically analyzable EF query are exercised.",
            )

    if "LoadNamesAsync(ExperimentalNativeAotDbContext" in source:
        add(
            "Program.cs",
            "DbContext method parameter query root",
            "EF Core 10 query precompilation does not recognize a DbContext method parameter as a static query root. Keep the representative query rooted in the local startup DbContext.",
        )

    restricted_fragments = (
        "RegisterEntityTypeConfiguration(",
        "RegisterAllEntities<",
        "GetModuleAssemblies(",
        "ApplySoftDeleteQueryFilters(",
        "IEntitySearcher",
        "EntitySearcher",
        "AddTcjOutbox",
    )
    for fragment in restricted_fragments:
        if fragment in source:
            add(
                "Program.cs",
                fragment,
                f"The experimental EF NativeAOT fixture must stay on the documented static path and must not use restricted or compiled-model-unsupported API '{fragment}'.",
            )

    return findings


def _validate_packed_native_aot_smoke(root: Path, policy: Any) -> list[Finding]:
    findings: list[Finding] = []
    package_id = "<packed-native-aot>"

    def add(rule: str, project: str, property_name: str, value: str, message: str) -> None:
        findings.append(Finding(package_id, rule, project, property_name, value, message))

    fixture = root / PACKED_AOT_FIXTURE
    if not fixture.is_file():
        add("AOT008", PACKED_AOT_FIXTURE, "fixture", "missing", "Add the packed-package Native AOT smoke project required by Important 8.")
        return findings

    try:
        xml_root = ET.parse(fixture).getroot()
    except ET.ParseError as error:
        add("AOT008", PACKED_AOT_FIXTURE, "fixture", "invalid XML", f"The packed Native AOT smoke project is invalid XML: {error}")
        return findings

    required_properties = {
        "PublishAot": "true",
        "SelfContained": "true",
        "IsAotCompatible": "true",
        "JsonSerializerIsReflectionEnabledByDefault": "false",
        "TreatWarningsAsErrors": "true",
        "RuntimeIdentifier": PACKED_AOT_RID,
    }
    for property_name, expected in required_properties.items():
        values = _property_values(fixture, property_name)
        if not any(value.lower() == expected.lower() and not condition for value, condition in values):
            add(
                "AOT008",
                PACKED_AOT_FIXTURE,
                property_name,
                " | ".join(value for value, _ in values) or "<missing>",
                f"The packed Native AOT smoke project must set {property_name}={expected} unconditionally.",
            )

    package_references: dict[str, str] = {}
    project_references: list[str] = []
    for element in xml_root.iter():
        tag = _tag_name(element)
        if tag == "PackageReference":
            include = (element.attrib.get("Include") or "").strip()
            if not include.startswith("TCJ."):
                continue
            version = (element.attrib.get("Version") or "").strip()
            if not version:
                version_node = next((child for child in element if _tag_name(child) == "Version"), None)
                version = ((version_node.text if version_node is not None else "") or "").strip()
            package_references[include] = version
        elif tag == "ProjectReference":
            project_references.append((element.attrib.get("Include") or "").strip())

    full_packages = sorted(package.package_id for package in policy.packages if package.tier == "Full")
    expected_packages = sorted(PACKED_AOT_PACKAGES)
    if full_packages != expected_packages:
        add(
            "AOT009",
            "eng/aot-policy.json",
            "FullPackages",
            ", ".join(full_packages) or "<none>",
            "The current Full support package set must match the packed Native AOT smoke closure. Add executable evidence before changing a support tier.",
        )

    if sorted(package_references) != expected_packages:
        add(
            "AOT008",
            PACKED_AOT_FIXTURE,
            "PackageReference",
            ", ".join(sorted(package_references)) or "<missing>",
            "The smoke project must consume exactly TCJ.Core, TCJ.DependencyInjection, and TCJ.AspNetCore as packages.",
        )
    for referenced_package, version in sorted(package_references.items()):
        if version != "$(TCJNativeAotPackageVersion)":
            add(
                "AOT008",
                PACKED_AOT_FIXTURE,
                f"PackageReference:{referenced_package}",
                version or "<missing>",
                "Every TCJ smoke PackageReference must use $(TCJNativeAotPackageVersion) so the candidate version is injected by CI/release.",
            )
    if project_references:
        add(
            "AOT008",
            PACKED_AOT_FIXTURE,
            "ProjectReference",
            ", ".join(sorted(project_references)),
            "The packed Native AOT release guarantee must not use repository project references.",
        )

    for package in policy.packages:
        if package.tier != "Full":
            continue
        matching = [
            evidence for evidence in package.full_support_evidence
            if evidence.get("consumerProject") == PACKED_AOT_FIXTURE
            and evidence.get("runtimeIdentifier") == PACKED_AOT_RID
            and evidence.get("resultArtifact") == PACKED_AOT_RESULT
        ]
        if not matching:
            add(
                "AOT009",
                "eng/aot-policy.json",
                package.package_id,
                "missing executable evidence",
                f"Full package '{package.package_id}' must point at the packed smoke project, {PACKED_AOT_RID}, and {PACKED_AOT_RESULT}.",
            )

    program = root / PACKED_AOT_PROGRAM
    if not program.is_file():
        add("AOT008", PACKED_AOT_PROGRAM, "Program.cs", "missing", "The packed Native AOT smoke program is missing.")
    else:
        source = program.read_text(encoding="utf-8")
        required_fragments = (
            "AddTcjDependencyInjection()",
            "AddTcjDomainEvent<SmokeDomainEvent>()",
            "IDomainEventHandler<SmokeDomainEvent>",
            "WebApplication.CreateSlimBuilder",
            "AddTcjAspNetCore()",
            "UseTcjAspNetCore()",
            '"/success"',
            '"/validation"',
            '"/not-found"',
            '"/conflict"',
            '"/unhandled"',
            '"/domain-event"',
            'ReadAssemblyMetadata(assembly, "PackageVersion")',
            "TCJ_PACKAGE_VERSION",
            "TCJ Native AOT packed-package smoke passed",
        )
        for fragment in required_fragments:
            if fragment not in source:
                add(
                    "AOT008",
                    PACKED_AOT_PROGRAM,
                    "Program.cs",
                    fragment,
                    f"The packed Native AOT smoke must exercise required behavior '{fragment}'.",
                )
        restricted_fragments = (
            "AddAssembly(",
            "AddAssemblies(",
            "AddAssemblyContaining<",
            "RegisterEntityTypeConfiguration(",
            "RegisterAllEntities<",
            "TCJ.EntityFrameworkCore",
        )
        for fragment in restricted_fragments:
            if fragment in source:
                add(
                    "AOT008",
                    PACKED_AOT_PROGRAM,
                    "Program.cs",
                    fragment,
                    f"The Full packed Native AOT smoke must stay on the explicit non-EF support path and must not use '{fragment}'.",
                )

    config = root / PACKED_AOT_NUGET_CONFIG
    if not config.is_file():
        add("AOT008", PACKED_AOT_NUGET_CONFIG, "NuGet.Config", "missing", "The packed Native AOT smoke must pin TCJ.* to the local artifacts/packages feed.")
    else:
        text = config.read_text(encoding="utf-8")
        for fragment in ('key="tcj-local" value="../artifacts/packages"', '<package pattern="TCJ.*" />', 'key="nuget.org"'):
            if fragment not in text:
                add("AOT008", PACKED_AOT_NUGET_CONFIG, "NuGet.Config", fragment, f"The packed Native AOT NuGet config is missing required source-mapping fragment {fragment!r}.")

    runner = root / PACKED_AOT_RUNNER
    if not runner.is_file():
        add("AOT008", PACKED_AOT_RUNNER, "runner", "missing", "The packed Native AOT smoke runner is missing.")

    required_workflow_fragments = (
        "python3 eng/verify-aot.py verify",
        "python3 eng/run-native-aot-smoke.py",
        "python3 eng/verify-aot.py verify-result",
        PACKED_AOT_RID,
    )
    for workflow_name in BLOCKING_AOT_WORKFLOWS:
        workflow = root / workflow_name
        if not workflow.is_file():
            add("AOT009", workflow_name, "workflow", "missing", "A blocking AOT workflow is missing.")
            continue
        workflow_text = workflow.read_text(encoding="utf-8")
        for fragment in required_workflow_fragments:
            if fragment not in workflow_text:
                add(
                    "AOT009",
                    workflow_name,
                    "workflow",
                    fragment,
                    f"{workflow_name} must run the blocking packed Native AOT policy/smoke/result gate.",
                )

    for package_id in ("TCJ.EntityFrameworkCore", "TCJ.EntityFrameworkCore.SqlServer"):
        package = next((item for item in policy.packages if item.package_id == package_id), None)
        if package is not None and package.tier != "Experimental":
            add(
                "AOT009",
                "eng/aot-policy.json",
                package_id,
                package.tier,
                f"{package_id} must remain Experimental until its separate packaged Native AOT support tier is explicitly upgraded.",
            )

    return findings


def _result_finding(property_name: str, value: Any, message: str) -> Finding:
    rendered = json.dumps(value, sort_keys=True) if isinstance(value, (dict, list)) else str(value)
    return Finding("<packed-native-aot>", "AOT100", PACKED_AOT_RESULT, property_name, rendered, message)


def verify_runtime_result(
    *,
    root: Path = ROOT,
    policy_path: Path | None = None,
    result_path: Path | None = None,
    package_directory: Path | None = None,
    expected_version: str,
    output_path: Path | None = None,
) -> tuple[dict[str, Any], bool]:
    root = root.resolve()
    policy_path = (policy_path or root / "eng/aot-policy.json").resolve()
    result_path = (result_path or root / PACKED_AOT_RESULT).resolve()
    package_directory = (package_directory or root / "artifacts/packages").resolve()
    output_path = (output_path or root / "artifacts/aot/aot-runtime-verification.json").resolve()
    findings: list[Finding] = []
    result: dict[str, Any] = {}
    full_packages: list[str] = []

    try:
        policy = POLICY.validate_configuration(root, policy_path)
        full_packages = sorted(package.package_id for package in policy.packages if package.tier == "Full")
        if full_packages != sorted(PACKED_AOT_PACKAGES):
            findings.append(_result_finding("FullPackages", full_packages, "Full support tiers drifted from the executable packed Native AOT smoke evidence."))

        if not result_path.is_file():
            findings.append(_result_finding("result", "missing", f"Missing Native AOT runtime result: {repo_relative(result_path, root)}"))
        else:
            try:
                raw = json.loads(result_path.read_text(encoding="utf-8"))
                if not isinstance(raw, dict):
                    raise ValueError("result root must be a JSON object")
                result = raw
            except (json.JSONDecodeError, ValueError) as error:
                findings.append(_result_finding("result", "invalid", f"Invalid Native AOT runtime result: {error}"))

        if result:
            expected_scalars = {
                "schemaVersion": 1,
                "status": "passed",
                "packageVersion": expected_version,
                "runtimeIdentifier": PACKED_AOT_RID,
                "consumerProject": PACKED_AOT_FIXTURE,
                "consumerSource": "PackedNuGet",
                "usesProjectReference": False,
                "publishAot": True,
                "packageSourceStatus": "pass",
                "restoreStatus": "pass",
                "publishStatus": "pass",
                "executionStatus": "pass",
                "unexpectedAotWarningCount": 0,
                "warningCount": 0,
                "failure": None,
            }
            for key, expected in expected_scalars.items():
                if result.get(key) != expected:
                    findings.append(_result_finding(key, result.get(key), f"Native AOT result {key} must be {expected!r}."))

            expected_package_set = sorted(full_packages)
            if sorted(result.get("expectedPackages") or []) != expected_package_set:
                findings.append(_result_finding("expectedPackages", result.get("expectedPackages"), "Runtime result expectedPackages must match the current Full support tier package set."))

            for key in ("resolvedPackages", "loadedPackageVersions"):
                value = result.get(key)
                if not isinstance(value, dict) or sorted(value) != expected_package_set:
                    findings.append(_result_finding(key, value, f"{key} must contain every and only Full support package."))
                    continue
                wrong = {package_id: version for package_id, version in value.items() if version != expected_version}
                if wrong:
                    findings.append(_result_finding(key, wrong, f"{key} must report candidate version {expected_version} for every Full package."))

            for key in ("trimWarnings", "aotWarnings", "tcjWarnings", "upstreamWarnings"):
                value = result.get(key)
                if value != []:
                    findings.append(_result_finding(key, value, "Full Native AOT evidence must contain no IL2xxx/IL3xxx warning baseline or accepted warning list."))

        for package_id in full_packages:
            package_path = package_directory / f"{package_id}.{expected_version}.nupkg"
            if not package_path.is_file():
                findings.append(_result_finding("package", package_path.name, f"Full package '{package_id}' is missing from the exact packed release candidate set."))
    except POLICY.AotPolicyError as error:
        findings.append(_result_finding("policy", "invalid", str(error)))

    success = not findings
    payload = {
        "schemaVersion": REPORT_SCHEMA_VERSION,
        "status": "passed" if success else "failed",
        "packageVersion": expected_version,
        "runtimeIdentifier": PACKED_AOT_RID,
        "fullPackages": full_packages,
        "result": repo_relative(result_path, root),
        "packageDirectory": repo_relative(package_directory, root),
        "findings": [asdict(item) for item in sorted(findings)],
    }
    _write_report(output_path, payload)
    return payload, success

def _snapshot(package: Any, project: Path, root: Path) -> PackageSnapshot:
    tracked = ("IsAotCompatible", "PublishTrimmed", "EnableTrimAnalyzer", "EnableAotAnalyzer")
    properties: dict[str, str | None] = {}
    for name in tracked:
        values = _property_values(project, name)
        if not values:
            properties[name] = None
        else:
            properties[name] = " | ".join(
                value if not condition else f"{value} [Condition: {condition}]"
                for value, condition in values
            )
    return PackageSnapshot(
        packageId=package.package_id,
        project=repo_relative(project, root),
        tier=package.tier,
        declaredAotProperties=properties,
    )


def _report_payload(
    *,
    root: Path,
    policy_path: Path,
    status: str,
    packages: Iterable[PackageSnapshot],
    findings: Iterable[Finding],
) -> dict[str, Any]:
    sorted_packages = sorted(packages, key=lambda item: item.packageId)
    sorted_findings = sorted(findings)
    return {
        "schemaVersion": REPORT_SCHEMA_VERSION,
        "status": status,
        "policy": repo_relative(policy_path, root),
        "packages": [asdict(item) for item in sorted_packages],
        "findings": [asdict(item) for item in sorted_findings],
    }


def _write_report(output: Path, payload: dict[str, Any]) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(payload, indent=2, sort_keys=True, ensure_ascii=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def verify_repository(
    root: Path = ROOT,
    policy_path: Path | None = None,
    output_path: Path | None = None,
) -> tuple[dict[str, Any], bool]:
    root = root.resolve()
    policy_path = (policy_path or root / "eng/aot-policy.json").resolve()
    output_path = (output_path or root / "artifacts/aot/aot-verification.json").resolve()

    packages: list[PackageSnapshot] = []
    findings: list[Finding] = []
    try:
        policy = POLICY.validate_configuration(root, policy_path)
        projects = _package_projects(root, policy)
        allowed = _allowed_suppressions(policy)
        for package in sorted(policy.packages, key=lambda item: item.package_id):
            project = projects[package.package_id]
            packages.append(_snapshot(package, project, root))
            evaluation_files = _project_evaluation_files(project, root)
            findings.extend(_validate_full_project_contract(package, evaluation_files, root))
            findings.extend(_validate_analyzer_fixture(package.package_id, project, root))
            findings.extend(
                _detect_suppressions(
                    package.package_id,
                    evaluation_files,
                    root,
                    allowed,
                )
            )
        findings.extend(_validate_ef_nativeaot_fixture(root))
        findings.extend(_validate_packed_native_aot_smoke(root, policy))
    except POLICY.AotPolicyError as error:
        findings.append(
            Finding(
                "<repository>",
                "AOT001",
                repo_relative(policy_path, root),
                "policy",
                "",
                str(error),
            )
        )

    success = not findings
    payload = _report_payload(
        root=root,
        policy_path=policy_path,
        status="passed" if success else "failed",
        packages=packages,
        findings=findings,
    )
    _write_report(output_path, payload)
    return payload, success


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    verify = subparsers.add_parser("verify", help="Validate policy, package projects, smoke fixture, and blocking workflow wiring.")
    verify.add_argument("--root", type=Path, default=ROOT)
    verify.add_argument("--policy", type=Path)
    verify.add_argument("--output", type=Path)

    verify_result = subparsers.add_parser("verify-result", help="Validate packed Native AOT execution evidence against the current Full support tiers.")
    verify_result.add_argument("--root", type=Path, default=ROOT)
    verify_result.add_argument("--policy", type=Path)
    verify_result.add_argument("--result", type=Path)
    verify_result.add_argument("--packages", type=Path)
    verify_result.add_argument("--version", required=True)
    verify_result.add_argument("--output", type=Path)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    root = args.root.resolve()
    policy = args.policy
    if policy is not None and not policy.is_absolute():
        policy = root / policy
    output = args.output
    if output is not None and not output.is_absolute():
        output = root / output

    if args.command == "verify-result":
        result = args.result
        if result is not None and not result.is_absolute():
            result = root / result
        packages = args.packages
        if packages is not None and not packages.is_absolute():
            packages = root / packages
        payload, success = verify_runtime_result(
            root=root,
            policy_path=policy,
            result_path=result,
            package_directory=packages,
            expected_version=args.version,
            output_path=output,
        )
        if success:
            print(
                f"Native AOT runtime evidence passed for {len(payload['fullPackages'])} Full packages at version {args.version}."
            )
            return 0
    else:
        payload, success = verify_repository(root, policy, output)
        if success:
            print(
                f"Native AOT verification passed for {len(payload['packages'])} production packages."
            )
            return 0

    for finding in payload["findings"]:
        print(
            f"{finding['rule']} {finding['package']} {finding['project']} "
            f"{finding['property']}: {finding['message']}",
            file=sys.stderr,
        )
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
