#!/usr/bin/env python3
"""Validate and verify TCJ API documentation policy and generated outputs."""
from __future__ import annotations

import argparse
import datetime as dt
import fnmatch
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from dataclasses import asdict, dataclass
from typing import Iterable, Sequence

ROOT = Path(__file__).resolve().parents[1]
POLICY_PATH = ROOT / "eng" / "documentation-policy.json"
BASELINE_PATH = ROOT / "eng" / "documentation-baseline.json"
TOOL_MANIFEST_PATH = ROOT / ".config" / "dotnet-tools.json"
DOCFX_CONFIG_PATH = ROOT / "docfx" / "docfx.json"

TYPE_RE = re.compile(
    r"^(?P<indent>\s*)(?P<visibility>public|protected(?:\s+internal)?|private\s+protected)\s+"
    r"(?P<mods>(?:(?:new|static|abstract|sealed|partial|readonly|ref|unsafe)\s+)*)"
    r"(?P<kind>class|struct|interface|record(?:\s+(?:class|struct))?|enum|delegate)\s+"
    r"(?P<name>@?[A-Za-z_][A-Za-z0-9_]*)"
)
NAMESPACE_RE = re.compile(r"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*[;{]")
DOC_LINE_RE = re.compile(r"^\s*///\s?(.*)$")
LINK_RE = re.compile(r"(?<!!)\[[^\]]+\]\(([^)]+)\)")
FENCE_RE = re.compile(r"```csharp\s+validate(?:\s+id=([A-Za-z0-9_.-]+))?\s*\n(.*?)```", re.S | re.I)


class DocumentationError(RuntimeError):
    pass


@dataclass(frozen=True)
class ApiItem:
    package: str
    documentation_id: str
    kind: str
    file: str
    line: int
    name: str
    visibility: str
    parameter_names: tuple[str, ...]
    type_parameter_names: tuple[str, ...]
    requires_returns: bool
    inherited: bool
    has_summary: bool
    documented_parameters: tuple[str, ...]
    documented_type_parameters: tuple[str, ...]
    has_returns: bool

    def missing_elements(self, policy: dict) -> tuple[str, ...]:
        if self.inherited:
            return ()
        missing: list[str] = []
        if policy["requireTypeSummaries"] and self.kind == "Type" and not self.has_summary:
            missing.append("summary")
        if policy["requireMemberSummaries"] and self.kind != "Type" and not self.has_summary:
            missing.append("summary")
        if policy["requireParameterDocumentation"]:
            for name in self.parameter_names:
                if name not in self.documented_parameters:
                    missing.append(f"param:{name}")
        for name in self.type_parameter_names:
            if name not in self.documented_type_parameters:
                missing.append(f"typeparam:{name}")
        if policy["requireReturnDocumentation"] and self.requires_returns and not self.has_returns:
            missing.append("returns")
        return tuple(missing)


@dataclass
class TypeScope:
    name: str
    full_name: str
    kind: str
    visibility: str
    start_line: int
    body_depth: int | None
    end_line: int
    type_parameters: tuple[str, ...]


def fail(message: str) -> None:
    raise DocumentationError(message)


def load_json(path: Path) -> dict:
    if not path.is_file():
        fail(f"Required JSON file does not exist: {path.relative_to(ROOT)}")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError) as exc:
        fail(f"Invalid JSON in {path.relative_to(ROOT)}: {exc}")
    if not isinstance(value, dict):
        fail(f"JSON root must be an object: {path.relative_to(ROOT)}")
    return value


def run(command: Sequence[str], *, cwd: Path = ROOT, capture: bool = False) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(
            list(command), cwd=cwd, text=True, check=True,
            stdout=subprocess.PIPE if capture else None,
            stderr=subprocess.STDOUT if capture else None,
        )
    except FileNotFoundError as exc:
        fail(f"Required command is unavailable: {command[0]}")
    except subprocess.CalledProcessError as exc:
        output = exc.stdout or ""
        fail(f"Command failed ({' '.join(command)}):\n{output}")
    raise AssertionError("unreachable")


