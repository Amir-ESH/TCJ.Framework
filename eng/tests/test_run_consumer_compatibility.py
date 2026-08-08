import importlib.util
import json
import tempfile
import unittest
import sys
from pathlib import Path

RUNNER_PATH = Path(__file__).resolve().parents[2] / "compatibility" / "scripts" / "run-compatibility.py"
SPEC = importlib.util.spec_from_file_location("tcj_consumer_runner", RUNNER_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class ConsumerRunnerTests(unittest.TestCase):
    def test_warning_pattern_ignores_zero_warning_summary(self) -> None:
        self.assertIsNone(MODULE.WARNING_PATTERN.search("0 Warning(s)"))
        self.assertIsNone(MODULE.WARNING_PATTERN.search("Build succeeded with no warnings."))

    def test_warning_pattern_detects_compiler_and_nuget_diagnostics(self) -> None:
        self.assertIsNotNone(MODULE.WARNING_PATTERN.search("x.cs(1,1): warning CA1234: message"))
        self.assertIsNotNone(MODULE.WARNING_PATTERN.search("project.csproj : warning NU1605: downgrade"))

    def test_architecture_normalization(self) -> None:
        self.assertEqual("x64", MODULE.normalize_architecture("x86_64"))
        self.assertEqual("x64", MODULE.normalize_architecture("AMD64"))
        self.assertEqual("arm64", MODULE.normalize_architecture("aarch64"))

    def _write_metadata(self, cache: Path, source: str) -> None:
        package = cache / "tcj.core" / "1.2.3"
        package.mkdir(parents=True)
        (package / ".nupkg.metadata").write_text(json.dumps({"version": 2, "source": source}), encoding="utf-8")

    def test_local_source_metadata_is_verified(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            cache = root / "cache"
            feed = root / "feed"
            feed.mkdir()
            self._write_metadata(cache, str(feed.resolve()))
            self.assertEqual("pass", MODULE.verify_package_sources(cache, ["TCJ.Core"], "1.2.3", "local", feed))

    def test_relative_local_source_metadata_is_verified(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            cache = root / "cache"
            compatibility_root = root / "compatibility"
            feed = root / "artifacts" / "compatibility" / "packages"
            compatibility_root.mkdir(parents=True)
            feed.mkdir(parents=True)
            self._write_metadata(cache, "../artifacts/compatibility/packages")
            original_root = MODULE.ROOT
            original_compatibility_root = MODULE.COMPATIBILITY_ROOT
            MODULE.ROOT = root
            MODULE.COMPATIBILITY_ROOT = compatibility_root
            try:
                self.assertEqual("pass", MODULE.verify_package_sources(cache, ["TCJ.Core"], "1.2.3", "local", feed))
            finally:
                MODULE.ROOT = original_root
                MODULE.COMPATIBILITY_ROOT = original_compatibility_root

    def test_remote_source_is_rejected_in_local_mode(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            cache = root / "cache"
            feed = root / "feed"
            feed.mkdir()
            self._write_metadata(cache, "https://api.nuget.org/v3/index.json")
            with self.assertRaisesRegex(MODULE.CompatibilityError, "unexpected remote source"):
                MODULE.verify_package_sources(cache, ["TCJ.Core"], "1.2.3", "local", feed)

    def test_published_source_must_be_nuget_org(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            cache = root / "cache"
            feed = root / "feed"
            feed.mkdir()
            self._write_metadata(cache, "https://example.invalid/v3/index.json")
            with self.assertRaisesRegex(MODULE.CompatibilityError, "expected NuGet.org"):
                MODULE.verify_package_sources(cache, ["TCJ.Core"], "1.2.3", "published", feed)


if __name__ == "__main__":
    unittest.main()
