# Contributing to TCJ Framework

Thank you for helping improve TCJ Framework.

## Contributor License Agreement

All contributions intended for inclusion in the Official TCJ Project are subject to the [TCJ Contributor License Agreement](CLA.md).

By submitting a pull request you must affirm that you have read and agree to the CLA and that you have authority to grant its rights. If an employer or another organization may own the contribution, obtain the required authorization before submitting it. Project Owners may require a separately signed individual or corporate agreement before accepting substantial or legally sensitive contributions.

The CLA does **not** transfer ownership of your unrelated work or independent fork. You retain copyright in your original Contribution while granting the Official TCJ Project broad rights to use, modify, distribute, sublicense, and relicense accepted Contributions.

## Project governance and upstream authority

Read [`GOVERNANCE.md`](GOVERNANCE.md) before proposing changes to ownership, licensing, release infrastructure, package identity, or project branding.

Contributors may propose changes through pull requests, but contribution does not grant ownership, merge authority, release authority, trademark rights, or control over the Official TCJ Project. Protected upstream changes become official only after the required validation and Project Owner approval.

Only Project Owners may approve changes to the Official TCJ Project's outbound license, CLA, governance policy, trademark policy, owner list, or official release/package identity.

Independent forks remain permitted under the applicable LGPL terms and may be developed or sold separately. Fork authors own their original modifications and fork-specific branding, but an independent fork is not the Official TCJ Project and must follow [`TRADEMARKS.md`](TRADEMARKS.md) when referring to TCJ.

## Before opening work

- Search existing issues and pull requests.
- Open an issue before large API or architectural changes.
- Keep changes focused on one concern.
- Do not include secrets, generated packages, build output, or personal IDE settings.

## Development setup

```bash
git clone https://github.com/Amir-ESH/TCJ.Framework.git
cd TCJ.Framework
python3 eng/verify-dependency-security.py
python3 eng/release-integrity.py validate-config
python3 eng/verify-coverage.py validate-config
python3 -m unittest discover --start-directory eng/tests --pattern "test_*.py"
python3 eng/verify-mutation-results.py validate-config
python3 eng/verify-performance-results.py validate-config
python3 eng/verify-architecture-policy.py validate-config
python3 eng/verify-aot.py verify
python3 eng/verify-sbom.py validate-config
python3 eng/verify-sqlserver-integration.py validate-config
python3 eng/verify-aspnetcore-integration.py validate-config
python3 eng/verify-consumer-compatibility.py validate-config
python3 eng/verify-reproducible-build.py validate-config
dotnet restore TCJ.slnx
dotnet build TCJ.slnx -c Release --no-restore
dotnet test TCJ.slnx -c Release --no-build --filter "Category!=SqlServer&Category!=AspNetCore&Category!=Concurrency" \
  --collect:"XPlat Code Coverage" \
  --settings tests/coverlet.runsettings \
  --results-directory TestResults
python3 eng/verify-coverage.py verify
```

For the first mutation baseline, run the dedicated workflow in `capture-baseline` mode, review both HTML reports, and accept the generated candidate with reviewer identity and notes. A pending baseline must not be used as a precondition that prevents Stryker from running; the complete process is documented in [`docs/mutation-testing.md`](docs/mutation-testing.md).

The Product API sample requires SQL Server. Its default Development configuration uses LocalDB on Windows.

## Branches

Create normal work from `develop`:

```bash
git switch develop
git pull --ff-only
git switch -c feature/short-description
```

Use prefixes such as `feature/`, `fix/`, `docs/`, `test/`, `build/`, and `chore/`.

## Code changes

- Follow `.editorconfig`.
- Preserve nullable-reference-type correctness.
- Prefer explicit behavior over hidden conventions.
- Keep `TCJ.Core` free from ASP.NET Core and EF Core dependencies.
- Add or update XML documentation for public APIs.
- Preserve binary compatibility with the published package baseline unless a breaking change is explicitly approved and documented.
- Do not disable NuGet Audit, weaken the audit threshold, or add an unreviewed package source.
- Do not weaken checksum or attestation enforcement in the tagged Release workflow.
- Add focused tests for bug fixes and public behavior changes.
- Do not weaken coverage thresholds or exclusions without a documented technical reason.
- Do not lower mutation thresholds, accept an unreviewed candidate, or add broad mutation exclusions merely to make CI green.
- Do not raise performance thresholds, remove benchmark categories, or exclude regressions without a documented technical justification.
- Never commit `BenchmarkDotNet.Artifacts/`, `artifacts/performance/`, `artifacts/sbom/`, `artifacts/reproducibility/`, `artifacts/compatibility/`, compatibility consumer `bin`/`obj`, or generated `*.cdx.json` files.
- Do not weaken SBOM package, hash, license, dependency, repository, or provenance metadata requirements merely to make CI green.
- Do not weaken deterministic-build settings or add broad reproducibility normalizations merely to make package comparison pass.
- Package-layout, SDK, compiler, Source Link, or reproducibility-policy changes require focused review and a successful full double build.
- Treat `eng/architecture-policy.json` as executable design documentation; dependency-direction changes require architectural justification.
- Treat `eng/aot-policy.json` as executable compatibility documentation. Run `python3 eng/verify-aot.py verify` after AOT-policy, production project-property, or warning-suppression changes; broad trim/AOT suppressions are prohibited and generated `artifacts/aot/` output must not be committed.
- Do not weaken, suppress, or broadly exclude architecture rules merely to make CI green.
- Update relevant pages under `docs/`.
- Analyzer and code-fix changes must preserve the build-time/runtime boundary documented in [`docs/analyzer-development.md`](docs/analyzer-development.md).