def git_tracked(path: Path) -> bool:
    rel = path.relative_to(ROOT).as_posix()
    proc = subprocess.run(["git", "ls-files", "--error-unmatch", rel], cwd=ROOT,
                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if proc.returncode == 0:
        return True
    # ZIP deliveries do not contain .git. Treat an existing required source file as tracked,
    # while GitHub CI performs the authoritative check in a real checkout.
    return not (ROOT / ".git").exists() and path.is_file()


def git_ignored(path: Path) -> bool:
    if not (ROOT / ".git").exists():
        return False
    rel = path.relative_to(ROOT).as_posix()
    proc = subprocess.run(["git", "check-ignore", "-q", "--", rel], cwd=ROOT)
    return proc.returncode == 0


def validate_policy(policy: dict) -> None:
    required_keys = {
        "schemaVersion", "docfxVersion", "requiredPackages", "projects", "packagePages",
        "minimumPublicApiDocumentationPercent", "requireTypeSummaries",
        "requireMemberSummaries", "requireParameterDocumentation",
        "requireReturnDocumentation", "requireExceptionDocumentation",
        "requireExampleForSelectedApis", "failOnBrokenInternalLinks",
        "failOnUnresolvedCrefs", "failOnMalformedXmlDocumentation",
        "selectedExamples", "baselineMaximumEntries", "baselineTargetMilestone",
        "baselineRecordedDate", "measuredPublicApiCount", "measuredFullyDocumentedApiCount",
    }
    missing = sorted(required_keys - policy.keys())
    if missing:
        fail(f"Documentation policy is missing keys: {', '.join(missing)}")
    if policy["schemaVersion"] != 1:
        fail("documentation-policy.json schemaVersion must be 1.")
    packages = policy["requiredPackages"]
    if not isinstance(packages, list) or len(packages) != 5 or len(set(packages)) != 5:
        fail("requiredPackages must contain the five unique TCJ package IDs.")
    for package in packages:
        project = policy["projects"].get(package)
        page = policy["packagePages"].get(package)
        if not project or not (ROOT / project).is_file():
            fail(f"Production project for {package} is missing: {project}")
        if not page or not (ROOT / page).is_file():
            fail(f"Package landing page for {package} is missing: {page}")
    percentage = policy["minimumPublicApiDocumentationPercent"]
    if not isinstance(percentage, (int, float)) or percentage < 0 or percentage > 100:
        fail("minimumPublicApiDocumentationPercent must be between 0 and 100.")
    if not isinstance(policy["baselineMaximumEntries"], int) or policy["baselineMaximumEntries"] < 0:
        fail("baselineMaximumEntries must be a non-negative integer.")
    if not isinstance(policy["selectedExamples"], list) or not policy["selectedExamples"]:
        fail("selectedExamples must list the important consumer-facing examples.")
    measured_total = policy["measuredPublicApiCount"]
    measured_documented = policy["measuredFullyDocumentedApiCount"]
    if not isinstance(measured_total, int) or measured_total <= 0:
        fail("measuredPublicApiCount must be a positive integer measured from the repository.")
    if not isinstance(measured_documented, int) or measured_documented < 0 or measured_documented > measured_total:
        fail("measuredFullyDocumentedApiCount must be between zero and measuredPublicApiCount.")
    measured_percent = round(measured_documented / measured_total * 100.0, 2)
    if abs(float(percentage) - measured_percent) > 0.005:
        fail(
            "minimumPublicApiDocumentationPercent must match the recorded repository baseline "
            f"({measured_documented}/{measured_total} = {measured_percent:.2f}%)."
        )
    try:
        dt.date.fromisoformat(policy["baselineRecordedDate"])
    except (TypeError, ValueError):
        fail("baselineRecordedDate must be an ISO-8601 date.")


def validate_tool_manifest(policy: dict) -> None:
    manifest = load_json(TOOL_MANIFEST_PATH)
    tool = manifest.get("tools", {}).get("docfx")
    if not isinstance(tool, dict):
        fail("DocFX is not pinned in .config/dotnet-tools.json.")
    if tool.get("version") != policy["docfxVersion"]:
        fail("Pinned DocFX version does not match documentation-policy.json.")
    commands = tool.get("commands")
    if commands != ["docfx"]:
        fail("The DocFX tool manifest must expose exactly the 'docfx' command.")
    if tool.get("rollForward") is not False:
        fail("DocFX tool rollForward must be false for reproducible documentation builds.")


def validate_docfx_config(policy: dict) -> None:
    config = load_json(DOCFX_CONFIG_PATH)
    metadata = config.get("metadata")
    if not isinstance(metadata, list) or not metadata:
        fail("docfx/docfx.json must define metadata generation.")
    configured: set[str] = set()
    for group in metadata:
        for source in group.get("src", []):
            for pattern in source.get("files", []):
                configured.add(str(Path(source.get("src", ".")) / pattern).replace("\\", "/"))
    for project in policy["projects"].values():
        normalized = "../" + project
        if normalized not in configured:
            fail(f"DocFX metadata does not include production project: {project}")
    build = config.get("build")
    if not isinstance(build, dict):
        fail("docfx/docfx.json must define a build section.")
    if build.get("output") != "../artifacts/documentation/site":
        fail("DocFX site output must be ../artifacts/documentation/site.")
    content_text = json.dumps(build.get("content", []))
    if "../docs" not in content_text or "api" not in content_text:
        fail("DocFX build content must include conceptual docs and generated API metadata.")
    metadata_text = json.dumps(build.get("globalMetadata", {}))
    for required in ("_appTitle", "_gitContribute", "_gitUrlPattern"):
        if required not in metadata_text:
            fail(f"DocFX global metadata is missing {required}.")


def validate_central_xml_docs() -> None:
    try:
        root = ET.parse(ROOT / "Directory.Build.props").getroot()
    except ET.ParseError as exc:
        fail(f"Directory.Build.props is malformed XML: {exc}")
    values = [node.text.strip().lower() for node in root.iter("GenerateDocumentationFile") if node.text]
    if "true" not in values:
        fail("GenerateDocumentationFile must be enabled centrally in Directory.Build.props.")


def validate_git_tracking(policy: dict) -> None:
    required_sources = {
        POLICY_PATH,
        DOCFX_CONFIG_PATH,
        TOOL_MANIFEST_PATH,
        ROOT / "docs" / "index.md",
        ROOT / "docs" / "toc.yml",
        ROOT / "docs" / "packages" / "index.md",
    }
    required_sources.update(ROOT / page for page in policy["packagePages"].values())
    required_sources.update(ROOT / item["path"] for item in policy["selectedExamples"])
    releases_dir = ROOT / "docs" / "releases"
    if releases_dir.is_dir():
        required_sources.update(releases_dir.glob("*.md"))

    for path in sorted(required_sources):
        if not path.is_file():
            fail(f"Required documentation source does not exist: {path.relative_to(ROOT)}")
        if git_ignored(path):
            fail(f"Required documentation source is ignored by Git: {path.relative_to(ROOT)}")
        if not git_tracked(path):
            fail(f"Required documentation source is not tracked by Git: {path.relative_to(ROOT)}")
    if BASELINE_PATH.exists():
        if git_ignored(BASELINE_PATH) or not git_tracked(BASELINE_PATH):
            fail("eng/documentation-baseline.json must remain tracked and not ignored.")


def validate_workflow_integration() -> None:
    checks = {
        ".github/workflows/documentation.yml": [
            "name: Documentation", "name: Build and validate documentation",
            "dotnet tool restore", "verify-documentation.py validate-config",
            "verify-documentation.py verify", "actions/upload-artifact@v7",
            "actions/upload-pages-artifact@v4", "actions/deploy-pages@v4",
            "github.ref == 'refs/heads/main'", "ENABLE_DOCUMENTATION_PAGES",
        ],
        ".github/workflows/ci.yml": ["verify-documentation.py validate-config", "verify-documentation.py verify"],
        ".github/workflows/release-preflight.yml": ["verify-documentation.py verify", "documentation-site"],
        ".github/workflows/release.yml": ["verify-documentation.py verify", "release-documentation"],
    }
    for rel, required_fragments in checks.items():
        path = ROOT / rel
        if not path.is_file():
            fail(f"Required workflow is missing: {rel}")
        text = path.read_text(encoding="utf-8")
        for fragment in required_fragments:
            if fragment not in text:
                fail(f"{rel} is missing documentation integration fragment: {fragment}")


def validate_examples(policy: dict) -> None:
    seen_ids: set[str] = set()
    for item in policy["selectedExamples"]:
        if not isinstance(item, dict) or not {"id", "path", "area"} <= item.keys():
            fail("Each selectedExamples entry requires id, path, and area.")
        path = ROOT / item["path"]
        if not path.is_file():
            fail(f"Selected example page is missing: {item['path']}")
        text = path.read_text(encoding="utf-8")
        matches = list(FENCE_RE.finditer(text))
        ids = {match.group(1) for match in matches if match.group(1)}
        if item["id"] not in ids:
            fail(f"Selected example '{item['id']}' is not marked as a validated C# fence in {item['path']}.")
        if item["id"] in seen_ids:
            fail(f"Duplicate selected example id: {item['id']}")
        seen_ids.add(item["id"])


def _strip_strings_and_line_comments(line: str) -> str:
    result: list[str] = []
    i = 0
    in_string = False
    quote = ""
    while i < len(line):
        ch = line[i]
        if in_string:
            if ch == "\\":
                result.extend("  ")
                i += 2
                continue
            if ch == quote:
                in_string = False
            result.append(" ")
            i += 1
            continue
        if ch in ('"', "'"):
            in_string = True
            quote = ch
            result.append(" ")
            i += 1
            continue
        if ch == "/" and i + 1 < len(line) and line[i + 1] == "/":
            break
        result.append(ch)
        i += 1
    return "".join(result)


def _brace_depths(lines: list[str]) -> tuple[list[int], list[int]]:
    before: list[int] = []
    after: list[int] = []
    depth = 0
    in_block = False
    for raw in lines:
        before.append(depth)
        line = raw
        cleaned: list[str] = []
        i = 0
        while i < len(line):
            if in_block:
                end = line.find("*/", i)
                if end < 0:
                    i = len(line)
                    continue
                in_block = False
                i = end + 2
                continue
            if line.startswith("/*", i):
                in_block = True
                i += 2
                continue
            cleaned.append(line[i])
            i += 1
        code = _strip_strings_and_line_comments("".join(cleaned))
        depth += code.count("{") - code.count("}")
        after.append(depth)
    return before, after


def _declaration_text(lines: list[str], start: int, max_lines: int = 20) -> str:
    parts: list[str] = []
    paren = bracket = angle = 0
    for index in range(start, min(len(lines), start + max_lines)):
        raw = _strip_strings_and_line_comments(lines[index]).strip()
        if not raw:
            continue
        parts.append(raw)
        paren += raw.count("(") - raw.count(")")
        bracket += raw.count("[") - raw.count("]")
        # Generic angle balance is approximate but only used to avoid stopping too early.
        angle += raw.count("<") - raw.count(">")
        if paren <= 0 and bracket <= 0 and ("{" in raw or ";" in raw or "=>" in raw):
            break
    return " ".join(parts)


def _xml_doc_before(lines: list[str], start: int) -> tuple[bool, bool, tuple[str, ...], tuple[str, ...], bool]:
    index = start - 1
    while index >= 0 and (not lines[index].strip() or lines[index].lstrip().startswith("[")):
        index -= 1
    docs: list[str] = []
    while index >= 0:
        match = DOC_LINE_RE.match(lines[index])
        if not match:
            break
        docs.append(match.group(1))
        index -= 1
    docs.reverse()
    if not docs:
        return False, False, (), (), False
    xml = "<doc>" + "\n".join(docs) + "</doc>"
    try:
        root = ET.fromstring(xml)
    except ET.ParseError:
        return False, False, (), (), False
    inherited = root.find("inheritdoc") is not None
    summary = root.find("summary")
    has_summary = summary is not None and "".join(summary.itertext()).strip() != ""
    params = tuple(sorted({node.get("name", "") for node in root.findall("param") if node.get("name")}))
    tparams = tuple(sorted({node.get("name", "") for node in root.findall("typeparam") if node.get("name")}))
    returns = root.find("returns")
    has_returns = returns is not None and "".join(returns.itertext()).strip() != ""
    return inherited, has_summary, params, tparams, has_returns


def _generic_names(text: str, name: str) -> tuple[str, ...]:
    match = re.search(rf"\b{re.escape(name)}\s*<([^>]+)>", text)
    if not match:
        return ()
    result = []
    for value in match.group(1).split(","):
        identifier = value.strip().split()[-1].strip("@")
        if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", identifier):
            result.append(identifier)
    return tuple(result)


def _split_parameters(text: str) -> list[str]:
    result: list[str] = []
    current: list[str] = []
    depth = 0
    for ch in text:
        if ch in "<([{": depth += 1
        elif ch in ">)]}": depth = max(0, depth - 1)
        if ch == "," and depth == 0:
            result.append("".join(current).strip())
            current = []
        else:
            current.append(ch)
    if "".join(current).strip():
        result.append("".join(current).strip())
    return result


def _parameter_names(signature: str) -> tuple[str, ...]:
    start = signature.find("(")
    if start < 0:
        return ()
    depth = 0
    end = -1
    for index in range(start, len(signature)):
        if signature[index] == "(": depth += 1
        elif signature[index] == ")":
            depth -= 1
            if depth == 0:
                end = index
                break
    if end < 0:
        return ()
    names: list[str] = []
    for parameter in _split_parameters(signature[start + 1:end]):
        parameter = re.sub(r"\[[^\]]+\]\s*", "", parameter)
        parameter = parameter.split("=", 1)[0].strip()
        tokens = re.findall(r"@?[A-Za-z_][A-Za-z0-9_]*", parameter)
        if tokens:
            candidate = tokens[-1].lstrip("@")
            if candidate not in {"this", "params", "ref", "in", "out"}:
                names.append(candidate)
    return tuple(names)


def _normalized_param_types(signature: str) -> str:
    start = signature.find("(")
    if start < 0:
        return ""
    depth = 0
    end = -1
    for index in range(start, len(signature)):
        if signature[index] == "(":
            depth += 1
        elif signature[index] == ")":
            depth -= 1
            if depth == 0:
                end = index
                break
    if end < start:
        return ""
    types: list[str] = []
    for parameter in _split_parameters(signature[start + 1:end]):
        parameter = re.sub(r"\[[^\]]+\]\s*", "", parameter)
        parameter = parameter.split("=", 1)[0].strip()
        parameter = re.sub(r"\b(this|params|ref|in|out|scoped)\b\s*", "", parameter)
        match = re.match(r"(.+?)\s+@?[A-Za-z_][A-Za-z0-9_]*$", parameter)
        value = match.group(1) if match else parameter
        types.append(re.sub(r"\s+", "", value))
    return ",".join(types)


def _member_item(package: str, rel: str, line_no: int, namespace: str, scope: TypeScope,
                 signature: str, docs: tuple[bool, bool, tuple[str, ...], tuple[str, ...], bool]) -> ApiItem | None:
    clean = re.sub(r"\s+", " ", signature).strip()
    if "=>" in clean:
        clean = clean.split("=>", 1)[0].rstrip()
    visibility_match = re.match(r"(public|protected(?: internal)?|private protected)\s+", clean)
    implicit_interface = scope.kind == "interface" and not re.match(r"(private|internal)\s+", clean)
    if not visibility_match and not implicit_interface:
        return None
    visibility = visibility_match.group(1) if visibility_match else "public"
    if visibility == "private protected":
        return None
    without_attrs = re.sub(r"^\[[^\]]+\]\s*", "", clean)
    # Nested type declarations are handled independently.
    if TYPE_RE.match(without_attrs):
        return None
    inherited, has_summary, doc_params, doc_tparams, has_returns = docs
    containing = scope.full_name

    event_match = re.search(r"\bevent\s+[^;=]+?\s+(@?[A-Za-z_][A-Za-z0-9_]*)\s*(?:[;{=])", clean)
    if event_match:
        name = event_match.group(1).lstrip("@")
        doc_id = f"E:{containing}.{name}"
        kind = "Event"
        params: tuple[str, ...] = ()
        tparams: tuple[str, ...] = ()
        requires_returns = False
    elif " operator " in f" {clean} ":
        name_match = re.search(r"operator\s+([^\s(]+)", clean)
        name = "operator" + (name_match.group(1) if name_match else "")
        params = _parameter_names(clean)
        doc_id = f"M:{containing}.{name}({_normalized_param_types(clean)})"
        kind = "Operator"
        tparams = ()
        requires_returns = True
    elif "(" in clean:
        before = clean[:clean.find("(")].strip()
        name_match = re.search(r"(@?[A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>]+>)?$", before)
        if not name_match:
            return None
        name = name_match.group(1).lstrip("@")
        params = _parameter_names(clean)
        tparams = _generic_names(before, name)
        is_ctor = name == scope.name
        kind = "Constructor" if is_ctor else "Method"
        doc_name = "#ctor" if is_ctor else name
        doc_id = f"M:{containing}.{doc_name}({_normalized_param_types(clean)})"
        prefix = before[:name_match.start()].strip().split()
        return_type = prefix[-1] if prefix else "void"
        requires_returns = not is_ctor and return_type not in {"void", "Task", "ValueTask"}
        if return_type.startswith("Task<") or return_type.startswith("ValueTask<"):
            requires_returns = True
    elif re.search(r"\bthis\s*\[", clean):
        name = "Item"
        params = _parameter_names(clean.replace("[", "(", 1).replace("]", ")", 1))
        doc_id = f"P:{containing}.Item({_normalized_param_types(clean)})"
        kind = "Property"
        tparams = ()
        requires_returns = False
    elif "{" in clean or "=>" in clean:
        head = re.split(r"\{|=>", clean, maxsplit=1)[0].strip()
        name_match = re.search(r"(@?[A-Za-z_][A-Za-z0-9_]*)$", head)
        if not name_match:
            return None
        name = name_match.group(1).lstrip("@")
        doc_id = f"P:{containing}.{name}"
        kind = "Property"
        params = ()
        tparams = ()
        requires_returns = False
    else:
        name_match = re.search(r"(@?[A-Za-z_][A-Za-z0-9_]*)\s*(?:=|;)", clean)
        if not name_match:
            return None
        name = name_match.group(1).lstrip("@")
        doc_id = f"F:{containing}.{name}"
        kind = "Field"
        params = ()
        tparams = ()
        requires_returns = False

    return ApiItem(package, doc_id, kind, rel, line_no, name, visibility, params, tparams,
                   requires_returns, inherited, has_summary, doc_params, doc_tparams, has_returns)


def parse_csharp_apis(policy: dict) -> list[ApiItem]:
    items: list[ApiItem] = []
    for package, project_rel in policy["projects"].items():
        project_dir = (ROOT / project_rel).parent
        for path in sorted(project_dir.rglob("*.cs")):
            if any(part in {"bin", "obj", "Generated"} for part in path.parts):
                continue
            lines = path.read_text(encoding="utf-8-sig").splitlines()
            before, after = _brace_depths(lines)
            namespace = ""
            for raw in lines:
                match = NAMESPACE_RE.match(raw)
                if match:
                    namespace = match.group(1)
                    break
            scopes: list[TypeScope] = []
            for index, raw in enumerate(lines):
                if not re.match(r"^\s*(?:public|protected(?:\s+internal)?|private\s+protected)\s+", raw):
                    continue
                declaration = _declaration_text(lines, index)
                match = TYPE_RE.match(declaration)
                if not match:
                    continue
                visibility = match.group("visibility")
                if visibility == "private protected":
                    continue
                kind_raw = match.group("kind")
                kind = "interface" if kind_raw == "interface" else "type"
                name = match.group("name").lstrip("@")
                parents = [scope for scope in scopes if scope.body_depth is not None and scope.start_line < index < scope.end_line]
                parent = max(parents, key=lambda value: value.body_depth or 0, default=None)
                full_name = f"{parent.full_name}.{name}" if parent else f"{namespace}.{name}".strip(".")
                body_depth: int | None = None
                end_line = index
                for cursor in range(index, min(len(lines), index + 20)):
                    code = _strip_strings_and_line_comments(lines[cursor])
                    if "{" in code:
                        body_depth = before[cursor] + 1
                        end_line = len(lines) - 1
                        for close in range(cursor + 1, len(lines)):
                            if after[close] < body_depth:
                                end_line = close
                                break
                        break
                    if ";" in code:
                        break
                scopes.append(TypeScope(name, full_name, kind, visibility, index, body_depth, end_line,
                                        _generic_names(declaration, name)))
                docs = _xml_doc_before(lines, index)
                primary_params = _parameter_names(declaration) if kind_raw.startswith("record") or "(" in declaration[:declaration.find("{") if "{" in declaration else len(declaration)] else ()
                items.append(ApiItem(
                    package=package, documentation_id=f"T:{full_name}", kind="Type",
                    file=path.relative_to(ROOT).as_posix(), line=index + 1, name=name,
                    visibility=visibility, parameter_names=primary_params,
                    type_parameter_names=_generic_names(declaration, name), requires_returns=False,
                    inherited=docs[0], has_summary=docs[1], documented_parameters=docs[2],
                    documented_type_parameters=docs[3], has_returns=docs[4],
                ))

            for scope in scopes:
                if scope.body_depth is None:
                    continue
                index = scope.start_line + 1
                while index < scope.end_line:
                    if before[index] != scope.body_depth:
                        index += 1
                        continue
                    stripped = lines[index].strip()
                    if not stripped or stripped.startswith(("///", "//", "/*", "*", "[", "#")):
                        index += 1
                        continue
                    docs = _xml_doc_before(lines, index)
                    starts_with_visibility = re.match(
                        r"^\s*(?:public|protected(?:\s+internal)?|private\s+protected)\s+", lines[index]
                    ) is not None
                    has_doc_comment = index > 0 and any(
                        DOC_LINE_RE.match(lines[cursor])
                        for cursor in range(max(0, index - 20), index)
                        if all(not lines[k].strip() or lines[k].lstrip().startswith(("///", "[")) for k in range(cursor + 1, index))
                    )
                    if not starts_with_visibility and not (scope.kind == "interface" and has_doc_comment):
                        index += 1
                        continue
                    declaration = _declaration_text(lines, index)
                    if not declaration or declaration.startswith(("using ", "namespace ", "where ")):
                        index += 1
                        continue
                    item = _member_item(package, path.relative_to(ROOT).as_posix(), index + 1, namespace,
                                        scope, declaration, docs)
                    if item is not None:
                        items.append(item)
                    index += 1
    unique: dict[str, ApiItem] = {}
    duplicates: list[str] = []
    for item in items:
        key = item.documentation_id
        if key in unique:
            # Partial declarations and overload signatures with indistinguishable source text are made stable by location.
            key = f"{key}@{item.file}:{item.line}"
            item = ApiItem(**{**asdict(item), "documentation_id": key})
        unique[key] = item
    return sorted(unique.values(), key=lambda value: value.documentation_id)


def load_baseline() -> dict:
    if not BASELINE_PATH.exists():
        return {"schemaVersion": 1, "entries": []}
    baseline = load_json(BASELINE_PATH)
    if baseline.get("schemaVersion") != 1 or not isinstance(baseline.get("entries"), list):
        fail("documentation-baseline.json must use schemaVersion 1 and contain an entries array.")
    return baseline


def validate_baseline(policy: dict, items: list[ApiItem]) -> tuple[dict[tuple[str, str], dict], list[dict]]:
    baseline = load_baseline()
    entries = baseline["entries"]
    if len(entries) > policy["baselineMaximumEntries"]:
        fail(f"Documentation baseline contains {len(entries)} entries; policy allows at most {policy['baselineMaximumEntries']}.")
    current = {item.documentation_id: item for item in items}
    indexed: dict[tuple[str, str], dict] = {}
    stale: list[dict] = []
    for entry in entries:
        required = {"package", "documentationId", "memberKind", "missingElement", "reason", "recordedDate", "targetMilestone"}
        if not isinstance(entry, dict) or not required <= entry.keys():
            fail("Every documentation baseline entry must include package, documentationId, memberKind, missingElement, reason, recordedDate, and targetMilestone.")
        key = (entry["documentationId"], entry["missingElement"])
        if key in indexed:
            fail(f"Duplicate documentation baseline entry: {key[0]} / {key[1]}")
        indexed[key] = entry
        item = current.get(entry["documentationId"])
        if item is None or entry["missingElement"] not in item.missing_elements(policy):
            stale.append(entry)
    if stale:
        ids = ", ".join(f"{entry['documentationId']} ({entry['missingElement']})" for entry in stale[:10])
        fail(f"Stale documentation baseline entries detected: {ids}")
    return indexed, entries


def assess_source_documentation(
    policy: dict,
    items: list[ApiItem],
    baseline_index: dict[tuple[str, str], dict],
) -> tuple[list[dict], float]:
    findings: list[dict] = []
    unapproved: list[dict] = []
    for item in items:
        for missing_element in item.missing_elements(policy):
            finding = {
                "package": item.package,
                "documentationId": item.documentation_id,
                "memberKind": item.kind,
                "missingElement": missing_element,
                "file": item.file,
                "line": item.line,
                "baseline": (item.documentation_id, missing_element) in baseline_index,
            }
            findings.append(finding)
            if not finding["baseline"]:
                unapproved.append(finding)

    incomplete_ids = {finding["documentationId"] for finding in findings}
    documented_count = len(items) - len(incomplete_ids)
    percentage = (documented_count / len(items) * 100.0) if items else 100.0
    if percentage + 1e-9 < float(policy["minimumPublicApiDocumentationPercent"]):
        fail(
            f"Public API documentation coverage {percentage:.2f}% is below policy minimum "
            f"{policy['minimumPublicApiDocumentationPercent']:.2f}%."
        )
    if unapproved:
        examples = ", ".join(
            f"{finding['documentationId']} ({finding['missingElement']})"
            for finding in unapproved[:20]
        )
        fail(f"New undocumented public API findings are not in the approved baseline: {examples}")
    return findings, percentage


def find_xml_docs(policy: dict, build_root: Path | None) -> dict[str, Path]:
    roots = [build_root] if build_root else []
    roots.extend([ROOT / "artifacts" / "documentation" / "build", ROOT / "src"])
    result: dict[str, Path] = {}
    for package in policy["requiredPackages"]:
        candidates: list[Path] = []
        for root in roots:
            if root and root.exists():
                candidates.extend(root.rglob(f"{package}.xml"))
        candidates = [p for p in candidates if "ref" not in p.parts and "obj" not in p.parts]
        if candidates:
            result[package] = max(candidates, key=lambda p: p.stat().st_mtime)
    return result


def verify_xml_docs(policy: dict, build_root: Path | None) -> tuple[int, list[str]]:
    paths = find_xml_docs(policy, build_root)
    missing = [package for package in policy["requiredPackages"] if package not in paths]
    if missing:
        fail("Missing XML documentation files for: " + ", ".join(missing))
    unresolved: list[str] = []
    seen_ids: set[str] = set()
    for package, path in paths.items():
        try:
            tree = ET.parse(path)
        except ET.ParseError as exc:
            fail(f"Malformed XML documentation file {path}: {exc}")
        for member in tree.getroot().findall("./members/member"):
            doc_id = member.get("name")
            if not doc_id:
                fail(f"XML documentation member without name in {path}")
            if doc_id in seen_ids:
                fail(f"Duplicate XML documentation ID: {doc_id}")
            seen_ids.add(doc_id)
            for node in member.iter():
                cref = node.get("cref")
                if cref and cref.startswith("!:"):
                    unresolved.append(f"{package}:{doc_id}:{cref}")
    if unresolved and policy["failOnUnresolvedCrefs"]:
        fail("Unresolved XML documentation cref references: " + ", ".join(unresolved[:20]))
    return len(seen_ids), unresolved


def verify_markdown_links(policy: dict, output: Path | None = None) -> list[dict]:
    broken: list[dict] = []
    docs_root = ROOT / "docs"
    docs_root_resolved = docs_root.resolve()
    for path in sorted(docs_root.rglob("*.md")):
        text = path.read_text(encoding="utf-8")
        for match in LINK_RE.finditer(text):
            raw = match.group(1).strip().strip("<>")
            target = raw.split("#", 1)[0].split("?", 1)[0]
            if not target or re.match(r"^(?:https?|mailto|tel|xref):", target, re.I):
                continue
            if target.startswith("/"):
                continue
            resolved = (path.parent / target).resolve()
            try:
                resolved.relative_to(docs_root_resolved)
            except ValueError:
                broken.append({
                    "source": path.relative_to(ROOT).as_posix(),
                    "target": raw,
                    "reason": "outside DocFX conceptual content root",
                })
                continue
            if not resolved.exists():
                # DocFX translates .html links from source .md files.
                md_candidate = resolved.with_suffix(".md") if resolved.suffix == ".html" else None
                if not (md_candidate and md_candidate.exists()):
                    broken.append({"source": path.relative_to(ROOT).as_posix(), "target": raw, "reason": "missing target"})
    if output is not None:
        (output / "broken-links.json").write_text(json.dumps(broken, indent=2) + "\n", encoding="utf-8")
    if broken and policy["failOnBrokenInternalLinks"]:
        formatted = ", ".join(f"{item['source']} -> {item['target']}" for item in broken[:20])
        fail(f"Broken internal documentation links: {formatted}")
    return broken


def collect_snippets(policy: dict) -> list[tuple[str, str, str]]:
    snippets: list[tuple[str, str, str]] = []
    selected = {item["id"]: item for item in policy["selectedExamples"]}
    for item_id, item in selected.items():
        path = ROOT / item["path"]
        for match in FENCE_RE.finditer(path.read_text(encoding="utf-8")):
            if match.group(1) == item_id:
                snippets.append((item_id, item["path"], match.group(2).strip() + "\n"))
                break
    return snippets


def compile_snippets(policy: dict, output: Path, configuration: str) -> tuple[int, int]:
    snippets = collect_snippets(policy)
    snippet_dir = output / "snippets"
    if snippet_dir.exists():
        shutil.rmtree(snippet_dir)
    snippet_dir.mkdir(parents=True)
    project_refs = "\n".join(
        f'    <ProjectReference Include="{(ROOT / rel).as_posix()}" />'
        for rel in policy["projects"].values()
    )
    csproj = f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Library</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
{project_refs}
  </ItemGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
'''
    (snippet_dir / "DocumentationSnippets.csproj").write_text(csproj, encoding="utf-8")
    for index, (item_id, _, code) in enumerate(snippets, 1):
        (snippet_dir / f"Snippet{index:02d}_{item_id.replace('.', '_')}.cs").write_text(code, encoding="utf-8")
    if not snippets:
        fail("No validated documentation snippets were found.")
    proc = subprocess.run(
        ["dotnet", "build", "DocumentationSnippets.csproj", "--configuration", configuration,
         "--nologo", "--verbosity", "minimal"], cwd=snippet_dir, text=True,
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
    )
    (snippet_dir / "build.log").write_text(proc.stdout or "", encoding="utf-8")
    if proc.returncode != 0:
        fail(f"Validated documentation snippets failed to compile. See {snippet_dir / 'build.log'}")
    return len(snippets), 0


def count_api_pages(api_root: Path | None) -> int:
    if api_root is None or not api_root.exists():
        return 0
    return sum(1 for path in api_root.rglob("*.yml") if path.name not in {"toc.yml", "xrefmap.yml"})


def version_from_packaging() -> str:
    tree = ET.parse(ROOT / "eng" / "Packaging.props")
    value = tree.getroot().findtext("./PropertyGroup/Version")
    if not value or not value.strip():
        fail("eng/Packaging.props does not define Version.")
    return value.strip()


def write_reports(output: Path, *, policy: dict, items: list[ApiItem], baseline_entries: list[dict],
                  missing: list[dict], unresolved: list[str], broken: list[dict], snippet_count: int,
                  failed_snippets: int, api_pages: int, xml_member_count: int, version: str,
                  commit_sha: str, release_tag: str, status: str) -> None:
    output.mkdir(parents=True, exist_ok=True)
    fully_documented = len(items) - len({entry["documentationId"] for entry in missing})
    percentage = round((fully_documented / len(items) * 100.0) if items else 100.0, 2)
    summary = {
        "schemaVersion": 1,
        "status": status,
        "packageVersion": version,
        "sourceCommit": commit_sha,
        "releaseTag": release_tag,
        "buildDateUtc": dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
        "productionPackageCount": len(policy["requiredPackages"]),
        "publicTypeCount": sum(item.kind == "Type" for item in items),
        "publicMemberCount": len(items),
        "documentedMemberCount": fully_documented,
        "documentationPercent": percentage,
        "minimumDocumentationPercent": policy["minimumPublicApiDocumentationPercent"],
        "baselineExceptionCount": len(baseline_entries),
        "missingDocumentationCount": len(missing),
        "xmlDocumentationMemberCount": xml_member_count,
        "unresolvedCrefCount": len(unresolved),
        "brokenLinkCount": len(broken),
        "validatedSnippetCount": snippet_count,
        "failedSnippetCount": failed_snippets,
        "generatedApiPageCount": api_pages,
    }
    (output / "documentation-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    (output / "missing-documentation.json").write_text(json.dumps(missing, indent=2) + "\n", encoding="utf-8")
    markdown = f"""# TCJ documentation quality summary

- **Status:** {status}
- **Package version:** `{version}`
- **Source commit:** `{commit_sha}`
- **Release tag:** `{release_tag}`
- **Production packages:** {len(policy['requiredPackages'])}
- **Public types:** {summary['publicTypeCount']}
- **Public API items:** {len(items)}
- **Fully documented API items:** {fully_documented}
- **Documentation coverage:** {percentage:.2f}%
- **Required minimum:** {policy['minimumPublicApiDocumentationPercent']:.2f}%
- **Baseline exceptions:** {len(baseline_entries)}
- **Missing documentation findings:** {len(missing)}
- **Unresolved cref references:** {len(unresolved)}
- **Broken internal links:** {len(broken)}
- **Validated snippets:** {snippet_count}
- **Failed snippets:** {failed_snippets}
- **Generated API pages:** {api_pages}
- **XML documentation members:** {xml_member_count}
"""
    (output / "DOCUMENTATION_SUMMARY.md").write_text(markdown, encoding="utf-8")


def command_validate_config(_: argparse.Namespace) -> int:
    policy = load_json(POLICY_PATH)
    validate_policy(policy)
    validate_tool_manifest(policy)
    validate_docfx_config(policy)
    validate_central_xml_docs()
    validate_git_tracking(policy)
    validate_workflow_integration()
    validate_examples(policy)
    verify_markdown_links(policy)
    items = parse_csharp_apis(policy)
    if not items:
        fail("No public production APIs were discovered.")
    baseline_index, _ = validate_baseline(policy, items)
    _, percentage = assess_source_documentation(policy, items, baseline_index)
    print(
        f"Documentation configuration verified for {len(policy['requiredPackages'])} packages, "
        f"{len(items)} public API items, and {percentage:.2f}% measured coverage."
    )
    return 0


def command_verify(args: argparse.Namespace) -> int:
    policy = load_json(POLICY_PATH)
    validate_policy(policy)
    validate_tool_manifest(policy)
    validate_docfx_config(policy)
    validate_central_xml_docs()
    validate_git_tracking(policy)
    validate_workflow_integration()
    validate_examples(policy)

    output = Path(args.output)
    if not output.is_absolute():
        output = ROOT / output
    output.mkdir(parents=True, exist_ok=True)
    items = parse_csharp_apis(policy)
    baseline_index, baseline_entries = validate_baseline(policy, items)
    findings, percentage = assess_source_documentation(policy, items, baseline_index)

    xml_member_count, unresolved = verify_xml_docs(policy, Path(args.build_root) if args.build_root else None)
    broken = verify_markdown_links(policy, output)
    snippet_count = failed_snippets = 0
    if not args.skip_snippets:
        snippet_count, failed_snippets = compile_snippets(policy, output, args.configuration)
    else:
        snippet_count = len(collect_snippets(policy))
    api_pages = count_api_pages(Path(args.api_root) if args.api_root else output / "api")
    if api_pages < len(policy["requiredPackages"]):
        fail(f"Generated API metadata contains {api_pages} pages; at least {len(policy['requiredPackages'])} are required.")
    version = args.version or version_from_packaging()
    commit_sha = args.commit_sha or os.environ.get("GITHUB_SHA", "local")
    release_tag = args.release_tag or os.environ.get("GITHUB_REF_NAME", "local")
    write_reports(output, policy=policy, items=items, baseline_entries=baseline_entries,
                  missing=findings, unresolved=unresolved, broken=broken, snippet_count=snippet_count,
                  failed_snippets=failed_snippets, api_pages=api_pages, xml_member_count=xml_member_count,
                  version=version, commit_sha=commit_sha, release_tag=release_tag, status="PASS")
    print(f"Documentation verification passed: {percentage:.2f}% coverage, {api_pages} API pages, {snippet_count} validated snippets.")
    return 0


def command_baseline(args: argparse.Namespace) -> int:
    policy = load_json(POLICY_PATH)
    validate_policy(policy)
    items = parse_csharp_apis(policy)
    entries: list[dict] = []
    recorded = args.recorded_date or dt.date.today().isoformat()
    for item in items:
        for missing_element in item.missing_elements(policy):
            entries.append({
                "package": item.package,
                "documentationId": item.documentation_id,
                "memberKind": item.kind,
                "missingElement": missing_element,
                "reason": "Existing public API predating the documentation quality gate",
                "recordedDate": recorded,
                "targetMilestone": policy["baselineTargetMilestone"],
            })
    result = {"schemaVersion": 1, "entries": entries}
    target = Path(args.output) if args.output else BASELINE_PATH
    if not target.is_absolute():
        target = ROOT / target
    target.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    incomplete_ids = {entry["documentationId"] for entry in entries}
    percentage = ((len(items) - len(incomplete_ids)) / len(items) * 100.0) if items else 100.0
    print(f"Wrote {len(entries)} baseline findings for {len(incomplete_ids)} API items; measured complete coverage {percentage:.2f}%.")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    validate = sub.add_parser("validate-config", help="Validate documentation configuration and policy.")
    validate.set_defaults(func=command_validate_config)
    verify = sub.add_parser("verify", help="Verify generated documentation and quality policy.")
    verify.add_argument("--configuration", default="Release")
    verify.add_argument("--output", default="artifacts/documentation")
    verify.add_argument("--build-root")
    verify.add_argument("--api-root")
    verify.add_argument("--version")
    verify.add_argument("--commit-sha")
    verify.add_argument("--release-tag")
    verify.add_argument("--skip-snippets", action="store_true")
    verify.set_defaults(func=command_verify)
    baseline = sub.add_parser("baseline", help="Measure and write the current documentation baseline.")
    baseline.add_argument("--output")
    baseline.add_argument("--recorded-date")
    baseline.set_defaults(func=command_baseline)
    return parser


def main() -> int:
    try:
        args = build_parser().parse_args()
        return args.func(args)
    except DocumentationError as exc:
        print(f"Documentation verification failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
