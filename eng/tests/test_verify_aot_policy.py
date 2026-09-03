from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

ENG = Path(__file__).resolve().parents[1]
if str(ENG) not in sys.path:
    sys.path.insert(0, str(ENG))

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-aot-policy.py"
SPEC = importlib.util.spec_from_file_location("verify_aot_policy", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class AotPolicyVerifierTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.policy_path = self.root / "eng/aot-policy.json"
        self._write_valid_repository()

    def tearDown(self) -> None:
        self.temp.cleanup()

    def test_valid_policy_parses_and_validates(self) -> None:
        policy = MODULE.validate_configuration(self.root, self.policy_path)
        self.assertEqual(6, len(policy.packages))
        self.assertEqual(set(MODULE.VALID_TIERS), set(policy.support_tiers))

    def test_invalid_support_tier_fails(self) -> None:
        policy = self._read_policy()
        policy["packages"][0]["tier"] = "Mostly"
        self._write_policy(policy)

        with self.assertRaisesRegex(MODULE.AotPolicyError, "invalid support tier 'Mostly'"):
            MODULE.validate_configuration(self.root, self.policy_path)

    def test_every_production_package_must_appear_exactly_once(self) -> None:
        policy = self._read_policy()
        policy["packages"].pop()
        self._write_policy(policy)

        with self.assertRaisesRegex(MODULE.AotPolicyError, "every production package exactly once"):
            MODULE.validate_configuration(self.root, self.policy_path)

    def test_duplicate_package_fails(self) -> None:
        policy = self._read_policy()
        policy["packages"].append(dict(policy["packages"][0]))
        self._write_policy(policy)

        with self.assertRaisesRegex(MODULE.AotPolicyError, "appears more than once"):
            MODULE.validate_configuration(self.root, self.policy_path)

    def test_unknown_package_name_fails(self) -> None:
        policy = self._read_policy()
        policy["packages"][0]["packageId"] = "TCJ.Unknown"
        self._write_policy(policy)

        with self.assertRaisesRegex(MODULE.AotPolicyError, "unknown: TCJ.Unknown"):
            MODULE.validate_configuration(self.root, self.policy_path)

    def test_full_tier_requires_packaged_consumer_evidence(self) -> None:
        policy = self._read_policy()
        ef_package = next(
            package
            for package in policy["packages"]
            if package["packageId"] == "TCJ.EntityFrameworkCore"
        )
        ef_package["tier"] = "Full"
        ef_package["fullSupportEvidence"] = []
        self._write_policy(policy)

        with self.assertRaisesRegex(MODULE.AotPolicyError, "cannot be Full without packaged-consumer"):
            MODULE.validate_configuration(self.root, self.policy_path)

    def test_project_reference_cannot_be_full_support_evidence(self) -> None:
        policy = self._read_policy()
        policy["minimumFullSupportEvidence"]["projectReferenceEvidenceAccepted"] = True
        self._write_policy(policy)

        with self.assertRaisesRegex(
            MODULE.AotPolicyError,
            "projectReferenceEvidenceAccepted must be False",
        ):
            MODULE.validate_configuration(self.root, self.policy_path)

    def _read_policy(self) -> dict:
        return json.loads(self.policy_path.read_text(encoding="utf-8"))

    def _write_policy(self, policy: dict) -> None:
        self.policy_path.write_text(json.dumps(policy, indent=2), encoding="utf-8")

    def _write_valid_repository(self) -> None:
        for path in (
            "eng",
            "src",
            "docs/guides",
            ".github/workflows",
            ".github",
        ):
            (self.root / path).mkdir(parents=True, exist_ok=True)

        package_ids = (
            "TCJ.Core",
            "TCJ.DependencyInjection",
            "TCJ.EntityFrameworkCore",
            "TCJ.EntityFrameworkCore.SqlServer",
            "TCJ.AspNetCore",
            "TCJ.Messaging",
        )
        (self.root / "eng/release-manifest.json").write_text(
            json.dumps({"packages": list(package_ids)}),
            encoding="utf-8",
        )
        for package_id in package_ids:
            project = self.root / f"src/{package_id}/{package_id}.csproj"
            project.parent.mkdir(parents=True, exist_ok=True)
            project.write_text(
                f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><PackageId>{package_id}</PackageId></PropertyGroup></Project>',
                encoding="utf-8",
            )

        restrictions = {
            "TCJ.DependencyInjection": [
                {
                    "kind": "PublicApi",
                    "symbol": "TCJ.DependencyInjection.Extensions.ServiceCollectionExtensions.AddTcjDependencyInjection",
                    "status": "Restricted",
                    "reason": "Reflection-based assembly scanning is restricted.",
                }
            ],
            "TCJ.EntityFrameworkCore": [
                {
                    "kind": "Upstream",
                    "symbol": "Microsoft.EntityFrameworkCore NativeAOT",
                    "status": "Experimental",
                    "reason": "Upstream EF Core support is experimental.",
                }
            ],
        }
        packages = []
        for package_id in package_ids:
            packages.append(
                {
                    "packageId": package_id,
                    "tier": "Experimental" if "EntityFrameworkCore" in package_id else "Conditional",
                    "rationale": "Initial compatibility baseline.",
                    "restrictions": restrictions.get(
                        package_id,
                        [
                            {
                                "kind": "PackageMetadata",
                                "symbol": f"{package_id} compatibility declaration",
                                "status": "Restricted",
                                "reason": "Full support evidence has not been recorded.",
                            }
                        ],
                    ),
                    "fullSupportEvidence": [],
                }
            )

        policy = {
            "schemaVersion": 2,
            "documentation": "docs/guides/native-aot-and-trimming.md",
            "supportTiers": {
                "Full": "full",
                "Conditional": "conditional",
                "Experimental": "experimental",
                "Unsupported": "unsupported",
            },
            "warningPolicy": {
                "full": {"tcjTrimWarnings": "Error", "tcjAotWarnings": "Error"},
                "conditional": {
                    "undocumentedTcjWarnings": "Error",
                    "documentedRestrictionWarningsAllowed": True,
                },
                "experimental": {"warningsMustBeRecorded": True},
                "suppressions": {
                    "supportClaimsMayRelyOnSuppressions": False,
                    "newSuppressionsAllowedByThisPolicyIssue": False,
                    "allowed": [],
                },
            },
            "supportedCiRuntimeIdentifiers": ["linux-x64"],
            "minimumFullSupportEvidence": {
                "consumerSource": "PackedNuGet",
                "projectReferenceEvidenceAccepted": False,
                "publishAot": True,
                "publishMustSucceed": True,
                "publishedBinaryMustExecute": True,
                "tcjTrimWarningCount": 0,
                "tcjAotWarningCount": 0,
                "minimumConsumerScenarios": 1,
            },
            "packages": packages,
        }
        self._write_policy(policy)

        documentation = """# Native AOT and trimming\n\nPublishAot is an application setting. IsAotCompatible is a library compatibility declaration.\n\nFull Conditional Experimental Unsupported\n\nFull evidence must use a Packed NuGet consumer, not a project reference.\n\nRestricted API: TCJ.DependencyInjection.Extensions.ServiceCollectionExtensions.AddTcjDependencyInjection.\n\nEF Core remains Experimental.\n"""
        (self.root / "docs/guides/native-aot-and-trimming.md").write_text(documentation, encoding="utf-8")
        (self.root / "docs/toc.yml").write_text("href: guides/native-aot-and-trimming.md\n", encoding="utf-8")
        (self.root / "docs/README.md").write_text("[AOT](guides/native-aot-and-trimming.md)\n", encoding="utf-8")

        (self.root / ".github/PULL_REQUEST_TEMPLATE.md").write_text(
            "- [ ] `aot-policy` changes are explicit and justified as compatibility changes\n",
            encoding="utf-8",
        )


if __name__ == "__main__":
    unittest.main()