## Commit messages

Use a concise Conventional Commit-style subject:

```text
feat: add specification projection
fix: preserve domain event order
docs: explain SQL Server retries
test: cover current user resolution
```

## Automated validation

Pull requests targeting `develop` or `main` must pass the `Build, test and pack` check and dependency review. Restore audits the complete resolved NuGet graph and fails on moderate-or-higher known vulnerabilities or an unavailable audit source. The Pack phase includes SDK package validation against the latest published TCJ version and rejects accidental binary-breaking API changes. CI also enforces the line and branch coverage policy and verifies the complete package checksum manifest. The separate `Mutation testing / Run mutation tests` check must pass for mutation-relevant changes; during the one-time baseline bootstrap, a candidate must be reviewed and committed before normal verification can pass. The separate `Performance benchmarks / Run benchmarks` workflow executes a short job for relevant pull requests and full jobs for scheduled or manual runs; it preserves within-run measurements and policy results for review. Architecture tests run inside the normal solution test command and enforce module dependency directions, namespace ownership, and public API boundaries from `eng/architecture-policy.json`. CI also generates and verifies a CycloneDX release SBOM from locally packed artifacts and restored production dependencies; policy changes, missing license data, or dependency-graph changes require explicit review. The separate `Reproducible builds / Compare package builds` workflow performs isolated Build A and Build B package production for relevant changes and scheduled/manual validation. Extracted package, assembly, PDB, Source Link, XML documentation, source, and NuGet metadata differences are blocking; raw ZIP-only differences are reported under the narrow documented policy. Trusted provenance attestations are created only by the official tagged Release workflow after the verified Build A package set is promoted. For changes affecting the EF Core or SQL Server integration paths, the separate `SQL Server integration / Run database tests` check must also pass against the pinned disposable SQL Server container; see [`docs/sqlserver-integration-testing.md`](docs/sqlserver-integration-testing.md). For changes affecting `TCJ.AspNetCore`, shared DI/current-user behavior, or common HTTP error mapping, the dedicated `ASP.NET Core integration / Test on Linux`, `Test on Windows`, and cross-platform verification jobs must pass; see [`docs/aspnetcore-integration-testing.md`](docs/aspnetcore-integration-testing.md). Do not bypass required checks for normal changes. Release tags and NuGet publishing are maintainer-only operations described in [`docs/releasing.md`](docs/releasing.md).


## Native AOT policy verification

Native AOT/trimming policy verification is a blocking CI and release contract. Run `python3 eng/verify-aot.py verify` after AOT policy, production package AOT metadata, smoke-fixture, or workflow changes. The verifier validates the current Full support-tier closure against `smoke/TCJ.NativeAot.SmokeTest`, the supported `linux-x64` RID, local packed-package source mapping, absence of TCJ project references, and blocking CI/release wiring.

The workflow-only publish/execute gate runs `python3 eng/run-native-aot-smoke.py --version <candidate-version>` against `artifacts/packages`, then runs `python3 eng/verify-aot.py verify-result --version <candidate-version>` to prove that the native process loaded exactly the candidate `TCJ.Core`, `TCJ.DependencyInjection`, and `TCJ.AspNetCore` packages and emitted no `IL2xxx`/`IL3xxx` warning baseline. Release builds retain the AOT logs and JSON evidence, and the publishing job re-verifies that retained result before NuGet publishing. The EF NativeAOT fixture remains separately Experimental. See [`docs/guides/native-aot-and-trimming.md`](docs/guides/native-aot-and-trimming.md) for the exact support contract and suppression rules.

## Package consumer compatibility

Packaging, dependency, target-framework, or public setup changes must keep the clean-room consumers green. Consumer projects under `compatibility/Consumers/` may use NuGet `PackageReference` items only; references into `src/` are prohibited. Run `python3 eng/verify-consumer-compatibility.py validate-config` locally, then use the package-only runner described in [Package consumer compatibility](docs/package-consumer-compatibility.md). The dedicated workflow must pass on Linux, Windows, and macOS before compatibility support is claimed or compatibility policy is weakened.

## Package upgrade compatibility

Changes that affect package APIs, dependencies, configuration, runtime wiring, or migration guidance must keep the `upgrade-tests/` clean-room scenarios valid. The published baseline comes from `eng/published-release.json`, the target comes from `eng/release-manifest.json`, and direct upgrades must not edit scenario source. Intentional breaking changes require an approved entry in `eng/breaking-changes.json`, a matching migration-guide section, and an explicit migration patch when consumer source must change. Run `python3 eng/verify-upgrade-compatibility.py validate-config` before opening the pull request.

