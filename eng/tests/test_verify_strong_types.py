import importlib.util
import json
import tempfile
import sys
import unittest
import zipfile
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify-strong-types.py"
SPEC = importlib.util.spec_from_file_location("verify_strong_types", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class StrongTypesVerifierTests(unittest.TestCase):
    def test_current_repository_configuration_is_valid(self) -> None:
        policy = MODULE.validate_config(MODULE.ROOT)

        self.assertEqual("TCJ.Generators", policy["generatorPackage"]["id"])
        self.assertEqual(
            ["TCJ.StrongTypes.StrongIdModels", "TCJ.StrongTypes.ValueObjectModels"],
            policy["incrementalTrackingNames"],
        )

    def test_generator_package_layout_accepts_analyzer_only_implementation(self) -> None:
        policy = MODULE.read_policy(MODULE.ROOT)
        with tempfile.TemporaryDirectory() as directory:
            packages = Path(directory)
            version = "9.9.9-test"
            package = packages / f"TCJ.Generators.{version}.nupkg"
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr("TCJ.Generators.nuspec", "<package />")
                archive.writestr("analyzers/dotnet/cs/TCJ.Generators.dll", b"generator")

            result = MODULE.verify_generator_package(packages, version, policy)

        self.assertEqual("analyzers/dotnet/cs/TCJ.Generators.dll", result["asset"])
        self.assertEqual([], result["forbiddenRuntimeAssets"])

    def test_generator_package_layout_rejects_runtime_implementation_asset(self) -> None:
        policy = MODULE.read_policy(MODULE.ROOT)
        with tempfile.TemporaryDirectory() as directory:
            packages = Path(directory)
            version = "9.9.9-test"
            package = packages / f"TCJ.Generators.{version}.nupkg"
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr("analyzers/dotnet/cs/TCJ.Generators.dll", b"generator")
                archive.writestr("lib/net10.0/TCJ.Generators.dll", b"runtime-leak")

            with self.assertRaisesRegex(MODULE.StrongTypesError, "forbidden runtime assets"):
                MODULE.verify_generator_package(packages, version, policy)

    def test_source_path_candidates_accepts_file_uri(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory).resolve()
            expected = MODULE.os.path.normcase(MODULE.os.path.normpath(str(source)))

            self.assertIn(expected, MODULE.source_path_candidates(source.as_uri()))

    def test_many_type_fixture_has_one_declaration_per_policy_count(self) -> None:
        policy = json.loads((MODULE.ROOT / "eng/strong-types-policy.json").read_text(encoding="utf-8"))
        source = MODULE.fixture_source(
            int(policy["determinism"]["strongIdCount"]),
            int(policy["determinism"]["valueObjectCount"]),
        )

        self.assertEqual(policy["determinism"]["strongIdCount"], source.count("[StronglyTypedId<long>]"))
        self.assertEqual(policy["determinism"]["valueObjectCount"], source.count("[ValueObject<int>]"))


if __name__ == "__main__":
    unittest.main()
