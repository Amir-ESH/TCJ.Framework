# Dependency and supply-chain security

TCJ Framework validates dependency security at restore time and reviews dependency changes before they are merged.

## Repository NuGet policy

[`NuGet.Config`](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/NuGet.Config) clears inherited package and audit sources, enables only the NuGet.org V3 feed, and maps every package ID to that source. This prevents machine-level NuGet configuration from silently introducing an additional restore source.

The published-package smoke project also uses this repository configuration. It must not define its own `RestoreSources` property.

## NuGet Audit policy

[`eng/DependencySecurity.props`](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/eng/DependencySecurity.props) is imported by [`Directory.Build.props`](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/Directory.Build.props), so it applies to source projects, tests, samples, and the published-package consumer.

The policy is:

```xml
<NuGetAudit>true</NuGetAudit>
<NuGetAuditMode>all</NuGetAuditMode>
<NuGetAuditLevel>moderate</NuGetAuditLevel>
```

Every restore audits both direct and transitive dependencies. Advisories with moderate, high, or critical severity fail restore through `NU1902`, `NU1903`, or `NU1904`. Audit transport or source failures (`NU1900` and `NU1905`) also fail restore so a green build cannot silently mean that auditing was skipped.

Low-severity advisories are outside the blocking threshold. They may still be evaluated separately when deciding whether to update a dependency.

## Pull-request dependency review

[`.github/workflows/dependency-review.yml`](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/.github/workflows/dependency-review.yml) runs for pull requests targeting `develop` or `main`. It uses GitHub Dependency Review to reject newly introduced runtime or development dependencies with moderate-or-higher known vulnerabilities.

This check complements NuGet Audit:

- Dependency Review evaluates the dependency change introduced by the pull request.
- NuGet Audit evaluates the complete resolved graph during restore.

## Scheduled audit

[`.github/workflows/dependency-audit.yml`](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/.github/workflows/dependency-audit.yml) runs every Monday and can also be started manually. A schedule is necessary because a new advisory can be published even when the repository has not changed.

The workflow audits:

1. the complete `TCJ.slnx` dependency graph;
2. the external consumer that installs the latest published TCJ packages.

## Handling an advisory

1. Identify whether the affected package is direct or transitive.
2. Upgrade the nearest direct dependency that resolves the vulnerable package.
3. Run `dotnet restore TCJ.slnx --force-evaluate`.
4. Run build, tests, pack, package validation, and the published-package smoke workflow as applicable.
5. Document user impact in `CHANGELOG.md` when a published package is affected.

Do not disable `NuGetAudit`, reduce `NuGetAuditMode`, lower the blocking severity, or suppress an advisory merely to make CI green. A temporary suppression requires a documented risk assessment, a tracking issue, a fixed expiry or removal condition, and maintainer approval.

## Local validation

```bash
python3 eng/verify-dependency-security.py
dotnet restore TCJ.slnx --force-evaluate
```

A successful restore confirms that the configured audit source was reachable and that no moderate-or-higher known vulnerability was reported for the resolved graph at that time. It is not a guarantee that dependencies contain no undiscovered vulnerabilities.

## SBOM inventory

NuGet Audit and Dependency Review identify known vulnerabilities, while the release CycloneDX SBOM records the exact dependency inventory, resolved versions, relationships, licenses, and hashes associated with the release artifacts. Use the SBOM during advisory triage to identify affected versions and paths, then evaluate reachability and impact separately. An SBOM is not a vulnerability scanner and does not replace scheduled audits. See [Software bill of materials](software-bill-of-materials.md).
