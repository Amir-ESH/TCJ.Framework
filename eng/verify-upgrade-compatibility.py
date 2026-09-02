#!/usr/bin/env python3
"""Validate TCJ package upgrade compatibility policy, migration guidance, and run results."""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any, Iterable

from sbom_common import get_release_package_ids

ROOT = Path(__file__).resolve().parents[1]
POLICY_REL = Path("eng/upgrade-compatibility-policy.json")
NUGET_ORG = "https://api.nuget.org/v3/index.json"
ALLOWED_CLASSIFICATIONS = {"Equivalent", "Compatible improvement", "Documented change", "Intentional breaking change"}


class VerificationError(RuntimeError):
    pass


def fail(message: str) -> None:
    raise VerificationError(message)


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        fail(f"Required file is missing: {path}")
    except json.JSONDecodeError as exc:
        fail(f"Malformed JSON in {path}: {exc}")


def semver_key(value: str) -> tuple[Any, ...]:
    core = value.split("+", 1)[0]
    main, sep, prerelease = core.partition("-")
    parts = main.split(".")
    if len(parts) != 3 or not all(item.isdigit() for item in parts):
        fail(f"Unsupported semantic version: {value}")
    major, minor, patch = map(int, parts)
    if not sep:
        return major, minor, patch, 1, ()
    ids: list[tuple[int, int, str]] = []
    for item in prerelease.split("."):
        ids.append((0, int(item), "") if item.isdigit() else (1, 0, item.casefold()))
    return major, minor, patch, 0, tuple(ids)


def xml_local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def parse_project(path: Path) -> set[str]:
    try:
        root = ET.parse(path).getroot()
    except (FileNotFoundError, ET.ParseError) as exc:
        fail(f"Invalid upgrade scenario project {path}: {exc}")
    tcj: set[str] = set()
    for item in root.iter():
        name = xml_local(item.tag)
        if name == "ProjectReference":
            fail(f"Upgrade scenario must not use ProjectReference: {path}")
        if name != "PackageReference":
            continue
        package = item.attrib.get("Include") or item.attrib.get("Update") or ""
        if package.startswith("TCJ."):
            tcj.add(package)
            version = item.attrib.get("Version") or next(
                (c.text for c in item if xml_local(c.tag) == "Version"), None
            )
            if version != "$(TCJUpgradeVersion)":
                fail(f"{path}: {package} must use $(TCJUpgradeVersion).")
    text = path.read_text(encoding="utf-8")
    if re.search(r"(?:^|[\\/])src[\\/]TCJ\.", text, re.I):
        fail(f"Upgrade scenario references production source: {path}")
    return tcj


def validate_nuget_config(path: Path, target: bool = False) -> None:
    try:
        root = ET.parse(path).getroot()
    except (FileNotFoundError, ET.ParseError) as exc:
        fail(f"Invalid NuGet config {path}: {exc}")
    sources: list[tuple[str, str]] = []
    mappings: dict[str, list[str]] = {}
    for node in root.iter():
        if xml_local(node.tag) == "packageSources":
            sources = [
                (c.attrib.get("key", ""), c.attrib.get("value", ""))
                for c in node
                if xml_local(c.tag) == "add"
            ]
        if xml_local(node.tag) == "packageSourceMapping":
            for source in node:
                if xml_local(source.tag) == "packageSource":
                    mappings[source.attrib.get("key", "")] = [
                        c.attrib.get("pattern", "")
                        for c in source
                        if xml_local(c.tag) == "package"
                    ]
    if target:
        if (
            len(sources) != 2
            or sources[0][0] != "tcj-target"
            or "artifacts/upgrade-compatibility/target/packages"
            not in sources[0][1].replace("\\", "/")
        ):
            fail("Target NuGet config must put the local candidate feed first.")
        if sources[1] != ("nuget.org", NUGET_ORG):
            fail("Target NuGet config must keep NuGet.org as the public dependency source.")
        if mappings.get("tcj-target") != ["TCJ.*"] or mappings.get("nuget.org") != ["*"]:
            fail("Target NuGet config must map TCJ.* exclusively to tcj-target and public dependencies to NuGet.org.")
    else:
        if sources != [("nuget.org", NUGET_ORG)]:
            fail("Baseline NuGet config must use only NuGet.org.")
        if mappings.get("nuget.org") != ["*"]:
            fail("Baseline NuGet config must map all packages to NuGet.org.")


