# Versioning and releases

TCJ Framework uses semantic versioning with pre-release identifiers during active development.

## Current version

```text
0.1.0-preview.1
```

The shared package version is defined in `eng/Packaging.props` and mirrored in `eng/release-manifest.json` for release validation.

## Preview versions

Preview releases use monotonically increasing identifiers:

```text
0.1.0-preview.1
0.1.0-preview.2
0.1.0-preview.3
```

The corresponding Git tag includes a leading `v`:

```text
v0.1.0-preview.1
```

Do not reuse a published package version for different bits. Increment the preview number for every new publication.

## When to create a release tag

Create the tag only after:

1. the release commit is on `main`;
2. build, test, and pack checks succeed;
3. the manual `Release preflight` workflow succeeds on `main`;
4. package contents and Package IDs have been inspected;
5. release notes and the changelog are updated;
6. the `nuget-production` environment and NuGet.org Trusted Publishing policy are configured.

The tag triggers `.github/workflows/release.yml`. The workflow validates that the tag version matches `eng/Packaging.props`, publishes through short-lived OIDC credentials, and creates the GitHub Release. See [Release automation](releasing.md).

Example:

```bash
git switch main
git pull --ff-only
git tag -a v0.1.0-preview.1 -m "TCJ Framework 0.1.0-preview.1"
git push origin v0.1.0-preview.1
```

Mark preview GitHub Releases as **Pre-release**.

## Compatibility expectations

Before `1.0.0`, minor and preview releases may contain breaking public API changes. Breaking changes must be documented in `CHANGELOG.md` and migration guidance should be provided when practical.

After `1.0.0`, follow standard semantic-versioning expectations:

- patch: compatible fixes
- minor: compatible features
- major: breaking changes
