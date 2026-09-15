import importlib.util
import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("verify_message_contracts", ROOT / "eng/verify-message-contracts.py")
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)

class MessageContractVerifierTests(unittest.TestCase):
    def test_current_configuration_is_valid(self):
        verifier.validate_config()

    def test_path_traversal_is_rejected(self):
        self.assertFalse(verifier.safe_relative_path("../contracts/schema.json"))
        self.assertFalse(verifier.safe_relative_path("contracts\\schema.json"))
        self.assertTrue(verifier.safe_relative_path("contracts/test/v1/schema.json"))

    def test_sensitive_markers_are_detected_case_insensitively(self):
        self.assertTrue(verifier.contains_secret_marker(b"SharedAccessKey=synthetic"))
        self.assertFalse(verifier.contains_secret_marker(b'{"id":"fixture-1"}'))

    @unittest.skipUnless(shutil.which("git"), "git is required for ignore-rule verification")
    def test_required_source_shadowed_by_artifacts_ignore_is_detected(self):
        relative = "src/TCJ.Messaging.Contracts/Artifacts/MessageContractModels.cs"
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = root / relative
            source.parent.mkdir(parents=True)
            source.write_text("// fixture\n", encoding="utf-8")
            (root / ".gitignore").write_text("artifacts/\n", encoding="utf-8")
            subprocess.run(["git", "init", "-q", str(root)], check=True)
            subprocess.run(["git", "-C", str(root), "config", "core.ignorecase", "true"], check=True)

            self.assertEqual([relative], verifier.ignored_required_paths([relative], root))

            (root / ".gitignore").write_text(
                "artifacts/\n!src/TCJ.Messaging.Contracts/Artifacts/\n!src/TCJ.Messaging.Contracts/Artifacts/**\n",
                encoding="utf-8",
            )
            self.assertEqual([], verifier.ignored_required_paths([relative], root))

    def test_verify_requires_commit_matched_package_consumer_evidence(self):
        commit = "a" * 40
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            results = root / "results"
            output = root / "output"
            results.mkdir()
            (results / "message-contracts.trx").write_text("<TestRun />", encoding="utf-8")
            for run in ["run-a", "run-b"]:
                directory = output / run
                directory.mkdir(parents=True)
                (directory / "manifest.json").write_text('{"schemaVersion":1}\n', encoding="utf-8")
            package = root / "package-consumer.json"
            package.write_text(json.dumps({
                "schemaVersion": 1,
                "sourceCommit": commit,
                "packageId": "TCJ.Messaging.Contracts",
                "packageVersion": "0.1.0-preview.5",
                "status": "passed"
            }), encoding="utf-8")

            evidence = verifier.verify(results, output, commit, package)
            payload = json.loads(evidence.read_text(encoding="utf-8"))
            self.assertEqual("passed", payload["packageConsumerStatus"])

            package.write_text(json.dumps({
                "sourceCommit": "b" * 40,
                "packageId": "TCJ.Messaging.Contracts",
                "status": "passed"
            }), encoding="utf-8")
            with self.assertRaises(verifier.VerificationError):
                verifier.verify(results, output, commit, package)

if __name__ == "__main__":
    unittest.main()
