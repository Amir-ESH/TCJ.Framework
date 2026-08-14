#!/usr/bin/env python3
"""Restore, build, and execute clean package-only TCJ consumers."""
from __future__ import annotations
import argparse, json, os, platform as platform_module, re, shutil, subprocess, sys
from urllib.parse import unquote, urlparse
from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Any
ROOT = Path(__file__).resolve().parents[2]
COMPATIBILITY_ROOT = ROOT / "compatibility"
POLICY_PATH = ROOT / "eng" / "compatibility-policy.json"
WARNING_PATTERN = re.compile(r"(?:^|:)\s*warning\s+[A-Z]{2,}\d+\s*:", re.IGNORECASE)
TCJ_LIBRARY_PATTERN = re.compile(r"^(TCJ\.[^/]+)/(.+)$", re.IGNORECASE)
class CompatibilityError(RuntimeError): pass
@dataclass
class ConsumerResult:
    name: str; project: str; expectedPackages: list[str]
    restoreStatus: str = "not-run"; buildStatus: str = "not-run"; runtimeStatus: str = "not-run"
    packageVersionStatus: str = "not-run"; packageSourceStatus: str = "not-run"; warningCount: int = 0
    resolvedPackages: dict[str, str] | None = None; output: str = ""; failure: str | None = None