def check_gitignore(root: Path, critical: Iterable[Path]) -> None:
    text = (root / ".gitignore").read_text(encoding="utf-8") if (root / ".gitignore").is_file() else ""
    rules = [line.strip() for line in text.splitlines() if line.strip() and not line.lstrip().startswith("#")]
    dangerous = {
        "upgrade-tests/",
        "upgrade-tests/**",
        "eng/*.json",
        "eng/**",
        "docs/migrations/",
        "docs/migrations/**",
    }
    if any(rule in dangerous for rule in rules if not rule.startswith("!")):
        fail(".gitignore hides upgrade policy, scenarios, manifest, or migration documentation.")
    if (root / ".git").exists():
        for rel in critical:
            ignored = subprocess.run(["git", "check-ignore", "-q", str(rel)], cwd=root, check=False)
            if ignored.returncode == 0:
                fail(f"Required upgrade source is ignored by Git: {rel}")


def markdown_anchor_exists(text: str, anchor: str) -> bool:
    anchor = anchor.strip().lstrip("#").casefold()
    for line in text.splitlines():
        if not line.startswith("#"):
            continue
        heading = line.lstrip("#").strip().casefold()
        slug = re.sub(r"[^a-z0-9\- _]", "", heading).replace(" ", "-")
        slug = re.sub(r"-+", "-", slug)
        if slug == anchor:
            return True
    return False


def runtime_release_packages(metadata: dict[str, Any]) -> set[str]:
    try:
        return set(get_release_package_ids(metadata, "runtime"))
    except ValueError as error:
        fail(str(error))


def scenario_names(items: Any, description: str, *, minimum: int) -> list[str]:
    if not isinstance(items, list) or len(items) < minimum:
        fail(f"{description} must contain at least {minimum} scenario(s).")
    names = [item.get("name") for item in items if isinstance(item, dict)]
    if len(names) != len(items) or any(not isinstance(name, str) or not name for name in names):
        fail(f"{description} must contain named scenario objects.")
    if len(names) != len(set(names)):
        fail(f"{description} scenario names must be unique.")
    return names


def validate_scenario(
    root: Path,
    scenario: dict[str, Any],
    *,
    target_packages: set[str],
    allow_outbox: bool,
) -> tuple[set[str], list[Path]]:
    for key in ("name", "project", "packages", "expectedBehavior", "expectedOutput"):
        if key not in scenario:
            fail(f"Upgrade scenario is missing {key}: {scenario}")
    project_rel = Path(scenario["project"])
    expected_rel = Path(scenario["expectedBehavior"])
    actual = parse_project(root / project_rel)
    if actual != set(scenario["packages"]):
        fail(
            f"Scenario {scenario['name']} package references do not match policy: "
            f"{actual} != {scenario['packages']}"
        )
    unknown = actual - target_packages
    if unknown:
        fail(f"Scenario {scenario['name']} references unknown target packages: {', '.join(sorted(unknown))}")
    if not (root / expected_rel).is_file():
        fail(f"Missing expected behavior fixture: {expected_rel}")
    behavior = read_json(root / expected_rel)
    if (
        behavior.get("scenario") != scenario["name"]
        or not isinstance(behavior.get("checks"), dict)
        or not behavior["checks"]
    ):
        fail(f"Invalid expected behavior fixture for {scenario['name']}")
    program_path = (root / project_rel).parent / "Program.cs"
    if not program_path.is_file():
        fail(f"Upgrade scenario Program.cs is missing: {program_path.relative_to(root)}")
    program_text = program_path.read_text(encoding="utf-8")
    if not allow_outbox and ("AddTcjOutbox" in program_text or "AddTcjOutboxProcessor" in program_text):
        fail(
            f"Existing baseline/target scenario {scenario['name']} must remain outbox-disabled "
            "so direct-upgrade behavior proves explicit opt-in compatibility."
        )
    return actual, [project_rel, expected_rel, program_path.relative_to(root)]


