#!/usr/bin/env python3
"""Validate TCJ clean-room package consumer compatibility configuration and results."""
from __future__ import annotations

import argparse
import fnmatch
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any, Iterable
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
POLICY_REL = Path("eng/compatibility-policy.json")
REQUIRED_PACKAGE_IDS = {
    "TCJ.Core", "TCJ.DependencyInjection", "TCJ.EntityFrameworkCore",
    "TCJ.EntityFrameworkCore.SqlServer", "TCJ.AspNetCore", "TCJ.Messaging",
}
SEMVER_PATTERN = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")
WINDOWS_ABSOLUTE_PATTERN = re.compile(rb"[A-Za-z]:\\(?:Users|agent|runner|work|src|home)\\", re.IGNORECASE)
UNIX_ABSOLUTE_MARKERS = (b"/home/runner/", b"/Users/runner/", b"/mnt/data/", b"/agent/_work/")
SOURCE_LINK_MARKER = b'"documents"'


class VerificationError(RuntimeError):
    pass


@dataclass(frozen=True)
class PackageValidation:
    package_id: str
    version: str
    target_frameworks: tuple[str, ...]
    tcj_dependencies: tuple[str, ...]
    nupkg: str
    snupkg: str
    xml_documentation: bool
    portable_pdb: bool
    source_link: bool
    repository_metadata: bool


def fail(message: str) -> None:
    raise VerificationError(message)


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        fail(f"Required file is missing: {path}")
    except json.JSONDecodeError as error:
        fail(f"Malformed JSON in {path}: {error}")


