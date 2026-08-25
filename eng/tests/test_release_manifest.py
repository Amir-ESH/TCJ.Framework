from __future__ import annotations

import sys
import unittest
from pathlib import Path

ENG = Path(__file__).resolve().parents[1]
if str(ENG) not in sys.path:
    sys.path.insert(0, str(ENG))

from sbom_common import get_release_package_ids, get_release_packages


class ReleaseManifestCompatibilityTests(unittest.TestCase):
    def test_new_schema_normalizes_runtime_and_tooling_packages(self) -> None:
        manifest = {
            "releasePackages": {
                "runtime": [{"id": "TCJ.Core"}],
                "tooling": [
                    {
                        "id": "TCJ.Generators",
                        "assetPath": "analyzers/dotnet/cs",
                        "forbidAssets": ["lib/", "runtime/"],
                    }
                ],
            }
        }

        packages = get_release_packages(manifest)

        self.assertEqual(
            [
                {
                    "id": packages[0].package_id,
                    "type": packages[0].package_type,
                    "assetPath": packages[0].asset_path,
                    "forbidAssets": list(packages[0].forbid_assets),
                },
                {
                    "id": packages[1].package_id,
                    "type": packages[1].package_type,
                    "assetPath": packages[1].asset_path,
                    "forbidAssets": list(packages[1].forbid_assets),
                },
            ],
            [
                {
                    "id": "TCJ.Core",
                    "type": "runtime",
                    "assetPath": None,
                    "forbidAssets": [],
                },
                {
                    "id": "TCJ.Generators",
                    "type": "tooling",
                    "assetPath": "analyzers/dotnet/cs",
                    "forbidAssets": ["lib", "runtime"],
                },
            ],
        )
        self.assertEqual(("TCJ.Core",), get_release_package_ids(manifest, "runtime"))
        self.assertEqual(("TCJ.Generators",), get_release_package_ids(manifest, "tooling"))

    def test_legacy_schema_is_read_as_runtime_packages(self) -> None:
        manifest = {"packages": ["TCJ.Core", "TCJ.AspNetCore"]}

        packages = get_release_packages(manifest)

        self.assertEqual(("TCJ.Core", "TCJ.AspNetCore"), get_release_package_ids(manifest))
        self.assertTrue(all(item.package_type == "runtime" for item in packages))

    def test_new_schema_rejects_duplicate_package_ids(self) -> None:
        manifest = {
            "releasePackages": {
                "runtime": [{"id": "TCJ.Core"}],
                "tooling": [{
                    "id": "tcj.core",
                    "assetPath": "analyzers/dotnet/cs",
                    "forbidAssets": [],
                }],
            }
        }

        with self.assertRaisesRegex(ValueError, "unique"):
            get_release_packages(manifest)