def load_policy(root: Path = ROOT) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any], dict[str, Any]]:
    policy = read_json(root / POLICY_REL)
    if not isinstance(policy, dict):
        fail("Upgrade compatibility policy must be an object.")
    required = {
        "schemaVersion",
        "baselineMetadata",
        "targetMetadata",
        "breakingChangesManifest",
        "migrationGuide",
        "configuration",
        "targetFramework",
        "minimumScenarioCount",
        "minimumTargetOnlyScenarioCount",
        "requiredPackages",
        "targetOnlyPackages",
        "requireBaselineRestore",
        "requireBaselineBuild",
        "requireBaselineRun",
        "requireTargetRestore",
        "requireTargetBuild",
        "requireTargetRun",
        "requirePackageOnlyReferences",
        "requireSourceTreeHash",
        "requireDependencyDiff",
        "requireBehaviorComparison",
        "requireMigrationGuideForBreakingChanges",
        "requireApprovedBreakingChanges",
        "requireExplicitMigrationPatches",
        "failOnUndocumentedBehaviorChange",
        "failOnDependencyDowngrade",
        "failOnWarnings",
        "requireOutboxOptInCompatibility",
        "requireOutboxSchemaMigrationGuidance",
        "requireOutboxEventNameMigrationGuidance",
        "publishedScenarios",
        "publishedTargetOnlyScenarios",
        "scenarios",
        "targetOnlyScenarios",
    }
    missing = sorted(required - set(policy))
    if missing:
        fail(f"Upgrade policy is missing fields: {', '.join(missing)}")
    if policy["schemaVersion"] != 1:
        fail("Unsupported upgrade policy schemaVersion.")
    if policy["configuration"] != "Release" or policy["targetFramework"] != "net10.0":
        fail("Initial upgrade matrix must validate Release/net10.0.")

    baseline = read_json(root / policy["baselineMetadata"])
    target = read_json(root / policy["targetMetadata"])
    manifest = read_json(root / policy["breakingChangesManifest"])
    baseline_packages = runtime_release_packages(baseline)
    target_packages = runtime_release_packages(target)
    target_only_packages = set(policy["targetOnlyPackages"])

    if not baseline_packages.issubset(target_packages):
        removed = sorted(baseline_packages - target_packages)
        fail(f"Target runtime package set removed published package IDs: {', '.join(removed)}")
    introduced = target_packages - baseline_packages
    if target_only_packages != introduced:
        fail(
            "targetOnlyPackages must exactly describe runtime packages introduced after the published baseline: "
            f"expected {sorted(introduced)}, found {sorted(target_only_packages)}."
        )
    if set(policy["requiredPackages"]) != target_packages:
        fail("Upgrade policy requiredPackages must exactly match target runtime release packages.")

    boolean_guarantees = [
        "requireBaselineRestore",
        "requireBaselineBuild",
        "requireBaselineRun",
        "requireTargetRestore",
        "requireTargetBuild",
        "requireTargetRun",
        "requirePackageOnlyReferences",
        "requireSourceTreeHash",
        "requireDependencyDiff",
        "requireBehaviorComparison",
        "requireMigrationGuideForBreakingChanges",
        "requireApprovedBreakingChanges",
        "requireExplicitMigrationPatches",
        "failOnUndocumentedBehaviorChange",
        "failOnDependencyDowngrade",
        "failOnWarnings",
        "requireOutboxOptInCompatibility",
        "requireOutboxSchemaMigrationGuidance",
        "requireOutboxEventNameMigrationGuidance",
    ]
    for key in boolean_guarantees:
        if policy.get(key) is not True:
            fail(f"{key} must be true.")

    for metadata, name in ((baseline, "baseline"), (target, "target")):
        if not isinstance(metadata, dict) or not metadata.get("version"):
            fail(f"{name} release metadata does not define a version.")
    if semver_key(target["version"]) <= semver_key(baseline["version"]):
        fail("Target release version must be newer than the published baseline.")
    if (
        manifest.get("schemaVersion") != 1
        or manifest.get("fromVersion") != baseline["version"]
        or manifest.get("toVersion") != target["version"]
    ):
        fail("Breaking-change manifest versions must match release metadata.")
    if manifest.get("migrationGuide") != policy["migrationGuide"]:
        fail("Breaking-change manifest must point at the policy migration guide.")

    workspace_required = [
        Path("upgrade-tests/README.md"),
        Path("upgrade-tests/Directory.Build.props"),
        Path("upgrade-tests/Directory.Packages.props"),
        Path("upgrade-tests/NuGet.Baseline.Config"),
        Path("upgrade-tests/NuGet.Target.Config"),
        Path("upgrade-tests/TCJ.UpgradeTests.slnx"),
        Path("upgrade-tests/scripts/run-upgrade-tests.py"),
        Path("docs/package-upgrade-testing.md"),
    ]
    missing_workspace = [str(rel) for rel in workspace_required if not (root / rel).is_file()]
    if missing_workspace:
        fail(f"Upgrade compatibility workspace is incomplete: {', '.join(missing_workspace)}")

    scenarios = policy["scenarios"]
    target_only_scenarios = policy["targetOnlyScenarios"]
    names = scenario_names(scenarios, "Direct upgrade scenarios", minimum=max(6, int(policy["minimumScenarioCount"])))
    target_only_names = scenario_names(
        target_only_scenarios,
        "Target-only package introduction scenarios",
        minimum=max(1, int(policy["minimumTargetOnlyScenarioCount"])),
    )
    if set(names) & set(target_only_names):
        fail("Direct and target-only upgrade scenario names must not overlap.")

    try:
        solution_root = ET.parse(root / "upgrade-tests/TCJ.UpgradeTests.slnx").getroot()
    except ET.ParseError as exc:
        fail(f"Invalid upgrade solution: {exc}")
    solution_projects = {
        node.attrib.get("Path", "").replace("\\", "/")
        for node in solution_root.iter()
        if xml_local(node.tag) == "Project"
    }
    expected_solution_projects = {
        str(Path(s["project"]).relative_to("upgrade-tests")).replace("\\", "/")
        for s in [*scenarios, *target_only_scenarios]
    }
    if solution_projects != expected_solution_projects:
        fail(
            "Upgrade solution project set does not match policy: "
            f"{solution_projects} != {expected_solution_projects}"
        )

    if set(policy["publishedScenarios"]) != {"CoreConsumer", "AspNetCoreConsumer", "FullStackConsumer"}:
        fail("Published upgrade validation must reuse Core, ASP.NET Core, and full-stack direct-upgrade scenarios.")
    if set(policy["publishedTargetOnlyScenarios"]) != set(target_only_names):
        fail("Every target-only package introduction scenario must run against published packages after release.")

    direct_coverage: set[str] = set()
    target_only_coverage: set[str] = set()
    critical: list[Path] = [
        POLICY_REL,
        Path(policy["breakingChangesManifest"]),
        Path(policy["migrationGuide"]),
        *workspace_required,
    ]
    for scenario in scenarios:
        actual, files = validate_scenario(root, scenario, target_packages=target_packages, allow_outbox=False)
        if not actual.issubset(baseline_packages):
            fail(
                f"Direct upgrade scenario {scenario['name']} references target-only package(s): "
                f"{', '.join(sorted(actual - baseline_packages))}"
            )
        direct_coverage.update(actual)
        critical.extend(files)
    if direct_coverage != baseline_packages:
        fail(
            "Direct upgrade scenarios must cover every package present in the published baseline: "
            f"expected {sorted(baseline_packages)}, found {sorted(direct_coverage)}."
        )

    for scenario in target_only_scenarios:
        actual, files = validate_scenario(root, scenario, target_packages=target_packages, allow_outbox=True)
        introduced_in_scenario = actual & target_only_packages
        if not introduced_in_scenario:
            fail(f"Target-only scenario {scenario['name']} must reference at least one target-only package.")
        target_only_coverage.update(introduced_in_scenario)
        critical.extend(files)
    if target_only_coverage != target_only_packages:
        fail(
            "Target-only scenarios do not cover every newly introduced runtime package: "
            f"expected {sorted(target_only_packages)}, found {sorted(target_only_coverage)}."
        )

    validate_nuget_config(root / "upgrade-tests/NuGet.Baseline.Config", target=False)
    validate_nuget_config(root / "upgrade-tests/NuGet.Target.Config", target=True)
    critical.extend(
        [
            Path("upgrade-tests/NuGet.Baseline.Config"),
            Path("upgrade-tests/NuGet.Target.Config"),
            Path("upgrade-tests/TCJ.UpgradeTests.slnx"),
        ]
    )

    guide_path = root / policy["migrationGuide"]
    if not guide_path.is_file():
        fail(f"Migration guide is missing: {policy['migrationGuide']}")
    guide = guide_path.read_text(encoding="utf-8")
    required_guide_terms = [
        baseline["version"],
        target["version"],
        "changed dependencies",
        "changed defaults",
        "configuration",
        "dependency injection",
        "middleware",
        "entity framework core",
        "sql server",
        "behavior",
        "rollback",
        "known limitations",
        "no source changes",
        "TCJ.Messaging",
        "new package",
    ]
    lower_guide = guide.casefold()
    missing_terms = [term for term in required_guide_terms if term.casefold() not in lower_guide]
    if missing_terms:
        fail(f"Migration guide is incomplete; missing topics: {', '.join(missing_terms)}")

    required_outbox_guide = [
        "transactional outbox is opt-in",
        "TCJ_OutboxMessages",
        "consumer-controlled migration",
        "event type names are compatibility contracts",
        "AddTcjOutbox",
    ]
    missing_outbox_guide = [term for term in required_outbox_guide if term.casefold() not in lower_guide]
    if missing_outbox_guide:
        fail(f"Migration guide is missing Step 44 outbox upgrade guidance: {', '.join(missing_outbox_guide)}")
    outbox_docs = (root / "docs/outbox.md").read_text(encoding="utf-8")
    for term in ("at-least-once", "consumer-controlled migration", "event names", "AddTcjOutbox"):
        if term.casefold() not in outbox_docs.casefold():
            fail(f"docs/outbox.md is missing upgrade compatibility guidance for {term!r}.")

    changes = manifest.get("changes")
    if not isinstance(changes, list):
        fail("breaking-changes.json changes must be an array.")
    ids: set[str] = set()
    all_scenario_names = set(names) | set(target_only_names)
    for change in changes:
        fields = {
            "id",
            "package",
            "category",
            "summary",
            "reason",
            "migrationGuideSection",
            "affectedScenarios",
            "approved",
            "approvedBy",
            "trackingUrl",
        }
        missing_change = fields - set(change)
        if missing_change:
            fail(f"Breaking-change entry is missing fields: {', '.join(sorted(missing_change))}")
        if change["id"] in ids:
            fail(f"Duplicate breaking-change id: {change['id']}")
        ids.add(change["id"])
        if change["package"] not in target_packages:
            fail(f"Unknown breaking-change package: {change['package']}")
        if (
            change["approved"] is not True
            or not isinstance(change["approvedBy"], str)
            or not change["approvedBy"].strip()
        ):
            fail(f"Breaking change must include explicit maintainer approval metadata: {change['id']}")
        if not set(change["affectedScenarios"]).issubset(all_scenario_names) or not change["affectedScenarios"]:
            fail(f"Stale or missing affected scenario in {change['id']}")
        if not markdown_anchor_exists(guide, change["migrationGuideSection"]):
            fail(f"Migration guide section not found for {change['id']}: {change['migrationGuideSection']}")
        if not re.match(
            r"^https://github\.com/Amir-ESH/TCJ\.Framework/(?:issues|pull)/\d+$",
            str(change["trackingUrl"]),
        ):
            fail(f"Breaking change {change['id']} must link to a repository issue or PR.")
        if change.get("requiresSourceChange"):
            mapping = change.get("migrationPatches")
            patch = mapping.get(target["version"]) if isinstance(mapping, dict) else None
            if not patch or not (root / patch).is_file():
                fail(f"Breaking change {change['id']} requires an explicit target-version migration patch.")
            critical.append(Path(patch))

    check_gitignore(root, critical)
    return policy, baseline, target, manifest


