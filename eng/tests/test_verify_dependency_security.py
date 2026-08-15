from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "verify-dependency-security.py"
SPEC = importlib.util.spec_from_file_location("verify_dependency_security", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class PublishedNuGetConfigTests(unittest.TestCase):
    def setUp(self) -> None:
        self.original_root = MODULE.ROOT

    def tearDown(self) -> None:
        MODULE.ROOT = self.original_root

    def write_config(self, root: Path, source: str, mapping_key: str = "nuget.org") -> Path:
        path = root / "smoke" / "NuGet.Published.Config"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(
            f'''<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="{mapping_key}" value="{source}" protocolVersion="3" />
  </packageSources>
  <auditSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </auditSources>
  <packageSourceMapping>
    <packageSource key="{mapping_key}">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
''',
            encoding="utf-8",
        )
        return path

    def test_nuget_org_only_config_is_accepted(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            MODULE.ROOT = root
            path = self.write_config(root, "https://api.nuget.org/v3/index.json")

            MODULE.verify_published_nuget_config(path)

    def test_local_candidate_source_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            MODULE.ROOT = root
            path = self.write_config(root, "../artifacts/packages", "tcj-local")

            with self.assertRaisesRegex(RuntimeError, "nuget.org"):
                MODULE.verify_published_nuget_config(path)

    def test_non_nuget_audit_source_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            MODULE.ROOT = root
            path = self.write_config(root, "https://api.nuget.org/v3/index.json")
            content = path.read_text(encoding="utf-8").replace(
                '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />\n  </auditSources>',
                '<add key="private" value="https://packages.example.invalid/v3/index.json" protocolVersion="3" />\n  </auditSources>',
                1,
            )
            path.write_text(content, encoding="utf-8")

            with self.assertRaisesRegex(RuntimeError, "audit source"):
                MODULE.verify_published_nuget_config(path)


if __name__ == "__main__":
    unittest.main()
