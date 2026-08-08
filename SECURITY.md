# Security policy

## Supported versions

TCJ Framework is currently in preview and has not published a stable release.

| Version | Supported |
| --- | --- |
| Latest preview source on supported branches | Yes |
| Older preview snapshots | No guaranteed support |

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability.

Use GitHub's **Private vulnerability reporting** feature for the repository:

1. Open the repository's **Security** tab.
2. Choose **Report a vulnerability**.
3. Include affected components, impact, reproduction details, and any proposed mitigation.

Do not include real credentials, personal data, or production secrets in a report.

## Response expectations

The maintainer will make a best effort to acknowledge a report, assess impact, coordinate a fix, and publish remediation guidance. Response times are not guaranteed while the project is maintained independently.

## Security boundaries

Consumers remain responsible for:

- authentication and authorization policy;
- secret storage and connection-string protection;
- database permissions and migrations;
- dependency and platform patching;
- logging and disclosure controls;
- validating any custom error metadata returned to clients.

## Dependency security automation

Repository restores use NuGet Audit for direct and transitive dependencies. Moderate, high, and critical advisories block CI and release workflows. Pull requests also run GitHub Dependency Review, and a scheduled workflow re-audits the resolved graph when no source change has occurred. See [`docs/dependency-security.md`](docs/dependency-security.md).

Do not publicly disclose an exploitable dependency path before a remediation plan is available. Report it through Private vulnerability reporting with the package ID, affected version, advisory identifier, reachable TCJ code path, and suggested fixed version.

## Release artifact integrity

Official GitHub release assets include a CycloneDX JSON software bill of materials, a `SHA256SUMS` manifest covering the packages and SBOM, and GitHub artifact attestations produced by the tagged Release workflow. The SBOM inventories package versions, dependency relationships, licenses, hashes, repository identity, and the source commit; it complements rather than replaces vulnerability scanning. Consumers can verify the exact GitHub-hosted files and workflow provenance as described in [`docs/software-bill-of-materials.md`](docs/software-bill-of-materials.md) and [`docs/release-integrity.md`](docs/release-integrity.md). A missing package, incomplete SBOM, invalid checksum, unexpected signer workflow, or provenance mismatch should be reported privately before using the affected asset.


## Reproducible release packages

Release preflight and tagged publication build all five primary packages and all five symbol packages twice in isolated output/intermediate directories. The verifier compares assemblies, portable PDBs, embedded Source Link metadata, XML documentation, source files, NuSpec/repository metadata, and all extracted package contents. Unexplained semantic differences block release publication. Raw ZIP differences are warnings only when extracted contents match and the difference is limited to the documented NuGet container metadata rules. The official workflow promotes one verified build before generating the SBOM, checksums, attestations, and release artifacts. See [`docs/reproducible-builds.md`](docs/reproducible-builds.md).

## Fuzzing artifacts

Fuzz inputs are untrusted data. The fuzz workflow does not execute corpus files as scripts, bounds input and artifact sizes, avoids shell interpolation of corpus bytes, rejects path traversal and configured secret markers, uses short artifact retention, and does not automatically commit generated crash corpora. Report a security-sensitive fuzz finding privately under the normal vulnerability-reporting process before publishing the reproducer.
