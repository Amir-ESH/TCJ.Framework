#!/usr/bin/env python3
"""Validate TCJ module architecture policy and repository integration."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_POLICY = ROOT / "eng/architecture-policy.json"
DEFAULT_SUMMARY = ROOT / "artifacts/architecture/ARCHITECTURE_TEST_SUMMARY.md"
REQUIRED_ASSEMBLIES = (
    "TCJ.Core",
    "TCJ.DependencyInjection",
    "TCJ.EntityFrameworkCore",
    "TCJ.EntityFrameworkCore.SqlServer",
    "TCJ.AspNetCore",
)
REQUIRED_WORKFLOWS = (
    ".github/workflows/ci.yml",
    ".github/workflows/release-preflight.yml",
    ".github/workflows/release.yml",
)
REQUIRED_TEST_FILES = (
    "tests/TCJ.Architecture.Tests/AssemblyDependencyArchitectureTests.cs",
    "tests/TCJ.Architecture.Tests/NamespaceArchitectureTests.cs",
    "tests/TCJ.Architecture.Tests/PublicApiArchitectureTests.cs",
    "tests/TCJ.Architecture.Tests/NamingAndVisibilityArchitectureTests.cs",
)


class ArchitecturePolicyError(RuntimeError):
    """Raised when architecture policy configuration is invalid."""


@dataclass(frozen=True)
class ArchitecturePolicy:
    documentation: str
    assemblies: dict[str, tuple[str, ...]]
    project_paths: dict[str, str]
    namespace_roots: dict[str, str]
    forbidden_dependency_prefixes: dict[str, tuple[str, ...]]
    forbidden_public_api_type_prefixes: dict[str, tuple[str, ...]]
    approved_public_option_types: tuple[str, ...]


def fail(message: str) -> None:
    raise ArchitecturePolicyError(message)


def relative(path: Path, root: Path = ROOT) -> str:
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def read_json(path: Path, description: str) -> Any:
    if not path.is_file():
        fail(f"Missing {description}: {relative(path)}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        fail(f"Invalid JSON in {relative(path)}: {error}")


def require_object(value: Any, description: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        fail(f"{description} must be a JSON object.")
    return value


def require_relative_path(value: Any, description: str) -> str:
    if not isinstance(value, str) or not value.strip():
        fail(f"{description} must be a non-empty repository-relative path.")
    normalized = value.strip().replace("\\", "/")
    path = PurePosixPath(normalized)
    if path.is_absolute() or ".." in path.parts:
        fail(f"{description} must stay inside the repository: {normalized}")
    return path.as_posix()


def require_string_map(value: Any, description: str) -> dict[str, str]:
    raw = require_object(value, description)
    result: dict[str, str] = {}
    for key, item in raw.items():
        if not isinstance(key, str) or not key.strip():
            fail(f"{description} contains an invalid key.")
        if not isinstance(item, str) or not item.strip():
            fail(f"{description}.{key} must be a non-empty string.")
        result[key.strip()] = item.strip()
    return result


def require_string_list_map(
    value: Any,
    description: str,
    *,
    allow_empty_lists: bool,
) -> dict[str, tuple[str, ...]]:
    raw = require_object(value, description)
    result: dict[str, tuple[str, ...]] = {}
    for key, items in raw.items():
        if not isinstance(key, str) or not key.strip():
            fail(f"{description} contains an invalid key.")
        if not isinstance(items, list) or (not allow_empty_lists and not items):
            qualifier = "possibly empty" if allow_empty_lists else "non-empty"
            fail(f"{description}.{key} must be a {qualifier} array.")
        if any(not isinstance(item, str) or not item.strip() for item in items):
            fail(f"{description}.{key} must contain non-empty strings.")
        normalized = tuple(item.strip() for item in items)
        if len(normalized) != len(set(normalized)):
            fail(f"{description}.{key} must not contain duplicates.")
        result[key.strip()] = normalized
    return result


def require_exact_keys(mapping: dict[str, Any], description: str) -> None:
    expected = set(REQUIRED_ASSEMBLIES)
    actual = set(mapping)
    if actual != expected:
        missing = sorted(expected - actual)
        unknown = sorted(actual - expected)
        details: list[str] = []
        if missing:
            details.append("missing: " + ", ".join(missing))
        if unknown:
            details.append("unknown: " + ", ".join(unknown))
        fail(f"{description} must represent all five production assemblies ({'; '.join(details)}).")


def find_cycles(graph: dict[str, tuple[str, ...]]) -> tuple[str, ...]:
    visiting: set[str] = set()
    visited: set[str] = set()
    stack: list[str] = []
    cycles: set[str] = set()

    def visit(node: str) -> None:
        if node in visited:
            return
        if node in visiting:
            start = stack.index(node)
            cycles.add(" -> ".join(stack[start:] + [node]))
            return

        visiting.add(node)
        stack.append(node)
        for dependency in sorted(graph[node]):
            visit(dependency)
        stack.pop()
        visiting.remove(node)
        visited.add(node)

    for assembly in sorted(graph):
        visit(assembly)
    return tuple(sorted(cycles))


def load_policy(path: Path = DEFAULT_POLICY) -> ArchitecturePolicy:
    raw = require_object(read_json(path, "architecture policy"), "Architecture policy")
    if raw.get("schemaVersion") != 1:
        fail("Architecture policy schemaVersion must be 1.")

    documentation = require_relative_path(raw.get("documentation"), "documentation")
    assemblies = require_string_list_map(
        raw.get("assemblies"), "assemblies", allow_empty_lists=True
    )
    project_paths = require_string_map(raw.get("projectPaths"), "projectPaths")
    namespace_roots = require_string_map(raw.get("namespaceRoots"), "namespaceRoots")
    forbidden_dependencies = require_string_list_map(
        raw.get("forbiddenDependencyPrefixes"),
        "forbiddenDependencyPrefixes",
        allow_empty_lists=False,
    )
    forbidden_api = require_string_list_map(
        raw.get("forbiddenPublicApiTypePrefixes"),
        "forbiddenPublicApiTypePrefixes",
        allow_empty_lists=False,
    )

    approved_options = raw.get("approvedPublicOptionTypes")
    if not isinstance(approved_options, list) or not approved_options:
        fail("approvedPublicOptionTypes must be a non-empty array.")
    if any(not isinstance(item, str) or not item.strip() for item in approved_options):
        fail("approvedPublicOptionTypes must contain non-empty strings.")
    normalized_options = tuple(item.strip() for item in approved_options)
    if len(normalized_options) != len(set(normalized_options)):
        fail("approvedPublicOptionTypes must not contain duplicates.")

    for mapping, description in (
        (assemblies, "assemblies"),
        (project_paths, "projectPaths"),
        (namespace_roots, "namespaceRoots"),
        (forbidden_dependencies, "forbiddenDependencyPrefixes"),
        (forbidden_api, "forbiddenPublicApiTypePrefixes"),
    ):
        require_exact_keys(mapping, description)

    known = set(REQUIRED_ASSEMBLIES)
    for assembly, dependencies in assemblies.items():
        if assembly in dependencies:
            fail(f"Assembly '{assembly}' must not depend on itself.")
        unknown = sorted(set(dependencies) - known)
        if unknown:
            fail(
                f"Assembly '{assembly}' contains unknown allowed dependencies: "
                + ", ".join(unknown)
            )

    cycles = find_cycles(assemblies)
    if cycles:
        fail("Architecture policy dependency graph contains a cycle: " + "; ".join(cycles))

    for assembly, path_value in project_paths.items():
        project_paths[assembly] = require_relative_path(
            path_value, f"projectPaths.{assembly}"
        )

    for assembly, root_namespace in namespace_roots.items():
        if root_namespace != assembly:
            fail(
                f"namespaceRoots.{assembly} must be '{assembly}', found '{root_namespace}'."
            )

    for option_type in normalized_options:
        if not any(
            option_type == root or option_type.startswith(root + ".")
            for root in namespace_roots.values()
        ):
            fail(
                f"approvedPublicOptionTypes contains '{option_type}', which is outside known TCJ namespaces."
            )

    return ArchitecturePolicy(
        documentation=documentation,
        assemblies=assemblies,
        project_paths=project_paths,
        namespace_roots=namespace_roots,
        forbidden_dependency_prefixes=forbidden_dependencies,
        forbidden_public_api_type_prefixes=forbidden_api,
        approved_public_option_types=normalized_options,
    )


def ensure_policy_not_ignored(root: Path, policy_path: Path) -> None:
    if not (root / ".git").exists():
        return
    try:
        relative_policy = policy_path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        fail("Architecture policy must be inside the repository root.")

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
        fail(
            "Unable to verify whether the architecture policy is ignored by Git: "
            + process.stderr.strip()
        )


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
            f"{relative(path, root)} is missing required integration fragments: "
            + ", ".join(missing)
        )
    return content


def validate_configuration(
    root: Path = ROOT,
    policy_path: Path | None = None,
    *,
    check_git: bool = True,
) -> ArchitecturePolicy:
    policy_path = policy_path or root / "eng/architecture-policy.json"
    policy = load_policy(policy_path)
    if check_git:
        ensure_policy_not_ignored(root, policy_path)

    manifest = require_object(
        read_json(root / "eng/release-manifest.json", "release manifest"),
        "Release manifest",
    )
    packages = manifest.get("packages")
    if not isinstance(packages, list) or any(not isinstance(item, str) for item in packages):
        fail("eng/release-manifest.json packages must be an array of strings.")
    if set(packages) != set(REQUIRED_ASSEMBLIES):
        fail(
            "Architecture policy assembly names must match release-manifest package IDs. "
            f"Expected: {', '.join(REQUIRED_ASSEMBLIES)}."
        )

    documentation_path = root / policy.documentation
    require_text(
        documentation_path,
        ("Approved dependency graph", "Architecture", "eng/architecture-policy.json"),
        root,
    )

    for assembly, relative_project in policy.project_paths.items():
        project = parse_xml(root / relative_project, root)
        package_id = (project.findtext("./PropertyGroup/PackageId") or "").strip()
        if package_id != assembly:
            fail(
                f"{relative_project} PackageId must be '{assembly}', found '{package_id or '<missing>'}'."
            )

    test_project_path = root / "tests/TCJ.Architecture.Tests/TCJ.Architecture.Tests.csproj"
    test_project = parse_xml(test_project_path, root)
    target_framework = (test_project.findtext("./PropertyGroup/TargetFramework") or "").strip()
    if target_framework != "net10.0":
        fail("The architecture-test project must explicitly target net10.0.")

    references = {
        Path(item.attrib.get("Include", "").replace("\\", "/")).stem
        for item in test_project.findall(".//ProjectReference")
    }
    if references != set(REQUIRED_ASSEMBLIES):
        missing = sorted(set(REQUIRED_ASSEMBLIES) - references)
        extra = sorted(references - set(REQUIRED_ASSEMBLIES))
        details = []
        if missing:
            details.append("missing: " + ", ".join(missing))
        if extra:
            details.append("unexpected: " + ", ".join(extra))
        fail(
            "Architecture-test project must reference all and only production projects ("
            + "; ".join(details)
            + ")."
        )

    package_references = {
        item.attrib.get("Include", "")
        for item in test_project.findall(".//PackageReference")
    }
    if package_references:
        fail(
            "Architecture-test project should use the repository test stack and BCL inspection APIs; "
            "unexpected direct packages: " + ", ".join(sorted(package_references))
        )

    solution = require_text(
        root / "TCJ.slnx",
        ("/tests/", "tests/TCJ.Architecture.Tests/TCJ.Architecture.Tests.csproj"),
        root,
    )
    if solution.count("tests/TCJ.Architecture.Tests/TCJ.Architecture.Tests.csproj") != 1:
        fail("TCJ.slnx must contain the architecture-test project exactly once.")

    for test_file in REQUIRED_TEST_FILES:
        require_text(
            root / test_file,
            ('[Trait("Category", "Architecture")]', "ArchitectureFailure.Format"),
            root,
        )

    for workflow_path in REQUIRED_WORKFLOWS:
        require_text(
            root / workflow_path,
            (
                "python3 eng/verify-architecture-policy.py validate-config",
                "dotnet test TCJ.slnx",
            ),
            root,
        )

    require_text(
        root / ".github/PULL_REQUEST_TEMPLATE.md",
        ("Architecture", "architecture-policy"),
        root,
    )
    require_text(root / "tests/README.md", ("TCJ.Architecture.Tests", "Category=Architecture"), root)
    require_text(root / "docs/README.md", ("architecture-tests.md",), root)

    return policy


def build_summary(policy: ArchitecturePolicy) -> str:
    lines = [
        "# TCJ architecture-test summary",
        "",
        "**Result:** architecture policy validation passed.",
        "",
        f"Policy: `{architecture_policy_path()}`",
        f"Documentation: `{policy.documentation}`",
        "",
        "## Approved dependency graph",
        "",
        "| Assembly | Allowed TCJ dependencies | Namespace root |",
        "|---|---|---|",
    ]
    for assembly in REQUIRED_ASSEMBLIES:
        dependencies = policy.assemblies[assembly]
        lines.append(
            f"| `{assembly}` | "
            + (", ".join(f"`{item}`" for item in dependencies) if dependencies else "None")
            + f" | `{policy.namespace_roots[assembly]}` |"
        )

    lines.extend(
        [
            "",
            "## Enforced rule families",
            "",
            "- Project and compiled-assembly dependency directions",
            "- Circular dependency detection",
            "- Forbidden infrastructure references",
            "- Namespace ownership and internal visibility",
            "- Public API infrastructure leak detection",
            "- Stable extension, options, and repository naming rules",
            "",
            "Intentional changes require policy, diagram, documentation, and pull-request justification updates.",
            "",
        ]
    )
    return "\n".join(lines)


def architecture_policy_path() -> str:
    return "eng/architecture-policy.json"


def write_summary(policy: ArchitecturePolicy, output: Path) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(build_summary(policy), encoding="utf-8")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    validate = subparsers.add_parser("validate-config", help="Validate architecture policy and integration.")
    validate.add_argument("--policy", type=Path, default=DEFAULT_POLICY)

    summary = subparsers.add_parser("write-summary", help="Validate configuration and write Markdown summary.")
    summary.add_argument("--policy", type=Path, default=DEFAULT_POLICY)
    summary.add_argument("--output", type=Path, default=DEFAULT_SUMMARY)

    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        policy = validate_configuration(ROOT, args.policy)
        if args.command == "write-summary":
            write_summary(policy, args.output)
            print(f"Architecture summary written to {relative(args.output)}.")
        else:
            print("Architecture policy configuration is valid.")
        return 0
    except ArchitecturePolicyError as error:
        print(f"Architecture policy validation failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
