# Release integrity and build provenance

TCJ release assets are protected by two complementary controls:

1. a deterministic `SHA256SUMS` manifest for the exact `.nupkg` and `.snupkg` files attached to the GitHub release;
2. a cryptographically signed GitHub artifact attestation binding those files to the tagged Release workflow, repository, source commit, and build environment.

These controls apply to official tag builds only. Pull-request and preflight builds validate the checksum tooling but do not create trusted attestations.

## Release output

Every official release contains:

- five primary `.nupkg` packages;
- five `.snupkg` symbol packages;
- `SHA256SUMS` covering all ten package files.

`eng/release-integrity.py` generates the manifest from the package IDs in `eng/release-manifest.json`. It rejects missing packages, unexpected packages, duplicate entries, unsafe filenames, malformed digests, and content mismatches.

Generate and verify a local package set:

```bash
python3 eng/release-integrity.py write \
  --package-directory artifacts/packages \
  --checksums artifacts/release/SHA256SUMS

python3 eng/release-integrity.py verify \
  --package-directory artifacts/packages \
  --checksums artifacts/release/SHA256SUMS
```

## GitHub artifact attestation

The tagged `.github/workflows/release.yml` workflow uses `actions/attest@v4` after package inspection and checksum generation. The build job receives only the additional permissions required to mint a short-lived OIDC identity and persist the attestation:

```yaml
permissions:
  contents: read
  id-token: write
  attestations: write
  artifact-metadata: write
```

The attestation subjects are the ten packages and `SHA256SUMS`. The publish job then downloads and re-verifies the artifacts before NuGet publication and GitHub Release creation.

## Verify downloaded GitHub release assets

Download all package assets and `SHA256SUMS` from the same GitHub release into one directory, then verify the checksums:

```bash
sha256sum --check SHA256SUMS
```

Verify provenance for an individual asset with GitHub CLI:

```bash
gh attestation verify TCJ.Core.0.1.0-preview.2.nupkg \
  --repo Amir-ESH/TCJ.Framework \
  --signer-workflow Amir-ESH/TCJ.Framework/.github/workflows/release.yml
```

Use the exact version downloaded. Repeat the attestation command for any package or symbol package whose provenance must be independently established.

## NuGet.org packages versus GitHub release assets

The attestation and `SHA256SUMS` describe the files produced by the TCJ Release workflow and attached to the GitHub release. NuGet.org may apply its own repository signature after upload, so a package downloaded from NuGet.org can have different bytes from the original GitHub release asset while containing the same package payload.

Use NuGet signature verification for files downloaded from NuGet.org. Use `SHA256SUMS` and GitHub attestation verification for files downloaded from the matching GitHub release.

## Failure policy

Do not publish when:

- checksum generation finds an incomplete or unexpected package set;
- checksum verification fails after artifact download;
- the attestation step fails;
- the GitHub release is missing `SHA256SUMS`;
- package assets were rebuilt outside the tagged Release workflow.

Never move an existing release tag or replace an immutable package version with different content. Correct the issue and publish a new version when any package has already reached NuGet.org.