def load_policy(root: Path = ROOT) -> dict[str, Any]:
    data = read_json(root / POLICY_REL)
    if not isinstance(data, dict): fail("Compatibility policy must be a JSON object.")
    required_keys = {
        "schemaVersion", "requiredPackages", "requiredOperatingSystems", "requiredConfigurations",
        "requiredArchitectures", "requiredArchitectureByOperatingSystem", "supportedTargetFrameworks", "minimumConsumerCount",
        "requirePackageOnlyReferences", "requireLocalPackageSource", "requireSourcePackageValidation",
        "requireSymbolPackageValidation", "requireSourceLinkValidation", "failOnWarnings", "consumers",
        "publishedConsumers", "expectedTcjDependencies",
    }
    missing = sorted(required_keys.difference(data))
    if missing: fail(f"Compatibility policy is missing fields: {', '.join(missing)}")
    if data["schemaVersion"] != 1: fail("Unsupported compatibility policy schemaVersion.")
    packages = data["requiredPackages"]
    if not isinstance(packages, list) or set(packages) != REQUIRED_PACKAGE_IDS or len(packages) != len(REQUIRED_PACKAGE_IDS):
        fail("Compatibility policy must list exactly the six TCJ runtime release packages.")
    expected_os = ["ubuntu-latest", "windows-latest", "macos-latest"]
    if data["requiredOperatingSystems"] != expected_os: fail(f"requiredOperatingSystems must be {expected_os}.")
    if data["requiredConfigurations"] != ["Release"]: fail("Release must be the only required compatibility configuration.")
    if data["requiredArchitectures"] != ["x64", "arm64"]:
        fail("Compatibility architecture set must cover x64 and arm64 for the current hosted-runner matrix.")
    expected_architecture_map = {"ubuntu-latest": "x64", "windows-latest": "x64", "macos-latest": "arm64"}
    if data["requiredArchitectureByOperatingSystem"] != expected_architecture_map:
        fail(f"requiredArchitectureByOperatingSystem must be {expected_architecture_map}.")
    frameworks = data["supportedTargetFrameworks"]
    if not isinstance(frameworks, list) or not frameworks or any(not isinstance(item, str) or not item for item in frameworks):
        fail("supportedTargetFrameworks must be a non-empty string array.")
    if int(data["minimumConsumerCount"]) < 8: fail("minimumConsumerCount must be at least 8 after the DI AOT-safe analyzer consumer was added.")
    for key in ("requirePackageOnlyReferences", "requireLocalPackageSource", "requireSourcePackageValidation", "requireSymbolPackageValidation", "requireSourceLinkValidation", "failOnWarnings"):
        if data[key] is not True: fail(f"{key} must be true.")
    consumers = data["consumers"]
    if not isinstance(consumers, list) or len(consumers) < int(data["minimumConsumerCount"]):
        fail("Compatibility policy does not define enough consumers.")
    names = [item.get("name") for item in consumers if isinstance(item, dict)]
    if len(names) != len(consumers) or len(names) != len(set(names)): fail("Consumer names must be unique strings.")
    if set(data["publishedConsumers"]) != {"Core.Console", "AspNetCore.MinimalApi", "FullStack.MinimalApi"}:
        fail("Published-package compatibility must reuse Core, ASP.NET Core, and full-stack consumers.")
    aot_safe_consumers = [item for item in consumers if item.get("name") == "DependencyInjection.AotSafe.Console"]
    if len(aot_safe_consumers) != 1:
        fail("Compatibility policy must define exactly one DependencyInjection.AotSafe.Console package consumer.")
    expected_aot_safe_packages = ["TCJ.Core", "TCJ.DependencyInjection"]
    if aot_safe_consumers[0].get("packages") != expected_aot_safe_packages:
        fail(f"DependencyInjection.AotSafe.Console must declare exactly {expected_aot_safe_packages}.")
    aot_safe_project = root / str(aot_safe_consumers[0].get("project", ""))
    try:
        aot_safe_root = ET.parse(aot_safe_project).getroot()
    except (FileNotFoundError, ET.ParseError) as error:
        fail(f"Invalid DependencyInjection.AotSafe.Console project: {error}")
    aot_properties = [
        (node.text or "").strip().casefold()
        for node in aot_safe_root.iter()
        if strip_namespace(node.tag) == "IsAotCompatible"
    ]
    if aot_properties != ["true"]:
        fail("DependencyInjection.AotSafe.Console must set IsAotCompatible=true so SDK trim/AOT analyzers run at the safe bootstrap call site.")

    aot_safe_program = aot_safe_project.parent / "Program.cs"
    try:
        aot_safe_source = aot_safe_program.read_text(encoding="utf-8")
    except FileNotFoundError:
        fail("DependencyInjection.AotSafe.Console must include Program.cs with explicit AOT-safe domain-event dispatch coverage.")
    for required_fragment in (
        "AddTcjDependencyInjection()",
        "AddTcjDomainEvent<",
        "AddTransient<IDomainEventHandler<",
        "DispatchAsync(",
    ):
        if required_fragment not in aot_safe_source:
            fail(
                "DependencyInjection.AotSafe.Console must exercise the reflection-free bootstrap, "
                "a closed domain-event route, explicit handler registration, and dispatch; "
                f"missing {required_fragment!r}."
            )

    aspnet_aot_consumers = [item for item in consumers if item.get("name") == "AspNetCore.MinimalApi"]
    if len(aspnet_aot_consumers) != 1:
        fail("Compatibility policy must define exactly one AspNetCore.MinimalApi package consumer.")
    expected_aspnet_aot_packages = ["TCJ.Core", "TCJ.DependencyInjection", "TCJ.AspNetCore"]
    if aspnet_aot_consumers[0].get("packages") != expected_aspnet_aot_packages:
        fail(f"AspNetCore.MinimalApi must declare exactly {expected_aspnet_aot_packages}.")
    aspnet_aot_project = root / str(aspnet_aot_consumers[0].get("project", ""))
    try:
        aspnet_aot_root = ET.parse(aspnet_aot_project).getroot()
    except (FileNotFoundError, ET.ParseError) as error:
        fail(f"Invalid AspNetCore.MinimalApi project: {error}")
    aspnet_aot_properties = {
        strip_namespace(node.tag): (node.text or "").strip().casefold()
        for node in aspnet_aot_root.iter()
    }
    required_aspnet_aot_properties = {
        "IsAotCompatible": "true",
        "EnableRequestDelegateGenerator": "true",
        "JsonSerializerIsReflectionEnabledByDefault": "false",
    }
    for property_name, expected_value in required_aspnet_aot_properties.items():
        if aspnet_aot_properties.get(property_name) != expected_value:
            fail(
                f"AspNetCore.MinimalApi must set {property_name}={expected_value} so its package-only "
                "Minimal API path is analyzed through the Native AOT request-delegate generator."
            )

    outbox_consumers = [item for item in consumers if item.get("name") == "Outbox.Console"]
    if len(outbox_consumers) != 1:
        fail("Compatibility policy must define exactly one Outbox.Console package consumer.")
    expected_outbox_packages = ["TCJ.Core", "TCJ.DependencyInjection", "TCJ.EntityFrameworkCore"]
    if outbox_consumers[0].get("packages") != expected_outbox_packages:
        fail(f"Outbox.Console must declare exactly {expected_outbox_packages}.")
    expected_deps = data["expectedTcjDependencies"]
    if set(expected_deps) != REQUIRED_PACKAGE_IDS: fail("expectedTcjDependencies must cover all TCJ packages.")
    return data


