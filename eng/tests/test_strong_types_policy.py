from pathlib import Path
import json


def test_strong_types_policy_contract():
    root = Path(__file__).resolve().parents[2]
    policy = json.loads((root / "eng/strong-types-policy.json").read_text(encoding="utf-8"))
    assert policy["stronglyTypedIds"]["supportedBackingTypes"] == ["Guid", "int", "long"]
    assert policy["valueObjects"]["supportedBackingTypes"] == ["string", "Guid", "int", "long", "decimal"]
    assert policy["stronglyTypedIds"]["implicitConversions"] is False
    assert policy["valueObjects"]["compositeObjects"] is False
