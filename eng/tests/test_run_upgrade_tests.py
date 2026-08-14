from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
import sys
from unittest import mock
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / ".." / "upgrade-tests" / "scripts" / "run-upgrade-tests.py"
spec = importlib.util.spec_from_file_location("run_upgrade_tests", MODULE_PATH.resolve())
runner = importlib.util.module_from_spec(spec)
assert spec and spec.loader
sys.modules[spec.name] = runner
spec.loader.exec_module(runner)


class RunUpgradeTestsUnitTests(unittest.TestCase):
    def test_semver_orders_preview_identifiers(self):
        self.assertLess(runner.semver_key("0.1.0-preview.1"), runner.semver_key("0.1.0-preview.2"))
        self.assertLess(runner.semver_key("0.1.0-preview.10"), runner.semver_key("0.1.0"))

    def test_semver_rejects_invalid_version(self):
        with self.assertRaises(runner.UpgradeError): runner.semver_key("preview")

    def test_warning_count_ignores_zero_warning_summary(self):
        self.assertEqual(0, runner.warning_count("Build succeeded.\n    0 Warning(s)\n"))
        self.assertEqual(1, runner.warning_count("a.cs(1,1): warning CA1000: sample\n    1 Warning(s)\n"))

    def test_source_tree_hash_ignores_bin_and_obj(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td); (root / "Program.cs").write_text("one")
            first = runner.source_tree_hash(root)
            (root / "bin").mkdir(); (root / "bin/out.dll").write_text("generated")
            (root / "obj").mkdir(); (root / "obj/assets.json").write_text("generated")
            self.assertEqual(first, runner.source_tree_hash(root))
            (root / "Program.cs").write_text("two")
            self.assertNotEqual(first, runner.source_tree_hash(root))

    def test_dependency_diff_detects_upgrade(self):
        baseline = {"targetFrameworks": ["net10.0"], "packages": {"A": {"version": "1.0.0", "compile": [], "runtime": ["lib/a.dll"], "build": [], "analyzers": []}}}
        target = {"targetFrameworks": ["net10.0"], "packages": {"A": {"version": "1.1.0", "compile": [], "runtime": ["lib/a.dll"], "build": [], "analyzers": []}, "B": {"version": "1.0.0", "compile": [], "runtime": [], "build": [], "analyzers": []}}}
        diff = runner.dependency_diff(baseline, target)
        self.assertEqual(["B"], diff["added"])
        self.assertEqual("A", diff["upgraded"][0]["package"])
        self.assertFalse(diff["downgraded"])

    def test_dependency_diff_detects_downgrade_and_removed_runtime_asset(self):
        baseline = {"targetFrameworks": ["net10.0"], "packages": {"A": {"version": "2.0.0", "compile": [], "runtime": ["lib/a.dll"], "build": [], "analyzers": []}}}
        target = {"targetFrameworks": ["net10.0"], "packages": {"A": {"version": "1.0.0", "compile": [], "runtime": [], "build": [], "analyzers": []}}}
        diff = runner.dependency_diff(baseline, target)
        self.assertEqual("A", diff["downgraded"][0]["package"])
        self.assertEqual("A", diff["removedRuntimeAssets"][0]["package"])

    def test_dependency_diff_detects_target_framework_change(self):
        baseline = {"targetFrameworks": ["net10.0"], "packages": {}}
        target = {"targetFrameworks": ["net9.0"], "packages": {}}
        self.assertTrue(runner.dependency_diff(baseline, target)["targetFrameworkChanged"])

    def test_behavior_classification_equivalent(self):
        behavior = {"checks": {"ok": True}}
        self.assertEqual("Equivalent", runner.behavior_classification(behavior, behavior, []))

    def test_behavior_classification_unexpected_without_manifest(self):
        self.assertEqual("Unexpected regression", runner.behavior_classification({"checks": {"ok": True}}, {"checks": {"ok": False}}, []))

    def test_behavior_classification_documented_with_manifest(self):
        change = {"breaking": False}
        self.assertEqual("Documented change", runner.behavior_classification({"a": 1}, {"a": 2}, [change]))

    def test_behavior_classification_intentional_breaking(self):
        self.assertEqual("Intentional breaking change", runner.behavior_classification({"a": 1}, {"a": 2}, [{"breaking": True}]))

    def test_parse_assets_rejects_wrong_tcj_version(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "project.assets.json"
            path.write_text(json.dumps({"libraries": {"TCJ.Core/0.1.0-preview.1": {}}, "targets": {"net10.0": {"TCJ.Core/0.1.0-preview.1": {}}}}))
            with self.assertRaises(runner.UpgradeError): runner.parse_assets(path, ["TCJ.Core"], "0.1.0-preview.2")

    def test_parse_assets_rejects_unexpected_tcj_closure(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "project.assets.json"
            path.write_text(json.dumps({"libraries": {"TCJ.Core/1.0.0": {}, "TCJ.AspNetCore/1.0.0": {}}, "targets": {"net10.0": {}}}))
            with self.assertRaises(runner.UpgradeError): runner.parse_assets(path, ["TCJ.Core"], "1.0.0")

    def test_guided_migration_applies_declared_patch(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            upgrade_root = root / "upgrade-tests"
            scenario_dir = upgrade_root / "Scenarios" / "Sample"
            scenario_dir.mkdir(parents=True)
            (upgrade_root / "Directory.Build.props").write_text("<Project />\n")
            (scenario_dir / "Sample.csproj").write_text("<Project Sdk=\"Microsoft.NET.Sdk\" />\n")
            (scenario_dir / "Program.cs").write_text("old\n")
            patch = upgrade_root / "Scenarios" / "Sample" / "Migrations" / "2.0.0.patch"
            patch.parent.mkdir()
            patch.write_text(
                "--- a/Program.cs\n"
                "+++ b/Program.cs\n"
                "@@ -1 +1 @@\n"
                "-old\n"
                "+new\n"
            )
            scenario = {"name": "Sample", "project": "upgrade-tests/Scenarios/Sample/Sample.csproj", "packages": [], "expectedOutput": "ok", "expectedBehavior": "unused.json"}
            changes = [{"id": "TCJ-BREAK-001", "requiresSourceChange": True, "migrationPatches": {"2.0.0": "upgrade-tests/Scenarios/Sample/Migrations/2.0.0.patch"}}]
            phase = runner.PhaseResult(restore="pass", build="pass", runtime="pass")
            old_root, old_upgrade_root = runner.ROOT, runner.UPGRADE_ROOT
            runner.ROOT, runner.UPGRADE_ROOT = root, upgrade_root
            try:
                with mock.patch.object(runner, "run_phase", return_value=(phase, {}, {})):
                    result = runner.run_guided_migration(
                        scenario, changes, "2.0.0", root / "target.config", {}, root / "cache", root / "artifacts", root / "packages", False
                    )
            finally:
                runner.ROOT, runner.UPGRADE_ROOT = old_root, old_upgrade_root
            self.assertEqual("pass", result["status"])
            self.assertIn("Program.cs", result["sourceChanges"]["files"])
            migrated = root / "artifacts" / "report" / "migration-results" / "Sample" / "source" / "Program.cs"
            self.assertEqual("new\n", migrated.read_text())


if __name__ == "__main__": unittest.main()