def load_policy() -> dict[str, Any]:
    try: value = json.loads(POLICY_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error: raise CompatibilityError(f"Unable to read compatibility policy: {error}") from error
    if not isinstance(value, dict): raise CompatibilityError("Compatibility policy must contain a JSON object.")
    return value

def clear_consumer_outputs(project: Path) -> None:
    for name in ("bin", "obj"):
        path = project.parent / name
        if path.exists(): shutil.rmtree(path)

def run_command(command: list[str], log_path: Path, env: dict[str, str]) -> tuple[int, str, int]:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    process = subprocess.run(command, cwd=ROOT, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                             text=True, encoding="utf-8", errors="replace", check=False)
    output = process.stdout or ""
    log_path.write_text(output, encoding="utf-8")
    warning_count = sum(1 for line in output.splitlines() if WARNING_PATTERN.search(line))
    return process.returncode, output, warning_count

def parse_assets(project: Path, expected_version: str, expected_packages: list[str], isolated_packages: Path) -> tuple[dict[str, str], str, str]:
    assets_path = project.parent / "obj" / "project.assets.json"
    if not assets_path.is_file(): raise CompatibilityError(f"Missing project.assets.json for {project}.")
    data = json.loads(assets_path.read_text(encoding="utf-8"))
    resolved: dict[str, str] = {}
    for library_name, metadata in data.get("libraries", {}).items():
        match = TCJ_LIBRARY_PATTERN.match(library_name)
        if not match: continue
        package_id, version = match.groups()
        if not isinstance(metadata, dict) or metadata.get("type") != "package":
            raise CompatibilityError(f"{project.name} resolved {package_id} as a non-package library.")
        resolved[package_id] = version
    expected_set, actual_set = set(expected_packages), set(resolved)
    if actual_set != expected_set:
        raise CompatibilityError(f"{project.name} TCJ package closure mismatch: expected {sorted(expected_set)}, found {sorted(actual_set)}.")
    wrong_versions = {package_id: version for package_id, version in resolved.items() if version != expected_version}
    if wrong_versions:
        raise CompatibilityError(f"{project.name} resolved unexpected TCJ versions: {wrong_versions}; expected {expected_version}.")
    restore = data.get("project", {}).get("restore", {})
    project_references = restore.get("projectReferences") or {}
    if project_references: raise CompatibilityError(f"{project.name} contains restored project references: {sorted(project_references)}")
    normalized_folders = {os.path.normcase(os.path.normpath(path.rstrip("/\\"))) for path in data.get("packageFolders", {})}
    expected_folder = os.path.normcase(os.path.normpath(str(isolated_packages.resolve())))
    if expected_folder not in normalized_folders:
        raise CompatibilityError(f"{project.name} did not use the isolated NUGET_PACKAGES directory {isolated_packages}.")
    return resolved, "pass", "not-verified"

def source_path_candidates(value: str) -> set[str]:
    parsed = urlparse(value)
    if parsed.scheme.casefold() == "file":
        raw = unquote(parsed.path)
        if os.name == "nt" and raw.startswith("/") and len(raw) > 2 and raw[2] == ":":
            raw = raw[1:]
        path = Path(raw)
    else:
        path = Path(value)
    bases = (ROOT, COMPATIBILITY_ROOT) if not path.is_absolute() else (None,)
    candidates: set[str] = set()
    for base in bases:
        resolved = path.resolve() if base is None else (base / path).resolve()
        candidates.add(os.path.normcase(os.path.normpath(str(resolved))))
    return candidates

def verify_package_sources(package_cache: Path, package_ids: list[str], version: str, source_mode: str, local_packages: Path) -> str:
    for package_id in package_ids:
        metadata_path = package_cache / package_id.casefold() / version.casefold() / ".nupkg.metadata"
        if not metadata_path.is_file():
            raise CompatibilityError(f"Missing NuGet source metadata for {package_id} {version}: {metadata_path}")
        metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        source = str(metadata.get("source", "")).strip()
        if not source:
            raise CompatibilityError(f"NuGet source metadata is empty for {package_id} {version}.")
        if source_mode == "local":
            if source.startswith(("http://", "https://")):
                raise CompatibilityError(f"{package_id} {version} restored from unexpected remote source {source!r}.")
            actual_candidates = source_path_candidates(source)
            expected = os.path.normcase(os.path.normpath(str(local_packages.resolve())))
            if expected not in actual_candidates:
                raise CompatibilityError(f"{package_id} {version} restored from {source!r}; expected local source {local_packages.resolve()}.")
        else:
            if source.rstrip("/").casefold() != "https://api.nuget.org/v3/index.json".casefold():
                raise CompatibilityError(f"{package_id} {version} restored from {source!r}; expected NuGet.org.")
    return "pass"

def normalize_architecture(value: str) -> str:
    normalized = value.strip().casefold().replace("-", "_")
    if normalized in {"x86_64", "amd64", "x64"}: return "x64"
    if normalized in {"arm64", "aarch64"}: return "arm64"
    return normalized or "unknown"

def get_dotnet_sdk_version(env: dict[str, str]) -> str:
    process = subprocess.run(["dotnet", "--version"], cwd=ROOT, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                             text=True, encoding="utf-8", errors="replace", check=False)
    value = (process.stdout or "").strip()
    if process.returncode != 0 or not value:
        raise CompatibilityError(f"Unable to determine .NET SDK version: {value or 'dotnet --version failed'}")
    return value.splitlines()[-1].strip()

def ensure_local_packages(packages: Path, version: str, required_packages: list[str]) -> None:
    if not packages.is_dir(): raise CompatibilityError(f"Local package feed does not exist: {packages}")
    missing = [package_id for package_id in required_packages if not (packages / f"{package_id}.{version}.nupkg").is_file()]
    if missing: raise CompatibilityError(f"Local package feed is missing: {', '.join(missing)}")

def main() -> int:
    policy = load_policy()
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--platform", required=True, choices=policy["requiredOperatingSystems"])
    parser.add_argument("--configuration", default="Release", choices=policy["requiredConfigurations"])
    parser.add_argument("--target-framework", default=policy["supportedTargetFrameworks"][0], choices=policy["supportedTargetFrameworks"])
    parser.add_argument("--source-mode", choices=("local", "published"), default="local")
    parser.add_argument("--packages", type=Path, default=ROOT / "artifacts" / "compatibility" / "packages")
    parser.add_argument("--results", type=Path, default=ROOT / "artifacts" / "compatibility" / "results")
    parser.add_argument("--consumer", action="append", dest="consumers")
    args = parser.parse_args()
    consumers = policy["consumers"]
    requested = set(args.consumers or [])
    if requested:
        known = {item["name"] for item in consumers}; unknown = sorted(requested.difference(known))
        if unknown: raise CompatibilityError(f"Unknown consumer(s): {', '.join(unknown)}")
        consumers = [item for item in consumers if item["name"] in requested]
    if args.source_mode == "local":
        ensure_local_packages(args.packages.resolve(), args.version, policy["requiredPackages"]); config_file = COMPATIBILITY_ROOT / "NuGet.Config"
    else: config_file = COMPATIBILITY_ROOT / "NuGet.Published.Config"
    result_root = args.results.resolve() / args.platform
    if result_root.exists(): shutil.rmtree(result_root)
    result_root.mkdir(parents=True)
    isolated_root = ROOT / "artifacts" / "compatibility" / "nuget" / args.platform
    if isolated_root.exists(): shutil.rmtree(isolated_root)
    package_cache, http_cache, temp_home = isolated_root / "packages", isolated_root / "http-cache", isolated_root / "home"
    for path in (package_cache, http_cache, temp_home): path.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy(); env.update({"NUGET_PACKAGES": str(package_cache.resolve()), "NUGET_HTTP_CACHE_PATH": str(http_cache.resolve()),
        "DOTNET_CLI_HOME": str(temp_home.resolve()), "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "true"})
    sdk_version = get_dotnet_sdk_version(env)
    consumer_results: list[ConsumerResult] = []
    for consumer in consumers:
        project = ROOT / consumer["project"]; clear_consumer_outputs(project)
        result = ConsumerResult(name=consumer["name"], project=consumer["project"], expectedPackages=list(consumer["packages"]), resolvedPackages={})
        consumer_results.append(result)
        common_properties = [f"-p:TCJCompatibilityVersion={args.version}", f"-p:TCJCompatibilityTargetFramework={args.target_framework}",
                             f"-p:RestorePackagesPath={package_cache.resolve()}", "-p:TreatWarningsAsErrors=true"]
        restore_command = ["dotnet", "restore", str(project), "--configfile", str(config_file), "--force", "--no-cache", "--verbosity", "normal", *common_properties]
        code, _, warnings = run_command(restore_command, result_root / "restore" / f"{consumer['name']}.log", env); result.warningCount += warnings
        if code != 0 or warnings: result.restoreStatus = "fail"; result.failure = f"restore exited {code} with {warnings} warning(s)"; continue
        result.restoreStatus = "pass"
        try:
            resolved, version_status, _ = parse_assets(project, args.version, list(consumer["packages"]), package_cache)
            result.resolvedPackages = resolved; result.packageVersionStatus = version_status
            result.packageSourceStatus = verify_package_sources(package_cache, list(consumer["packages"]), args.version, args.source_mode, args.packages.resolve())
        except (CompatibilityError, OSError, json.JSONDecodeError) as error:
            result.failure = str(error); result.packageVersionStatus = "fail"; result.packageSourceStatus = "fail"; continue
        build_command = ["dotnet", "build", str(project), "--configuration", args.configuration, "--no-restore", "--verbosity", "normal", *common_properties]
        code, _, warnings = run_command(build_command, result_root / "build" / f"{consumer['name']}.log", env); result.warningCount += warnings
        if code != 0 or warnings: result.buildStatus = "fail"; result.failure = f"build exited {code} with {warnings} warning(s)"; continue
        result.buildStatus = "pass"
        run_args = ["dotnet", "run", "--project", str(project), "--configuration", args.configuration, "--no-build", "--no-restore", *common_properties]
        code, output, warnings = run_command(run_args, result_root / "runtime" / f"{consumer['name']}.log", env); result.warningCount += warnings; result.output = output.strip()
        if code != 0 or warnings or consumer["expectedOutput"] not in output:
            result.runtimeStatus = "fail"; result.failure = f"runtime exited {code} with {warnings} warning(s); expected output {consumer['expectedOutput']!r}"; continue
        result.runtimeStatus = "pass"
    overall = all(item.restoreStatus == item.buildStatus == item.runtimeStatus == "pass" and item.packageVersionStatus == item.packageSourceStatus == "pass" and item.warningCount == 0 for item in consumer_results)
    payload = {"schemaVersion": 1, "sourceMode": args.source_mode, "platform": args.platform, "operatingSystem": platform_module.platform(),
        "architecture": normalize_architecture(platform_module.machine()), "dotnetSdkVersion": sdk_version, "configuration": args.configuration, "targetFramework": args.target_framework, "packageVersion": args.version,
        "consumerCount": len(consumer_results), "restoreSuccessCount": sum(item.restoreStatus == "pass" for item in consumer_results),
        "buildSuccessCount": sum(item.buildStatus == "pass" for item in consumer_results), "runtimeSuccessCount": sum(item.runtimeStatus == "pass" for item in consumer_results),
        "warningCount": sum(item.warningCount for item in consumer_results), "consumers": [asdict(item) for item in consumer_results], "overall": "pass" if overall else "fail"}
    (result_root / "platform-result.json").write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    if overall:
        print(f"Compatibility consumers passed on {args.platform}: {len(consumer_results)} restore/build/runtime paths, version {args.version}."); return 0
    print(f"Compatibility consumers failed on {args.platform}:", file=sys.stderr)
    for item in consumer_results:
        if item.failure: print(f"  - {item.name}: {item.failure}", file=sys.stderr)
    return 1
if __name__ == "__main__":
    try: raise SystemExit(main())
    except (CompatibilityError, OSError, KeyError, json.JSONDecodeError) as error:
        print(f"Compatibility runner failed: {error}", file=sys.stderr); raise SystemExit(1)
