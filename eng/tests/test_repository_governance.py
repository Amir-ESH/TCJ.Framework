from __future__ import annotations

import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


class RepositoryGovernanceTests(unittest.TestCase):
    def read(self, relative_path: str) -> str:
        return (ROOT / relative_path).read_text(encoding="utf-8")

    def test_readme_banner_is_branch_relative(self) -> None:
        readme = self.read("README.md")
        self.assertIn('src=".github/assets/tcj-framework-banner.png"', readme)
        self.assertNotIn(
            "raw.githubusercontent.com/Amir-ESH/TCJ.Framework/main/.github/assets/tcj-framework-banner.png",
            readme,
        )

    def test_governance_and_cla_are_linked_from_readme(self) -> None:
        readme = self.read("README.md")
        self.assertIn("[Project governance](GOVERNANCE.md)", readme)
        self.assertIn("[Contributor License Agreement](CLA.md)", readme)
        self.assertIn("[`TRADEMARKS.md`](TRADEMARKS.md)", readme)
        self.assertIn("[`LICENSE.txt`](LICENSE.txt)", readme)

    def test_codeowners_keeps_official_repository_under_owner_review(self) -> None:
        codeowners = self.read(".github/CODEOWNERS")
        self.assertIn("* @Amir-ESH", codeowners)
        for path in (
            "/LICENSE.txt @Amir-ESH",
            "/CLA.md @Amir-ESH",
            "/GOVERNANCE.md @Amir-ESH",
            "/TRADEMARKS.md @Amir-ESH",
            "/.github/CODEOWNERS @Amir-ESH",
        ):
            self.assertIn(path, codeowners)


    def test_repository_uses_single_canonical_license_file(self) -> None:
        self.assertTrue((ROOT / "LICENSE.txt").is_file())
        self.assertFalse((ROOT / "LICENSE").exists())

    def test_governance_preserves_fork_freedom_and_owner_authority(self) -> None:
        governance = self.read("GOVERNANCE.md")
        self.assertIn("charge money for distributing copies", governance)
        self.assertIn("Only Project Owners may approve a future license change", governance)
        self.assertIn("require Code Owner approval for changes submitted by non-owners", governance)

    def test_cla_retains_contributor_ownership_and_grants_relicensing_rights(self) -> None:
        cla = self.read("CLA.md")
        self.assertIn("You retain the copyright", cla)
        self.assertIn("license or relicense the Contribution", cla)
        self.assertIn("does **not**, by itself", cla)

    def test_current_outbound_license_remains_lgpl(self) -> None:
        packaging = self.read("eng/Packaging.props")
        manifest = self.read("eng/release-manifest.json")
        self.assertIn("<PackageLicenseExpression>LGPL-3.0-only</PackageLicenseExpression>", packaging)
        self.assertIn('"licenseExpression": "LGPL-3.0-only"', manifest)


if __name__ == "__main__":
    unittest.main()
