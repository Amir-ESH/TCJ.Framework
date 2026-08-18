# TCJ analyzer diagnostic governance

TCJ analyzer diagnostic IDs are public compatibility identifiers. Consumers can configure severity and suppressions by ID, so an ID that has shipped must never be renamed, renumbered, or reused for a different meaning.

This governance applies before the first TCJ diagnostic is introduced. A diagnostic is not ready to merge or ship unless its ID, category, default severity, release metadata, documentation, and code-fix policy satisfy the rules below.

## Diagnostic ID namespace and category ranges

All analyzer diagnostics use the `TCJxxxx` namespace with exactly four decimal digits. `TCJ0000` is intentionally not allocated.

| Category | Diagnostic category string | Allocated IDs | Intended rules |
| --- | --- | --- | --- |
| Dependency injection | `TCJ.DependencyInjection` | `TCJ0001`-`TCJ0999` | Convention registration, lifetime markers, and dependency-registration contracts |
| Persistence | `TCJ.Persistence` | `TCJ1000`-`TCJ1999` | Repository, Unit of Work, persistence-boundary, and database correctness contracts |
| Specifications | `TCJ.Specifications` | `TCJ2000`-`TCJ2999` | Specification construction, ordering, paging, and query-shape contracts |
| AOT/trimming | `TCJ.AotTrimming` | `TCJ3000`-`TCJ3999` | Native AOT, trimming, reflection, and project-configuration compatibility |
| Strong types | `TCJ.StrongTypes` | `TCJ4000`-`TCJ4999` | Strongly Typed ID and Value Object generator/analyzer contracts |

`TCJ5000`-`TCJ9999` are reserved for future categories. Do not allocate from the reserved range without an explicit governance change that defines the category first.

The machine-readable source for the allocated category ranges is `src/TCJ.Analyzers/DiagnosticCategoryAndIdRanges.txt`. The analyzer project supplies that file to `Microsoft.CodeAnalysis.Analyzers`, and repository `.editorconfig` settings promote the relevant Roslyn governance diagnostics to build errors.

## ID stability rules

- Allocate the next available ID from the rule's category range. Do not choose IDs for visual grouping inside a range.
- Never reuse a shipped ID, even after its original rule is removed.
- Never change the meaning of a shipped ID to avoid allocating a new one.
- A category change for an existing rule is a compatibility event and must be recorded in analyzer release tracking.
- A diagnostic ID must be unique across the entire `TCJ.Analyzers` assembly, not only inside one analyzer type.

## Default severity rules

Default severity reflects the strongest conclusion TCJ can prove statically; it is not a measure of how strongly maintainers prefer a coding style.

### Error

Use `Error` when TCJ can statically prove that the code violates a framework contract that would otherwise produce a deterministic startup/configuration failure or an invalid operation. A default error must have a clear, actionable remediation path. New default-error diagnostics after `1.0.0` require explicit compatibility review because they can break consumer builds immediately after an analyzer update.

### Warning

Use `Warning` for correctness, reliability, persistence, or compatibility risks where the pattern is unsafe or strongly discouraged but TCJ cannot prove that every execution is invalid. Warnings are the normal default for rules that protect behavior without representing a guaranteed compile-time-invalid state.

### Info

Use `Info` only for actionable, non-style guidance where a warning would overstate the risk. An informational rule still needs a TCJ framework contract or compatibility reason; it must not become a general style preference.

### Hidden or disabled by default

Do not use hidden or disabled-by-default rules as a way to bypass the severity policy. They require an explicit design reason and compatibility review. Style preferences do not become TCJ diagnostics at any severity.

## Code-fix policy

"Mandatory" describes what a diagnostic implementation PR must provide; it does not force consumers to apply a fix.

### Mandatory code fix

A code fix is mandatory when the analyzer can perform a deterministic, local, semantics-preserving repair and the correct transformation does not require domain knowledge. If several equally safe choices exist, provide explicit user-selectable actions rather than guessing.

Typical examples include removing only conflicting TCJ marker interfaces or replacing a marker with the corresponding self-registration marker while preserving lifetime.

### Optional code fix

A code fix is optional when a safe transformation exists for a narrow subset of cases, when the manual change is trivial, or when Fix All cannot be made deterministic. The diagnostic documentation must state which cases are fixable and which require manual remediation.

### Unsafe: no code fix

Do not offer a code fix when remediation requires choosing domain semantics, moving transaction or persistence boundaries, selecting an ordering key, changing unrelated public API, deleting user behavior, performing a data migration, or making any other inference TCJ cannot prove safe. In those cases, diagnostics should explain the contract and leave the refactoring to the developer.

## Release tracking

`src/TCJ.Analyzers/AnalyzerReleases.Unshipped.md` tracks diagnostic metadata that has not shipped. `src/TCJ.Analyzers/AnalyzerReleases.Shipped.md` is the immutable historical record of released diagnostics.

When adding a diagnostic:

1. Allocate an unused ID from `DiagnosticCategoryAndIdRanges.txt`.
2. Create the `DiagnosticDescriptor` with the allocated category and default severity.
3. Add the rule to `AnalyzerReleases.Unshipped.md` using the Roslyn release-tracking format.
4. Add `docs/analyzers/TCJxxxx.md` from the diagnostic documentation template.
5. Add focused analyzer tests and, when required by the policy above, code-fix tests.
6. Run the analyzer test project. The governance tests reject duplicate IDs, out-of-range IDs/categories, missing release tracking, and missing diagnostic documentation.

At release time, move unshipped entries into a versioned section in `AnalyzerReleases.Shipped.md`. Do not delete shipped history to make a validation failure disappear.

The analyzer project treats Roslyn release-tracking diagnostics as errors. This means a descriptor that is missing from release metadata, has stale metadata, or reuses invalid release history blocks the analyzer build instead of becoming a warning that can accidentally ship.

## Suppression and severity configuration

Consumers configure TCJ analyzer severities with standard `.editorconfig` settings. TCJ does not require a proprietary suppression file.

```ini
[*.cs]
dotnet_diagnostic.TCJ0001.severity = warning
```

To suppress a rule for a justified scope, prefer the narrowest standard mechanism that keeps the decision visible in code review: a targeted `.editorconfig` path section, `#pragma warning disable/restore` around the smallest relevant code region, or `SuppressMessage` when an attribute-based suppression is appropriate.

Do not recommend broad suppression of all `TCJ*` diagnostics. A rule page must explain legitimate suppression scenarios and the behavioral risk that remains when the rule is suppressed.

## Diagnostic documentation

Every registered descriptor must have a page named `docs/analyzers/TCJxxxx.md`. Start from [`diagnostic-template.md`](diagnostic-template.md) and keep the ID, category, severity, code-fix availability, examples, suppression guidance, and known limitations current with the implementation.

The rule page is part of the compatibility surface: if severity or behavior changes, update release tracking and documentation in the same pull request.
