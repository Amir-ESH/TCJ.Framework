#!/usr/bin/env python3
"""Validate the TCJ Native AOT and trimming compatibility policy."""

from __future__ import annotations

import argparse
import json
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_POLICY = ROOT / "eng/aot-policy.json"
VALID_TIERS = ("Full", "Conditional", "Experimental", "Unsupported")
VALID_RESTRICTION_KINDS = ("PublicApi", "Upstream", "PackageMetadata")
VALID_RESTRICTION_STATUSES = ("Restricted", "Experimental", "Unsupported")
RELEASE_MANIFEST = "eng/release-manifest.json"
PR_TEMPLATE = ".github/PULL_REQUEST_TEMPLATE.md"


class AotPolicyError(RuntimeError):
    """Raised when the Native AOT/trimming policy is invalid."""


@dataclass(frozen=True)
class PackagePolicy:
    package_id: str
    tier: str
    rationale: str
    restrictions: tuple[dict[str, str], ...]
    full_support_evidence: tuple[dict[str, Any], ...]


@dataclass(frozen=True)
class AotPolicy:
    documentation: str
    support_tiers: dict[str, str]
    warning_policy: dict[str, Any]
    minimum_full_support_evidence: dict[str, Any]
    packages: tuple[PackagePolicy, ...]


def fail(message: str) -> None:
    raise AotPolicyError(message)


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


def require_bool(value: Any, description: str) -> bool:
    if not isinstance(value, bool):
        fail(f"{description} must be a boolean.")
    return value


def require_int(value: Any, description: str, *, minimum: int = 0) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < minimum:
        fail(f"{description} must be an integer >= {minimum}.")
    return value


def require_string(value: Any, description: str) -> str:
    if not isinstance(value, str) or not value.strip():
        fail(f"{description} must be a non-empty string.")
    return value.strip()


def require_relative_path(value: Any, description: str) -> str:
    normalized = require_string(value, description).replace("\\", "/")
    path = PurePosixPath(normalized)
    if path.is_absolute() or ".." in path.parts:
        fail(f"{description} must stay inside the repository: {normalized}")
    return path.as_posix()


def require_exact_keys(mapping: dict[str, Any], expected: set[str], description: str) -> None:
    actual = set(mapping)
    if actual == expected:
        return
    details: list[str] = []
    missing = sorted(expected - actual)
    unknown = sorted(actual - expected)
    if missing:
        details.append("missing: " + ", ".join(missing))
    if unknown:
        details.append("unknown: " + ", ".join(unknown))
    fail(f"{description} has invalid keys ({'; '.join(details)}).")


