from __future__ import annotations

import argparse
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

ENG = Path(__file__).resolve().parents[1]
if str(ENG) not in sys.path:
    sys.path.insert(0, str(ENG))

MODULE_PATH = ENG / "verify-upgrade-compatibility.py"
spec = importlib.util.spec_from_file_location("verify_upgrade", MODULE_PATH)
verify = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(verify)


def phase(version: str, packages: list[str], source: str):
    return {
        "restore": "pass",
        "build": "pass",
        "runtime": "pass",
        "warningCount": 0,
        "packageVersions": {package: version for package in packages},
        "packageSources": {package: source for package in packages},
        "dependencyGraph": "graph.json",
        "behavior": "behavior.json",
        "failure": None,
    }


class VerifyUpgradeCompatibilityTests(unittest.TestCase):
    def setUp(self):
        self.policy, self.baseline, self.target, self.manifest = verify.load_policy(verify.ROOT)

    def test_repository_configuration_is_valid(self):
        verify.validate_repository_wiring(verify.ROOT)
        self.assertEqual(6, len(self.policy["scenarios"]))
        self.assertEqual(1, len(self.policy["targetOnlyScenarios"]))

    def test_metadata_versions_are_ordered(self):
        self.assertLess(verify.semver_key(self.baseline["version"]), verify.semver_key(self.target["version"]))

    def test_published_baseline_and_target_only_packages_are_partitioned(self):
        baseline = verify.runtime_release_packages(self.baseline)
        target = verify.runtime_release_packages(self.target)
        target_only = set(self.policy["targetOnlyPackages"])
        self.assertEqual(target - baseline, target_only)
        self.assertEqual({"TCJ.Messaging", "TCJ.Messaging.RabbitMQ"}, target_only)

    def test_all_runtime_packages_are_covered(self):
        direct = {package for scenario in self.policy["scenarios"] for package in scenario["packages"]}
        target_only = {package for scenario in self.policy["targetOnlyScenarios"] for package in scenario["packages"]}
        target_only_ids = set(self.policy["targetOnlyPackages"])
        self.assertEqual(verify.runtime_release_packages(self.baseline), direct)
        self.assertTrue(target_only_ids.issubset(target_only))
        self.assertEqual(verify.runtime_release_packages(self.target), direct | target_only_ids)

    def test_breaking_change_manifest_is_empty_for_direct_upgrade(self):
        self.assertEqual([], self.manifest["changes"])

    def test_migration_guide_has_required_no_source_change_and_messaging_statements(self):
        text = (verify.ROOT / self.policy["migrationGuide"]).read_text().casefold()
        self.assertIn("no source changes", text)
        self.assertIn("tcj.messaging", text)
        self.assertIn("new package", text)

    def make_suite(self, root: Path, *, published: bool = False):
        direct = [
            scenario
            for scenario in self.policy["scenarios"]
            if not published or scenario["name"] in self.policy["publishedScenarios"]
        ]
        target_only = [
            scenario
            for scenario in self.policy["targetOnlyScenarios"]
            if not published or scenario["name"] in self.policy["publishedTargetOnlyScenarios"]
        ]
        target_packages = root / "target-packages"
        target_packages.mkdir()
        for package in self.policy["requiredPackages"]:
            (target_packages / f"{package}.{self.target['version']}.nupkg").write_bytes(b"fixture")
        local = str(target_packages.resolve())
        values = []
        for item in direct:
            packages = item["packages"]
            values.append(
                {
                    "scenarioKind": "direct-upgrade",
                    "name": item["name"],
                    "sourceHashBefore": "abc",
                    "sourceHashAfter": "abc",
                    "sourceUnchanged": True,
                    "baseline": phase(self.baseline["version"], packages, verify.NUGET_ORG),
                    "target": phase(self.target["version"], packages, verify.NUGET_ORG if published else local),
                    "dependencyDiff": {
                        "added": [],
                        "removed": [],
                        "versionChanged": [],
                        "upgraded": [],
                        "downgraded": [],
                        "removedRuntimeAssets": [],
                        "targetFrameworkChanged": False,
                    },
                    "behaviorClassification": "Equivalent",
                    "behaviorChanges": [],
                    "migration": {"required": False, "status": "not-required", "patches": []},
                    "overall": "pass",
                }
            )
        for item in target_only:
            packages = item["packages"]
            values.append(
                {
                    "scenarioKind": "target-only",
                    "name": item["name"],
                    "sourceHashBefore": "abc",
                    "sourceHashAfter": "abc",
                    "sourceUnchanged": True,
                    "baseline": None,
                    "target": phase(self.target["version"], packages, verify.NUGET_ORG if published else local),
                    "dependencyDiff": None,
                    "behaviorClassification": "Target-only package introduction",
                    "behaviorChanges": [],
                    "migration": {"required": False, "status": "not-applicable", "patches": []},
                    "overall": "pass",
                }
            )
        suite = {
            "baselineVersion": self.baseline["version"],
            "targetVersion": self.target["version"],
            "targetSourceMode": "published" if published else "local",
            "sourceCommit": "abc123",
            "scenarios": values,
            "overall": "pass",
        }
        results = root / "results"
        results.mkdir()
        (results / "suite-result.json").write_text(json.dumps(suite))
        output = root / "report"
        (output / "dependency-diffs").mkdir(parents=True)
        (output / "behavior-diffs").mkdir(parents=True)
        (output / "target-only").mkdir(parents=True)
        direct_names = {item["name"] for item in direct}
        for item in values:
            (output / "behavior-diffs" / f"{item['name']}.json").write_text(
                json.dumps({"classification": item["behaviorClassification"]})
            )
            if item["name"] in direct_names:
                (output / "dependency-diffs" / f"{item['name']}.json").write_text(
                    json.dumps(item["dependencyDiff"])
                )
            else:
                (output / "target-only" / f"{item['name']}.json").write_text(
                    json.dumps({"scenario": item["name"], "overall": "pass"})
                )
        args = argparse.Namespace(
            baseline_version=self.baseline["version"],
            target_version=self.target["version"],
            results=results,
            target_packages=target_packages,
            output=output,
        )
        return suite, args

    def test_valid_local_results_pass(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            _, args = self.make_suite(root)
            totals = verify.verify_results(
                self.policy, self.baseline, self.target, self.manifest, args, published=False
            )
            self.assertEqual(6, totals["directUpgradeSuccessCount"])
            self.assertEqual(1, totals["targetOnlySuccessCount"])
            self.assertEqual(7, totals["scenarioCount"])

    def test_valid_published_results_pass_for_selected_scenarios(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            _, args = self.make_suite(root, published=True)
            totals = verify.verify_results(
                self.policy, self.baseline, self.target, self.manifest, args, published=True
            )
            self.assertEqual(3, totals["directUpgradeSuccessCount"])
            self.assertEqual(1, totals["targetOnlySuccessCount"])
            self.assertEqual(4, totals["scenarioCount"])

    def test_target_only_scenario_cannot_fabricate_baseline(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            item = next(s for s in suite["scenarios"] if s["name"] == "MessagingConsumer")
            item["baseline"] = phase(self.baseline["version"], ["TCJ.Core"], verify.NUGET_ORG)
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_target_only_scenario_wrong_source_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            item = next(s for s in suite["scenarios"] if s["name"] == "MessagingConsumer")
            item["target"]["packageSources"]["TCJ.Messaging"] = verify.NUGET_ORG
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_wrong_baseline_source_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            suite["scenarios"][0]["baseline"]["packageSources"]["TCJ.Core"] = "/local"
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_wrong_target_source_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            suite["scenarios"][0]["target"]["packageSources"]["TCJ.Core"] = verify.NUGET_ORG
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_wrong_package_version_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            suite["scenarios"][0]["target"]["packageVersions"]["TCJ.Core"] = "9.9.9"
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_failed_baseline_build_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            suite["scenarios"][0]["baseline"]["build"] = "fail"
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_failed_target_runtime_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            suite["scenarios"][0]["target"]["runtime"] = "fail"
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_warning_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            suite["scenarios"][0]["target"]["warningCount"] = 1
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_source_modification_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            suite["scenarios"][0]["sourceUnchanged"] = False
            suite["scenarios"][0]["sourceHashAfter"] = "def"
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_dependency_downgrade_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            suite["scenarios"][0]["dependencyDiff"]["downgraded"] = [
                {"package": "X", "from": "2.0.0", "to": "1.0.0"}
            ]
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_removed_runtime_asset_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            suite["scenarios"][0]["dependencyDiff"]["removedRuntimeAssets"] = [
                {"package": "X", "assets": ["a.dll"]}
            ]
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_target_framework_change_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            suite["scenarios"][0]["dependencyDiff"]["targetFrameworkChanged"] = True
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_unexpected_behavior_change_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            suite["scenarios"][0]["behaviorClassification"] = "Unexpected regression"
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_failed_guided_migration_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            suite["scenarios"][0]["migration"] = {"required": True, "status": "fail", "patches": []}
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_successful_guided_migration_passes_for_declared_source_change(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            manifest = {"changes": [{"id": "TCJ-BREAK-001", "affectedScenarios": ["CoreConsumer"], "requiresSourceChange": True}]}
            item = next(s for s in suite["scenarios"] if s["name"] == "CoreConsumer")
            item["target"]["build"] = "fail"
            item["target"]["runtime"] = "not-run"
            item["behaviorClassification"] = "Intentional breaking change"
            item["migration"] = {"required": True, "status": "pass", "patches": ["migration.patch"]}
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            totals = verify.verify_results(self.policy, self.baseline, self.target, manifest, args, published=False)
            self.assertEqual(1, totals["guidedMigrationSuccessCount"])
            self.assertEqual(5, totals["directUpgradeSuccessCount"])
            self.assertEqual(1, totals["targetOnlySuccessCount"])

    def test_stale_source_breaking_change_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            manifest = {"changes": [{"id": "TCJ-BREAK-001", "affectedScenarios": ["CoreConsumer"], "requiresSourceChange": True}]}
            item = next(s for s in suite["scenarios"] if s["name"] == "CoreConsumer")
            item["migration"] = {"required": True, "status": "pass", "patches": ["migration.patch"]}
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaisesRegex(verify.VerificationError, "Stale breaking-change entry"):
                verify.verify_results(self.policy, self.baseline, self.target, manifest, args, published=False)

    def test_missing_scenario_result_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            suite, args = self.make_suite(root)
            suite["scenarios"].pop()
            (args.results / "suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError):
                verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)

    def test_summary_contains_required_counts(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            _, args = self.make_suite(root)
            totals = verify.verify_results(self.policy, self.baseline, self.target, self.manifest, args, published=False)
            out = root / "report"
            verify.write_summary(out, self.baseline["version"], self.target["version"], totals, published=False)
            text = (out / "UPGRADE_COMPATIBILITY_SUMMARY.md").read_text()
            self.assertIn("Direct-upgrade success count: 6", text)
            self.assertIn("Target-only package introduction success count: 1", text)
            self.assertIn("Dependency downgrades: 0", text)
            self.assertIn("**PASS**", text)


if __name__ == "__main__":
    unittest.main()
