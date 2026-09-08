import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("verify_kafka", ROOT / "eng/verify-kafka.py")
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class KafkaVerifierTests(unittest.TestCase):
    def test_kafka_policy_and_contract_validate(self) -> None:
        MODULE.validate()


if __name__ == "__main__":
    unittest.main()