def load_policy(path: Path = DEFAULT_POLICY) -> AotPolicy:
    raw = require_object(read_json(path, "AOT policy"), "AOT policy")
    require_exact_keys(
        raw,
        {"schemaVersion", "documentation", "supportTiers", "warningPolicy", "minimumFullSupportEvidence", "packages"},
        "AOT policy",
    )
    if raw.get("schemaVersion") != 1:
        fail("AOT policy schemaVersion must be 1.")

    documentation = require_relative_path(raw.get("documentation"), "documentation")

    support_tiers_raw = require_object(raw.get("supportTiers"), "supportTiers")
    require_exact_keys(support_tiers_raw, set(VALID_TIERS), "supportTiers")
    support_tiers = {
        tier: require_string(description, f"supportTiers.{tier}")
        for tier, description in support_tiers_raw.items()
    }

    warning_policy = require_object(raw.get("warningPolicy"), "warningPolicy")
    require_exact_keys(
        warning_policy,
        {"full", "conditional", "experimental", "suppressions"},
        "warningPolicy",
    )
    full_warnings = require_object(warning_policy["full"], "warningPolicy.full")
    if full_warnings.get("tcjTrimWarnings") != "Error":
        fail("warningPolicy.full.tcjTrimWarnings must be 'Error'.")
    if full_warnings.get("tcjAotWarnings") != "Error":
        fail("warningPolicy.full.tcjAotWarnings must be 'Error'.")
    conditional_warnings = require_object(
        warning_policy["conditional"], "warningPolicy.conditional"
    )
    if conditional_warnings.get("undocumentedTcjWarnings") != "Error":
        fail("warningPolicy.conditional.undocumentedTcjWarnings must be 'Error'.")
    require_bool(
        conditional_warnings.get("documentedRestrictionWarningsAllowed"),
        "warningPolicy.conditional.documentedRestrictionWarningsAllowed",
    )
    experimental_warnings = require_object(
        warning_policy["experimental"], "warningPolicy.experimental"
    )
    require_bool(
        experimental_warnings.get("warningsMustBeRecorded"),
        "warningPolicy.experimental.warningsMustBeRecorded",
    )
    suppressions = require_object(warning_policy["suppressions"], "warningPolicy.suppressions")
    require_exact_keys(
        suppressions,
        {"supportClaimsMayRelyOnSuppressions", "newSuppressionsAllowedByThisPolicyIssue", "allowed"},
        "warningPolicy.suppressions",
    )
    if require_bool(
        suppressions.get("supportClaimsMayRelyOnSuppressions"),
        "warningPolicy.suppressions.supportClaimsMayRelyOnSuppressions",
    ):
        fail("AOT support claims must not rely on warning suppressions.")
    if require_bool(
        suppressions.get("newSuppressionsAllowedByThisPolicyIssue"),
        "warningPolicy.suppressions.newSuppressionsAllowedByThisPolicyIssue",
    ):
        fail("This policy issue must not allow new warning suppressions.")

    allowed_suppressions = suppressions.get("allowed")
    if not isinstance(allowed_suppressions, list):
        fail("warningPolicy.suppressions.allowed must be an array.")
    seen_suppressions: set[tuple[str, str, str, str]] = set()
    for index, value in enumerate(allowed_suppressions):
        description = f"warningPolicy.suppressions.allowed[{index}]"
        item = require_object(value, description)
        require_exact_keys(
            item,
            {"packageId", "project", "property", "diagnostic", "reason"},
            description,
        )
        package_id = require_string(item.get("packageId"), f"{description}.packageId")
        project = require_relative_path(item.get("project"), f"{description}.project")
        property_name = require_string(item.get("property"), f"{description}.property")
        if property_name not in ("NoWarn", "WarningsNotAsErrors"):
            fail(f"{description}.property must be NoWarn or WarningsNotAsErrors.")
        diagnostic = require_string(item.get("diagnostic"), f"{description}.diagnostic").upper()
        if len(diagnostic) != 6 or not diagnostic.startswith(("IL2", "IL3")) or not diagnostic[2:].isdigit():
            fail(f"{description}.diagnostic must be one exact IL2xxx or IL3xxx diagnostic ID.")
        require_string(item.get("reason"), f"{description}.reason")
        key = (package_id, project, property_name, diagnostic)
        if key in seen_suppressions:
            fail(f"Duplicate allowed AOT suppression for {package_id} {project} {property_name} {diagnostic}.")
        seen_suppressions.add(key)

    evidence = require_object(
        raw.get("minimumFullSupportEvidence"), "minimumFullSupportEvidence"
    )
    expected_evidence = {
        "consumerSource": "PackedNuGet",
        "projectReferenceEvidenceAccepted": False,
        "publishAot": True,
        "publishMustSucceed": True,
        "publishedBinaryMustExecute": True,
        "tcjTrimWarningCount": 0,
        "tcjAotWarningCount": 0,
    }
    for key, expected in expected_evidence.items():
        if evidence.get(key) != expected:
            fail(f"minimumFullSupportEvidence.{key} must be {expected!r}.")
    require_int(
        evidence.get("minimumConsumerScenarios"),
        "minimumFullSupportEvidence.minimumConsumerScenarios",
        minimum=1,
    )

    packages_raw = raw.get("packages")
    if not isinstance(packages_raw, list) or not packages_raw:
        fail("packages must be a non-empty array.")

    packages: list[PackagePolicy] = []
    seen: set[str] = set()
    for index, item in enumerate(packages_raw):
        package = require_object(item, f"packages[{index}]")
        require_exact_keys(
            package,
            {"packageId", "tier", "rationale", "restrictions", "fullSupportEvidence"},
            f"packages[{index}]",
        )
        package_id = require_string(package.get("packageId"), f"packages[{index}].packageId")
        if package_id in seen:
            fail(f"Package '{package_id}' appears more than once in the AOT policy.")
        seen.add(package_id)

        tier = require_string(package.get("tier"), f"packages[{index}].tier")
        if tier not in VALID_TIERS:
            fail(
                f"Package '{package_id}' has invalid support tier '{tier}'. "
                f"Expected one of: {', '.join(VALID_TIERS)}."
            )
        rationale = require_string(package.get("rationale"), f"packages[{index}].rationale")

        restrictions_raw = package.get("restrictions")
        if not isinstance(restrictions_raw, list):
            fail(f"packages[{index}].restrictions must be an array.")
        restrictions: list[dict[str, str]] = []
        for restriction_index, restriction_value in enumerate(restrictions_raw):
            description = f"packages[{index}].restrictions[{restriction_index}]"
            restriction = require_object(restriction_value, description)
            require_exact_keys(restriction, {"kind", "symbol", "status", "reason"}, description)
            kind = require_string(restriction.get("kind"), f"{description}.kind")
            if kind not in VALID_RESTRICTION_KINDS:
                fail(
                    f"{description}.kind '{kind}' is invalid. Expected one of: "
                    + ", ".join(VALID_RESTRICTION_KINDS)
                )
            symbol = require_string(restriction.get("symbol"), f"{description}.symbol")
            status = require_string(restriction.get("status"), f"{description}.status")
            if status not in VALID_RESTRICTION_STATUSES:
                fail(
                    f"{description}.status '{status}' is invalid. Expected one of: "
                    + ", ".join(VALID_RESTRICTION_STATUSES)
                )
            reason = require_string(restriction.get("reason"), f"{description}.reason")
            restrictions.append(
                {"kind": kind, "symbol": symbol, "status": status, "reason": reason}
            )

        full_support_evidence_raw = package.get("fullSupportEvidence")
        if not isinstance(full_support_evidence_raw, list):
            fail(f"packages[{index}].fullSupportEvidence must be an array.")
        if tier == "Full" and len(full_support_evidence_raw) < evidence["minimumConsumerScenarios"]:
            fail(
                f"Package '{package_id}' cannot be Full without packaged-consumer "
                "Native AOT evidence."
            )

        validated_evidence: list[dict[str, Any]] = []
        for evidence_index, evidence_value in enumerate(full_support_evidence_raw):
            description = f"packages[{index}].fullSupportEvidence[{evidence_index}]"
            evidence_item = require_object(evidence_value, description)
            require_exact_keys(
                evidence_item,
                {"scenario", "consumerProject", "workflow", "consumerSource", "usesProjectReference", "publishAot", "publishSucceeded", "publishedBinaryExecuted", "tcjTrimWarningCount", "tcjAotWarningCount"},
                description,
            )
            scenario = require_string(evidence_item.get("scenario"), f"{description}.scenario")
            consumer_project = require_relative_path(
                evidence_item.get("consumerProject"), f"{description}.consumerProject"
            )
            workflow = require_relative_path(
                evidence_item.get("workflow"), f"{description}.workflow"
            )
            if evidence_item.get("consumerSource") != "PackedNuGet":
                fail(f"{description}.consumerSource must be 'PackedNuGet'.")
            if require_bool(
                evidence_item.get("usesProjectReference"),
                f"{description}.usesProjectReference",
            ):
                fail(f"{description} must not use a TCJ project reference.")
            for key in ("publishAot", "publishSucceeded", "publishedBinaryExecuted"):
                if not require_bool(evidence_item.get(key), f"{description}.{key}"):
                    fail(f"{description}.{key} must be true.")
            for key in ("tcjTrimWarningCount", "tcjAotWarningCount"):
                if require_int(evidence_item.get(key), f"{description}.{key}") != 0:
                    fail(f"{description}.{key} must be 0.")
            validated_evidence.append(
                {
                    "scenario": scenario,
                    "consumerProject": consumer_project,
                    "workflow": workflow,
                    "consumerSource": "PackedNuGet",
                    "usesProjectReference": False,
                    "publishAot": True,
                    "publishSucceeded": True,
                    "publishedBinaryExecuted": True,
                    "tcjTrimWarningCount": 0,
                    "tcjAotWarningCount": 0,
                }
            )

        packages.append(
            PackagePolicy(
                package_id=package_id,
                tier=tier,
                rationale=rationale,
                restrictions=tuple(restrictions),
                full_support_evidence=tuple(validated_evidence),
            )
        )

    package_ids = {package.package_id for package in packages}
    unknown_suppression_packages = sorted({key[0] for key in seen_suppressions} - package_ids)
    if unknown_suppression_packages:
        fail(
            "Allowed AOT suppressions reference unknown packages: "
            + ", ".join(unknown_suppression_packages)
        )

    return AotPolicy(
        documentation=documentation,
        support_tiers=support_tiers,
        warning_policy=warning_policy,
        minimum_full_support_evidence=evidence,
        packages=tuple(packages),
    )


