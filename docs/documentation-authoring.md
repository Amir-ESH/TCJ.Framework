# Documentation authoring and API reference

TCJ Framework treats documentation as a versioned build artifact. DocFX `2.78.5` is pinned in `.config/dotnet-tools.json`, all production projects generate XML documentation, and `eng/verify-documentation.py` prevents undocumented public API regressions.

## Documentation layout

- `docs/` contains conceptual, contributor, security, release, and package guidance.
- `docs/packages/` contains one landing page for each public NuGet package.
- `docs/api/` contains the conceptual API-reference landing page.
- `docfx/docfx.json` defines metadata generation and the static site.
- `artifacts/documentation/api/` contains generated managed-reference YAML.
- `artifacts/documentation/site/` contains the generated static site.
- `eng/documentation-policy.json` defines the quality gate.
- `eng/documentation-baseline.json` records only pre-existing documentation debt.

Generated folders are never committed.

## XML documentation IDs

The C# compiler assigns stable IDs to documented symbols:

- `T:` identifies a type;
- `M:` identifies a method or constructor;
- `P:` identifies a property or indexer;
- `F:` identifies a field;
- `E:` identifies an event.

The baseline and reports use these prefixes so a finding can be tied to one public API. Overloads include parameter types. Removing or renaming an API makes its old baseline entry stale and validation fails until the entry is removed.

## Writing useful summaries

A `<summary>` should describe the contract from the consumer's perspective. Prefer a precise verb such as “Registers”, “Returns”, “Creates”, or “Determines”. Do not repeat the member name without explaining behavior.

```csharp
/// <summary>
/// Registers TCJ repositories, auditing, and Unit of Work services for the DbContext.
/// </summary>
```

Use `<remarks>` for lifecycle rules, ordering constraints, idempotency, side effects, or behavior that is too detailed for the summary. Important extension methods should explain null handling, duplicate registration, and failure behavior.

## Parameters, type parameters, returns, and cancellation

Document every public parameter with `<param>` and every generic parameter with `<typeparam>`. A non-void result requires `<returns>`. Methods accepting `CancellationToken` should state what operation is cancelled and should not promise rollback unless the implementation guarantees it.

```csharp
/// <summary>Loads an entity by its identifier.</summary>
/// <param name="id">The entity identifier.</param>
/// <param name="cancellationToken">A token used to cancel the query.</param>
/// <returns>The entity, or <see langword="null"/> when no match exists.</returns>
```

Document important nullability semantics: whether null means “not found”, “not configured”, or “use the default”. Do not restate nullable annotations without explaining their meaning.

## References

Use `<see cref="TypeName"/>` for inline references and `<seealso cref="TypeName"/>` for related APIs. Prefer compiler-resolved `cref` expressions instead of plain text. An unresolved reference is emitted with a `!:` prefix in the XML file and is blocking.

For Markdown links in conceptual documentation, keep relative file links inside the `docs/` content root. Files that live elsewhere in the repository—such as policies, workflows, samples, or release checklists—must use an absolute GitHub source URL. DocFX validates local file links against the configured content set, so a repository-relative path that escapes `docs/` is not a valid site link.

## Examples

Important consumer examples are listed explicitly in `eng/documentation-policy.json`. A compiled example uses this fence:

````markdown
```csharp validate id=result-usage
// Complete compilable C# declaration
```
````

Each ID must be unique. The verifier extracts the selected fences and builds them against all five production projects. Use a normal `csharp` fence for illustrative fragments or pseudocode; never mark pseudocode as `validate`.

## Current baseline

The initial gate measured the `develop` source on August 6, 2026:

- 453 public or protected API items were discovered;
- 259 were complete under the initial policy;
- measured complete coverage was 57.17%;
- 606 missing-element findings were recorded for pre-existing APIs.

The threshold is the measured baseline, not a guessed target. New missing elements are rejected even when the overall percentage remains above the threshold.

Baseline rules:

1. Do not add a new public API to the baseline merely to make CI pass.
2. A baseline addition requires a reason and planned milestone.
3. The entry limit cannot increase without an explicit policy change.
4. Removed APIs and completed documentation must remove their stale entries.
5. Improvements should raise the minimum percentage and reduce the entry count.

Generate a proposed baseline only while intentionally re-measuring an approved policy change:

```bash
python3 eng/verify-documentation.py baseline \
  --output artifacts/documentation/proposed-baseline.json
```

Review the diff; do not copy it blindly.

## Local build and preview

```bash
dotnet tool restore
python3 eng/verify-documentation.py validate-config

dotnet restore TCJ.slnx --force-evaluate
dotnet build TCJ.slnx --configuration Release --no-restore

dotnet tool run docfx metadata docfx/docfx.json \
  --noRestore \
  --warningsAsErrors

dotnet tool run docfx build docfx/docfx.json \
  --warningsAsErrors

python3 eng/verify-documentation.py verify \
  --configuration Release \
  --build-root src \
  --api-root artifacts/documentation/api \
  --output artifacts/documentation

dotnet tool run docfx serve artifacts/documentation/site
```

The verifier writes `DOCUMENTATION_SUMMARY.md`, `documentation-summary.json`, `missing-documentation.json`, and `broken-links.json`.

## Versioned documentation

CI metadata records the package version, source commit, release tag, and UTC build date. Release workflows archive the site as `TCJ.Framework.Documentation.<version>.zip`. This makes it possible to publish `/latest/` and `/<version>/` paths later without generating documentation from a different commit.

## GitHub Pages preparation

The dedicated Documentation workflow produces a Pages-ready static-site artifact. Pull-request sites remain downloadable artifacts and are never deployed. To enable Pages later:

1. allow only a trusted workflow on `main` to deploy;
2. use the generated site artifact rather than rebuilding after approval;
3. grant only `pages: write` and `id-token: write` to the deployment job;
4. protect the `github-pages` environment;
5. never deploy arbitrary pull-request content;
6. keep secrets out of documentation metadata and generated pages.

No Pages deployment is enabled by Step 34; the repository is prepared for a separate, reviewed activation.

## Diagnosing failures

- **Missing summary/parameter/return:** update the XML comment on the reported source symbol.
- **Stale baseline:** remove the resolved or removed entry.
- **Unresolved cref:** correct the symbol expression; do not suppress the warning globally.
- **Broken link:** update the target or add the missing source page.
- **Snippet failure:** compile the extracted project locally and inspect `artifacts/documentation/snippets/build.log`.
- **DocFX warning:** fix the source, metadata, or navigation problem instead of lowering all warning severities.

## Safe GitHub Pages enablement

Pull requests and `develop` builds upload preview sites only as ordinary workflow artifacts. Pages deployment is restricted to trusted pushes to `main` and remains disabled until maintainers:

1. open **Settings → Pages** and select **GitHub Actions** as the source;
2. create the repository variable `ENABLE_DOCUMENTATION_PAGES` with value `true`;
3. review protection rules for the `github-pages` environment.

The deployment job receives only `contents: read`, `pages: write`, and `id-token: write`. It deploys the Pages artifact produced by the same successful documentation build; pull-request content is never deployed.
