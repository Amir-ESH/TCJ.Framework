#!/usr/bin/env python3
"""Run clean-room TCJ package upgrade scenarios against baseline and target packages."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
UPGRADE_ROOT = ROOT / "upgrade-tests"
POLICY_PATH = ROOT / "eng" / "upgrade-compatibility-policy.json"
NUGET_ORG = "https://api.nuget.org/v3/index.json"
WARNING_RE = re.compile(r"(?im)^.*(?:warning\s+[A-Z]{1,5}\d{3,5}|:\s*warning\s+[A-Z]{1,5}\d{3,5}).*$")
SKIP_HASH_PARTS = {"bin", "obj", ".git", "__pycache__"}


class UpgradeError(RuntimeError):
    pass


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (FileNotFoundError, json.JSONDecodeError) as exc:
        raise UpgradeError(f"Cannot read JSON {path}: {exc}") from exc


def semver_key(value: str) -> tuple[Any, ...]:
    core = value.split("+", 1)[0]
    main, sep, prerelease = core.partition("-")
    parts = main.split(".")
    if len(parts) != 3 or not all(p.isdigit() for p in parts):
        raise UpgradeError(f"Unsupported semantic version: {value}")
    major, minor, patch = map(int, parts)
    if not sep:
        return major, minor, patch, 1, ()
    identifiers: list[tuple[int, int, str]] = []
    for item in prerelease.split("."):
        identifiers.append((0, int(item), "") if item.isdigit() else (1, 0, item.casefold()))
    return major, minor, patch, 0, tuple(identifiers)


def source_tree_hash(path: Path) -> str:
    digest = hashlib.sha256()
    for file in sorted(p for p in path.rglob("*") if p.is_file() and not SKIP_HASH_PARTS.intersection(p.relative_to(path).parts)):
        rel = file.relative_to(path).as_posix().encode()
        digest.update(len(rel).to_bytes(4, "big")); digest.update(rel)
        data = file.read_bytes(); digest.update(len(data).to_bytes(8, "big")); digest.update(data)
    return digest.hexdigest()


def normalize_source(value: str) -> str:
    value = value.strip()
    if value.startswith("file://"):
        value = value[7:]
        if os.name == "nt" and value.startswith("/") and len(value) > 2 and value[2] == ":": value = value[1:]
    if value.startswith(("http://", "https://")):
        return value.rstrip("/").casefold()
    path = Path(value)
    if not path.is_absolute(): path = (ROOT / path).resolve()
    return os.path.normcase(os.path.normpath(str(path.resolve())))


def warning_count(output: str) -> int:
    return len(WARNING_RE.findall(output))


def run(command: list[str], cwd: Path, env: dict[str, str], log: Path, *, allow_failure: bool = False) -> tuple[int, str, int]:
    log.parent.mkdir(parents=True, exist_ok=True)
    proc = subprocess.run(command, cwd=cwd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                          text=True, encoding="utf-8", errors="replace", check=False)
    output = proc.stdout or ""
    log.write_text("$ " + " ".join(command) + "\n\n" + output, encoding="utf-8")
    warnings = warning_count(output)
    if proc.returncode and not allow_failure:
        raise UpgradeError(f"Command failed ({proc.returncode}); see {log}")
    return proc.returncode, output, warnings


def clear_outputs(project: Path) -> None:
    for name in ("bin", "obj"):
        path = project.parent / name
        if path.exists(): shutil.rmtree(path)


def make_env(root: Path) -> tuple[dict[str, str], Path]:
    packages, http_cache, home = root / "packages", root / "http-cache", root / "home"
    if root.exists(): shutil.rmtree(root)
    for path in (packages, http_cache, home): path.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env.update({
        "NUGET_PACKAGES": str(packages.resolve()),
        "NUGET_HTTP_CACHE_PATH": str(http_cache.resolve()),
        "DOTNET_CLI_HOME": str(home.resolve()),
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "true",
    })
    return env, packages


def write_target_config(destination: Path, target_packages: Path, published: bool) -> None:
    if published:
        destination.write_text(f'''<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <packageSources><clear/><add key="nuget.org" value="{NUGET_ORG}" protocolVersion="3"/></packageSources>\n  <packageSourceMapping><clear/><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>\n</configuration>\n''', encoding="utf-8")
        return
    destination.write_text(f'''<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <packageSources><clear/><add key="tcj-target" value="{target_packages.resolve()}"/><add key="nuget.org" value="{NUGET_ORG}" protocolVersion="3"/></packageSources>\n  <packageSourceMapping><clear/><packageSource key="tcj-target"><package pattern="TCJ.*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>\n</configuration>\n''', encoding="utf-8")


def parse_assets(path: Path, expected_tcj: list[str], expected_version: str) -> tuple[dict[str, str], dict[str, Any]]:
    data = read_json(path)
    libraries = data.get("libraries", {})
    resolved: dict[str, str] = {}
    for key in libraries:
        package_id, _, version = key.partition("/")
        if package_id.startswith("TCJ."): resolved[package_id] = version
    if set(resolved) != set(expected_tcj):
        raise UpgradeError(f"TCJ package closure mismatch in {path}: expected {expected_tcj}, resolved {resolved}")
    wrong = {k: v for k, v in resolved.items() if v.casefold() != expected_version.casefold()}
    if wrong: raise UpgradeError(f"TCJ package version mismatch: {wrong}; expected {expected_version}")

    graph: dict[str, Any] = {"targetFrameworks": sorted((data.get("targets") or {}).keys()), "packages": {}}
    package_map: dict[str, dict[str, Any]] = {}
    for target_name, target_items in (data.get("targets") or {}).items():
        for key, item in target_items.items():
            package_id, _, version = key.partition("/")
            entry = package_map.setdefault(package_id, {"version": version, "compile": set(), "runtime": set(), "build": set(), "analyzers": set(), "targets": set()})
            entry["targets"].add(target_name)
            for group in ("compile", "runtime", "build", "buildTransitive", "analyzers"):
                values = item.get(group) or {}
                target_group = "build" if group.startswith("build") else group
                if isinstance(values, dict): entry[target_group].update(values.keys())
    for package_id, item in sorted(package_map.items()):
        graph["packages"][package_id] = {key: sorted(value) if isinstance(value, set) else value for key, value in item.items()}
    return resolved, graph


def source_metadata(package_cache: Path, package_id: str, version: str) -> str:
    path = package_cache / package_id.casefold() / version.casefold() / ".nupkg.metadata"
    if not path.is_file(): raise UpgradeError(f"Missing NuGet source metadata: {path}")
    source = str(read_json(path).get("source", "")).strip()
    if not source: raise UpgradeError(f"Empty NuGet source metadata for {package_id} {version}")
    return source


def verify_sources(package_cache: Path, package_ids: list[str], version: str, phase: str, target_packages: Path, published: bool) -> dict[str, str]:
    found: dict[str, str] = {}
    expected = normalize_source(NUGET_ORG if phase == "baseline" or published else str(target_packages.resolve()))
    for package_id in package_ids:
        source = source_metadata(package_cache, package_id, version)
        found[package_id] = source
        if normalize_source(source) != expected:
            raise UpgradeError(f"{phase}: {package_id} {version} restored from {source!r}; expected {expected!r}")
    return found


def dependency_diff(baseline: dict[str, Any], target: dict[str, Any]) -> dict[str, Any]:
    bp, tp = baseline["packages"], target["packages"]
    bkeys, tkeys = set(bp), set(tp)
    added, removed = sorted(tkeys - bkeys), sorted(bkeys - tkeys)
    changes, upgraded, downgraded, asset_changes, removed_runtime = [], [], [], [], []
    for package in sorted(bkeys & tkeys):
        bv, tv = bp[package]["version"], tp[package]["version"]
        if bv != tv:
            record = {"package": package, "from": bv, "to": tv}; changes.append(record)
            try:
                if semver_key(tv) > semver_key(bv): upgraded.append(record)
                elif semver_key(tv) < semver_key(bv): downgraded.append(record)
            except UpgradeError:
                pass
        assets: dict[str, Any] = {}
        for group in ("compile", "runtime", "build", "analyzers"):
            before, after = set(bp[package][group]), set(tp[package][group])
            if before != after:
                assets[group] = {"added": sorted(after-before), "removed": sorted(before-after)}
                if group == "runtime" and before-after: removed_runtime.append({"package": package, "assets": sorted(before-after)})
        if assets: asset_changes.append({"package": package, "assets": assets})
    return {
        "added": added, "removed": removed, "versionChanged": changes, "upgraded": upgraded, "downgraded": downgraded,
        "assetChanges": asset_changes, "removedRuntimeAssets": removed_runtime,
        "targetFrameworkChanged": baseline["targetFrameworks"] != target["targetFrameworks"],
        "baselineTargetFrameworks": baseline["targetFrameworks"], "targetTargetFrameworks": target["targetFrameworks"],
    }


def load_behavior(path: Path) -> dict[str, Any]:
    data = read_json(path)
    if not isinstance(data, dict) or not isinstance(data.get("checks"), dict): raise UpgradeError(f"Invalid behavior JSON: {path}")
    return data


@dataclass
class PhaseResult:
    restore: str = "not-run"
    build: str = "not-run"
    runtime: str = "not-run"
    warningCount: int = 0
    packageVersions: dict[str, str] | None = None
    packageSources: dict[str, str] | None = None
    dependencyGraph: str | None = None
    assetsFile: str | None = None
    behavior: str | None = None
    failure: str | None = None


def run_phase(*, scenario: dict[str, Any], phase: str, version: str, config: Path, env: dict[str, str], package_cache: Path,
              output_root: Path, target_packages: Path, target_published: bool, project_override: Path | None = None) -> tuple[PhaseResult, dict[str, Any] | None, dict[str, Any] | None]:
    project = project_override or ROOT / scenario["project"]
    clear_outputs(project)
    result = PhaseResult(packageVersions={}, packageSources={})
    graph: dict[str, Any] | None = None
    behavior: dict[str, Any] | None = None
    result_dir = output_root / "results" / scenario["name"] / phase
    props = [f"-p:TCJUpgradeVersion={version}", "-p:TCJUpgradeTargetFramework=net10.0", f"-p:RestorePackagesPath={package_cache.resolve()}", "-p:TreatWarningsAsErrors=true"]
    try:
        code, _, warnings = run(["dotnet", "restore", str(project), "--configfile", str(config), "--force", "--no-cache", "--verbosity", "normal", *props], ROOT, env, result_dir / "restore.log")
        result.warningCount += warnings
        if code or warnings: raise UpgradeError(f"restore exited {code} with {warnings} warning(s)")
        result.restore = "pass"
        assets = project.parent / "obj" / "project.assets.json"
        resolved, graph = parse_assets(assets, scenario["packages"], version)
        assets_copy = result_dir / "project.assets.json"; shutil.copy2(assets, assets_copy); result.assetsFile = str(assets_copy.relative_to(output_root))
        result.packageVersions = resolved
        result.packageSources = verify_sources(package_cache, scenario["packages"], version, phase, target_packages, target_published)
        graph_path = result_dir / "dependency-graph.json"; graph_path.parent.mkdir(parents=True, exist_ok=True)
        graph_path.write_text(json.dumps(graph, indent=2, sort_keys=True)+"\n", encoding="utf-8"); result.dependencyGraph = str(graph_path.relative_to(output_root))

        code, _, warnings = run(["dotnet", "build", str(project), "--configuration", "Release", "--no-restore", "--verbosity", "normal", *props], ROOT, env, result_dir / "build.log")
        result.warningCount += warnings
        if code or warnings: raise UpgradeError(f"build exited {code} with {warnings} warning(s)")
        result.build = "pass"

        behavior_path = result_dir / "behavior.json"
        run_env = env.copy(); run_env["TCJ_UPGRADE_BEHAVIOR_PATH"] = str(behavior_path.resolve()); run_env["TCJ_UPGRADE_PHASE"] = phase; run_env["TCJ_UPGRADE_DATA_PATH"] = str((output_root / "results" / scenario["name"] / "persisted-data").resolve())
        code, text, warnings = run(["dotnet", "run", "--project", str(project), "--configuration", "Release", "--no-build", "--no-restore", *props], ROOT, run_env, result_dir / "runtime.log")
        result.warningCount += warnings
        if code or warnings or scenario["expectedOutput"] not in text:
            raise UpgradeError(f"runtime exited {code} with {warnings} warning(s); expected {scenario['expectedOutput']!r}")
        behavior = load_behavior(behavior_path)
        expected = load_behavior(ROOT / scenario["expectedBehavior"])
        if behavior != expected: raise UpgradeError(f"{phase} behavior differs from expected fixture for {scenario['name']}")
        result.runtime = "pass"; result.behavior = str(behavior_path.relative_to(output_root))
        return result, graph, behavior
    except UpgradeError as exc:
        result.failure = str(exc)
        return result, graph, behavior


def manifest_changes_for(manifest: dict[str, Any], scenario: str) -> list[dict[str, Any]]:
    return [item for item in manifest.get("changes", []) if scenario in item.get("affectedScenarios", [])]


def behavior_classification(baseline: dict[str, Any] | None, target: dict[str, Any] | None, manifest_changes: list[dict[str, Any]]) -> str:
    if baseline is not None and target is not None and baseline == target: return "Equivalent"
    if manifest_changes:
        return "Intentional breaking change" if any(item.get("breaking", True) for item in manifest_changes) else "Documented change"
    return "Unexpected regression"


def behavior_changes(baseline: dict[str, Any] | None, target: dict[str, Any] | None) -> list[dict[str, Any]]:
    if baseline is None or target is None: return []
    before = baseline.get("checks", {}); after = target.get("checks", {})
    keys = sorted(set(before) | set(after)); changes = []
    for key in keys:
        if before.get(key) != after.get(key): changes.append({"check": key, "baseline": before.get(key), "target": after.get(key)})
    return changes


def run_guided_migration(scenario: dict[str, Any], changes: list[dict[str, Any]], target_version: str, target_config: Path,
                         target_env: dict[str, str], target_cache: Path, output_root: Path, target_packages: Path, target_published: bool) -> dict[str, Any]:
    source_changes = [item for item in changes if item.get("requiresSourceChange")]
    if not source_changes: return {"required": False, "status": "not-required", "patches": []}
    patch_paths: list[Path] = []
    for item in source_changes:
        mapping = item.get("migrationPatches", {})
        patch = mapping.get(target_version)
        if not patch: return {"required": True, "status": "fail", "failure": f"Missing migration patch for {item.get('id')} and {target_version}", "patches": []}
        patch_paths.append(ROOT / patch)
    migration_root = output_root / "report" / "migration-results" / scenario["name"]
    source = migration_root / "source"
    if migration_root.exists(): shutil.rmtree(migration_root)
    source.parent.mkdir(parents=True, exist_ok=True); shutil.copytree((ROOT / scenario["project"]).parent, source, ignore=shutil.ignore_patterns("bin", "obj"))
    shutil.copy2(UPGRADE_ROOT / "Directory.Build.props", migration_root / "Directory.Build.props")
    try:
        run(["git", "init", "-q"], source, os.environ.copy(), migration_root / "git-init.log")
        run(["git", "add", "."], source, os.environ.copy(), migration_root / "git-add-baseline.log")
        for patch in patch_paths:
            if not patch.is_file(): raise UpgradeError(f"Migration patch missing: {patch}")
            run(["git", "apply", "--check", str(patch)], source, os.environ.copy(), migration_root / f"{patch.stem}-check.log")
            run(["git", "apply", str(patch)], source, os.environ.copy(), migration_root / f"{patch.stem}-apply.log")
        project = source / Path(scenario["project"]).name
        _, change_text, _ = run(["git", "diff", "--numstat"], source, os.environ.copy(), migration_root / "source-changes.log")
        files = []; insertions = deletions = 0
        for line in change_text.splitlines():
            parts = line.split("\t", 2)
            if len(parts) != 3: continue
            added, removed, name = parts; files.append(name)
            if added.isdigit(): insertions += int(added)
            if removed.isdigit(): deletions += int(removed)
        phase, _, _ = run_phase(scenario=scenario, phase="migrated", version=target_version, config=target_config, env=target_env,
                                package_cache=target_cache, output_root=output_root, target_packages=target_packages,
                                target_published=target_published, project_override=project)
        return {"required": True, "status": "pass" if phase.restore == phase.build == phase.runtime == "pass" else "fail",
                "patches": [str(p.relative_to(ROOT)) for p in patch_paths], "sourceChanges": {"files": files, "insertions": insertions, "deletions": deletions}, "phase": asdict(phase)}
    except UpgradeError as exc:
        return {"required": True, "status": "fail", "patches": [str(p) for p in patch_paths], "failure": str(exc)}


def main() -> int:
    policy = read_json(POLICY_PATH); manifest = read_json(ROOT / policy["breakingChangesManifest"])
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline-version", required=True)
    parser.add_argument("--target-version", required=True)
    parser.add_argument("--target-packages", type=Path, default=ROOT / "artifacts/upgrade-compatibility/target/packages")
    parser.add_argument("--target-source-mode", choices=("local", "published"), default="local")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/upgrade-compatibility")
    parser.add_argument("--scenario", action="append")
    parser.add_argument("--commit-sha", default=os.environ.get("GITHUB_SHA", "local"))
    args = parser.parse_args()
    if semver_key(args.target_version) <= semver_key(args.baseline_version): raise UpgradeError("Target version must be newer than baseline version.")

    scenarios = policy["scenarios"]
    if args.scenario:
        wanted = set(args.scenario); known = {s["name"] for s in scenarios}
        unknown = wanted-known
        if unknown: raise UpgradeError(f"Unknown scenario(s): {', '.join(sorted(unknown))}")
        scenarios = [s for s in scenarios if s["name"] in wanted]
    if args.target_source_mode == "local":
        missing = [p for p in policy["requiredPackages"] if not (args.target_packages / f"{p}.{args.target_version}.nupkg").is_file()]
        if missing: raise UpgradeError(f"Target feed is missing package(s): {', '.join(missing)}")

    output_root = args.output.resolve()
    for child in (output_root / "baseline", output_root / "results", output_root / "report"):
        if child.exists(): shutil.rmtree(child)
        child.mkdir(parents=True, exist_ok=True)
    (output_root / "target").mkdir(parents=True, exist_ok=True)
    baseline_env, baseline_cache = make_env(output_root / "baseline" / "nuget")
    target_env, target_cache = make_env(output_root / "target" / "nuget")
    (output_root / "report" / "migration-results").mkdir(parents=True, exist_ok=True)
    baseline_config = UPGRADE_ROOT / "NuGet.Baseline.Config"
    target_config = output_root / "target" / "NuGet.Target.Generated.Config"
    write_target_config(target_config, args.target_packages, args.target_source_mode == "published")

    suite: dict[str, Any] = {"schemaVersion": 1, "sourceCommit": args.commit_sha, "baselineVersion": args.baseline_version,
        "targetVersion": args.target_version, "targetSourceMode": args.target_source_mode, "scenarioCount": len(scenarios),
        "operatingSystem": platform.platform(), "architecture": platform.machine(), "scenarios": []}
    overall = True
    for scenario in scenarios:
        scenario_dir = (ROOT / scenario["project"]).parent
        before_hash = source_tree_hash(scenario_dir)
        baseline_phase, baseline_graph, baseline_behavior = run_phase(scenario=scenario, phase="baseline", version=args.baseline_version,
            config=baseline_config, env=baseline_env, package_cache=baseline_cache, output_root=output_root,
            target_packages=args.target_packages.resolve(), target_published=False)
        target_phase, target_graph, target_behavior = run_phase(scenario=scenario, phase="target", version=args.target_version,
            config=target_config, env=target_env, package_cache=target_cache, output_root=output_root,
            target_packages=args.target_packages.resolve(), target_published=args.target_source_mode == "published")
        after_hash = source_tree_hash(scenario_dir); source_unchanged = before_hash == after_hash
        changes = manifest_changes_for(manifest, scenario["name"])
        classification = behavior_classification(baseline_behavior, target_behavior, changes)
        diff = dependency_diff(baseline_graph, target_graph) if baseline_graph and target_graph else None
        diff_path = output_root / "report" / "dependency-diffs" / f"{scenario['name']}.json"; diff_path.parent.mkdir(parents=True, exist_ok=True)
        if diff is not None: diff_path.write_text(json.dumps(diff, indent=2, sort_keys=True)+"\n", encoding="utf-8")
        observed_behavior_changes = behavior_changes(baseline_behavior, target_behavior)
        behavior_diff = {"scenario": scenario["name"], "classification": classification, "equivalent": baseline_behavior == target_behavior,
                         "changes": observed_behavior_changes, "manifestChangeIds": [c.get("id") for c in changes]}
        behavior_path = output_root / "report" / "behavior-diffs" / f"{scenario['name']}.json"; behavior_path.parent.mkdir(parents=True, exist_ok=True)
        behavior_path.write_text(json.dumps(behavior_diff, indent=2)+"\n", encoding="utf-8")
        migration = run_guided_migration(scenario, changes, args.target_version, target_config, target_env, target_cache, output_root,
                                         args.target_packages.resolve(), args.target_source_mode == "published")
        baseline_ok = baseline_phase.restore == baseline_phase.build == baseline_phase.runtime == "pass"
        target_ok = target_phase.restore == target_phase.build == target_phase.runtime == "pass"
        dependency_ok = diff is not None and not diff["downgraded"] and not diff["removedRuntimeAssets"] and not diff["targetFrameworkChanged"]
        source_change_expected = any(item.get("requiresSourceChange") for item in changes)
        if source_change_expected:
            # A source-changing manifest entry is stale if the direct target path still passes unchanged and remains equivalent.
            direct_contract_ok = (not target_ok or classification != "Equivalent") and migration["status"] == "pass"
        else:
            direct_contract_ok = target_ok and migration["status"] == "not-required" and classification in {"Equivalent", "Compatible improvement", "Documented change", "Intentional breaking change"}
        scenario_ok = baseline_ok and source_unchanged and dependency_ok and direct_contract_ok
        overall &= scenario_ok
        result = {"name": scenario["name"], "project": scenario["project"], "packages": scenario["packages"],
                  "sourceHashBefore": before_hash, "sourceHashAfter": after_hash, "sourceUnchanged": source_unchanged,
                  "baseline": asdict(baseline_phase), "target": asdict(target_phase), "dependencyDiff": diff,
                  "behaviorClassification": classification, "behaviorChanges": observed_behavior_changes, "migration": migration, "overall": "pass" if scenario_ok else "fail"}
        (output_root / "results" / scenario["name"] / "scenario-result.json").write_text(json.dumps(result, indent=2)+"\n", encoding="utf-8")
        suite["scenarios"].append(result)
    suite["overall"] = "pass" if overall else "fail"
    (output_root / "results" / "suite-result.json").write_text(json.dumps(suite, indent=2)+"\n", encoding="utf-8")
    if overall:
        print(f"Upgrade compatibility passed for {len(scenarios)} scenario(s): {args.baseline_version} -> {args.target_version} ({args.target_source_mode}).")
        return 0
    print("Upgrade compatibility failed:", file=sys.stderr)
    for result in suite["scenarios"]:
        if result["overall"] != "pass":
            print(f"  - {result['name']}: baseline={result['baseline']['failure']}, target={result['target']['failure']}, behavior={result['behaviorClassification']}, sourceUnchanged={result['sourceUnchanged']}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    try: raise SystemExit(main())
    except (UpgradeError, OSError, KeyError, json.JSONDecodeError) as exc:
        print(f"Upgrade runner failed: {exc}", file=sys.stderr); raise SystemExit(1)