def release_packages(root: Path) -> tuple[str, ...]:
    manifest = require_object(
        read_json(root / RELEASE_MANIFEST, "release manifest"), "Release manifest"
    )
    packages = manifest.get("packages")
    if not isinstance(packages, list) or not packages:
        fail("Release manifest packages must be a non-empty array.")
    normalized = tuple(require_string(value, "release-manifest package") for value in packages)
    if len(normalized) != len(set(normalized)):
        fail("Release manifest packages must not contain duplicates.")
    return normalized


def source_package_ids(root: Path) -> tuple[str, ...]:
    result: list[str] = []
    for project in sorted((root / "src").glob("*/*.csproj")):
        try:
            xml = ET.parse(project).getroot()
        except ET.ParseError as error:
            fail(f"Invalid project XML in {relative(project, root)}: {error}")
        package_id_node = xml.find(".//PackageId")
        if package_id_node is None or not package_id_node.text or not package_id_node.text.strip():
            continue
        result.append(package_id_node.text.strip())
    if len(result) != len(set(result)):
        fail("Production project PackageId values must be unique.")
    return tuple(result)


def validate_package_inventory(root: Path, policy: AotPolicy) -> None:
    expected = set(release_packages(root))
    source = set(source_package_ids(root))
    actual = [package.package_id for package in policy.packages]

    if source != expected:
        fail(
            "Release manifest packages must match src PackageId values "
            f"(manifest={sorted(expected)}, src={sorted(source)})."
        )

    actual_set = set(actual)
    missing = sorted(expected - actual_set)
    unknown = sorted(actual_set - expected)
    if missing or unknown or len(actual) != len(expected):
        details: list[str] = []
        if missing:
            details.append("missing: " + ", ".join(missing))
        if unknown:
            details.append("unknown: " + ", ".join(unknown))
        if len(actual) != len(expected) and not (missing or unknown):
            details.append("duplicate package entries")
        fail(
            "AOT policy must contain every production package exactly once "
            f"({'; '.join(details)})."
        )


