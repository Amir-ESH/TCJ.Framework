# Release integrity and build provenance

TCJ release assets are protected by four complementary controls:

1. a deterministic `SHA256SUMS` manifest for the exact packages and SBOM attached to the GitHub release;
2. a CycloneDX JSON software bill of materials describing release packages, dependencies, licenses, hashes, repository identity, and source commit;
3. a cryptographically signed GitHub artifact attestation binding those files to the tagged Release workflow, repository, source commit, and build environment.

Checksums answer whether bytes changed. The SBOM answers what components and relationships are represented. Reproducibility answers whether the trusted inputs can create equivalent package contents again. Attestations answer where and how the files were produced.

## Release output

Every official release contains:

- five primary `.nupkg` packages;
- five `.snupkg` symbol packages;
- one `TCJ.Framework.<version>.cdx.json` CycloneDX SBOM;
- one `SHA256SUMS` file covering all eleven preceding artifacts.

`eng/release-integrity.py` derives expected names from `eng/release-manifest.json`. It rejects missing packages, an absent or incorrectly named SBOM, an additional `.cdx.json`, unexpected artifacts, duplicate checksum entries, unsafe filenames, malformed digests, and content mismatches.

Generate and verify a local release set after generating the SBOM:

```bash
version="$(python3 -c 'import json; print(json.load(open("eng/release-manifest.json"))["version"])')"
sbom="artifacts/sbom/TCJ.Framework.$version.cdx.json"

python3 eng/release-integrity.py write \
  --version "$version" \
  --package-directory artifacts/packages \
  --sbom "$sbom" \
  --checksums artifacts/release/SHA256SUMS

python3 eng/release-integrity.py verify \
  --version "$version" \
  --package-directory artifacts/packages \
  --sbom "$sbom" \
  --checksums artifacts/release/SHA256SUMS
```

SBOM generation and structural verification are documented in [Software bill of materials](software-bill-of-materials.md).

## GitHub artifact attestation

The tagged `.github/workflows/release.yml` workflow uses `actions/attest@v4` after package inspection, SBOM verification, and checksum generation. The build job receives only the additional permissions required to mint a short-lived OIDC identity and persist the attestation:

```yaml
permissions:
  contents: read
  id-token: write
  attestations: write
  artifact-metadata: write
```

The attestation subjects are the ten package files, the CycloneDX SBOM, and `SHA256SUMS`. Pull-request and preflight builds validate the same tooling but do not create trusted release attestations.

The publish job downloads packages, release metadata, and SBOM as separate workflow artifacts. It restores dependency metadata, verifies the downloaded SBOM, verifies all checksums, and only then publishes packages and creates or updates the GitHub Release.

## Verify downloaded GitHub release assets

Download all eleven hashed assets and `SHA256SUMS` from the same GitHub Release into one directory, then run:

```bash
sha256sum --check SHA256SUMS
```

Verify provenance for a package:

```bash
gh attestation verify TCJ.Core.0.1.0-preview.2.nupkg \
  --repo Amir-ESH/TCJ.Framework \
  --signer-workflow Amir-ESH/TCJ.Framework/.github/workflows/release.yml
```

Verify the matching SBOM:

```bash
gh attestation verify TCJ.Framework.0.1.0-preview.2.cdx.json \
  --repo Amir-ESH/TCJ.Framework \
  --signer-workflow Amir-ESH/TCJ.Framework/.github/workflows/release.yml
```

Use the exact downloaded version. Repeat verification for any artifact whose provenance must be established independently.

## NuGet.org packages versus GitHub Release assets

The attestation and `SHA256SUMS` describe files produced by the TCJ Release workflow and attached to the GitHub Release. NuGet.org may apply its own repository signature after upload, so a package downloaded from NuGet.org can have different bytes from the original GitHub asset while containing the same package payload.

Use NuGet signature verification for files downloaded from NuGet.org. Use `SHA256SUMS`, SBOM validation, and GitHub attestation verification for files downloaded from the matching GitHub Release.

## Failure policy

Do not publish when:

- package or SBOM generation finds an incomplete or unexpected release set;
- SBOM validation reports missing packages, dependency relationships, hashes, licenses, repository metadata, or version metadata;
- the SBOM hash is missing from `SHA256SUMS` or does not match;
- checksum verification fails after workflow artifact download;
- the attestation step fails;
- the GitHub Release is missing the SBOM or `SHA256SUMS`;
- package assets were rebuilt outside the tagged Release workflow.

Never move an existing release tag or replace an immutable package version with different content. Correct the issue and publish a new version when any package has already reached NuGet.org.
