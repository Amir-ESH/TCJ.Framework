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

SCRIPT = ENG / "check-nuget-package-ids.py"
SPEC = importlib.util.spec_from_file_location("check_nuget_package_ids", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class NuGetPackageIdPolicyTests(unittest.TestCase):
    def test_transition_requires_published_ids_to_exist_and_new_ids_to_be_available(self) -> None:
        published = {"TCJ.Core", "TCJ.DependencyInjection"}

        self.assertIs(
            MODULE.expected_exists("transition", "TCJ.Core", published),
            True,
        )
        self.assertIs(
            MODULE.expected_exists("transition", "TCJ.Generators", published),
            False,
        )

    def test_explicit_policies_ignore_published_package_set(self) -> None:
        published = {"TCJ.Core"}

        self.assertIs(MODULE.expected_exists("available", "TCJ.Core", published), False)
        self.assertIs(MODULE.expected_exists("existing", "TCJ.Generators", published), True)
        self.assertIsNone(MODULE.expected_exists("report-only", "TCJ.Core", published))

    def test_published_tooling_is_existing_on_later_transitions(self) -> None:
        published = {"TCJ.Core", "TCJ.Generators"}

        self.assertIs(
            MODULE.expected_exists("transition", "TCJ.Generators", published),
            True,
        )

    def test_repository_manifests_produce_preview4_transition(self) -> None:
        root = Path(__file__).resolve().parents[2]
        current = MODULE.load_package_ids(root)
        published = MODULE.load_published_package_ids(root)

        self.assertEqual(
            current,
            [
                "TCJ.Core",
                "TCJ.DependencyInjection",
                "TCJ.EntityFrameworkCore",
                "TCJ.EntityFrameworkCore.SqlServer",
                "TCJ.AspNetCore",
                "TCJ.Generators",
            ],
        )
        self.assertNotIn("TCJ.Generators", published)
        self.assertTrue(
            all(
                MODULE.expected_exists("transition", package_id, published)
                is (package_id != "TCJ.Generators")
                for package_id in current
            )
        )

    def test_schema2_manifest_loads_runtime_and_tooling_ids(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            eng = root / "eng"
            eng.mkdir()
            (eng / "published-release.json").write_text(
                json.dumps(
                    {
                        "schemaVersion": 2,
                        "releasePackages": {
                            "runtime": [{"id": "TCJ.Core"}],
                            "tooling": [
                                {
                                    "id": "TCJ.Generators",
                                    "assetPath": "analyzers/dotnet/cs",
                                    "forbidAssets": ["lib/", "runtime/"],
                                }
                            ],
                        },
                    }
                ),
                encoding="utf-8",
            )

            self.assertEqual(
                MODULE.load_published_package_ids(root),
                {"TCJ.Core", "TCJ.Generators"},
            )


if __name__ == "__main__":
    unittest.main()
