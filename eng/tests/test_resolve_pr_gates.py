import importlib.util
import json
import sys
import unittest
from pathlib import Path

### This is for Self-Protection fan-out test

ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "eng" / "resolve-pr-gates.py"
SPEC = importlib.util.spec_from_file_location("resolve_pr_gates", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)
POLICY = MODULE.load_policy(ROOT / "eng" / "required-pr-gates.json")


class RequiredPrGateTests(unittest.TestCase):
    def required(self, target, *paths):
        plan = MODULE.resolve_plan(POLICY, target, paths)
        return {name for name, decision in plan["gates"].items() if decision["required"]}

    def test_unrelated_metadata_keeps_only_core_gates(self):
        self.assertEqual(
            {"ci", "dependency_review"},
            self.required("develop", "CODE_OF_CONDUCT.md"),
        )

    def test_docs_change_runs_documentation_only_beyond_core(self):
        self.assertEqual(
            {"ci", "dependency_review", "documentation"},
            self.required("develop", "docs/getting-started.md"),
        )

    def test_documentation_pages_workflow_change_runs_documentation_gate(self):
        self.assertEqual(
            {"ci", "dependency_review", "documentation"},
            self.required("develop", ".github/workflows/documentation-pages.yml"),
        )

    def test_outbox_documentation_also_runs_outbox_contract_gate(self):
        selected = self.required("develop", "docs/outbox.md")
        self.assertIn("documentation", selected)
        self.assertIn("transactional_outbox", selected)

    def test_entity_framework_change_selects_relevant_specialized_gates(self):
        selected = self.required("develop", "src/TCJ.EntityFrameworkCore/Outbox/Processor.cs")
        for gate in (
            "reproducible_builds",
            "documentation",
            "sqlserver_integration",
            "consumer_compatibility",
            "upgrade_compatibility",
            "concurrency_stress",
            "health_checks",
            "transactional_outbox",
            "performance",
        ):
            self.assertIn(gate, selected)
        self.assertNotIn("mutation", selected)
        self.assertNotIn("property_fuzz", selected)
        self.assertNotIn("aspnetcore_integration", selected)
        self.assertNotIn("resilience", selected)

    def test_core_change_fans_out_to_every_conditional_gate(self):
        selected = self.required("develop", "src/TCJ.Core/Results/Result.cs")
        expected = {"ci", "dependency_review", *POLICY["gates"].keys()}
        self.assertEqual(expected, selected)

    def test_main_release_infrastructure_escalates_release_facing_gates(self):
        selected = self.required("main", "eng/Packaging.props")
        for gate in POLICY["mainEscalation"]["gates"]:
            self.assertIn(gate, selected)
        self.assertNotIn("mutation", selected)
        self.assertNotIn("property_fuzz", selected)

    def test_gate_infrastructure_change_runs_all_conditional_gates(self):
        selected = self.required("develop", ".github/workflows/required-pr-gate.yml")
        self.assertEqual({"ci", "dependency_review", *POLICY["gates"].keys()}, selected)

    def test_unknown_target_is_rejected(self):
        with self.assertRaises(ValueError):
            MODULE.resolve_plan(POLICY, "feature", ["src/TCJ.Core/Foo.cs"])

    def test_required_failure_fails_aggregate(self):
        plan = MODULE.resolve_plan(POLICY, "develop", ["docs/getting-started.md"])
        results = {name: "skipped" for name in plan["gates"]}
        results["ci"] = "success"
        results["dependency_review"] = "success"
        results["documentation"] = "failure"
        valid, _ = MODULE.verify_results(plan, results)
        self.assertFalse(valid)

    def test_skipped_non_required_gate_is_allowed(self):
        plan = MODULE.resolve_plan(POLICY, "develop", ["CODE_OF_CONDUCT.md"])
        results = {name: "skipped" for name in plan["gates"]}
        results["ci"] = "success"
        results["dependency_review"] = "success"
        valid, _ = MODULE.verify_results(plan, results)
        self.assertTrue(valid)


if __name__ == "__main__":
    unittest.main()
