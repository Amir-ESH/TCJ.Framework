from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-documentation.py"
SPEC = importlib.util.spec_from_file_location("tcj_verify_documentation", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class DocumentationVerifierTests(unittest.TestCase):
    def policy(self) -> dict:
        return {
            "requireTypeSummaries": True,
            "requireMemberSummaries": True,
            "requireParameterDocumentation": True,
            "requireReturnDocumentation": True,
            "failOnUnresolvedCrefs": True,
            "failOnMalformedXmlDocumentation": True,
            "failOnBrokenInternalLinks": True,
            "baselineMaximumEntries": 20,
            "minimumPublicApiDocumentationPercent": 0.0,
            "requiredPackages": ["Example"],
            "projects": {"Example": "src/Example/Example.csproj"},
            "selectedExamples": [],
        }

    def item(self, **overrides):
        values = {
            "package": "Example",
            "documentation_id": "M:Example.Widget.Run(System.String)",
            "kind": "Method",
            "file": "src/Example/Widget.cs",
            "line": 10,
            "name": "Run",
            "visibility": "public",
            "parameter_names": ("value",),
            "type_parameter_names": (),
            "requires_returns": True,
            "inherited": False,
            "has_summary": True,
            "documented_parameters": ("value",),
            "documented_type_parameters": (),
            "has_returns": True,
        }
        values.update(overrides)
        return MODULE.ApiItem(**values)

    def test_fully_documented_api_has_no_missing_elements(self):
        self.assertEqual((), self.item().missing_elements(self.policy()))

    def test_missing_type_summary_is_detected(self):
        item = self.item(kind="Type", documentation_id="T:Example.Widget", parameter_names=(),
                         requires_returns=False, has_summary=False)
        self.assertIn("summary", item.missing_elements(self.policy()))

    def test_missing_parameter_documentation_is_detected(self):
        item = self.item(documented_parameters=())
        self.assertIn("param:value", item.missing_elements(self.policy()))

    def test_missing_return_documentation_is_detected(self):
        item = self.item(has_returns=False)
        self.assertIn("returns", item.missing_elements(self.policy()))

    def test_inheritdoc_satisfies_member_requirements(self):
        item = self.item(inherited=True, has_summary=False, documented_parameters=(), has_returns=False)
        self.assertEqual((), item.missing_elements(self.policy()))

    def test_unresolved_cref_is_blocking(self):
        with tempfile.TemporaryDirectory() as directory:
            xml_path = Path(directory) / "Example.xml"
            xml_path.write_text(
                '<doc><members><member name="T:Example.Widget"><summary>'
                '<see cref="!:Missing.Type"/></summary></member></members></doc>',
                encoding="utf-8",
            )
            with mock.patch.object(MODULE, "find_xml_docs", return_value={"Example": xml_path}):
                with self.assertRaisesRegex(MODULE.DocumentationError, "Unresolved XML documentation cref"):
                    MODULE.verify_xml_docs(self.policy(), None)

    def test_malformed_xml_documentation_is_blocking(self):
        with tempfile.TemporaryDirectory() as directory:
            xml_path = Path(directory) / "Example.xml"
            xml_path.write_text("<doc><members>", encoding="utf-8")
            with mock.patch.object(MODULE, "find_xml_docs", return_value={"Example": xml_path}):
                with self.assertRaisesRegex(MODULE.DocumentationError, "Malformed XML documentation"):
                    MODULE.verify_xml_docs(self.policy(), None)

    def test_duplicate_documentation_id_is_blocking(self):
        with tempfile.TemporaryDirectory() as directory:
            xml_path = Path(directory) / "Example.xml"
            xml_path.write_text(
                '<doc><members><member name="T:Example.Widget"/><member name="T:Example.Widget"/>'
                '</members></doc>', encoding="utf-8")
            with mock.patch.object(MODULE, "find_xml_docs", return_value={"Example": xml_path}):
                with self.assertRaisesRegex(MODULE.DocumentationError, "Duplicate XML documentation ID"):
                    MODULE.verify_xml_docs(self.policy(), None)

    def test_broken_internal_link_is_blocking(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            docs = root / "docs"
            docs.mkdir()
            (docs / "index.md").write_text("[missing](missing.md)\n", encoding="utf-8")
            output = root / "artifacts"
            output.mkdir()
            with mock.patch.object(MODULE, "ROOT", root):
                with self.assertRaisesRegex(MODULE.DocumentationError, "Broken internal documentation links"):
                    MODULE.verify_markdown_links(self.policy(), output)

    def test_link_outside_docfx_conceptual_root_is_blocking(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            docs = root / "docs"
            docs.mkdir()
            (root / "README.md").write_text("# Repository\n", encoding="utf-8")
            (docs / "index.md").write_text("[repository](../README.md)\n", encoding="utf-8")
            with mock.patch.object(MODULE, "ROOT", root):
                with self.assertRaisesRegex(MODULE.DocumentationError, "Broken internal documentation links"):
                    MODULE.verify_markdown_links(self.policy())

    def test_stale_baseline_entry_is_blocking(self):
        with tempfile.TemporaryDirectory() as directory:
            baseline_path = Path(directory) / "documentation-baseline.json"
            baseline_path.write_text(json.dumps({
                "schemaVersion": 1,
                "entries": [{
                    "package": "Example",
                    "documentationId": "T:Example.Removed",
                    "memberKind": "Type",
                    "missingElement": "summary",
                    "reason": "Existing debt",
                    "recordedDate": "2026-08-06",
                    "targetMilestone": "0.1.0-rc.1",
                }],
            }), encoding="utf-8")
            with mock.patch.object(MODULE, "BASELINE_PATH", baseline_path):
                with self.assertRaisesRegex(MODULE.DocumentationError, "Stale documentation baseline"):
                    MODULE.validate_baseline(self.policy(), [self.item()])

    def test_new_undocumented_api_is_blocking(self):
        item = self.item(has_returns=False)
        with self.assertRaisesRegex(MODULE.DocumentationError, "New undocumented public API"):
            MODULE.assess_source_documentation(self.policy(), [item], {})

    def test_documentation_coverage_regression_is_blocking(self):
        item = self.item(has_returns=False)
        policy = self.policy()
        policy["minimumPublicApiDocumentationPercent"] = 100.0
        baseline = {(item.documentation_id, "returns"): {}}
        with self.assertRaisesRegex(MODULE.DocumentationError, "coverage .* below policy minimum"):
            MODULE.assess_source_documentation(policy, [item], baseline)

    def test_invalid_required_snippet_is_blocking(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "artifacts"
            output.mkdir()
            policy = self.policy()
            with mock.patch.object(MODULE, "ROOT", root), \
                 mock.patch.object(MODULE, "collect_snippets", return_value=[("example", "docs/example.md", "invalid code\n")]), \
                 mock.patch.object(subprocess, "run", return_value=subprocess.CompletedProcess([], 1, stdout="compile failed")):
                with self.assertRaisesRegex(MODULE.DocumentationError, "failed to compile"):
                    MODULE.compile_snippets(policy, output, "Release")
                self.assertTrue((output / "snippets" / "build.log").is_file())


    def test_git_tracking_rejects_ignored_package_landing_page(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            required = [
                root / ".config" / "dotnet-tools.json",
                root / "docfx" / "docfx.json",
                root / "eng" / "documentation-policy.json",
                root / "docs" / "index.md",
                root / "docs" / "toc.yml",
                root / "docs" / "packages" / "index.md",
                root / "docs" / "packages" / "tcj-core.md",
                root / "docs" / "examples.md",
            ]
            for path in required:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("content\n", encoding="utf-8")
            policy = {
                "packagePages": {"TCJ.Core": "docs/packages/tcj-core.md"},
                "selectedExamples": [{"id": "example", "area": "Example", "path": "docs/examples.md"}],
            }
            ignored = root / "docs" / "packages" / "tcj-core.md"
            with mock.patch.object(MODULE, "ROOT", root), \
                 mock.patch.object(MODULE, "POLICY_PATH", root / "eng" / "documentation-policy.json"), \
                 mock.patch.object(MODULE, "DOCFX_CONFIG_PATH", root / "docfx" / "docfx.json"), \
                 mock.patch.object(MODULE, "TOOL_MANIFEST_PATH", root / ".config" / "dotnet-tools.json"), \
                 mock.patch.object(MODULE, "BASELINE_PATH", root / "eng" / "documentation-baseline.json"), \
                 mock.patch.object(MODULE, "git_ignored", side_effect=lambda path: path == ignored), \
                 mock.patch.object(MODULE, "git_tracked", return_value=True):
                with self.assertRaisesRegex(MODULE.DocumentationError, "ignored by Git: docs/packages/tcj-core.md"):
                    MODULE.validate_git_tracking(policy)

    def test_git_tracking_accepts_tracked_package_landing_pages(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            required = [
                root / ".config" / "dotnet-tools.json",
                root / "docfx" / "docfx.json",
                root / "eng" / "documentation-policy.json",
                root / "docs" / "index.md",
                root / "docs" / "toc.yml",
                root / "docs" / "packages" / "index.md",
                root / "docs" / "packages" / "tcj-core.md",
                root / "docs" / "examples.md",
            ]
            for path in required:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("content\n", encoding="utf-8")
            policy = {
                "packagePages": {"TCJ.Core": "docs/packages/tcj-core.md"},
                "selectedExamples": [{"id": "example", "area": "Example", "path": "docs/examples.md"}],
            }
            with mock.patch.object(MODULE, "ROOT", root), \
                 mock.patch.object(MODULE, "POLICY_PATH", root / "eng" / "documentation-policy.json"), \
                 mock.patch.object(MODULE, "DOCFX_CONFIG_PATH", root / "docfx" / "docfx.json"), \
                 mock.patch.object(MODULE, "TOOL_MANIFEST_PATH", root / ".config" / "dotnet-tools.json"), \
                 mock.patch.object(MODULE, "BASELINE_PATH", root / "eng" / "documentation-baseline.json"), \
                 mock.patch.object(MODULE, "git_ignored", return_value=False), \
                 mock.patch.object(MODULE, "git_tracked", return_value=True):
                MODULE.validate_git_tracking(policy)

    def test_api_page_counter_excludes_toc_and_xref(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ("Example.Widget.yml", "Example.Other.yml", "toc.yml", "xrefmap.yml"):
                (root / name).write_text("items: []\n", encoding="utf-8")
            self.assertEqual(2, MODULE.count_api_pages(root))


if __name__ == "__main__":
    unittest.main()
