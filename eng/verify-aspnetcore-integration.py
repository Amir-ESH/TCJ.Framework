#!/usr/bin/env python3
"""Validate and summarize the TCJ ASP.NET Core end-to-end integration gate."""

from __future__ import annotations

import argparse
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_POLICY = ROOT / "eng/aspnetcore-integration-policy.json"
DEFAULT_OUTPUT = ROOT / "artifacts/aspnetcore-integration"
DEFAULT_RESULTS = ROOT / "TestResults/AspNetCoreIntegration"
EXPECTED_PROJECT = "tests/TCJ.AspNetCore.IntegrationTests/TCJ.AspNetCore.IntegrationTests.csproj"
EXPECTED_TEST_ROOT = ROOT / "tests/TCJ.AspNetCore.IntegrationTests"
EXPECTED_FACTORY = EXPECTED_TEST_ROOT / "Fixtures/TcjWebApplicationFactory.cs"
EXPECTED_AUTH_HANDLER = EXPECTED_TEST_ROOT / "TestHost/TestAuthenticationHandler.cs"
EXPECTED_WORKFLOW = ROOT / ".github/workflows/aspnetcore-integration.yml"
EXPECTED_NATIVE_AOT_PROJECT = ROOT / "tests/TCJ.AspNetCore.NativeAotSmoke/TCJ.AspNetCore.NativeAotSmoke.csproj"
EXPECTED_NATIVE_AOT_PROGRAM = EXPECTED_NATIVE_AOT_PROJECT.parent / "Program.cs"


class AspNetCoreIntegrationError(RuntimeError):
    pass


@dataclass(frozen=True)
class Policy:
    path: Path
    test_project: str
    minimum_test_count: int
    required_categories: tuple[str, ...]
    require_linux: bool
    require_windows: bool
    require_authenticated_request_tests: bool
    require_anonymous_request_tests: bool
    require_production_environment_tests: bool
    require_development_environment_tests: bool
    collect_host_diagnostics_on_failure: bool
    scan_uploaded_diagnostics_for_secrets: bool


@dataclass(frozen=True)
class TestResult:
    name: str
    outcome: str


def fail(message: str) -> None:
    raise AspNetCoreIntegrationError(message)


def read_json_object(path: Path, description: str) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        fail(f"{description} is missing: {path}")
    except json.JSONDecodeError as error:
        fail(f"{description} is malformed JSON at {path}: {error}")
    except OSError as error:
        fail(f"Unable to read {description} at {path}: {error}")
    if not isinstance(value, dict):
        fail(f"{description} must contain a JSON object: {path}")
    return value


