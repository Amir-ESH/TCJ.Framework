# Versioning and releases

TCJ Framework uses semantic versioning with pre-release identifiers during active development.

## Published and development versions

Latest published preview:

```text
0.1.0-preview.1
```

Current development version:

```text
0.1.0-preview.2
```

The mutable next-release state is stored in `eng/release-manifest.json`. The latest immutable public release is recorded separately in `eng/published-release.json` and mirrored for MSBuild package validation in `eng/PackageValidation.props`.

## Release-manifest lifecycle

A development manifest uses:

```json
{
  "status": "development",
  "releaseDate": null
}
```

This state is valid for normal CI and package creation, but release preflight and tag publication reject it.

When a release candidate is finalized:

1. move the completed notes from `[Unreleased]` to a dated version section in `CHANGELOG.md`;
2. set `status` to `ready`;
3. set `releaseDate` to the publication date;
4. verify that `version`, `tag`, and `eng/Packaging.props` match;
5. run `Release preflight` from `main`.

After publication, copy the released values to `eng/published-release.json`, increment the development version, return the release manifest to `development`, and set `releaseDate` back to `null`.

## Preview versions

Preview releases use monotonically increasing identifiers:

```text
0.1.0-preview.1
0.1.0-preview.2
0.1.0-preview.3
```

The corresponding Git tag includes a leading `v`, for example `v0.1.0-preview.2`. Never reuse a published package version or move a published tag.

## Compatibility expectations

Before `1.0.0`, minor and preview releases may contain deliberately approved breaking public API changes. Normal CI still rejects accidental binary breaks by validating packed assemblies against the latest published package baseline. Breaking changes must be documented in `CHANGELOG.md`, include migration guidance when practical, and use only narrowly reviewed compatibility suppressions.

After `1.0.0`, follow standard semantic-versioning expectations:

- patch: compatible fixes
- minor: compatible features
- major: breaking changes

See [Release automation](releasing.md), [Published-package validation](published-package-validation.md), and [Public API compatibility](api-compatibility.md).
