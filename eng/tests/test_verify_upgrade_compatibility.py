from __future__ import annotations

import argparse
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-upgrade-compatibility.py"
spec = importlib.util.spec_from_file_location("verify_upgrade", MODULE_PATH)
verify = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(verify)


def phase(version: str, packages: list[str], source: str):
    return {"restore": "pass", "build": "pass", "runtime": "pass", "warningCount": 0,
            "packageVersions": {p: version for p in packages}, "packageSources": {p: source for p in packages},
            "dependencyGraph": "graph.json", "behavior": "behavior.json", "failure": None}


class VerifyUpgradeCompatibilityTests(unittest.TestCase):
    def setUp(self):
        self.policy, self.baseline, self.target, self.manifest = verify.load_policy(verify.ROOT)

    def test_repository_configuration_is_valid(self):
        verify.validate_repository_wiring(verify.ROOT)
        self.assertEqual(6, len(self.policy["scenarios"]))

    def test_metadata_versions_are_ordered(self):
        self.assertLess(verify.semver_key(self.baseline["version"]), verify.semver_key(self.target["version"]))

    def test_all_five_packages_are_covered(self):
        covered = {p for scenario in self.policy["scenarios"] for p in scenario["packages"]}
        self.assertEqual(verify.REQUIRED_PACKAGES, covered)

    def test_breaking_change_manifest_is_empty_for_direct_upgrade(self):
        self.assertEqual([], self.manifest["changes"])

    def test_migration_guide_has_required_no_source_change_statement(self):
        text = (verify.ROOT / self.policy["migrationGuide"]).read_text().casefold()
        self.assertIn("no source changes", text)

    def make_suite(self, root: Path, *, published=False):
        scenarios = [s for s in self.policy["scenarios"] if not published or s["name"] in self.policy["publishedScenarios"]]
        target_packages = root / "target-packages"
        target_packages.mkdir()
        for package in self.policy["requiredPackages"]:
            (target_packages / f"{package}.{self.target['version']}.nupkg").write_bytes(b"fixture")
        local = str(target_packages.resolve())
        values = []
        for item in scenarios:
            packages = item["packages"]
            values.append({
                "name": item["name"], "sourceHashBefore": "abc", "sourceHashAfter": "abc", "sourceUnchanged": True,
                "baseline": phase(self.baseline["version"], packages, verify.NUGET_ORG),
                "target": phase(self.target["version"], packages, verify.NUGET_ORG if published else local),
                "dependencyDiff": {"added": [], "removed": [], "versionChanged": [], "upgraded": [], "downgraded": [], "removedRuntimeAssets": [], "targetFrameworkChanged": False},
                "behaviorClassification": "Equivalent", "behaviorChanges": [], "migration": {"required": False, "status": "not-required", "patches": []}, "overall": "pass",
            })
        suite = {"baselineVersion": self.baseline["version"], "targetVersion": self.target["version"], "targetSourceMode": "published" if published else "local", "sourceCommit": "abc123", "scenarios": values, "overall": "pass"}
        results = root / "results"; results.mkdir(); (results / "suite-result.json").write_text(json.dumps(suite))
        output = root / "report"; (output / "dependency-diffs").mkdir(parents=True); (output / "behavior-diffs").mkdir(parents=True)
        for item in values:
            (output / "dependency-diffs" / f"{item['name']}.json").write_text(json.dumps(item["dependencyDiff"]))
            (output / "behavior-diffs" / f"{item['name']}.json").write_text(json.dumps({"classification": item["behaviorClassification"]}))
        args = argparse.Namespace(baseline_version=self.baseline["version"], target_version=self.target["version"], results=results, target_packages=root / "target-packages", output=output)
        return suite, args

    def test_valid_local_results_pass(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); _, args=self.make_suite(root)
            totals=verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)
            self.assertEqual(6, totals["directUpgradeSuccessCount"])

    def test_valid_published_results_pass_for_selected_scenarios(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); _, args=self.make_suite(root,published=True)
            totals=verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=True)
            self.assertEqual(3, totals["scenarioCount"])

    def test_wrong_baseline_source_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root); suite["scenarios"][0]["baseline"]["packageSources"]["TCJ.Core"]="/local"
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError): verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)

    def test_wrong_target_source_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root); suite["scenarios"][0]["target"]["packageSources"]["TCJ.Core"]=verify.NUGET_ORG
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError): verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)

    def test_wrong_package_version_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root); suite["scenarios"][0]["target"]["packageVersions"]["TCJ.Core"]="9.9.9"
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError): verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)

    def test_failed_baseline_build_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root); suite["scenarios"][0]["baseline"]["build"]="fail"
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError): verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)

    def test_failed_target_runtime_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root); suite["scenarios"][0]["target"]["runtime"]="fail"
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError): verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)

    def test_warning_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root); suite["scenarios"][0]["target"]["warningCount"]=1
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError): verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)

    def test_source_modification_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root); suite["scenarios"][0]["sourceUnchanged"]=False; suite["scenarios"][0]["sourceHashAfter"]="def"
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError): verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)

    def test_dependency_downgrade_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root); suite["scenarios"][0]["dependencyDiff"]["downgraded"]=[{"package":"X","from":"2.0.0","to":"1.0.0"}]
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError): verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)

    def test_removed_runtime_asset_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root); suite["scenarios"][0]["dependencyDiff"]["removedRuntimeAssets"]=[{"package":"X","assets":["a.dll"]}]
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError): verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)

    def test_target_framework_change_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root); suite["scenarios"][0]["dependencyDiff"]["targetFrameworkChanged"]=True
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError): verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)

    def test_unexpected_behavior_change_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root); suite["scenarios"][0]["behaviorClassification"]="Unexpected regression"
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError): verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)

    def test_failed_guided_migration_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root); suite["scenarios"][0]["migration"]={"required":True,"status":"fail","patches":[]}
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError): verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)

    def test_successful_guided_migration_passes_for_declared_source_change(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root)
            manifest={"changes":[{"id":"TCJ-BREAK-001","affectedScenarios":["CoreConsumer"],"requiresSourceChange":True}]}
            item=next(s for s in suite["scenarios"] if s["name"]=="CoreConsumer")
            item["target"]["build"]="fail"; item["target"]["runtime"]="not-run"
            item["behaviorClassification"]="Intentional breaking change"
            item["migration"]={"required":True,"status":"pass","patches":["migration.patch"]}
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            totals=verify.verify_results(self.policy,self.baseline,self.target,manifest,args,published=False)
            self.assertEqual(1, totals["guidedMigrationSuccessCount"])
            self.assertEqual(5, totals["directUpgradeSuccessCount"])

    def test_stale_source_breaking_change_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root)
            manifest={"changes":[{"id":"TCJ-BREAK-001","affectedScenarios":["CoreConsumer"],"requiresSourceChange":True}]}
            item=next(s for s in suite["scenarios"] if s["name"]=="CoreConsumer")
            item["migration"]={"required":True,"status":"pass","patches":["migration.patch"]}
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            with self.assertRaisesRegex(verify.VerificationError, "Stale breaking-change entry"):
                verify.verify_results(self.policy,self.baseline,self.target,manifest,args,published=False)

    def test_missing_scenario_result_fails(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); suite,args=self.make_suite(root); suite["scenarios"].pop()
            (args.results/"suite-result.json").write_text(json.dumps(suite))
            with self.assertRaises(verify.VerificationError): verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)

    def test_summary_contains_required_counts(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td); _, args=self.make_suite(root); totals=verify.verify_results(self.policy,self.baseline,self.target,self.manifest,args,published=False)
            out=root/"report"; verify.write_summary(out,self.baseline["version"],self.target["version"],totals,published=False)
            text=(out/"UPGRADE_COMPATIBILITY_SUMMARY.md").read_text()
            self.assertIn("Direct-upgrade success count: 6",text); self.assertIn("Dependency downgrades: 0",text); self.assertIn("**PASS**",text)


if __name__ == "__main__": unittest.main()