def validate_evidence_paths(root: Path, policy: AotPolicy) -> None:
    for package in policy.packages:
        for evidence in package.full_support_evidence:
            for key in ("consumerProject", "workflow"):
                path = root / evidence[key]
                if not path.is_file():
                    fail(
                        f"Package '{package.package_id}' Full evidence references missing "
                        f"{key}: {evidence[key]}"
                    )


def validate_documentation(root: Path, policy: AotPolicy) -> None:
    path = root / policy.documentation
    if not path.is_file():
        fail(f"Missing AOT policy documentation: {policy.documentation}")
    text = path.read_text(encoding="utf-8")
    required_fragments = (
        "PublishAot",
        "IsAotCompatible",
        "Packed NuGet",
        "project reference",
        "TCJ.DependencyInjection.Extensions.ServiceCollectionExtensions.AddTcjDependencyInjection",
        "EF Core",
        "Experimental",
        "Full",
        "Conditional",
        "Unsupported",
    )
    missing = [fragment for fragment in required_fragments if fragment not in text]
    if missing:
        fail("AOT documentation is missing required contract text: " + ", ".join(missing))

    toc = (root / "docs/toc.yml").read_text(encoding="utf-8")
    docs_readme = (root / "docs/README.md").read_text(encoding="utf-8")
    if "guides/native-aot-and-trimming.md" not in toc:
        fail("docs/toc.yml must link guides/native-aot-and-trimming.md.")
    if "guides/native-aot-and-trimming.md" not in docs_readme:
        fail("docs/README.md must link guides/native-aot-and-trimming.md.")


def validate_repository_wiring(root: Path) -> None:
    template = root / PR_TEMPLATE
    if not template.is_file():
        fail(f"Missing pull request template: {PR_TEMPLATE}")
    template_text = template.read_text(encoding="utf-8")
    if "`aot-policy` changes are explicit and justified as compatibility changes" not in template_text:
        fail("Pull request template must review aot-policy changes as compatibility changes.")


def validate_configuration(root: Path = ROOT, policy_path: Path | None = None) -> AotPolicy:
    policy_path = policy_path or root / "eng/aot-policy.json"
    policy = load_policy(policy_path)
    validate_package_inventory(root, policy)
    validate_evidence_paths(root, policy)
    validate_documentation(root, policy)
    validate_repository_wiring(root)
    return policy


def build_summary(policy: AotPolicy) -> str:
    lines = [
        "# Native AOT and trimming policy",
        "",
        "| Package | Tier | Restrictions |",
        "|---|---|---:|",
    ]
    for package in policy.packages:
        lines.append(f"| `{package.package_id}` | {package.tier} | {len(package.restrictions)} |")
    lines.extend(
        [
            "",
            "Full support requires packed-NuGet Native AOT publish and execution evidence with zero TCJ-caused trim/AOT warnings.",
            "",
            "AOT policy validation passed.",
        ]
    )
    return "\n".join(lines)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser("validate-config")
    subparsers.add_parser("summary")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        policy = validate_configuration()
        if args.command == "summary":
            print(build_summary(policy))
        else:
            print("Native AOT and trimming policy validation passed.")
        return 0
    except AotPolicyError as error:
        print(f"AOT policy error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
