#!/usr/bin/env python3
"""Validate repository-level NuGet and dependency-review security policy."""

from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
REQUIRED_AUDIT_ERRORS = {"NU1900", "NU1902", "NU1903", "NU1904", "NU1905"}


def fail(message: str) -> None:
    raise RuntimeError(message)


def parse_xml(path: Path) -> ET.Element:
    if not path.is_file():
        fail(f"Required file is missing: {path.relative_to(ROOT)}")
    try:
        return ET.parse(path).getroot()
    except ET.ParseError as error:
        fail(f"Invalid XML in {path.relative_to(ROOT)}: {error}")


def text_of(root: ET.Element, path: str) -> str:
    value = root.findtext(path)
    return value.strip() if value else ""


def verify_msbuild_policy() -> None:
    directory_build = parse_xml(ROOT / "Directory.Build.props")
    imports = [
        element.attrib.get("Project", "").replace("\\", "/")
        for element in directory_build.findall("Import")
    ]
    if not any(value.endswith("eng/DependencySecurity.props") for value in imports):
        fail("Directory.Build.props must import eng/DependencySecurity.props.")

    policy = parse_xml(ROOT / "eng" / "DependencySecurity.props")
    expected = {
        "NuGetAudit": "true",
        "NuGetAuditMode": "all",
        "NuGetAuditLevel": "moderate",
    }
    for property_name, expected_value in expected.items():
        actual = text_of(policy, f"./PropertyGroup/{property_name}").lower()
        if actual != expected_value:
            fail(
                f"{property_name} must be '{expected_value}' in "
                "eng/DependencySecurity.props."
            )

    warnings = {
        item.strip().upper()
        for item in text_of(policy, "./PropertyGroup/WarningsAsErrors").split(";")
        if item.strip() and not item.strip().startswith("$(")
    }
    missing = sorted(REQUIRED_AUDIT_ERRORS.difference(warnings))
    if missing:
        fail(
            "Dependency audit warnings must be errors; missing: "
            + ", ".join(missing)
        )


def verify_nuget_config() -> None:
    config = parse_xml(ROOT / "NuGet.Config")

    package_sources = config.find("packageSources")
    if package_sources is None or package_sources.find("clear") is None:
        fail("NuGet.Config packageSources must begin with <clear />.")
    package_entries = package_sources.findall("add")
    if len(package_entries) != 1:
        fail("NuGet.Config must define exactly one package source.")
    package_source = package_entries[0]
    if package_source.attrib.get("key") != "nuget.org":
        fail("The only package source must be named 'nuget.org'.")
    if package_source.attrib.get("value") != "https://api.nuget.org/v3/index.json":
        fail("The nuget.org package source URL is incorrect.")

    audit_sources = config.find("auditSources")
    if audit_sources is None or audit_sources.find("clear") is None:
        fail("NuGet.Config auditSources must begin with <clear />.")
    audit_entries = audit_sources.findall("add")
    if len(audit_entries) != 1:
        fail("NuGet.Config must define exactly one audit source.")
    audit_source = audit_entries[0]
    if audit_source.attrib.get("key") != "nuget.org":
        fail("The only audit source must be named 'nuget.org'.")
    if audit_source.attrib.get("value") != "https://api.nuget.org/v3/index.json":
        fail("The nuget.org audit source URL is incorrect.")

    mapping = config.find("packageSourceMapping/packageSource")
    if mapping is None or mapping.attrib.get("key") != "nuget.org":
        fail("NuGet.Config must map packages to the nuget.org source.")
    patterns = {item.attrib.get("pattern") for item in mapping.findall("package")}
    if "*" not in patterns:
        fail("NuGet.Config must map all package IDs with pattern '*'.")


def require_workflow_fragments(path: Path, fragments: tuple[str, ...]) -> None:
    if not path.is_file():
        fail(f"Required workflow is missing: {path.relative_to(ROOT)}")
    content = path.read_text(encoding="utf-8")
    missing = [fragment for fragment in fragments if fragment not in content]
    if missing:
        fail(
            f"{path.relative_to(ROOT)} is missing required policy fragments: "
            + ", ".join(missing)
        )


def verify_workflows() -> None:
    require_workflow_fragments(
        ROOT / ".github" / "workflows" / "dependency-review.yml",
        (
            "pull_request:",
            "- main",
            "- develop",
            "actions/dependency-review-action@v4",
            "fail-on-severity: moderate",
            "fail-on-scopes: runtime, development",
        ),
    )
    require_workflow_fragments(
        ROOT / ".github" / "workflows" / "dependency-audit.yml",
        (
            "schedule:",
            "workflow_dispatch:",
            "python3 eng/verify-dependency-security.py",
            "dotnet restore TCJ.slnx --force-evaluate",
            "eng/published-release.json",
            "TCJ.PublishedPackages.SmokeTest.csproj",
            "-p:TCJPackageVersion=${{ steps.published-version.outputs.value }}",
        ),
    )
    for workflow_name in ("ci.yml", "release-preflight.yml", "release.yml"):
        require_workflow_fragments(
            ROOT / ".github" / "workflows" / workflow_name,
            (
                "python3 eng/verify-dependency-security.py",
                "dotnet restore TCJ.slnx",
            ),
        )
    require_workflow_fragments(
        ROOT / ".github" / "workflows" / "published-package-smoke.yml",
        (
            "python3 eng/verify-dependency-security.py",
            "TCJ.PublishedPackages.SmokeTest.csproj",
            "dotnet restore",
        ),
    )


def verify_no_local_policy_bypass() -> None:
    policy_path = (ROOT / "eng" / "DependencySecurity.props").resolve()
    candidates = sorted(
        path
        for pattern in ("*.csproj", "*.props", "*.targets")
        for path in ROOT.rglob(pattern)
        if path.resolve() != policy_path
    )
    forbidden_elements = {
        "NuGetAudit",
        "NuGetAuditMode",
        "NuGetAuditLevel",
        "RestoreSources",
    }
    for path in candidates:
        root = parse_xml(path)
        for element_name in forbidden_elements:
            if root.find(f".//{element_name}") is not None:
                fail(
                    f"{path.relative_to(ROOT)} overrides central dependency policy "
                    f"with <{element_name}>."
                )


def main() -> int:
    verify_msbuild_policy()
    verify_nuget_config()
    verify_workflows()
    verify_no_local_policy_bypass()
    print("Dependency security policy verification succeeded.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError) as error:
        print(f"Dependency security policy verification failed: {error}", file=sys.stderr)
        raise SystemExit(1)