## Pull requests

Target regular changes to `develop`. Release pull requests flow from `develop` to `main`. After a successful publication, merge the reviewed post-release reset into `develop` and synchronize its safe metadata, documentation, and maintenance-workflow state to `main` through a protected pull request. Release tags, not the moving `main` branch, preserve immutable published source snapshots.

A pull request should explain:

- the problem;
- the chosen approach;
- compatibility or migration impact, including any `CPxxxx` suppression;
- tests performed;
- architecture-policy and documentation changes when module boundaries are intentionally changed;
- reproducibility-policy or normalization justification when build/package comparison behavior changes.

By contributing, you agree that your contribution may be distributed under the repository's **GNU Lesser General Public License v3.0 only (`LGPL-3.0-only`)**. Contributions do not grant ownership of, or broader permission to use, the TCJ project marks; brand use is governed separately by [`TRADEMARKS.md`](TRADEMARKS.md).

## API documentation changes

Public API changes must include useful XML documentation for types and members, matching `<param>` and `<typeparam>` elements, and `<returns>` for non-void results. Important consumer-facing behavior should also have a validated example or a linked conceptual guide.

The existing debt is recorded explicitly in `eng/documentation-baseline.json`. New APIs must not be added to that baseline merely to make CI pass; baseline additions require a reason and milestone, stale entries fail validation, and improvements should remove entries.

Run the repository-pinned DocFX tool and quality gate before opening a pull request:

```bash
dotnet tool restore
dotnet build TCJ.slnx --configuration Release
dotnet docfx metadata docfx/docfx.json --warningsAsErrors
dotnet docfx build docfx/docfx.json --warningsAsErrors
python3 eng/verify-documentation.py verify \
  --configuration Release \
  --build-root src \
  --api-root artifacts/documentation/api
```

See [`docs/documentation-authoring.md`](docs/documentation-authoring.md) for XML IDs, `cref`, examples, local preview, baseline maintenance, and versioned site metadata.

## Property and fuzz findings

Changes to foundational Core or dependency-registration behavior should preserve the property and fuzz gates. Reproduce a property failure from its FsCheck seed or a fuzz failure from its minimized corpus input. Every confirmed finding requires a conventional regression test before the failure corpus is considered resolved. Do not disable a flaky property, lower fuzz limits, remove a required target, or swallow broad exception classes to make a gate pass.


## Concurrency stress findings

Concurrency failures are first-class quality-gate failures. Reproduce the recorded scenario and seed before changing timeouts or worker counts. Do not add retries that turn a failed run into success, do not weaken a documented concurrency boundary, and do not make `DbContext` or Unit of Work appear thread-safe. Confirmed race, leakage, duplicate/missing operation, deadlock, timeout, or transaction-interference findings require an ordinary regression test where practical. Failure traces must stay sanitized and generated `TestResults/Concurrency/` and `artifacts/concurrency/` content must not be committed.

## Observability changes

Treat activity names, metric names/types/units, and commonly consumed tags as compatibility contracts. Telemetry changes must update `eng/observability-contract.json`, focused observability tests, documentation, and release notes when applicable. Do not add exporter/vendor dependencies to production packages, unbounded dimensions, raw SQL, connection strings, entity/user identifiers, or exception messages by default.

### Resilience changes

Do not add broad retry loops to make transient tests pass. Any resilience change must identify its operation/transaction/handler/timeout/circuit boundary, preserve cancellation, prove permanent failures are not retried, and add deterministic fault-injection coverage. Side-effect retries require documented idempotency. Keep `eng/resilience-policy.json`, `eng/resilience-contract.json`, telemetry contracts, benchmarks, and release notes synchronized.

## Health-check changes

Changes to public health-check names, tags, endpoint defaults, response fields, timeout/cache bounds, or telemetry require an intentional update to `eng/health-check-contract.json` and `eng/health-check-policy.json`. Run `python3 eng/verify-health-checks.py validate-config` and the health-check test project before opening a PR.

## Transactional-outbox changes

Outbox schema, delivery semantics, event naming, option defaults, telemetry, or health-check changes require an intentional update to `eng/outbox-contract.json` and `eng/outbox-policy.json`. Keep the guarantee at at-least-once unless an independently reviewed design can prove stronger semantics end to end; do not describe duplicate-prone delivery as exactly-once. Run `python3 eng/verify-outbox.py validate-config` and `tests/TCJ.Outbox.Tests` before opening a PR. Schema changes require consumer migration guidance. Never add payloads, aggregate identifiers, exception messages, credentials, or connection strings to outbox logs/telemetry/health responses.

## Transactional Inbox changes

Changes to Inbox identity, schema, retry, replay, cleanup, telemetry, health, or transaction behavior must update `eng/inbox-policy.json`, `eng/inbox-contract.json`, tests, and `docs/inbox.md` as applicable. Do not weaken database uniqueness, leak payload/header data, or claim global exactly-once delivery. Run `python3 eng/verify-inbox.py validate-config` before opening the pull request.