def validate_repository_wiring(root: Path = ROOT) -> None:
    requirements = {
        ".github/workflows/ci.yml": ["verify-upgrade-compatibility.py validate-config"],
        ".github/workflows/upgrade-compatibility.yml": [
            "name: Package upgrade compatibility",
            "run-upgrade-tests.py",
            "verify-upgrade-compatibility.py verify",
            "src/TCJ.Messaging/TCJ.Messaging.csproj",
            "TCJ.Messaging",
        ],
        ".github/workflows/release-preflight.yml": ["run-upgrade-tests.py", "verify-upgrade-compatibility.py verify"],
        ".github/workflows/release.yml": ["run-upgrade-tests.py", "verify-upgrade-compatibility.py verify"],
        ".github/workflows/published-package-smoke.yml": ["run-upgrade-tests.py", "verify-upgrade-compatibility.py verify-published"],
        ".github/PULL_REQUEST_TEMPLATE.md": [
            "Baseline upgrade consumers restore from NuGet.org",
            "Direct package upgrades pass without source changes",
            "Breaking changes are declared and migration guidance is complete",
        ],
    }
    for rel, snippets in requirements.items():
        path = root / rel
        if not path.is_file():
            fail(f"Required integration file is missing: {rel}")
        text = path.read_text(encoding="utf-8")
        for snippet in snippets:
            if snippet not in text:
                fail(f"{rel} is missing upgrade compatibility wiring: {snippet}")
    ignore = (root / ".gitignore").read_text(encoding="utf-8")
    for pattern in (
        "artifacts/upgrade-compatibility/",
        "upgrade-tests/**/bin/",
        "upgrade-tests/**/obj/",
    ):
        if pattern not in ignore:
            fail(f".gitignore must ignore generated upgrade output: {pattern}")