def strip_namespace(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def xml_children(element: ET.Element, name: str) -> list[ET.Element]:
    return [child for child in element.iter() if strip_namespace(child.tag) == name]


def validate_nuget_config(root: Path, published: bool = False) -> None:
    path = root / "compatibility" / ("NuGet.Published.Config" if published else "NuGet.Config")
    try: tree = ET.parse(path)
    except FileNotFoundError: fail(f"Required NuGet config is missing: {path}")
    except ET.ParseError as error: fail(f"Invalid NuGet config {path}: {error}")
    package_sources = next((item for item in tree.getroot().iter() if strip_namespace(item.tag) == "packageSources"), None)
    if package_sources is None: fail(f"{path} has no packageSources section.")
    sources = [(item.attrib.get("key", ""), item.attrib.get("value", "")) for item in package_sources if strip_namespace(item.tag) == "add"]
    if published:
        if sources != [("nuget.org", "https://api.nuget.org/v3/index.json")]: fail("Published NuGet config must use only NuGet.org.")
    else:
        if len(sources) != 2 or sources[0][0] != "tcj-local" or "artifacts/compatibility/packages" not in sources[0][1].replace("\\", "/"):
            fail("Local TCJ feed must be the first compatibility package source.")
        if sources[1] != ("nuget.org", "https://api.nuget.org/v3/index.json"):
            fail("NuGet.org must be the second compatibility package source.")
    mappings: dict[str, list[str]] = {}
    mapping_root = next((item for item in tree.getroot().iter() if strip_namespace(item.tag) == "packageSourceMapping"), None)
    if mapping_root is None: fail(f"{path} must use packageSourceMapping.")
    for source in mapping_root:
        if strip_namespace(source.tag) != "packageSource": continue
        mappings[source.attrib.get("key", "")] = [item.attrib.get("pattern", "") for item in source if strip_namespace(item.tag) == "package"]
    if published:
        if mappings.get("nuget.org") != ["*"]:
            fail("Published NuGet config must map all packages to NuGet.org.")
    else:
        if mappings.get("tcj-local") != ["TCJ.*"]:
            fail("Local NuGet config must map TCJ.* to tcj-local with the specific prefix pattern.")
        public_patterns = mappings.get("nuget.org", [])
        if public_patterns != ["*"]:
            fail("NuGet.org must use a wildcard mapping for public direct and transitive dependencies.")
        if "TCJ.*" in public_patterns:
            fail("NuGet.org must not define a competing TCJ-specific source mapping.")


def parse_project(path: Path) -> tuple[set[str], bool]:
    try: root = ET.parse(path).getroot()
    except (FileNotFoundError, ET.ParseError) as error: fail(f"Invalid consumer project {path}: {error}")
    project_refs = [item for item in root.iter() if strip_namespace(item.tag) == "ProjectReference"]
    if project_refs: fail(f"Consumer project must not contain ProjectReference: {path}")
    text = path.read_text(encoding="utf-8")
    if re.search(r"(?:^|[\\/])src[\\/]TCJ\.", text, re.IGNORECASE): fail(f"Consumer project references repository source paths: {path}")
    packages: set[str] = set()
    version_ok = True
    for item in root.iter():
        if strip_namespace(item.tag) != "PackageReference": continue
        package_id = item.attrib.get("Include") or item.attrib.get("Update") or ""
        if package_id.startswith("TCJ."):
            packages.add(package_id)
            version = item.attrib.get("Version") or next((child.text for child in item if strip_namespace(child.tag) == "Version"), None)
            if version != "$(TCJCompatibilityVersion)": version_ok = False
    return packages, version_ok


def check_gitignore(root: Path, critical: Iterable[Path]) -> None:
    ignore_path = root / ".gitignore"
    text = ignore_path.read_text(encoding="utf-8") if ignore_path.is_file() else ""
    normalized = [line.strip() for line in text.splitlines() if line.strip() and not line.lstrip().startswith("#")]
    dangerous = {"compatibility/", "compatibility/**", "eng/*.json", "eng/**", "eng/compatibility-policy.json"}
    if any(line in dangerous for line in normalized if not line.startswith("!")):
        fail(".gitignore contains a rule that hides compatibility policy or consumer source files.")
    git_dir = root / ".git"
    if git_dir.exists():
        for relative in critical:
            check = subprocess.run(["git", "check-ignore", "-q", str(relative)], cwd=root, check=False)
            if check.returncode == 0: fail(f"Required compatibility source is ignored by Git: {relative}")
            tracked = subprocess.run(["git", "ls-files", "--error-unmatch", str(relative)], cwd=root, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            if tracked.returncode != 0: fail(f"Required compatibility source is not tracked by Git: {relative}")


def require_text(path: Path, snippets: Iterable[str]) -> None:
    if not path.is_file(): fail(f"Required file is missing: {path}")
    text = path.read_text(encoding="utf-8")
    missing = [snippet for snippet in snippets if snippet not in text]
    if missing: fail(f"{path} is missing required compatibility integration: {missing}")


def validate_config(root: Path = ROOT) -> dict[str, Any]:
    policy = load_policy(root)
    compatibility_root = root / "compatibility"
    required_files = [
        Path("eng/compatibility-policy.json"), Path("eng/verify-consumer-compatibility.py"),
        Path("compatibility/NuGet.Config"), Path("compatibility/NuGet.Published.Config"),
        Path("compatibility/TCJ.Compatibility.slnx"), Path("compatibility/Directory.Build.props"),
        Path("compatibility/Directory.Packages.props"), Path("compatibility/scripts/run-compatibility.py"),
        Path("docs/package-consumer-compatibility.md"), Path(".github/workflows/consumer-compatibility.yml"),
    ]
    for relative in required_files:
        if not (root / relative).is_file(): fail(f"Required compatibility file is missing: {relative}")
    validate_nuget_config(root, published=False); validate_nuget_config(root, published=True)
    require_text(root / "compatibility/scripts/run-compatibility.py", [
        ".nupkg.metadata", "dotnetSdkVersion", "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "TreatWarningsAsErrors=true",
    ])
    solution_text = (compatibility_root / "TCJ.Compatibility.slnx").read_text(encoding="utf-8")
    if "src/" in solution_text.lower() or "src\\" in solution_text.lower(): fail("Compatibility solution must not include repository source projects.")
    direct_combinations: set[tuple[str, ...]] = set()
    critical = list(required_files)
    for consumer in policy["consumers"]:
        project_rel = Path(consumer["project"]); project = root / project_rel
        if not project.is_file(): fail(f"Missing consumer project: {project_rel}")
        if project_rel.as_posix().replace("compatibility/", "") not in solution_text: fail(f"Compatibility solution does not contain {project_rel}.")
        direct_packages, version_ok = parse_project(project)
        if not version_ok: fail(f"TCJ PackageReference versions must use $(TCJCompatibilityVersion): {project_rel}")
        declared = set(consumer["packages"])
        if not direct_packages.issubset(declared): fail(f"{consumer['name']} directly references an unexpected TCJ package.")
        if not declared.issubset(REQUIRED_PACKAGE_IDS): fail(f"{consumer['name']} declares unsupported TCJ packages.")
        direct_combinations.add(tuple(consumer["packages"]))
        critical.append(project_rel)
        program = project.parent / "Program.cs"
        if not program.is_file(): fail(f"Missing consumer program: {program.relative_to(root)}")
        critical.append(program.relative_to(root))
        if consumer["name"] == "Outbox.Console":
            require_text(program, [
                "AddTcjOutbox<OutboxConsumerDbContext>",
                "AddTcjOutboxEvent<ConsumerCreatedEvent>",
                "modelBuilder.AddTcjOutbox()",
                "IOutboxStorage",
                "IOutboxProcessor",
                "ProcessBatchAsync",
                "TCJ transactional outbox consumer passed",
            ])
    required_combinations = {
        ("TCJ.Core",),
        ("TCJ.Core", "TCJ.DependencyInjection"),
        ("TCJ.Core", "TCJ.DependencyInjection", "TCJ.EntityFrameworkCore"),
        ("TCJ.Core", "TCJ.DependencyInjection", "TCJ.EntityFrameworkCore", "TCJ.EntityFrameworkCore.SqlServer"),
        ("TCJ.Core", "TCJ.DependencyInjection", "TCJ.AspNetCore"),
        ("TCJ.Core", "TCJ.DependencyInjection", "TCJ.EntityFrameworkCore", "TCJ.EntityFrameworkCore.SqlServer", "TCJ.AspNetCore"),
    }
    if not required_combinations.issubset(direct_combinations): fail("Required package-combination matrix is incomplete.")
    props = (compatibility_root / "Directory.Build.props").read_text(encoding="utf-8")
    if "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>" not in props or "$(TCJCompatibilityTargetFramework)" not in props:
        fail("Compatibility build props must enforce warnings-as-errors and policy-driven target frameworks.")
    check_gitignore(root, critical)
    gitignore = (root / ".gitignore").read_text(encoding="utf-8")
    for required_ignore in ("artifacts/compatibility/", "compatibility/**/bin/", "compatibility/**/obj/"):
        if required_ignore not in gitignore: fail(f".gitignore is missing {required_ignore!r}.")
    require_text(root / ".github/workflows/consumer-compatibility.yml", [
        "name: Package consumer compatibility", "workflow_call:", "ubuntu-latest", "windows-latest", "macos-latest",
        "eng/verify-consumer-compatibility.py validate-config", "compatibility/scripts/run-compatibility.py",
        "Verify Linux, Windows, and macOS results", "schedule:", "src/**", "compatibility/**",
    ])
    require_text(root / ".github/workflows/ci.yml", ["eng/verify-consumer-compatibility.py validate-config"])
    require_text(root / ".github/workflows/release-preflight.yml", ["consumer-compatibility", "Run exact release-candidate consumers"])
    require_text(root / ".github/workflows/release.yml", ["consumer-compatibility", "Run exact tagged-package consumers"])
    require_text(root / ".github/workflows/published-package-smoke.yml", [
        "compatibility/scripts/run-compatibility.py", "Core.Console", "AspNetCore.MinimalApi", "FullStack.MinimalApi",
    ])
    require_text(root / ".github/PULL_REQUEST_TEMPLATE.md", [
        "All package consumers restore from the expected package source", "Linux, Windows, and macOS package consumers pass",
        "Source and symbol package compatibility validation passes",
    ])
    return policy


def safe_zip_entries(path: Path) -> dict[str, bytes]:
    try:
        with zipfile.ZipFile(path, "r") as archive:
            result: dict[str, bytes] = {}
            seen: set[str] = set()
            for info in archive.infolist():
                if info.is_dir(): continue
                name = str(PurePosixPath(info.filename))
                if name.startswith("/") or ".." in PurePosixPath(name).parts: fail(f"{path.name} contains unsafe archive entry {info.filename!r}.")
                folded = name.casefold()
                if folded in seen: fail(f"{path.name} contains duplicate package entry {name!r}.")
                seen.add(folded); result[name] = archive.read(info)
            return result
    except zipfile.BadZipFile as error: fail(f"Invalid NuGet package {path}: {error}")


def find_nuspec(entries: dict[str, bytes], path: Path) -> tuple[ET.Element, bytes]:
    names = [name for name in entries if name.casefold().endswith(".nuspec") and "/" not in name]
    if len(names) != 1: fail(f"{path.name} must contain exactly one root .nuspec file.")
    data = entries[names[0]]
    try: return ET.fromstring(data), data
    except ET.ParseError as error: fail(f"Invalid .nuspec in {path.name}: {error}")


def element_text(root: ET.Element, name: str) -> str:
    item = next((element for element in root.iter() if strip_namespace(element.tag) == name), None)
    return (item.text or "").strip() if item is not None else ""


def extract_json_object(data: bytes, marker: bytes = SOURCE_LINK_MARKER) -> dict[str, Any] | None:
    search_from = 0
    while True:
        marker_index = data.find(marker, search_from)
        if marker_index < 0: return None
        start = data.rfind(b"{", 0, marker_index + 1)
        while start >= 0:
            depth = 0; in_string = False; escaped = False
            for index in range(start, len(data)):
                byte = data[index]
                if in_string:
                    if escaped: escaped = False
                    elif byte == 0x5C: escaped = True
                    elif byte == 0x22: in_string = False
                    continue
                if byte == 0x22: in_string = True
                elif byte == 0x7B: depth += 1
                elif byte == 0x7D:
                    depth -= 1
                    if depth == 0:
                        try: value = json.loads(data[start:index + 1].decode("utf-8"))
                        except (UnicodeDecodeError, json.JSONDecodeError): break
                        if isinstance(value, dict) and isinstance(value.get("documents"), dict): return value
                        break
            start = data.rfind(b"{", 0, start)
        search_from = marker_index + len(marker)


def validate_no_machine_paths(entries: dict[str, bytes], path: Path) -> None:
    for name, data in entries.items():
        if any(part.casefold() in {"bin", "obj", "testresults"} for part in PurePosixPath(name).parts):
            fail(f"{path.name} contains unintended build output {name}.")
        lowered = name.casefold()
        if "/tests/" in f"/{lowered}" or "/samples/" in f"/{lowered}": fail(f"{path.name} contains test/sample content {name}.")
        if WINDOWS_ABSOLUTE_PATTERN.search(data) or any(marker in data for marker in UNIX_ABSOLUTE_MARKERS):
            fail(f"{path.name} contains an absolute machine path in {name}.")


def validate_primary_package(path: Path, package_id: str, version: str, policy: dict[str, Any], commit_sha: str | None) -> tuple[tuple[str, ...], tuple[str, ...], bool]:
    entries = safe_zip_entries(path); nuspec, _ = find_nuspec(entries, path)
    if element_text(nuspec, "id") != package_id or element_text(nuspec, "version") != version: fail(f"{path.name} identity does not match {package_id} {version}.")
    repository = next((item for item in nuspec.iter() if strip_namespace(item.tag) == "repository"), None)
    repository_ok = repository is not None and repository.attrib.get("type", "").casefold() == "git" and repository.attrib.get("url", "").rstrip("/").casefold() == "https://github.com/amir-esh/tcj.framework.git"
    if not repository_ok: fail(f"{path.name} has invalid repository metadata.")
    if commit_sha and repository is not None and repository.attrib.get("commit", "").casefold() != commit_sha.casefold():
        fail(f"{path.name} repository commit does not match {commit_sha}.")
    tfms = sorted({PurePosixPath(name).parts[1] for name in entries if len(PurePosixPath(name).parts) >= 3 and PurePosixPath(name).parts[0] == "lib" and name.casefold().endswith(".dll")})
    expected_tfms = sorted(policy["supportedTargetFrameworks"])
    if tfms != expected_tfms: fail(f"{path.name} target frameworks are {tfms}; expected {expected_tfms}.")
    expected_dll = {f"lib/{tfm}/{package_id}.dll" for tfm in expected_tfms}
    expected_xml = {f"lib/{tfm}/{package_id}.xml" for tfm in expected_tfms}
    if not expected_dll.issubset(entries): fail(f"{path.name} is missing expected library assemblies.")
    if not expected_xml.issubset(entries): fail(f"{path.name} is missing XML documentation.")
    tcj_dependencies: set[str] = set()
    for dependency in nuspec.iter():
        if strip_namespace(dependency.tag) == "dependency":
            dependency_id = dependency.attrib.get("id", "")
            if dependency_id.startswith("TCJ."): tcj_dependencies.add(dependency_id)
    expected_dependencies = set(policy["expectedTcjDependencies"][package_id])
    if tcj_dependencies != expected_dependencies: fail(f"{path.name} TCJ dependencies are {sorted(tcj_dependencies)}; expected {sorted(expected_dependencies)}.")
    for required in ("README.md", "LICENSE.txt"):
        if required not in entries: fail(f"{path.name} is missing {required}.")
    validate_no_machine_paths(entries, path)
    return tuple(tfms), tuple(sorted(tcj_dependencies)), repository_ok


def validate_symbol_package(path: Path, package_id: str, version: str, policy: dict[str, Any], commit_sha: str | None) -> tuple[bool, bool]:
    entries = safe_zip_entries(path); nuspec, _ = find_nuspec(entries, path)
    if element_text(nuspec, "id") != package_id or element_text(nuspec, "version") != version: fail(f"{path.name} symbol package identity mismatch.")
    portable_ok = True; source_link_ok = True
    expected_pdbs = [f"lib/{tfm}/{package_id}.pdb" for tfm in policy["supportedTargetFrameworks"]]
    for pdb_name in expected_pdbs:
        data = entries.get(pdb_name)
        if data is None: fail(f"{path.name} is missing portable PDB {pdb_name}.")
        if not data.startswith(b"BSJB"): fail(f"{pdb_name} in {path.name} is not a portable PDB.")
        source_link = extract_json_object(data)
        if source_link is None: fail(f"{pdb_name} in {path.name} has no Source Link metadata.")
        documents = source_link.get("documents")
        if not isinstance(documents, dict) or not documents: fail(f"{pdb_name} Source Link documents map is empty.")
        urls = [str(value) for value in documents.values()]
        if not all("github" in url.casefold() and "amir-esh/tcj.framework" in url.casefold() for url in urls):
            fail(f"{pdb_name} Source Link does not reference the TCJ repository.")
        if commit_sha and not all(commit_sha.casefold() in url.casefold() for url in urls):
            fail(f"{pdb_name} Source Link does not reference commit {commit_sha}.")
    validate_no_machine_paths(entries, path)
    return portable_ok, source_link_ok


def validate_packages(packages: Path, version: str, policy: dict[str, Any], commit_sha: str | None) -> list[PackageValidation]:
    if not SEMVER_PATTERN.fullmatch(version): fail(f"Invalid package version: {version}")
    if not packages.is_dir(): fail(f"Package directory does not exist: {packages}")
    expected_primary = {f"{package_id}.{version}.nupkg" for package_id in policy["requiredPackages"]}
    expected_symbols = {f"{package_id}.{version}.snupkg" for package_id in policy["requiredPackages"]}
    actual_primary = {path.name for path in packages.glob("TCJ.*.nupkg")}
    actual_symbols = {path.name for path in packages.glob("TCJ.*.snupkg")}
    if actual_primary != expected_primary: fail(f"Primary package set mismatch. Expected {sorted(expected_primary)}, found {sorted(actual_primary)}.")
    if actual_symbols != expected_symbols: fail(f"Symbol package set mismatch. Expected {sorted(expected_symbols)}, found {sorted(actual_symbols)}.")
    results: list[PackageValidation] = []
    for package_id in policy["requiredPackages"]:
        nupkg = packages / f"{package_id}.{version}.nupkg"; snupkg = packages / f"{package_id}.{version}.snupkg"
        tfms, deps, repository_ok = validate_primary_package(nupkg, package_id, version, policy, commit_sha)
        portable, source_link = validate_symbol_package(snupkg, package_id, version, policy, commit_sha)
        results.append(PackageValidation(package_id, version, tfms, deps, nupkg.name, snupkg.name, True, portable, source_link, repository_ok))
    return results


def find_platform_result(results: Path, platform: str) -> Path:
    candidates = [path for path in results.rglob("platform-result.json") if path.parent.name == platform]
    if not candidates:
        candidates = [path for path in results.rglob("platform-result.json") if read_json(path).get("platform") == platform]
    if len(candidates) != 1: fail(f"Expected exactly one result for {platform}; found {len(candidates)}.")
    return candidates[0]


def validate_platform_result(path: Path, policy: dict[str, Any], version: str, platform: str, source_mode: str, published: bool = False) -> dict[str, Any]:
    data = read_json(path)
    if data.get("schemaVersion") != 1: fail(f"Unsupported result schema in {path}.")
    if data.get("platform") != platform: fail(f"Result platform mismatch in {path}.")
    if data.get("packageVersion") != version: fail(f"Result package version mismatch in {path}.")
    if data.get("configuration") not in policy["requiredConfigurations"]: fail(f"Unsupported configuration in {path}.")
    if data.get("targetFramework") not in policy["supportedTargetFrameworks"]: fail(f"Unsupported target framework in {path}.")
    expected_architecture = policy["requiredArchitectureByOperatingSystem"][platform]
    if data.get("architecture") != expected_architecture:
        fail(f"Architecture mismatch in {path}: expected {expected_architecture!r} for {platform}, found {data.get('architecture')!r}.")
    if not isinstance(data.get("dotnetSdkVersion"), str) or not data["dotnetSdkVersion"].strip(): fail(f"Missing .NET SDK version in {path}.")
    if data.get("sourceMode") != source_mode: fail(f"Package source mode mismatch in {path}.")
    expected_names = set(policy["publishedConsumers"] if published else [item["name"] for item in policy["consumers"]])
    consumers = data.get("consumers")
    if not isinstance(consumers, list): fail(f"Result consumers are invalid in {path}.")
    actual_names = {item.get("name") for item in consumers if isinstance(item, dict)}
    if actual_names != expected_names: fail(f"Consumer result set mismatch in {path}: expected {sorted(expected_names)}, found {sorted(actual_names)}.")
    if not published and len(consumers) < int(policy["minimumConsumerCount"]): fail(f"Insufficient consumer count in {path}.")
    for item in consumers:
        for status in ("restoreStatus", "buildStatus", "runtimeStatus", "packageVersionStatus", "packageSourceStatus"):
            if item.get(status) != "pass": fail(f"{item.get('name')} {status} is not pass on {platform}.")
        if item.get("warningCount") != 0: fail(f"{item.get('name')} emitted warnings on {platform}.")
    if data.get("overall") != "pass" or int(data.get("warningCount", -1)) != 0: fail(f"Compatibility result is not clean on {platform}.")
    return data


def copy_logs(result_path: Path, output: Path) -> None:
    platform_root = result_path.parent
    for category in ("restore", "build", "runtime"):
        source = platform_root / category
        destination = output / category / platform_root.name
        if source.is_dir():
            if destination.exists(): shutil.rmtree(destination)
            shutil.copytree(source, destination)


def write_summary(output: Path, version: str, results: list[dict[str, Any]], package_validations: list[PackageValidation] | None, commit_sha: str | None, profile: str) -> None:
    output.mkdir(parents=True, exist_ok=True)
    consumer_projects = max(int(item["consumerCount"]) for item in results) if results else 0
    consumer_executions = sum(int(item["consumerCount"]) for item in results)
    restore = sum(int(item["restoreSuccessCount"]) for item in results)
    build = sum(int(item["buildSuccessCount"]) for item in results)
    runtime = sum(int(item["runtimeSuccessCount"]) for item in results)
    payload = {
        "schemaVersion": 1, "profile": profile, "sourceCommit": commit_sha or os.environ.get("GITHUB_SHA", "unknown"),
        "packageVersion": version, "platforms": [item["platform"] for item in results], "operatingSystems": [item["operatingSystem"] for item in results],
        "architectures": [item["architecture"] for item in results], "dotnetSdkVersions": sorted({item["dotnetSdkVersion"] for item in results}), "targetFrameworks": sorted({item["targetFramework"] for item in results}),
        "consumerProjectCount": consumer_projects, "consumerExecutionCount": consumer_executions, "restoreSuccessCount": restore, "buildSuccessCount": build, "runtimeSuccessCount": runtime,
        "packageSourceVerification": "pass", "packageVersionVerification": "pass", "dependencyConflictStatus": "pass",
        "nupkgValidation": "pass" if package_validations is not None else "not-applicable", "snupkgValidation": "pass" if package_validations is not None else "not-applicable",
        "sourceLinkStatus": "pass" if package_validations is not None else "not-applicable", "warningCount": sum(int(item["warningCount"]) for item in results), "overall": "pass",
    }
    (output / "compatibility-summary.json").write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    lines = [
        "# Package consumer compatibility", "", "| Field | Result |", "|---|---|",
        f"| Overall | PASS |", f"| Profile | `{profile}` |", f"| Source commit | `{payload['sourceCommit']}` |", f"| Package version | `{version}` |",
        f"| Platforms | {', '.join(payload['platforms'])} |", f"| Operating systems | {'; '.join(payload['operatingSystems'])} |",
        f"| Architectures | {', '.join(payload['architectures'])} |", f"| .NET SDK | {', '.join(payload['dotnetSdkVersions'])} |",
        f"| Target frameworks | {', '.join(payload['targetFrameworks'])} |", f"| Consumer projects | {consumer_projects} |",
        f"| Consumer executions | {consumer_executions} |", f"| Restores | {restore}/{consumer_executions} |", f"| Builds | {build}/{consumer_executions} |", f"| Runtime checks | {runtime}/{consumer_executions} |",
        f"| Package source verification | PASS |", f"| Package version verification | PASS |", f"| Dependency conflicts | PASS |",
        f"| `.nupkg` validation | {payload['nupkgValidation'].upper()} |", f"| `.snupkg` validation | {payload['snupkgValidation'].upper()} |",
        f"| Source Link | {payload['sourceLinkStatus'].upper()} |", f"| Warnings | {payload['warningCount']} |", "",
    ]
    if package_validations:
        lines += ["## Package artifacts", "", "| Package | TFM | TCJ dependencies | Symbols | Source Link |", "|---|---|---|---|---|"]
        for item in package_validations:
            lines.append(f"| `{item.package_id}` | {', '.join(item.target_frameworks)} | {', '.join(item.tcj_dependencies) or 'none'} | PASS | PASS |")
        lines.append("")
    (output / "COMPATIBILITY_SUMMARY.md").write_text("\n".join(lines), encoding="utf-8")


def verify_local(args: argparse.Namespace, policy: dict[str, Any]) -> None:
    platforms = [args.platform] if args.platform else policy["requiredOperatingSystems"]
    package_validations = validate_packages(args.packages.resolve(), args.version, policy, args.commit_sha)
    data: list[dict[str, Any]] = []
    for platform in platforms:
        result_path = find_platform_result(args.results.resolve(), platform)
        data.append(validate_platform_result(result_path, policy, args.version, platform, "local"))
        copy_logs(result_path, args.output.resolve())
    write_summary(args.output.resolve(), args.version, data, package_validations, args.commit_sha, "local")
    print(f"Package consumer compatibility verification passed for {len(data)} platform(s), {len(package_validations)} packages.")


def verify_platforms(args: argparse.Namespace, policy: dict[str, Any]) -> None:
    data = [validate_platform_result(find_platform_result(args.results.resolve(), platform), policy, args.version, platform, "local") for platform in policy["requiredOperatingSystems"]]
    write_summary(args.output.resolve(), args.version, data, None, args.commit_sha, "cross-platform")
    print("Package consumer compatibility cross-platform verification passed for Linux, Windows, and macOS.")


def verify_published(args: argparse.Namespace, policy: dict[str, Any]) -> None:
    result_path = find_platform_result(args.results.resolve(), args.platform)
    data = [validate_platform_result(result_path, policy, args.version, args.platform, "published", published=True)]
    copy_logs(result_path, args.output.resolve())
    write_summary(args.output.resolve(), args.version, data, None, args.commit_sha, "published")
    print(f"Published package consumer verification passed on {args.platform}.")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("validate-config")
    verify = subparsers.add_parser("verify")
    verify.add_argument("--version", required=True); verify.add_argument("--packages", type=Path, required=True); verify.add_argument("--results", type=Path, required=True); verify.add_argument("--output", type=Path, required=True); verify.add_argument("--platform"); verify.add_argument("--commit-sha")
    cross = subparsers.add_parser("verify-platforms")
    cross.add_argument("--version", required=True); cross.add_argument("--results", type=Path, required=True); cross.add_argument("--output", type=Path, required=True); cross.add_argument("--commit-sha")
    published = subparsers.add_parser("verify-published")
    published.add_argument("--version", required=True); published.add_argument("--platform", required=True); published.add_argument("--results", type=Path, required=True); published.add_argument("--output", type=Path, required=True); published.add_argument("--commit-sha")
    return parser


def main() -> int:
    parser = build_parser(); args = parser.parse_args(); policy = validate_config(ROOT)
    if args.command == "validate-config":
        print(f"Compatibility configuration is valid: packages={len(policy['requiredPackages'])}, consumers={len(policy['consumers'])}, platforms={'+'.join(policy['requiredOperatingSystems'])}, frameworks={'+'.join(policy['supportedTargetFrameworks'])}.")
    elif args.command == "verify": verify_local(args, policy)
    elif args.command == "verify-platforms": verify_platforms(args, policy)
    elif args.command == "verify-published": verify_published(args, policy)
    return 0

if __name__ == "__main__":
    try: raise SystemExit(main())
    except (VerificationError, OSError, KeyError, TypeError, ValueError, json.JSONDecodeError) as error:
        print(f"Package consumer compatibility verification failed: {error}", file=sys.stderr); raise SystemExit(1)
