from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from xml.etree import ElementTree as ET

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-concurrency.py"
spec = importlib.util.spec_from_file_location("verify_concurrency", MODULE_PATH)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)


class ConcurrencyVerifierTests(unittest.TestCase):
    def policy(self):
        return json.loads((module.POLICY_PATH).read_text(encoding="utf-8"))

    def valid_trace(self, scenario="ScenarioOne", group="core", seed=4001):
        return {
            "schemaVersion": 1,
            "scenario": scenario,
            "group": group,
            "status": "Pass",
            "seed": seed,
            "workers": 2,
            "iterations": 3,
            "operationTimeoutMilliseconds": 1000,
            "scenarioTimeoutSeconds": 30,
            "operatingSystem": "Linux",
            "architecture": "X64",
            "runtime": ".NET 10",
            "commitSha": "abc",
            "startedAtUtc": "2026-08-09T00:00:00Z",
            "completedAtUtc": "2026-08-09T00:00:01Z",
            "expectedOperations": 6,
            "completedOperations": 6,
            "duplicateOperations": 0,
            "missingOperations": 0,
            "canceledOperations": 0,
            "deadlockDetected": False,
            "timeoutDetected": False,
            "scopeLeakage": 0,
            "identityLeakage": 0,
            "transactionInterference": 0,
            "exceptions": [],
            "timeline": [],
            "replay": {
                "scenario": scenario,
                "seed": seed,
                "workers": 2,
                "iterations": 3,
                "command": f"TCJ_STRESS_SEED={seed} dotnet test --filter FullyQualifiedName~{scenario}"
            }
        }

    def valid_project(self):
        return ET.fromstring("""
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.AspNetCore.TestHost" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" />
    <PackageReference Include="Testcontainers.MsSql" />
  </ItemGroup>
</Project>
""")

    def test_repository_policy_is_valid(self):
        scenarios = module.validate_policy_data(self.policy())
        self.assertGreaterEqual(len(scenarios), 20)

    def test_project_dependencies_accept_aspnetcore_framework_reference(self):
        module.validate_project_dependencies(self.valid_project())

    def test_project_dependencies_reject_missing_aspnetcore_framework_reference(self):
        project = self.valid_project()
        item_group = project.find("./ItemGroup")
        framework = project.find(".//FrameworkReference")
        self.assertIsNotNone(item_group)
        self.assertIsNotNone(framework)
        item_group.remove(framework)
        with self.assertRaisesRegex(
            module.VerificationError,
            "must reference Microsoft.AspNetCore.App",
        ):
            module.validate_project_dependencies(project)

    def test_project_dependencies_reject_redundant_dependency_injection_package(self):
        project = self.valid_project()
        item_group = project.find("./ItemGroup")
        self.assertIsNotNone(item_group)
        ET.SubElement(
            item_group,
            "PackageReference",
            {"Include": "Microsoft.Extensions.DependencyInjection"},
        )
        with self.assertRaisesRegex(
            module.VerificationError,
            "must not directly reference Microsoft.Extensions.DependencyInjection",
        ):
            module.validate_project_dependencies(project)

    def test_policy_rejects_too_few_scenarios(self):
        policy = self.policy()
        policy["scenarios"] = policy["scenarios"][:2]
        with self.assertRaises(module.VerificationError):
            module.validate_policy_data(policy)

    def test_policy_rejects_duplicate_seeds(self):
        policy = self.policy()
        policy["scheduledSeeds"] = [4001, 4001, 4003]
        with self.assertRaises(module.VerificationError):
            module.validate_policy_data(policy)

    def test_policy_rejects_unpinned_sql_image(self):
        policy = self.policy()
        policy["sqlServerContainerImage"] = "mcr.microsoft.com/mssql/server:latest"
        with self.assertRaises(module.VerificationError):
            module.validate_policy_data(policy)

    def test_trace_accepts_complete_pass(self):
        seed = module.validate_trace_data(self.valid_trace(), "ScenarioOne", "core")
        self.assertEqual(4001, seed)

    def test_trace_rejects_deadlock(self):
        trace = self.valid_trace()
        trace["deadlockDetected"] = True
        with self.assertRaises(module.VerificationError):
            module.validate_trace_data(trace, "ScenarioOne", "core")

    def test_trace_rejects_timeout(self):
        trace = self.valid_trace()
        trace["timeoutDetected"] = True
        with self.assertRaises(module.VerificationError):
            module.validate_trace_data(trace, "ScenarioOne", "core")

    def test_trace_rejects_duplicate_operations(self):
        trace = self.valid_trace()
        trace["duplicateOperations"] = 1
        with self.assertRaises(module.VerificationError):
            module.validate_trace_data(trace, "ScenarioOne", "core")

    def test_trace_rejects_missing_operations(self):
        trace = self.valid_trace()
        trace["completedOperations"] = 5
        trace["missingOperations"] = 1
        with self.assertRaises(module.VerificationError):
            module.validate_trace_data(trace, "ScenarioOne", "core")

    def test_trace_rejects_scope_leakage(self):
        trace = self.valid_trace()
        trace["scopeLeakage"] = 1
        with self.assertRaises(module.VerificationError):
            module.validate_trace_data(trace, "ScenarioOne", "core")

    def test_trace_rejects_identity_leakage(self):
        trace = self.valid_trace()
        trace["identityLeakage"] = 1
        with self.assertRaises(module.VerificationError):
            module.validate_trace_data(trace, "ScenarioOne", "core")

    def test_trace_rejects_transaction_interference(self):
        trace = self.valid_trace()
        trace["transactionInterference"] = 1
        with self.assertRaises(module.VerificationError):
            module.validate_trace_data(trace, "ScenarioOne", "core")

    def test_trace_rejects_missing_replay_metadata(self):
        trace = self.valid_trace()
        trace["replay"] = {}
        with self.assertRaises(module.VerificationError):
            module.validate_trace_data(trace, "ScenarioOne", "core")

    def test_parse_trx_reads_results(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "result.trx"
            path.write_text('''<?xml version="1.0" encoding="utf-8"?>\n<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="Namespace.Class.ScenarioOne" outcome="Passed" /></Results></TestRun>''', encoding="utf-8")
            results = module.parse_trx(Path(temp))
            self.assertEqual(["Passed"], results["Namespace.Class.ScenarioOne"])


if __name__ == "__main__":
    unittest.main()
