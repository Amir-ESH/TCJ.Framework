import importlib.util
import sys
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "eng" / "verify-outbox.py"
SPEC = importlib.util.spec_from_file_location("verify_outbox", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class OutboxVerifierTests(unittest.TestCase):
    def test_generic_build_output_rules_are_accepted(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            subprocess.run(["git", "init", "--quiet"], cwd=root, check=True)
            (root / ".gitignore").write_text("**/[Bb]in/*\n**/[Oo]bj/*\n", encoding="utf-8")
            MODULE.require_ignored(root, (
                "tests/TCJ.Outbox.Tests/bin/.tcj-ignore-probe",
                "tests/TCJ.Outbox.Tests/obj/.tcj-ignore-probe",
            ))

    def test_missing_build_output_ignore_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            subprocess.run(["git", "init", "--quiet"], cwd=root, check=True)
            (root / ".gitignore").write_text("", encoding="utf-8")
            with self.assertRaisesRegex(MODULE.OutboxError, "does not ignore"):
                MODULE.require_ignored(root, ("tests/TCJ.Outbox.Tests/bin/.tcj-ignore-probe",))

    def test_metadata_based_system_text_json_contract_is_accepted(self):
        source = """
        using System.Text.Json.Serialization.Metadata;
        if (_options.TypeInfoResolver is null && JsonSerializer.IsReflectionEnabledByDefault)
        {
            _options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        }
        JsonTypeInfo typeInfo = _options.GetTypeInfo(eventType);
        JsonSerializer.Serialize(domainEvent, typeInfo);
        JsonSerializer.Deserialize(payload, typeInfo);
        """

        MODULE.validate_system_text_json_serializer_source(source)

    def test_runtime_type_based_system_text_json_contract_is_rejected(self):
        source = """
        if (_options.TypeInfoResolver is null && JsonSerializer.IsReflectionEnabledByDefault)
        {
            _options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        }
        JsonTypeInfo typeInfo = _options.GetTypeInfo(eventType);
        JsonSerializer.Serialize(domainEvent, typeInfo);
        JsonSerializer.Deserialize(payload, typeInfo);
        JsonSerializer.Deserialize(payload, eventType, _options);
        """

        with self.assertRaises(MODULE.OutboxError):
            MODULE.validate_system_text_json_serializer_source(source)


if __name__ == "__main__":
    unittest.main()
