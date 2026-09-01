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

SCRIPT = ENG / "stage-compatibility-packages.py"
SPEC = importlib.util.spec_from_file_location("stage_compatibility_packages", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)

ROOT = ENG.parent
VERSION = "0.1.0-preview.4"


class CompatibilityPackageStagingTests(unittest.TestCase):
    def write_manifest(self, path: Path) -> None:
        path.write_text(
            json.dumps(
                {
                    "schemaVersion": 2,
                    "version": VERSION,
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

    def test_stages_only_runtime_packages_from_release_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest = root / "release-manifest.json"
            source = root / "packages"
            destination = root / "compatibility"
            source.mkdir()
            self.write_manifest(manifest)

            artifacts = {
                f"TCJ.Core.{VERSION}.nupkg": b"runtime-primary",
                f"TCJ.Core.{VERSION}.snupkg": b"runtime-symbols",
                f"TCJ.Generators.{VERSION}.nupkg": b"tooling-primary",
                f"TCJ.Generators.{VERSION}.snupkg": b"tooling-symbols",
            }
            for filename, content in artifacts.items():
                (source / filename).write_bytes(content)

            staged = MODULE.stage_runtime_packages(
                manifest,
                source,
                destination,
                VERSION,
            )

            self.assertEqual(("TCJ.Core",), staged)
            self.assertEqual(
                {
                    f"TCJ.Core.{VERSION}.nupkg",
                    f"TCJ.Core.{VERSION}.snupkg",
                },
                {path.name for path in destination.iterdir()},
            )
            self.assertEqual(
                b"runtime-primary",
                (destination / f"TCJ.Core.{VERSION}.nupkg").read_bytes(),
            )

    def test_missing_runtime_symbol_package_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest = root / "release-manifest.json"
            source = root / "packages"
            destination = root / "compatibility"
            source.mkdir()
            self.write_manifest(manifest)
            (source / f"TCJ.Core.{VERSION}.nupkg").write_bytes(b"runtime-primary")

            with self.assertRaisesRegex(
                MODULE.StagingError,
                "Missing runtime release package artifacts",
            ):
                MODULE.stage_runtime_packages(
                    manifest,
                    source,
                    destination,
                    VERSION,
                )

    def test_overlapping_source_and_destination_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest = root / "release-manifest.json"
            source = root / "packages"
            source.mkdir()
            self.write_manifest(manifest)

            with self.assertRaisesRegex(MODULE.StagingError, "must not overlap"):
                MODULE.stage_runtime_packages(
                    manifest,
                    source,
                    source / "compatibility",
                    VERSION,
                )

    def test_manifest_version_must_match_requested_version(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest = root / "release-manifest.json"
            source = root / "packages"
            source.mkdir()
            self.write_manifest(manifest)

            with self.assertRaisesRegex(MODULE.StagingError, "manifest version"):
                MODULE.stage_runtime_packages(
                    manifest,
                    source,
                    root / "compatibility",
                    "0.1.0-preview.5",
                )

    def test_release_workflows_use_runtime_filtered_compatibility_staging(self) -> None:
        for relative_path in (
            ".github/workflows/release-preflight.yml",
            ".github/workflows/release.yml",
        ):
            workflow = (ROOT / relative_path).read_text(encoding="utf-8")
            self.assertIn("python3 eng/stage-compatibility-packages.py", workflow)
            self.assertNotIn(
                "cp artifacts/packages/* artifacts/compatibility/packages/",
                workflow,
            )


if __name__ == "__main__":
    unittest.main()