def normalize_source(value: str) -> str:
    value = str(value).strip()
    if value.startswith(("http://", "https://")):
        return value.rstrip("/").casefold()
    if value.startswith("file://"):
        value = value[7:]
    return os.path.normcase(os.path.normpath(str(Path(value).resolve())))


def verify_phase(
    *,
    name: str,
    phase: dict[str, Any],
    expected_packages: set[str],
    expected_version: str,
    expected_source: str,
) -> None:
    if any(phase.get(item) != "pass" for item in ("restore", "build", "runtime")):
        fail(f"{name} restore/build/runtime did not all pass.")
    if int(phase.get("warningCount", 0)) != 0:
        fail(f"{name} produced warnings.")
    resolved = phase.get("packageVersions", {})
    if set(resolved) != expected_packages or any(
        str(version).casefold() != expected_version.casefold() for version in resolved.values()
    ):
        fail(f"{name} package versions are incorrect: {resolved}")
    sources = phase.get("packageSources", {})
    if set(sources) != expected_packages:
        fail(f"{name} package source evidence is incomplete.")
    if any(normalize_source(src) != normalize_source(expected_source) for src in sources.values()):
        fail(f"{name} restored TCJ packages from an unexpected source: {sources}")


def verify_results(
    policy: dict[str, Any],
    baseline_meta: dict[str, Any],
    target_meta: dict[str, Any],
    manifest: dict[str, Any],
    args: argparse.Namespace,
    *,
    published: bool,
) -> dict[str, Any]:
    required_packages = runtime_release_packages(target_meta)
    if args.baseline_version != baseline_meta["version"] or args.target_version != target_meta["version"]:
        fail(f"Requested versions must match metadata: {baseline_meta['version']} -> {target_meta['version']}")
    results_root = Path(args.results).resolve()
    suite = read_json(results_root / "suite-result.json")
    if suite.get("baselineVersion") != args.baseline_version or suite.get("targetVersion") != args.target_version:
        fail("Suite result versions do not match requested upgrade path.")
    expected_mode = "published" if published else "local"
    if suite.get("targetSourceMode") != expected_mode:
        fail(f"Suite target source mode must be {expected_mode}.")

    target_package_dir = Path(args.target_packages).resolve()
    if not published:
        missing_packages = [
            package
            for package in required_packages
            if not (target_package_dir / f"{package}.{args.target_version}.nupkg").is_file()
        ]
        if missing_packages:
            fail(f"Target candidate feed is missing expected packages: {', '.join(missing_packages)}")

    direct_policy = {item["name"]: item for item in policy["scenarios"]}
    target_only_policy = {item["name"]: item for item in policy["targetOnlyScenarios"]}
    if published:
        expected_names = set(policy["publishedScenarios"]) | set(policy["publishedTargetOnlyScenarios"])
    else:
        expected_names = set(direct_policy) | set(target_only_policy)
    scenario_results = suite.get("scenarios")
    if not isinstance(scenario_results, list) or {s.get("name") for s in scenario_results} != expected_names:
        fail(f"Upgrade result scenario set does not match required {expected_mode} scenarios.")

    guide_text = (ROOT / policy["migrationGuide"]).read_text(encoding="utf-8").casefold()
    changelog_text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8").casefold()
    totals = {
        "dependencyAdditions": 0,
        "dependencyRemovals": 0,
        "dependencyUpgrades": 0,
        "dependencyDowngrades": 0,
        "directUpgradeSuccessCount": 0,
        "targetOnlySuccessCount": 0,
        "guidedMigrationSuccessCount": 0,
        "documentedBehaviorChanges": 0,
        "unexpectedBehaviorChanges": 0,
    }

    for scenario in scenario_results:
        name = scenario["name"]
        if (
            scenario.get("sourceHashBefore") in (None, "")
            or scenario.get("sourceHashAfter") in (None, "")
            or scenario.get("sourceUnchanged") is not True
        ):
            fail(f"Upgrade scenario source changed or source hash is missing: {name}")

        if name in target_only_policy:
            policy_item = target_only_policy[name]
            if scenario.get("scenarioKind") != "target-only":
                fail(f"Target-only scenario {name} is missing scenarioKind=target-only.")
            if scenario.get("baseline") is not None:
                fail(f"Target-only scenario {name} must not fabricate a published baseline execution.")
            expected_packages = set(policy_item["packages"])
            expected_source = NUGET_ORG if published else str(target_package_dir)
            verify_phase(
                name=f"{name} target-only target",
                phase=scenario.get("target", {}),
                expected_packages=expected_packages,
                expected_version=args.target_version,
                expected_source=expected_source,
            )
            if scenario.get("overall") != "pass":
                fail(f"Target-only scenario did not report overall pass: {name}")
            evidence = Path(args.output).resolve() / "target-only" / f"{name}.json"
            if not evidence.is_file():
                fail(f"Target-only package introduction evidence is missing for {name}.")
            totals["targetOnlySuccessCount"] += 1
            continue

        policy_item = direct_policy[name]
        scenario_manifest = [
            change for change in manifest.get("changes", []) if name in change.get("affectedScenarios", [])
        ]
        source_change_expected = any(change.get("requiresSourceChange") for change in scenario_manifest)
        expected_packages = set(policy_item["packages"])

        baseline_phase = scenario.get("baseline", {})
        verify_phase(
            name=f"{name} baseline",
            phase=baseline_phase,
            expected_packages=expected_packages,
            expected_version=args.baseline_version,
            expected_source=NUGET_ORG,
        )

        target_phase = scenario.get("target", {})
        if target_phase.get("restore") != "pass":
            fail(f"{name} target restore did not pass.")
        if not source_change_expected and any(target_phase.get(item) != "pass" for item in ("build", "runtime")):
            fail(f"{name} target build/runtime did not pass.")
        if int(target_phase.get("warningCount", 0)) != 0:
            fail(f"{name} target produced warnings.")
        resolved = target_phase.get("packageVersions", {})
        if set(resolved) != expected_packages or any(
            str(version).casefold() != args.target_version.casefold() for version in resolved.values()
        ):
            fail(f"{name} target package versions are incorrect: {resolved}")
        sources = target_phase.get("packageSources", {})
        if set(sources) != expected_packages:
            fail(f"{name} target package source evidence is incomplete.")
        expected_target_source = NUGET_ORG if published else str(target_package_dir)
        if any(normalize_source(src) != normalize_source(expected_target_source) for src in sources.values()):
            fail(f"{name} target restored TCJ packages from an unexpected source.")

        diff = scenario.get("dependencyDiff")
        if not isinstance(diff, dict):
            fail(f"Dependency diff is missing for {name}.")
        if diff.get("downgraded"):
            fail(f"Dependency downgrade detected for {name}: {diff['downgraded']}")
        if diff.get("removedRuntimeAssets"):
            fail(f"Required runtime assets were removed for {name}: {diff['removedRuntimeAssets']}")
        if diff.get("targetFrameworkChanged"):
            fail(f"Target-framework selection changed unexpectedly for {name}.")
        dependency_artifact = Path(args.output).resolve() / "dependency-diffs" / f"{name}.json"
        behavior_artifact = Path(args.output).resolve() / "behavior-diffs" / f"{name}.json"
        if not dependency_artifact.is_file() or not behavior_artifact.is_file():
            fail(f"Required dependency/behavior diff artifact is missing for {name}.")

        consumer_dependency_changes = set(diff.get("added", [])) | set(diff.get("removed", []))
        consumer_dependency_changes.update(
            item.get("package")
            for item in diff.get("versionChanged", [])
            if item.get("package") not in required_packages
        )
        undocumented = sorted(
            package
            for package in consumer_dependency_changes
            if package
            and package.casefold() not in guide_text
            and package.casefold() not in changelog_text
        )
        if undocumented:
            fail(f"Consumer-facing dependency changes are not documented for {name}: {', '.join(undocumented)}")
        totals["dependencyAdditions"] += len(diff.get("added", []))
        totals["dependencyRemovals"] += len(diff.get("removed", []))
        totals["dependencyUpgrades"] += len(diff.get("upgraded", []))
        totals["dependencyDowngrades"] += len(diff.get("downgraded", []))

        classification = scenario.get("behaviorClassification")
        if classification not in ALLOWED_CLASSIFICATIONS:
            totals["unexpectedBehaviorChanges"] += 1
            fail(f"Unexpected runtime behavior change for {name}: {classification}")
        if classification != "Equivalent":
            totals["documentedBehaviorChanges"] += 1
        migration = scenario.get("migration", {})
        observed_changes = scenario.get("behaviorChanges", [])
        if source_change_expected:
            if not migration.get("required") or migration.get("status") != "pass":
                fail(f"Guided migration failed for {name}.")
            if (
                target_phase.get("build") == "pass"
                and target_phase.get("runtime") == "pass"
                and classification == "Equivalent"
            ):
                fail(
                    f"Stale breaking-change entry: {name} is declared source-changing "
                    "but direct upgrade remains equivalent."
                )
            totals["guidedMigrationSuccessCount"] += 1
        else:
            if migration.get("status") != "not-required":
                fail(f"Unexpected guided migration for {name}: {migration}")
            if scenario_manifest and classification == "Equivalent" and not observed_changes:
                fail(f"Stale breaking-change entry: {name} has no observed behavior or migration change.")
        if scenario.get("overall") != "pass":
            fail(f"Scenario did not report overall pass: {name}")
        if not source_change_expected:
            totals["directUpgradeSuccessCount"] += 1

    if suite.get("overall") != "pass":
        fail("Upgrade suite did not report overall pass.")
    totals["breakingChangeManifestCount"] = len(manifest.get("changes", []))
    totals["scenarioCount"] = len(scenario_results)
    totals["targetOnlyScenarioCount"] = sum(
        1 for scenario in scenario_results if scenario.get("name") in target_only_policy
    )
    totals["sourceCommit"] = suite.get("sourceCommit", "unknown")
    return totals