def require_string(data: dict[str, Any], key: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        fail(f"Policy property '{key}' must be a non-empty string.")
    return value.strip()


def require_int(data: dict[str, Any], key: str, minimum: int) -> int:
    value = data.get(key)
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        fail(f"Policy property '{key}' must be an integer >= {minimum}.")
    return value


def require_bool(data: dict[str, Any], key: str) -> bool:
    value = data.get(key)
    if not isinstance(value, bool):
        fail(f"Policy property '{key}' must be a boolean.")
    return value


def require_strings(data: dict[str, Any], key: str) -> tuple[str, ...]:
    value = data.get(key)
    if not isinstance(value, list) or not value:
        fail(f"Policy property '{key}' must be a non-empty array.")
    if any(not isinstance(item, str) or not item.strip() for item in value):
        fail(f"Policy property '{key}' must contain only non-empty strings.")
    normalized = tuple(item.strip() for item in value)
    if len(set(normalized)) != len(normalized):
        fail(f"Policy property '{key}' must not contain duplicates.")
    return normalized


def load_policy(path: Path = DEFAULT_POLICY) -> Policy:
    data = read_json_object(path, "ASP.NET Core integration policy")
    if data.get("schemaVersion") != 1:
        fail("ASP.NET Core integration policy schemaVersion must be 1.")
    return Policy(
        path=path,
        test_project=require_string(data, "testProject"),
        minimum_test_count=require_int(data, "minimumTestCount", 15),
        required_categories=require_strings(data, "requiredCategories"),
        require_linux=require_bool(data, "requireLinux"),
        require_windows=require_bool(data, "requireWindows"),
        require_authenticated_request_tests=require_bool(data, "requireAuthenticatedRequestTests"),
        require_anonymous_request_tests=require_bool(data, "requireAnonymousRequestTests"),
        require_production_environment_tests=require_bool(data, "requireProductionEnvironmentTests"),
        require_development_environment_tests=require_bool(data, "requireDevelopmentEnvironmentTests"),
        collect_host_diagnostics_on_failure=require_bool(data, "collectHostDiagnosticsOnFailure"),
        scan_uploaded_diagnostics_for_secrets=require_bool(data, "scanUploadedDiagnosticsForSecrets"),
    )


def read_xml(path: Path, description: str) -> ET.Element:
    try:
        return ET.parse(path).getroot()
    except FileNotFoundError:
        fail(f"{description} is missing: {path}")
    except (OSError, ET.ParseError) as error:
        fail(f"Unable to parse {description} at {path}: {error}")


def validate_project(policy: Policy) -> None:
    if policy.test_project != EXPECTED_PROJECT:
        fail(f"Policy testProject must be '{EXPECTED_PROJECT}'.")
    project = read_xml(ROOT / policy.test_project, "ASP.NET Core integration test project")
    if project.findtext("./PropertyGroup/TargetFramework") != "net10.0":
        fail("ASP.NET Core integration test project must explicitly target net10.0.")
    references = {item.attrib.get("Include", "") for item in project.findall(".//PackageReference")}
    if "Microsoft.AspNetCore.TestHost" not in references and "Microsoft.AspNetCore.Mvc.Testing" not in references:
        fail("ASP.NET Core integration test project must reference TestHost or Microsoft.AspNetCore.Mvc.Testing.")
    project_refs = {item.attrib.get("Include", "").replace("\\", "/") for item in project.findall(".//ProjectReference")}
    if "../../src/TCJ.AspNetCore/TCJ.AspNetCore.csproj" not in project_refs:
        fail("ASP.NET Core integration test project must reference TCJ.AspNetCore.")

    central = read_xml(ROOT / "Directory.Packages.props", "central package policy")
    versions = {item.attrib.get("Include", ""): item.attrib.get("Version", "") for item in central.findall(".//PackageVersion")}
    testing_version = versions.get("Microsoft.AspNetCore.TestHost") or versions.get("Microsoft.AspNetCore.Mvc.Testing") or ""
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?", testing_version):
        fail("ASP.NET Core testing dependency must have a centrally pinned semantic version.")


def validate_test_host() -> None:
    try:
        factory = EXPECTED_FACTORY.read_text(encoding="utf-8")
        auth = EXPECTED_AUTH_HANDLER.read_text(encoding="utf-8")
    except FileNotFoundError as error:
        fail(f"ASP.NET Core integration host source is missing: {error.filename}")
    required_factory = [
        "WebApplication.CreateBuilder",
        "UseTestServer()",
        "AddTcjAspNetCore",
        "UseTcjAspNetCore",
        "UseAuthentication()",
        "UseAuthorization()",
        "GetTestServer().CreateHandler()",
        "/health",
        "/current-user",
        "/services/scoped",
        "/errors/unhandled",
        "/errors/canceled",
    ]
    missing = [marker for marker in required_factory if marker not in factory]
    if missing:
        fail("ASP.NET Core test host is missing required real-pipeline markers: " + ", ".join(missing))
    for marker in ("AuthenticationHandler<AuthenticationSchemeOptions>", "X-Test-UserId", "X-Test-Roles", "AuthenticateResult"):
        if marker not in auth:
            fail(f"Deterministic test authentication handler is missing marker: {marker}")
    production_tree = ROOT / "src"
    for path in production_tree.rglob("*.cs"):
        if "TestAuthenticationHandler" in path.read_text(encoding="utf-8", errors="ignore"):
            fail(f"Test authentication leaked into a production assembly: {path.relative_to(ROOT)}")


def validate_test_inventory(policy: Policy) -> None:
    if not EXPECTED_TEST_ROOT.exists():
        fail(f"ASP.NET Core integration test directory is missing: {EXPECTED_TEST_ROOT}")
    sources = list(EXPECTED_TEST_ROOT.rglob("*.cs"))
    text = "\n".join(path.read_text(encoding="utf-8") for path in sources)
    test_count = len(re.findall(r"\[(?:Fact|Theory)(?:\([^\]]*\))?\]", text))
    if test_count < policy.minimum_test_count:
        fail(f"Integration test source contains {test_count} Fact/Theory tests, below policy minimum {policy.minimum_test_count}.")
    missing = [category for category in policy.required_categories if f'[Trait("Category", "{category}")]' not in text]
    if missing:
        fail("Missing required ASP.NET Core test categories: " + ", ".join(missing))
    required_scenarios = {
        "authenticated": "Authenticated_request",
        "anonymous": "Anonymous_request",
        "production": "Production",
        "development": "Development",
        "cancellation": "cancellation",
        "duplicate registration": "Duplicate_framework_registration",
    }
    for description, marker in required_scenarios.items():
        if marker.lower() not in text.lower():
            fail(f"ASP.NET Core integration tests are missing required {description} scenario.")



def validate_native_aot_smoke() -> None:
    project = read_xml(EXPECTED_NATIVE_AOT_PROJECT, "ASP.NET Core Native AOT smoke project")
    properties = {
        child.tag.rsplit("}", 1)[-1]: (child.text or "").strip()
        for group in project.findall("./PropertyGroup")
        for child in group
    }
    if properties.get("TargetFramework") != "net10.0":
        fail("ASP.NET Core Native AOT smoke project must target net10.0.")
    if properties.get("PublishAot", "").casefold() != "true":
        fail("ASP.NET Core Native AOT smoke project must set PublishAot=true.")
    if properties.get("JsonSerializerIsReflectionEnabledByDefault", "").casefold() != "false":
        fail("ASP.NET Core Native AOT smoke project must disable reflection-based System.Text.Json defaults.")
    warning_exceptions = {value.strip() for value in properties.get("WarningsNotAsErrors", "").split(";") if value.strip()}
    expected_warning_exceptions = {"IDE0011", "CA1001", "CA1512", "CA1859"}
    if warning_exceptions != expected_warning_exceptions:
        fail("ASP.NET Core Native AOT smoke must exempt only the known non-AOT Core diagnostics from warnings-as-errors.")
    project_refs = {item.attrib.get("Include", "").replace("\\", "/") for item in project.findall(".//ProjectReference")}
    if "../../src/TCJ.AspNetCore/TCJ.AspNetCore.csproj" not in project_refs:
        fail("ASP.NET Core Native AOT smoke project must reference TCJ.AspNetCore.")
    source = EXPECTED_NATIVE_AOT_PROGRAM.read_text(encoding="utf-8")
    required = [
        "WebApplication.CreateSlimBuilder",
        "NativeAotSmokeJsonContext.Default",
        '"/success"',
        '"/validation"',
        '"/not-found"',
        '"/conflict"',
        '"/unhandled"',
        "UNEXPECTED_ERROR",
        "TCJ.AspNetCore Native AOT smoke passed",
    ]
    missing = [marker for marker in required if marker not in source]
    if missing:
        fail("ASP.NET Core Native AOT smoke source is missing required markers: " + ", ".join(missing))
    forbidden = [marker for marker in ("AddControllers", "MapControllers", "Newtonsoft.Json") if marker in source]
    if forbidden:
        fail("ASP.NET Core Native AOT smoke must remain Minimal-API-only; forbidden markers: " + ", ".join(forbidden))

def validate_workflows() -> None:
    try:
        workflow = EXPECTED_WORKFLOW.read_text(encoding="utf-8")
    except FileNotFoundError:
        fail(f"Dedicated ASP.NET Core integration workflow is missing: {EXPECTED_WORKFLOW}")
    markers = [
        "name: ASP.NET Core integration",
        "push:",
        "workflow_dispatch:",
        "workflow_call:",
        "schedule:",
        "ubuntu-latest",
        "windows-latest",
        "Test on ${{ matrix.name }}",
        "verify-aspnetcore-integration.py validate-config",
        "Category=AspNetCore",
        "verify-aspnetcore-integration.py verify",
        "verify-platforms",
        "GITHUB_STEP_SUMMARY",
        "actions/upload-artifact",
        "Native AOT Minimal API smoke",
        "tests/TCJ.AspNetCore.NativeAotSmoke/TCJ.AspNetCore.NativeAotSmoke.csproj",
        "dotnet publish",
        "--runtime linux-x64",
        "-p:WarningsNotAsErrors=IDE0011%3BCA1001%3BCA1512%3BCA1859",
        "Execute Native AOT smoke host",
    ]
    missing = [marker for marker in markers if marker not in workflow]
    if missing:
        fail("Dedicated ASP.NET Core workflow is missing required markers: " + ", ".join(missing))

    gate = (ROOT / ".github/workflows/required-pr-gate.yml").read_text(encoding="utf-8")
    if "pull_request:" not in gate or "uses: ./.github/workflows/aspnetcore-integration.yml" not in gate:
        fail("Required PR Gate must route relevant pull requests through ASP.NET Core integration.")

    ci = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
    if "verify-aspnetcore-integration.py validate-config" not in ci:
        fail("Normal CI must run ASP.NET Core integration configuration validation.")
    if "Category!=AspNetCore" not in ci:
        fail("Normal coverage CI must exclude the dedicated ASP.NET Core integration suite.")

    preflight = (ROOT / ".github/workflows/release-preflight.yml").read_text(encoding="utf-8")
    release = (ROOT / ".github/workflows/release.yml").read_text(encoding="utf-8")
    if "aspnetcore-integration.yml" not in preflight or "aspnetcore-integration" not in preflight:
        fail("Release preflight must enforce the ASP.NET Core integration workflow.")
    if "aspnetcore-integration.yml" not in release or "aspnetcore-integration" not in release:
        fail("Official release must enforce the ASP.NET Core integration workflow before publication.")


def validate_repository_wiring(policy: Policy) -> None:
    template = (ROOT / ".github/PULL_REQUEST_TEMPLATE.md").read_text(encoding="utf-8")
    checklist = [
        "ASP.NET Core integration tests pass",
        "Application startup succeeds",
        "Exception mapping is verified",
        "Production responses hide sensitive details",
        "Current-user behavior is verified",
        "Request-scope isolation is verified",
        "Linux and Windows ASP.NET Core integration results are green",
        "Test authentication remains test-only",
        "ASP.NET Core diagnostics are sanitized",
        "Generated ASP.NET Core integration output is not committed",
    ]
    missing = [item for item in checklist if item not in template]
    if missing:
        fail("Pull-request template is missing ASP.NET Core integration checklist items: " + ", ".join(missing))

    ignore = (ROOT / ".gitignore").read_text(encoding="utf-8")
    for rule in ("TestResults/AspNetCoreIntegration/", "artifacts/aspnetcore-integration/"):
        if rule not in ignore:
            fail(f".gitignore is missing ASP.NET Core integration output rule: {rule}")

    coverage = read_json_object(ROOT / "eng/coverage-policy.json", "coverage policy")
    excluded = coverage.get("excludedTestProjects", [])
    if EXPECTED_PROJECT not in excluded:
        fail("Coverage policy must explicitly exclude the dedicated ASP.NET Core integration test project.")

    if (ROOT / ".git").exists():
        for path in (policy.path, ROOT / "eng/verify-aspnetcore-integration.py"):
            relative = path.relative_to(ROOT).as_posix()
            ignored = subprocess.run(["git", "check-ignore", "--quiet", "--no-index", "--", relative], cwd=ROOT, check=False)
            if ignored.returncode == 0:
                fail(f"ASP.NET Core integration source is ignored by Git: {relative}")
            tracked = subprocess.run(["git", "ls-files", "--error-unmatch", "--", relative], cwd=ROOT, check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            if tracked.returncode != 0:
                fail(f"ASP.NET Core integration source is not tracked by Git: {relative}")


def validate_config() -> Policy:
    policy = load_policy()
    if not policy.require_linux or not policy.require_windows:
        fail("ASP.NET Core integration policy must require both Linux and Windows execution.")
    if not policy.collect_host_diagnostics_on_failure or not policy.scan_uploaded_diagnostics_for_secrets:
        fail("ASP.NET Core integration diagnostics collection and secret scanning must remain enabled.")
    validate_project(policy)
    validate_test_host()
    validate_test_inventory(policy)
    validate_native_aot_smoke()
    validate_workflows()
    validate_repository_wiring(policy)
    return policy


def parse_trx_files(results_directory: Path) -> list[TestResult]:
    trx_files = sorted(results_directory.rglob("*.trx"))
    if not trx_files:
        fail(f"No TRX test-result files were found under {results_directory}.")
    results: list[TestResult] = []
    for trx in trx_files:
        try:
            root = ET.parse(trx).getroot()
        except (OSError, ET.ParseError) as error:
            fail(f"Unable to parse TRX result {trx}: {error}")
        for element in root.iter():
            if element.tag.rsplit("}", 1)[-1] == "UnitTestResult":
                results.append(TestResult(element.attrib.get("testName", "<unnamed>"), element.attrib.get("outcome", "Unknown")))
    if not results:
        fail(f"TRX files under {results_directory} contain no UnitTestResult entries.")
    return results


def scan_for_secrets(paths: Iterable[Path]) -> list[str]:
    leaks: list[str] = []
    header_patterns = [
        (re.compile(r"(?im)^\s*Authorization\s*[:=]\s*(.+)$"), "authorization header"),
        (re.compile(r"(?im)^\s*(?:Cookie|Set-Cookie)\s*[:=]\s*(.+)$"), "authentication cookie"),
        (re.compile(r"(?im)^\s*(?:Password|Pwd)\s*=\s*(.+)$"), "password value"),
    ]
    bearer_pattern = re.compile(r"(?i)\bBearer\s+([^\s]+)")
    credential_pattern = re.compile(r"(?i)tcj-test-secret-[A-Za-z0-9_-]+")
    redacted = {"<redacted>", "***", "*****", "redacted"}
    for path in paths:
        if not path.is_file():
            continue
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for pattern, description in header_patterns:
            if any(match.group(1).strip().strip("\"'").lower() not in redacted for match in pattern.finditer(text)):
                leaks.append(f"{path}: {description}")
        if any(match.group(1).strip().lower() not in redacted for match in bearer_pattern.finditer(text)):
            leaks.append(f"{path}: bearer token")
        if credential_pattern.search(text):
            leaks.append(f"{path}: raw test credential")
    return leaks

def sanitize_generated_file(path: Path) -> None:
    if not path.is_file():
        return
    try:
        text = path.read_text(encoding="utf-8", errors="strict")
    except (OSError, UnicodeError):
        return
    sanitized = re.sub(r"(?i)(Authorization\s*[:=]\s*)[^\r\n]+", r"\1<redacted>", text)
    sanitized = re.sub(r"(?i)Bearer\s+[A-Za-z0-9._~+/-]+=*", "Bearer <redacted>", sanitized)
    sanitized = re.sub(r"(?i)((?:Cookie|Set-Cookie)\s*[:=]\s*)[^\r\n]+", r"\1<redacted>", sanitized)
    sanitized = re.sub(r"(?i)(Password|Pwd)\s*=\s*[^;\r\n]+", r"\1=<redacted>", sanitized)
    sanitized = re.sub(r"(?i)tcj-test-secret-[A-Za-z0-9_-]+", "<redacted>", sanitized)
    if sanitized != text:
        path.write_text(sanitized, encoding="utf-8")


def source_commit() -> str:
    if os.environ.get("GITHUB_SHA", "").strip():
        return os.environ["GITHUB_SHA"].strip()
    try:
        completed = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, check=False, capture_output=True, text=True, timeout=10)
    except (OSError, subprocess.TimeoutExpired):
        return "unknown"
    return completed.stdout.strip() if completed.returncode == 0 else "unknown"


def package_version() -> str:
    root = read_xml(ROOT / "eng/Packaging.props", "packaging properties")
    value = root.findtext("./PropertyGroup/Version")
    return value.strip() if value and value.strip() else "unknown"


def tool_version() -> str:
    try:
        completed = subprocess.run(["dotnet", "--version"], cwd=ROOT, check=False, capture_output=True, text=True, timeout=15)
    except (OSError, subprocess.TimeoutExpired):
        return "unavailable"
    return completed.stdout.strip() if completed.returncode == 0 else "unavailable"


def category_status(results: list[TestResult], keywords: tuple[str, ...]) -> str:
    matching = [item for item in results if any(keyword.lower() in item.name.lower() for keyword in keywords)]
    if not matching:
        return "NOT RUN"
    return "PASS" if all(item.outcome.lower() == "passed" for item in matching) else "FAIL"


def write_summary(output: Path, policy: Policy, results: list[TestResult], operating_system: str, leaks: list[str], errors: list[str]) -> None:
    output.mkdir(parents=True, exist_ok=True)
    passed = sum(item.outcome.lower() == "passed" for item in results)
    failed = sum(item.outcome.lower() == "failed" for item in results)
    skipped = sum(item.outcome.lower() in {"notexecuted", "skipped", "notrun"} for item in results)
    environments = [name for name in ("Development", "Production") if any(name.lower() in item.name.lower() for item in results)]
    summary = {
        "sourceCommitSha": source_commit(),
        "packageVersion": package_version(),
        "dotnetSdkVersion": tool_version(),
        "operatingSystem": operating_system,
        "environmentNamesTested": environments,
        "testCount": len(results),
        "passedTestCount": passed,
        "failedTestCount": failed,
        "skippedTestCount": skipped,
        "startupTestStatus": category_status(results, ("startup", "application_starts", "registration")),
        "dependencyInjectionTestStatus": category_status(results, ("scoped", "transient", "singleton", "framework_registration")),
        "exceptionHandlerTestStatus": category_status(results, ("exception", "failure", "unhandled")),
        "problemDetailsTestStatus": category_status(results, ("problem", "validation", "not_found", "conflict")),
        "currentUserTestStatus": category_status(results, ("current_user", "claims", "identity")),
        "requestScopeTestStatus": category_status(results, ("scoped", "canceled")),
        "secretLeakScanStatus": "PASS" if not leaks else "FAIL",
        "overallStatus": "PASS" if not errors else "FAIL",
        "errors": errors,
    }
    (output / "aspnetcore-integration-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    lines = [
        "# ASP.NET Core integration summary", "", "| Item | Result |", "| --- | --- |",
        f"| Source commit SHA | `{summary['sourceCommitSha']}` |",
        f"| Package version | `{summary['packageVersion']}` |",
        f"| .NET SDK version | `{summary['dotnetSdkVersion']}` |",
        f"| Operating system | `{operating_system}` |",
        f"| Environments tested | `{', '.join(environments)}` |",
        f"| Test count | {len(results)} |", f"| Passed | {passed} |", f"| Failed | {failed} |", f"| Skipped | {skipped} |",
        f"| Startup tests | **{summary['startupTestStatus']}** |",
        f"| Dependency-injection tests | **{summary['dependencyInjectionTestStatus']}** |",
        f"| Exception-handler tests | **{summary['exceptionHandlerTestStatus']}** |",
        f"| Problem Details tests | **{summary['problemDetailsTestStatus']}** |",
        f"| Current-user tests | **{summary['currentUserTestStatus']}** |",
        f"| Request-scope tests | **{summary['requestScopeTestStatus']}** |",
        f"| Secret-leak scan | **{summary['secretLeakScanStatus']}** |",
        f"| Overall | **{summary['overallStatus']}** |",
    ]
    if errors:
        lines.extend(["", "## Failures", ""] + [f"- {error}" for error in errors])
    (output / "ASPNETCORE_INTEGRATION_SUMMARY.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def verify(results_directory: Path, output_directory: Path, operating_system: str) -> None:
    policy = validate_config()
    results = parse_trx_files(results_directory)
    errors: list[str] = []
    if len(results) < policy.minimum_test_count:
        errors.append(f"Test count {len(results)} is below policy minimum {policy.minimum_test_count}.")
    failed = [item for item in results if item.outcome.lower() == "failed"]
    skipped = [item for item in results if item.outcome.lower() in {"notexecuted", "skipped", "notrun"}]
    if failed:
        errors.append(f"{len(failed)} ASP.NET Core integration test(s) failed.")
    if skipped:
        errors.append(f"{len(skipped)} ASP.NET Core integration test(s) were skipped/not executed; critical tests may not be skipped.")

    names = "\n".join(item.name for item in results if item.outcome.lower() == "passed").lower()
    required_runtime = []
    if policy.require_production_environment_tests:
        required_runtime.append(("production-environment", "production"))
    if policy.require_development_environment_tests:
        required_runtime.append(("development-environment", "development"))
    if policy.require_authenticated_request_tests:
        required_runtime.append(("authenticated-request", "authenticated_request"))
    if policy.require_anonymous_request_tests:
        required_runtime.append(("anonymous-request", "anonymous_request"))
    for description, marker in required_runtime:
        if marker not in names:
            errors.append(f"Required {description} test did not pass in the result set.")

    diagnostics = results_directory / "diagnostics"
    if policy.collect_host_diagnostics_on_failure:
        for name in ("host.log", "http.log", "environment-summary.txt"):
            if not (diagnostics / name).is_file():
                errors.append(f"Required sanitized host diagnostic is missing: diagnostics/{name}")

    scan_paths = list(results_directory.rglob("*"))
    leaks = scan_for_secrets(scan_paths) if policy.scan_uploaded_diagnostics_for_secrets else []
    if leaks:
        for path in scan_paths:
            sanitize_generated_file(path)
        errors.append(f"Secret-leak scan found {len(leaks)} suspicious generated artifact(s); affected text files were redacted before upload.")

    logs = output_directory / "logs"
    if diagnostics.exists():
        logs.mkdir(parents=True, exist_ok=True)
        for path in diagnostics.iterdir():
            if path.is_file():
                shutil.copy2(path, logs / path.name)

    write_summary(output_directory, policy, results, operating_system, leaks, errors)
    if errors:
        fail("ASP.NET Core integration verification failed:\n- " + "\n- ".join(errors))


def verify_platforms(input_directory: Path, output_directory: Path) -> None:
    policy = validate_config()
    summaries = []
    for path in input_directory.rglob("aspnetcore-integration-summary.json"):
        summaries.append(read_json_object(path, "ASP.NET Core platform summary"))
    if not summaries:
        fail(f"No ASP.NET Core platform summaries were found under {input_directory}.")
    passed_platforms = {str(item.get("operatingSystem", "")).lower() for item in summaries if item.get("overallStatus") == "PASS"}
    missing = []
    if policy.require_linux and not any("linux" in item for item in passed_platforms):
        missing.append("Linux")
    if policy.require_windows and not any("windows" in item for item in passed_platforms):
        missing.append("Windows")
    commits = {item.get("sourceCommitSha") for item in summaries}
    if len(commits) != 1:
        fail("Cross-platform ASP.NET Core results do not come from one source commit.")
    if missing:
        fail("Missing successful required ASP.NET Core platform execution: " + ", ".join(missing))
    output_directory.mkdir(parents=True, exist_ok=True)
    aggregate = {
        "sourceCommitSha": next(iter(commits)),
        "platforms": sorted(passed_platforms),
        "overallStatus": "PASS",
    }
    (output_directory / "aspnetcore-integration-platform-summary.json").write_text(json.dumps(aggregate, indent=2) + "\n", encoding="utf-8")
    (output_directory / "ASPNETCORE_INTEGRATION_PLATFORM_SUMMARY.md").write_text(
        "# ASP.NET Core cross-platform integration summary\n\n"
        f"- Source commit: `{aggregate['sourceCommitSha']}`\n"
        f"- Platforms: {', '.join(aggregate['platforms'])}\n"
        "- Overall: **PASS**\n",
        encoding="utf-8",
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subs = parser.add_subparsers(dest="command", required=True)
    subs.add_parser("validate-config")
    verify_parser = subs.add_parser("verify")
    verify_parser.add_argument("--results", type=Path, default=DEFAULT_RESULTS)
    verify_parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    verify_parser.add_argument("--platform", default=os.environ.get("RUNNER_OS") or platform.system())
    platforms = subs.add_parser("verify-platforms")
    platforms.add_argument("--input", type=Path, required=True)
    platforms.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.command == "validate-config":
            policy = validate_config()
            print(f"ASP.NET Core integration configuration is valid: minimumTestCount={policy.minimum_test_count}, platforms=Linux+Windows.")
        elif args.command == "verify":
            verify(args.results.resolve(), args.output.resolve(), args.platform)
            print(f"ASP.NET Core integration verification passed: {args.output / 'ASPNETCORE_INTEGRATION_SUMMARY.md'}")
        else:
            verify_platforms(args.input.resolve(), args.output.resolve())
            print(f"ASP.NET Core cross-platform verification passed: {args.output / 'ASPNETCORE_INTEGRATION_PLATFORM_SUMMARY.md'}")
        return 0
    except AspNetCoreIntegrationError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
