# Documentation authoring and API reference

TCJ Framework treats documentation as a versioned build artifact. DocFX `2.78.5` is pinned in `.config/dotnet-tools.json`, all production projects generate XML documentation, and `eng/verify-documentation.py` prevents undocumented public API regressions.

## Documentation layout

- `docs/` contains conceptual, contributor, security, release, and package guidance.
- `docs/packages/` contains one landing page for each public NuGet package.
- `docs/nuget/` contains the package-specific Markdown source packed as `README.md` inside each `.nupkg`.
- `docs/api/` contains the conceptual API-reference landing page.
- `docfx/docfx.json` defines metadata generation and the static site.
- `artifacts/documentation/api/` contains generated managed-reference YAML.
- `artifacts/documentation/site/` contains the generated static site.
- `eng/documentation-policy.json` defines the quality gate.
- `eng/documentation-baseline.json` records only pre-existing documentation debt.

Generated folders are never committed.

## NuGet package READMEs

The GitHub repository README and NuGet package READMEs are separate artifacts with different rendering constraints. The repository root `README.md` may use GitHub-oriented HTML and repository-relative assets; it is never packed into a NuGet package.

Each public package has exactly one source file under `docs/nuget/`, named with the package/project ID:

```text
docs/nuget/TCJ.Core.md
docs/nuget/TCJ.DependencyInjection.md
docs/nuget/TCJ.EntityFrameworkCore.md
docs/nuget/TCJ.EntityFrameworkCore.SqlServer.md
docs/nuget/TCJ.AspNetCore.md
```

`eng/Packaging.props` selects the file through `$(MSBuildProjectName)` and packs it at the package root as `README.md`. Production project names must therefore continue to match their `PackageId`.

Package README rules are intentionally stricter than general conceptual documentation:

1. use Markdown only; do not use raw HTML layout or image tags;
2. use absolute HTTPS links for repository documentation and external resources; relative repository paths are not allowed;
3. keep the package name and scope explicit so every NuGet page describes the package actually installed;
4. avoid embedding a mutable "current version" in the source README when the same content can remain correct across preview increments;
5. do not replace `smoke/NuGet.Config` or otherwise couple package documentation changes to the local Native AOT candidate feed.

`python3 eng/verify-release.py` validates all five source READMEs during normal development. Package validation additionally confirms that the bytes packed as `README.md` match the corresponding file under `docs/nuget/`. Published versions from `0.1.0-preview.3` onward are also checked by `eng/verify-published-packages.py` against the package README policy.

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

For Markdown links in conceptual documentation, keep relative file links inside the `docs/` content root. When referring to files outside `docs/`—such as policies, workflows, samples, or release checklists—prefer a repository path in backticks rather than a mutable `blob/develop` link. If a public document must link to source outside the DocFX content root, use an immutable release-tag or commit URL. This keeps historical documentation from silently redirecting readers to a newer development branch.

## Examples

Important consumer examples are listed explicitly in `eng/documentation-policy.json`. A compiled example uses this fence:

````markdown
```csharp validate id=result-usage
// Complete compilable C# declaration
```
````

Each ID must be unique. The verifier extracts the selected fences and builds them against all five production projects. Use a normal `csharp` fence for illustrative fragments or pseudocode; never mark pseudocode as `validate`.

## Current baseline

The public API documentation debt has been retired. The current gate measures:

- 780 public or protected API items;
- 780 fully documented API items;
- 100% measured documentation coverage;
- zero baseline exceptions.

The documentation baseline file therefore contains an empty `entries` array and the policy requires 100% coverage. New public API documentation gaps are blocking; do not lower the threshold or create a baseline exception merely to make CI pass.

If an approved future policy change genuinely requires re-measurement, generate a proposed report without replacing the committed zero-debt baseline until the change is reviewed:

```bash
python3 eng/verify-documentation.py baseline \
  --output artifacts/documentation/proposed-baseline.json
```

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

The reusable `Documentation` workflow is validation-only from a permissions perspective: it keeps `contents: read` and cannot deploy Pages. When a trusted caller explicitly requests it, the same successful build can additionally upload the Pages artifact. Pull-request sites remain ordinary downloadable artifacts and are never deployable.

The separate `Documentation Pages` workflow is the only workflow allowed to deploy the site. It runs for trusted `main` activity, calls the validation workflow first, and grants `pages: write` plus `id-token: write` only to its final deployment job. This separation keeps PR orchestration unable to acquire deployment credentials while still avoiding a second documentation build.

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

The deployment job in `.github/workflows/documentation-pages.yml` receives only `contents: read`, `pages: write`, and `id-token: write`. The reusable `.github/workflows/documentation.yml` and `.github/workflows/required-pr-gate.yml` are statically verified to contain no Pages write or OIDC write capability. The deployment job consumes the Pages artifact produced by the same successful trusted build; pull-request content is never deployed.
