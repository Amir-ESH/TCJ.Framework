from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "verify-published-packages.py"
SPEC = importlib.util.spec_from_file_location("verify_published_packages", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class PublishedPackageLicenseResolutionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.published = {
            "version": "0.1.0-preview.1",
            "licenseExpression": "MIT",
        }

    def write_release_manifest(self, directory: str) -> Path:
        path = Path(directory) / "release-manifest.json"
        path.write_text(
            json.dumps(
                {
                    "version": "0.1.0-preview.2",
                    "licenseExpression": "LGPL-3.0-only",
                }
            ),
            encoding="utf-8",
        )
        return path

    def test_published_version_uses_immutable_published_license(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            release_manifest = self.write_release_manifest(directory)
            actual = MODULE.resolve_expected_license_expression(
                "0.1.0-preview.1",
                self.published,
                release_manifest,
            )
            self.assertEqual(actual, "MIT")

    def test_current_candidate_uses_release_manifest_license(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            release_manifest = self.write_release_manifest(directory)
            actual = MODULE.resolve_expected_license_expression(
                "0.1.0-preview.2",
                self.published,
                release_manifest,
            )
            self.assertEqual(actual, "LGPL-3.0-only")

    def test_unknown_historical_version_requires_explicit_license(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            release_manifest = self.write_release_manifest(directory)
            with self.assertRaisesRegex(RuntimeError, "--license-expression"):
                MODULE.resolve_expected_license_expression(
                    "0.0.9",
                    self.published,
                    release_manifest,
                )

            actual = MODULE.resolve_expected_license_expression(
                "0.0.9",
                self.published,
                release_manifest,
                "MIT",
            )
            self.assertEqual(actual, "MIT")

    def test_current_release_readmes_are_loaded_from_docs_nuget(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "eng").mkdir()
            (root / "docs" / "nuget").mkdir(parents=True)
            release_manifest = root / "eng" / "release-manifest.json"
            release_manifest.write_text(
                json.dumps(
                    {
                        "version": "0.1.0-preview.3",
                        "packages": ["TCJ.Core"],
                    }
                ),
                encoding="utf-8",
            )
            expected = b"# TCJ.Core\n"
            (root / "docs" / "nuget" / "TCJ.Core.md").write_bytes(expected)

            readmes = MODULE.expected_readmes_for_current_release(
                "0.1.0-preview.3",
                release_manifest,
            )

            self.assertEqual(readmes, {"TCJ.Core": expected})
            self.assertIsNone(
                MODULE.expected_readmes_for_current_release(
                    "0.1.0-preview.2",
                    release_manifest,
                )
            )


if __name__ == "__main__":
    unittest.main()