def write_summary(
    output: Path,
    baseline: str,
    target: str,
    totals: dict[str, Any],
    *,
    published: bool,
) -> None:
    output.mkdir(parents=True, exist_ok=True)
    mode = "Published NuGet.org target" if published else "Local release-candidate target"
    payload = {
        "schemaVersion": 1,
        "sourceCommit": totals["sourceCommit"],
        "baselineVersion": baseline,
        "targetVersion": target,
        "mode": mode,
        **{key: value for key, value in totals.items() if key != "sourceCommit"},
        "migrationGuideValidation": "pass",
        "overall": "pass",
    }
    (output / "upgrade-compatibility-summary.json").write_text(
        json.dumps(payload, indent=2) + "\n", encoding="utf-8"
    )
    lines = [
        "# Package upgrade compatibility",
        "",
        f"- Source commit: `{totals['sourceCommit']}`",
        f"- Baseline version: `{baseline}`",
        f"- Target version: `{target}`",
        f"- Mode: {mode}",
        f"- Scenario count: {totals['scenarioCount']}",
        f"- Target-only introduction scenarios: {totals['targetOnlyScenarioCount']}",
        "- Baseline restore/build/run for published packages: **PASS**",
        "- Target restore/build/run: **PASS**",
        f"- Direct-upgrade success count: {totals['directUpgradeSuccessCount']}",
        f"- Target-only package introduction success count: {totals['targetOnlySuccessCount']}",
        f"- Guided-migration success count: {totals['guidedMigrationSuccessCount']}",
        f"- Dependency additions: {totals['dependencyAdditions']}",
        f"- Dependency removals: {totals['dependencyRemovals']}",
        f"- Dependency upgrades: {totals['dependencyUpgrades']}",
        f"- Dependency downgrades: {totals['dependencyDowngrades']}",
        f"- Documented behavior changes: {totals['documentedBehaviorChanges']}",
        f"- Unexpected behavior changes: {totals['unexpectedBehaviorChanges']}",
        f"- Breaking-change manifest entries: {totals['breakingChangeManifestCount']}",
        "- Migration-guide validation: **PASS**",
        "",
        "## Overall",
        "",
        "**PASS**",
        "",
    ]
    (output / "UPGRADE_COMPATIBILITY_SUMMARY.md").write_text("\n".join(lines), encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("validate-config")
    for command in ("verify", "verify-published"):
        item = sub.add_parser(command)
        item.add_argument("--baseline-version", required=True)
        item.add_argument("--target-version", required=True)
        item.add_argument("--results", type=Path, required=True)
        item.add_argument("--output", type=Path, required=True)
        item.add_argument(
            "--target-packages",
            type=Path,
            default=ROOT / "artifacts/upgrade-compatibility/target/packages",
        )
    args = parser.parse_args()
    policy, baseline, target, manifest = load_policy()
    validate_repository_wiring()
    if args.command == "validate-config":
        count = len(policy["scenarios"]) + len(policy["targetOnlyScenarios"])
        print(
            "Upgrade compatibility configuration is valid: "
            f"{baseline['version']} -> {target['version']}, scenarios={count}, "
            f"packages={len(runtime_release_packages(target))}."
        )
        return 0
    published = args.command == "verify-published"
    totals = verify_results(policy, baseline, target, manifest, args, published=published)
    write_summary(args.output.resolve(), args.baseline_version, args.target_version, totals, published=published)
    print(
        f"Upgrade compatibility verification passed for {totals['scenarioCount']} scenario(s): "
        f"{args.baseline_version} -> {args.target_version}."
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except VerificationError as exc:
        print(f"Upgrade compatibility verification failed: {exc}", file=sys.stderr)
        raise SystemExit(1)
