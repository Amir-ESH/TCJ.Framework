import importlib.util
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "eng" / "verify-inbox.py"
SPEC = importlib.util.spec_from_file_location("verify_inbox", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class InboxVerifierTests(unittest.TestCase):
    def test_metadata_based_system_text_json_contract_is_accepted(self):
        source = """
        using System.Text.Json.Serialization.Metadata;
        JsonTypeInfo typeInfo = _options.GetTypeInfo(messageType);
        JsonSerializer.Deserialize(payload, typeInfo);
        """

        MODULE.validate_system_text_json_serializer_source(source)

    def test_runtime_type_based_system_text_json_contract_is_rejected(self):
        source = """
        using System.Text.Json.Serialization.Metadata;
        JsonTypeInfo typeInfo = _options.GetTypeInfo(messageType);
        JsonSerializer.Deserialize(payload, typeInfo);
        JsonSerializer.Deserialize(payload, messageType, _options);
        """

        with self.assertRaises(MODULE.InboxError):
            MODULE.validate_system_text_json_serializer_source(source)


if __name__ == "__main__":
    unittest.main()
