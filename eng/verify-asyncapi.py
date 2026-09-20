#!/usr/bin/env python3
"""Validate the Step 53 AsyncAPI foundation policy and governance contract."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path, PurePosixPath
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_POLICY = ROOT / "eng/asyncapi-policy.json"
DEFAULT_GOVERNANCE = ROOT / "eng/asyncapi-governance-contract.json"
DEFAULT_CATALOG_SCHEMA = ROOT / "eng/messaging-catalog-input.schema.json"
PINNED_ASYNCAPI_VERSION = "3.1.0"
CANONICAL_FORMAT = "JSON"
REQUIRED_OUTPUT_MODES = ("referenced", "bundled")
DELIVERY_SEMANTICS = ("AtLeastOnce", "AtMostOnce", "BestEffort", "TransportSpecific")
ORDERING_SEMANTICS = ("None", "BestEffort", "PerPartition", "PerSession")
DEAD_LETTER_SEMANTICS = ("Native", "ConventionBased", "Unsupported", "TransportSpecific")


class AsyncApiPolicyError(RuntimeError):
    pass


def fail(message: str) -> None:
    raise AsyncApiPolicyError(message)


def read_json(path: Path, description: str) -> dict[str, Any]:
    if not path.is_file():
        fail(f"Missing {description}: {path.as_posix()}")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        fail(f"Invalid JSON in {path.as_posix()}: {error}")
    if not isinstance(value, dict):
        fail(f"{description} must be a JSON object.")
    return value


def require_non_empty_string(value: Any, description: str) -> str:
    if not isinstance(value, str) or not value.strip():
        fail(f"{description} must be a non-empty string.")
    return value.strip()


def require_version(value: Any, description: str) -> str:
    version = require_non_empty_string(value, description)
    parts = version.split(".")
    if len(parts) != 2 or any(not part.isdigit() for part in parts):
        fail(f"{description} must use '<major>.<minor>' version syntax.")
    return version


def require_relative_path(value: Any, description: str) -> str:
    path_value = require_non_empty_string(value, description).replace("\\", "/")
    path = PurePosixPath(path_value)
    if path.is_absolute() or ".." in path.parts:
        fail(f"{description} must stay inside the repository.")
    return path.as_posix()


def require_exact_enum(value: Any, expected: tuple[str, ...], description: str) -> None:
    if not isinstance(value, list) or any(not isinstance(item, str) for item in value):
        fail(f"{description} must be a string array.")
    if tuple(value) != expected:
        fail(f"{description} must be exactly: {', '.join(expected)}.")


def validate_configuration(
    root: Path = ROOT,
    policy_path: Path | None = None,
    governance_path: Path | None = None,
    catalog_schema_path: Path | None = None,
) -> tuple[dict[str, Any], dict[str, Any]]:
    policy_path = policy_path or root / "eng/asyncapi-policy.json"
    governance_path = governance_path or root / "eng/asyncapi-governance-contract.json"
    catalog_schema_path = catalog_schema_path or root / "eng/messaging-catalog-input.schema.json"
    policy = read_json(policy_path, "AsyncAPI policy")
    governance = read_json(governance_path, "AsyncAPI governance contract")
    catalog_schema = read_json(catalog_schema_path, "messaging catalog input schema")

    if policy.get("schemaVersion") != 1:
        fail("AsyncAPI policy schemaVersion must be 1.")
    require_version(policy.get("policyVersion"), "policyVersion")
    if policy.get("asyncApiSpecificationVersion") != PINNED_ASYNCAPI_VERSION:
        fail(f"AsyncAPI specification version must be exactly {PINNED_ASYNCAPI_VERSION}.")

    canonical = policy.get("canonicalOutput")
    if not isinstance(canonical, dict):
        fail("canonicalOutput must be an object.")
    if canonical.get("format") != CANONICAL_FORMAT:
        fail(f"Canonical output format must be {CANONICAL_FORMAT}.")
    if canonical.get("asyncApiFileName") != "asyncapi.json":
        fail("Canonical AsyncAPI file name must be asyncapi.json.")
    require_exact_enum(policy.get("outputModes"), REQUIRED_OUTPUT_MODES, "outputModes")

    validator = policy.get("validator")
    if not isinstance(validator, dict) or validator.get("strategy") != "Deferred":
        fail("Validator strategy must remain Deferred in Step 53.1.")
    if validator.get("implementation") is not None:
        fail("Validator implementation must not be selected in Step 53.1.")
    if validator.get("selectionRequiresGovernanceReview") is not True:
        fail("Validator selection must require governance review.")

    versions = policy.get("versions")
    if not isinstance(versions, dict):
        fail("versions must be an object.")
    for key in ("tcjExtensionSchema", "eventCatalogSchema", "governanceContract"):
        require_version(versions.get(key), f"versions.{key}")
    if versions.get("messagingCatalogInputSchema") != 1:
        fail("versions.messagingCatalogInputSchema must be 1.")

    remote = policy.get("remoteReferences")
    if not isinstance(remote, dict) or remote.get("allowed") is not False:
        fail("Remote references must be explicitly disallowed.")
    secret = policy.get("secretScanning")
    if not isinstance(secret, dict) or secret.get("secretValuesAllowed") is not False or secret.get("required") is not True:
        fail("Secret-safety policy must forbid secret values and require scanning.")
    if policy.get("deterministicOutputRequired") is not True:
        fail("Deterministic output must be required.")

    generation = policy.get("generation")
    if not isinstance(generation, dict):
        fail("generation must be an object.")
    if generation.get("canonicalNewline") != "LF":
        fail("Generation canonical newline must be LF.")
    if generation.get("identifierNormalization") != "stable-lowercase-kebab-case-with-collision-detection":
        fail("Generation identifier normalization must match the governed contract.")
    if generation.get("serverDefaultBehavior") != "omit-when-not-explicitly-supplied":
        fail("Generation must omit servers unless safe metadata is explicitly supplied.")
    for key in ("maximumChannels", "maximumMessages", "maximumOperations", "maximumServers", "maximumSecuritySchemes"):
        if not isinstance(generation.get(key), int) or generation[key] <= 0:
            fail(f"generation.{key} must be a positive integer.")

    step52 = policy.get("step52ContractArtifacts")
    if not isinstance(step52, dict):
        fail("step52ContractArtifacts must be an object.")
    if step52.get("manifestFileName") != "manifest.json":
        fail("Step 52 contract manifest file name must be manifest.json.")
    if step52.get("manifestValidation") != "TCJ.Messaging.Contracts.MessageContractManifestSerializer":
        fail("Step 52 manifest validation must reuse MessageContractManifestSerializer.")
    if step52.get("fingerprintValidation") != "TCJ.Messaging.Contracts.MessageContractSchemaGenerator":
        fail("Step 52 schema fingerprint validation must reuse MessageContractSchemaGenerator.")
    for key in ("maximumSchemaBytes", "maximumExampleBytes", "maximumReferenceDepth"):
        if not isinstance(step52.get(key), int) or step52[key] <= 0:
            fail(f"step52ContractArtifacts.{key} must be a positive integer.")
    if step52["maximumReferenceDepth"] > 128:
        fail("Step 52 local reference depth must remain bounded to 128 or fewer levels.")
    local_refs = step52.get("localReferences")
    if not isinstance(local_refs, dict) or local_refs.get("allowed") is not True or local_refs.get("artifactRootConstrained") is not True or local_refs.get("parentTraversalAllowed") is not False:
        fail("Step 52 local references must be root-constrained and parent traversal must be forbidden.")
    if step52.get("governedExamplesOnly") is not True:
        fail("Only Step 52 governed examples may be consumed.")

    paths = policy.get("paths")
    if not isinstance(paths, dict):
        fail("paths must be an object.")
    required_paths = {
        "package": "src/TCJ.Messaging.AsyncApi/TCJ.Messaging.AsyncApi.csproj",
        "tool": "eng/TCJ.AsyncApi.Tool/TCJ.AsyncApi.Tool.csproj",
        "tests": "eng/tests/test_verify_asyncapi.py",
    }
    for key, expected in required_paths.items():
        actual = require_relative_path(paths.get(key), f"paths.{key}")
        if actual != expected:
            fail(f"paths.{key} must be '{expected}'.")
        if not (root / actual).is_file():
            fail(f"Configured path does not exist: {actual}")

    roots = policy.get("expectedGeneratedArtifactRoots")
    if not isinstance(roots, list) or len(roots) < 2:
        fail("expectedGeneratedArtifactRoots must contain the configured artifact roots.")
    normalized_roots = [require_relative_path(item, "expectedGeneratedArtifactRoots item") for item in roots]
    if normalized_roots != ["artifacts/asyncapi", "TestResults/AsyncApi"]:
        fail("Expected generated artifact roots are not canonical.")

    if governance.get("schemaVersion") != 1:
        fail("Governance schemaVersion must be 1.")
    governance_version = require_version(governance.get("governanceVersion"), "governanceVersion")
    if governance.get("compatibilitySensitive") is not True:
        fail("Governance-sensitive changes must be compatibility-sensitive.")
    if governance.get("asyncApiSpecificationVersion") != policy.get("asyncApiSpecificationVersion"):
        fail("Policy and governance AsyncAPI versions must agree.")
    if governance_version != versions["governanceContract"]:
        fail("Policy governance contract version must agree with governanceVersion.")

    governance_versions = governance.get("versions")
    if not isinstance(governance_versions, dict):
        fail("Governance versions must be an object.")
    if governance_versions.get("messagingCatalogInputSchema") != 1:
        fail("Governance messagingCatalogInputSchema version must be 1.")
    if governance_versions["messagingCatalogInputSchema"] != versions["messagingCatalogInputSchema"]:
        fail("Governance messagingCatalogInputSchema version must agree with policy.")
    agreements = {"eventCatalogSchema": "eventCatalogSchema", "tcjExtensionSchema": "tcjExtensionSchema"}
    for governance_key, policy_key in agreements.items():
        version = require_version(governance_versions.get(governance_key), f"governance.versions.{governance_key}")
        if version != versions[policy_key]:
            fail(f"Governance {governance_key} version must agree with policy.")
    for key in ("identifierNormalization", "deterministicSerialization", "validationReportSchema"):
        require_version(governance_versions.get(key), f"governance.versions.{key}")

    names = governance.get("canonicalFileNames")
    if not isinstance(names, dict) or names.get("asyncApi") != "asyncapi.json" or names.get("eventCatalog") != "event-catalog.json":
        fail("Canonical governance file names are invalid.")
    if names["asyncApi"] != canonical["asyncApiFileName"]:
        fail("Policy and governance canonical AsyncAPI file names must agree.")

    output_roots = governance.get("canonicalOutputRoots")
    if not isinstance(output_roots, dict):
        fail("canonicalOutputRoots must be an object.")
    if output_roots.get("generatedArtifacts") != normalized_roots[0] or output_roots.get("validationResults") != normalized_roots[1]:
        fail("Policy and governance canonical output roots must agree.")

    normalization = governance.get("identifierNormalization")
    if not isinstance(normalization, dict) or not require_non_empty_string(normalization.get("contract"), "identifierNormalization.contract"):
        fail("Identifier normalization contract must be explicit.")
    serialization = governance.get("deterministicSerialization")
    expected_serialization = {"format": "JSON", "propertyOrdering": "ordinal", "newline": "LF", "encoding": "UTF-8"}
    if serialization != expected_serialization:
        fail("Deterministic serialization contract is invalid.")

    require_exact_enum(governance.get("deliverySemantics"), DELIVERY_SEMANTICS, "deliverySemantics")
    require_exact_enum(governance.get("orderingSemantics"), ORDERING_SEMANTICS, "orderingSemantics")
    require_exact_enum(governance.get("deadLetterSemantics"), DEAD_LETTER_SEMANTICS, "deadLetterSemantics")

    integration = governance.get("step52ContractIntegration")
    expected_integration = {
        "contractIdentity": "MessageType+MessageVersion",
        "manifestSource": "TCJ.Messaging.Contracts.MessageContractManifestSerializer",
        "schemaSource": "Step52-governed-artifact",
        "fingerprintSource": "TCJ.Messaging.Contracts.MessageContractSchemaGenerator",
        "localReferenceBehavior": "root-constrained-no-parent-traversal",
        "remoteReferenceBehavior": "fail-closed-no-network",
        "exampleBehavior": "manifest-declared-governed-only",
        "compatibilityBehavior": "link-existing-Step52-evidence-only",
    }
    if integration != expected_integration:
        fail("Step 52 governed-contract integration semantics are invalid.")

    core_generation = governance.get("coreGeneration")
    expected_core_generation = {
        "documentFormatVersion": "1.0",
        "messageComponentIdentity": "normalized-MessageType.vMessageVersion",
        "channelIdentity": "normalized-explicit-catalog-channel-id",
        "operationIdentity": "normalized-application.action.MessageType.version.logical-id",
        "schemaModes": ["referenced", "bundled"],
        "referencedSchemaBehavior": "validated-Step52-relative-path-no-rewrite",
        "bundledSchemaBehavior": "validated-Step52-bytes-no-regeneration",
        "serverBehavior": "omit-unless-explicit-safe-metadata",
        "collisionBehavior": "fail-closed",
    }
    if core_generation != expected_core_generation:
        fail("Core AsyncAPI generation governance semantics are invalid.")

    if catalog_schema.get("$schema") != "https://json-schema.org/draft/2020-12/schema":
        fail("Messaging catalog input schema must use JSON Schema draft 2020-12.")
    if catalog_schema.get("type") != "object" or catalog_schema.get("additionalProperties") is not False:
        fail("Messaging catalog input schema root must be a closed object.")
    properties = catalog_schema.get("properties")
    if not isinstance(properties, dict) or properties.get("schemaVersion") != {"const": 1}:
        fail("Messaging catalog input schema version must be exactly 1.")
    required = catalog_schema.get("required")
    if required != ["schemaVersion", "application", "document"]:
        fail("Messaging catalog input schema must require schemaVersion, application, and document.")

    return policy, governance


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("validate-config",))
    args = parser.parse_args()
    try:
        validate_configuration()
    except AsyncApiPolicyError as error:
        print(f"AsyncAPI foundation validation failed: {error}", file=sys.stderr)
        return 1
    print("AsyncAPI foundation policy and governance contract are valid.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
