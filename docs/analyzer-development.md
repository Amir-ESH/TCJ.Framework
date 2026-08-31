# Analyzer development

`TCJ.Analyzers` is build-time tooling. It must remain isolated from the runtime package graph so adding diagnostics or code fixes never adds Roslyn or analyzer implementation assemblies to a TCJ application's runtime output.

## Project boundaries

The analyzer infrastructure is split into three projects:

- `src/TCJ.Analyzers/TCJ.Analyzers.csproj` contains diagnostic analyzers and targets `netstandard2.0` for compiler/IDE compatibility.
- `src/TCJ.Analyzers.CodeFixes/TCJ.Analyzers.CodeFixes.csproj` contains code fixes, also targets `netstandard2.0`, and may reference the analyzer implementation for shared diagnostic metadata.
- `eng/packaging/TCJ.Analyzers/TCJ.Analyzers.Package.csproj` is packaging-only. It produces the `TCJ.Analyzers` NuGet package and places implementation assemblies under `analyzers/dotnet/cs` rather than `lib/` or `ref/`.

The analyzer and code-fix implementation projects must not reference `TCJ.Core`, `TCJ.DependencyInjection`, `TCJ.EntityFrameworkCore`, `TCJ.EntityFrameworkCore.SqlServer`, or `TCJ.AspNetCore`. Runtime TCJ APIs must be identified from source compilations with Roslyn symbols and stable metadata names. Do not load TCJ runtime assemblies through reflection from analyzer code.

Roslyn compiler/workspace references are private build-time dependencies. Do not allow `Microsoft.CodeAnalysis*` dependencies or analyzer/code-fix assemblies to become runtime package dependencies.

## Package lifecycle

The packaging project is intentionally not listed in `TCJ.slnx`. The current release pipeline still produces and verifies the existing five runtime packages, while analyzer packaging is validated independently by `TCJ.Analyzers.Tests`. This keeps Important 9 focused on analyzer infrastructure instead of changing release-package governance.

Until `TCJ.Analyzers` has a published baseline, SDK package validation is disabled only for the analyzer packaging project. Existing runtime package validation remains unchanged. Once the first analyzer package is published and adopted by release governance, add an appropriate analyzer package baseline rather than keeping a permanent exception.

Pack the analyzer package directly:

```bash
dotnet pack eng/packaging/TCJ.Analyzers/TCJ.Analyzers.Package.csproj -c Release
```

The primary package must contain:

```text
analyzers/dotnet/cs/TCJ.Analyzers.dll
analyzers/dotnet/cs/TCJ.Analyzers.CodeFixes.dll
README.md
LICENSE.txt
```

It must not contain analyzer implementation assemblies under `lib/` or `ref/`, and it must not package Roslyn implementation DLLs beside the TCJ analyzer assemblies.

## Test harness

`tests/TCJ.Analyzers.Tests` provides Roslyn compiler/workspace helpers for source snippets, analyzer execution, and code-fix application. Tests that need TCJ APIs pass the required compiled TCJ assembly paths explicitly as metadata references. This keeps runtime references in the test fixture rather than the analyzer implementation.

Run the analyzer test project directly:

```bash
dotnet test tests/TCJ.Analyzers.Tests/TCJ.Analyzers.Tests.csproj -c Release
```

The package tests pack `TCJ.Analyzers`, restore it into an isolated `net10.0` consumer, build that consumer, and verify that neither `TCJ.Analyzers*.dll` nor `Microsoft.CodeAnalysis*.dll` is copied to the application's runtime output.

## Diagnostic governance

Before adding the first or any subsequent TCJ diagnostic, follow the [analyzer diagnostic governance](analyzers/README.md). The governance defines the stable `TCJxxxx` ID/category ranges, default severity policy, code-fix safety rules, release tracking, suppression guidance, and required per-rule documentation.

`DiagnosticCategoryAndIdRanges.txt`, `AnalyzerReleases.Shipped.md`, and `AnalyzerReleases.Unshipped.md` are Roslyn `AdditionalFiles` for the analyzer project. The repository enables the corresponding Roslyn governance diagnostics as errors, and `AnalyzerGovernanceTests` provides regression coverage for duplicate IDs, category ranges, release tracking, and documentation.

## Adding future diagnostics

Do not add a diagnostic by reflecting over runtime TCJ types. Resolve framework symbols from the user's `Compilation` with metadata names, compare Roslyn symbols, and keep all analyzer behavior deterministic for the same compilation. Diagnostics and code fixes introduced after this infrastructure issue must follow the repository's diagnostic-governance and release-tracking rules before they ship.
