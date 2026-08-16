# Public API compatibility

TCJ Framework validates every locally packed development package against the latest immutable package version published to NuGet.org. The current baseline is `0.1.0-preview.3`.

## How the gate works

All packable TCJ projects import `eng/Packaging.props`, which imports `eng/PackageValidation.props`. The latter enables the .NET SDK package validator:

```xml
<EnablePackageValidation>true</EnablePackageValidation>
<PackageValidationBaselineVersion>$(TCJPublishedPackageVersion)</PackageValidationBaselineVersion>
```

During `dotnet pack`, the SDK resolves the matching baseline package from configured NuGet sources and compares its shipped assemblies and target frameworks with the package being created. Accidental binary-breaking changes fail the Pack target with diagnostics such as `CP0002`.

The gate catches changes such as:

- removing a public type or member;
- changing a public member signature in a binary-incompatible way;
- removing a previously shipped target framework;
- producing incompatible compile-time and runtime assets.

Adding a compatible public API is allowed, but it must still be documented and tested.

## Sources of truth

The immutable published release is recorded in `eng/published-release.json`. MSBuild reads the same baseline version from `eng/PackageValidation.props`. The development and published manifests keep their own `licenseExpression` values so historical package licensing remains explicit. `eng/verify-release.py` rejects CI when release metadata, package IDs, repository identity, or current package-license metadata diverge.

After each successful public release:

1. update `eng/published-release.json`, including the released `licenseExpression`;
2. update `TCJPublishedPackageVersion` in `eng/PackageValidation.props`;
3. increment the development version in `eng/Packaging.props` and `eng/release-manifest.json`;
4. run CI and the published-package smoke workflow.

## Intentional breaking changes

Before `1.0.0`, a breaking change can be accepted only when it is deliberate and reviewed. Prefer a compatibility-preserving overload, adapter, or obsolete transition period first.

When a breaking change is unavoidable:

1. document the reason and migration path in `CHANGELOG.md`;
2. describe the impact in the pull request;
3. generate or hand-author an API compatibility suppression containing only the reviewed diagnostics;
4. check the suppression into source control with the affected project;
5. remove obsolete suppressions and advance the baseline after the breaking release is published.

Do not disable `EnablePackageValidation` in CI or Release workflows. A local emergency diagnostic run may pass `-p:EnablePackageValidation=false`, but that result is not acceptable for merging or publishing.

## Local commands

```bash
dotnet restore TCJ.slnx
dotnet build TCJ.slnx -c Release --no-restore
dotnet test TCJ.slnx -c Release --no-build --filter "Category!=SqlServer&Category!=AspNetCore&Category!=Concurrency"
dotnet pack TCJ.slnx -c Release --no-build
```

A successful Pack means the packages passed both TCJ metadata/content inspection and the SDK API-compatibility baseline check.
