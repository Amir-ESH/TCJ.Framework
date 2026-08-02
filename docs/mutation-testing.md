# Mutation testing quality gate

Mutation testing checks whether tests detect deliberate behavioral changes in production code. Stryker.NET creates mutants, runs the tests against them, and records whether each mutant was killed, survived, timed out, could not compile, or was outside the tested behavior.

Code coverage and mutation testing answer different questions:

- coverage asks whether code executed;
- mutation testing asks whether assertions detect a meaningful behavioral change.

Both gates are required. A high coverage percentage is not a substitute for a valid mutation result.

## Why the original Step 29 result was rejected

PR #52 introduced the first mutation workflow, but its only mutation run was not a valid baseline:

- Stryker reported that coverage capture failed for both projects;
- all `524` tested mutants survived;
- no mutant was killed;
- the aggregate score was `0.00%`;
- the verification step failed;
- the pull request was merged before the mutation workflow became green;
- the repository contained a suggested `50%` threshold, but no reviewed, measured baseline.

The rejected run is preserved in `eng/mutation-baseline.json` as incident evidence. It is deliberately marked `pending`, not recorded as a baseline.

A structurally valid JSON report is not automatically a trustworthy mutation result. The verifier now rejects degenerate executions such as an all-survived run, zero killed mutants, incomplete statuses, excessive compile errors, runner failures, report hash mismatches, and inconsistent source revisions.

## Current controlled scope

The first accepted baseline is intentionally limited to reviewed behavior in two production projects.

### `TCJ.Core`

- `Entities/Entity.cs`
- `Identifiers/GuidGenerator.cs`
- `Results/CommonErrors.cs`
- `Results/Result.cs`
- `Results/ResultError.cs`
- `Results/ResultOfT.cs`

Tests:

```text
tests/TCJ.Core.Tests
```

### `TCJ.DependencyInjection`

- `DomainEvents/DomainEventDispatcher.cs`
- `Extensions/ServiceCollectionExtensions.cs`

Tests:

```text
tests/TCJ.DependencyInjection.Tests
```

The selected files contain meaningful behavior and have focused assertions. Marker interfaces, generated files, migrations, samples, smoke projects, and option-only boilerplate are outside the first controlled scope.

`DomainEventHandlerInvoker.cs` is temporarily outside the first baseline because the Stryker 4.16.0 run in PR #52 placed its generated mutations in safe mode and reported compile errors for the file. This exclusion is visible in `eng/mutation-policy.json` and must be reconsidered when the runner or source shape changes.

## Runner configuration

The test suite uses xUnit v3. `tests/TestProject.props` therefore enables the Microsoft Testing Platform runner:

```xml
<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
```

Stryker is pinned in `.config/dotnet-tools.json` and configured with:

```text
test-runner: mtp
coverage-analysis: off
```

Coverage optimization is disabled because the original `perTest` coverage capture failed. In `off` mode, Stryker runs all relevant tests against every tested mutant. This is slower but gives a safer remediation baseline. Coverage optimization may be re-enabled only in a dedicated pull request that proves equivalent results.

## Files that define the gate

```text
.config/dotnet-tools.json
stryker-config.json
eng/mutation-policy.json
eng/mutation-baseline.json
eng/run-mutation-testing.py
eng/verify-mutation-results.py
eng/tests/test_run_mutation_testing.py
eng/tests/test_verify_mutation_results.py
.github/workflows/mutation-testing.yml
.github/workflows/ci.yml
```

The full mutation workflow is called by `.github/workflows/ci.yml` when selected production, test, mutation-policy, runner, or workflow files change. A path-detection job skips the expensive mutation run for unrelated documentation-only changes. When mutation testing is relevant, the normal `Build, test and pack` job cannot succeed unless the reusable mutation gate succeeds. This prevents a repeat of PR #52 without running Stryker twice for the same CI event.

## Valid-result requirements

Before score enforcement, the verifier validates execution health.

A result is rejected when any of these conditions occurs:

- the Stryker runner failed;
- the expected JSON or HTML report is missing;
- the report is not Stryker schema version 2;
- no tests were discovered;
- the report identifies the wrong production project;
- report, runner-log, and run-metadata hashes differ;
- a runner log contains a known invalid-execution marker such as failed coverage capture;
- project reports were generated from different commits;
- a project killed no mutants;
- all tested mutants survived;
- tested-mutant counts are below policy;
- compile-error percentage exceeds policy;
- runtime-error, pending, or not-run mutants exceed policy.

Only after execution health passes are the mutation-score and recorded-baseline floors evaluated.

## Quality policy

`eng/mutation-policy.json` defines:

- pinned Stryker version;
- required test runner and coverage-analysis mode;
- controlled mutation targets;
- minimum aggregate mutation score;
- minimum tested and killed mutant counts;
- per-project minimums;
- maximum compile-error rate;
- report and metadata paths;
- baseline path;
- documented scope notes.

The initial quality target is `50.0%`. This target is not itself the baseline. The baseline is the measured and reviewed result stored in `eng/mutation-baseline.json`.

## Baseline states

### Pending

A pending baseline blocks merge validation. It documents why no result has been accepted yet.

### Recorded

A recorded baseline contains:

- source revision;
- Stryker version;
- runner mode;
- measured aggregate score;
- complete mutant counts;
- per-project counts;
- SHA-256 hashes of both reports;
- reviewer identity, review time, and review notes;
- an explicit attestation that survived mutants were reviewed.

Normal verification enforces both:

- the policy minimum;
- the recorded baseline score, minus only an explicitly configured regression allowance.

The default regression allowance is zero.

## Capture the first real baseline

The first real baseline requires two commits. This avoids inventing a score before Stryker has completed a valid run.

1. Push the remediation files to a branch.
2. Open **Actions → Mutation testing → Run workflow**.
3. Select `capture-baseline`.
4. Confirm that the workflow is green.
5. Download the mutation artifact.
6. Review both HTML reports, especially every survived mutant.
7. Place `mutation-baseline-candidate.json` under `artifacts/mutation/`.
8. Convert the candidate into an accepted baseline with explicit review metadata:

   ```bash
   python3 eng/verify-mutation-results.py accept-baseline \
     --candidate artifacts/mutation/mutation-baseline-candidate.json \
     --output-baseline eng/mutation-baseline.json \
     --reviewed-by "<github-user>" \
     --review-notes "Reviewed both HTML reports; documented meaningful survivors in the PR."
   ```

9. Commit `eng/mutation-baseline.json`. Do not copy the unreviewed candidate over it.
10. Rerun normal CI and the mutation workflow in `verify` mode.
11. Merge only when the reusable **Mutation quality gate** is green.

The capture command does not bypass execution health or the policy score. It creates a `candidate`, not a recorded baseline. The separate acceptance command validates the candidate and records the human review attestation.

## Run locally

From the repository root:

```bash
dotnet tool restore
dotnet restore TCJ.slnx

dotnet build tests/TCJ.Core.Tests/TCJ.Core.Tests.csproj \
  --configuration Release \
  --no-restore

dotnet build tests/TCJ.DependencyInjection.Tests/TCJ.DependencyInjection.Tests.csproj \
  --configuration Release \
  --no-restore

python3 -m unittest discover \
  --start-directory eng/tests \
  --pattern "test_*.py"

python3 eng/verify-mutation-results.py validate-config
```

Prepare clean outputs and run both projects:

```bash
rm -rf artifacts/mutation
mkdir -p artifacts/mutation/reports

python3 eng/run-mutation-testing.py --project TCJ.Core
python3 eng/run-mutation-testing.py --project TCJ.DependencyInjection
```

For a normal recorded-baseline check:

```bash
python3 eng/verify-mutation-results.py validate-baseline
python3 eng/verify-mutation-results.py verify
```

For the first valid baseline candidate:

```bash
python3 eng/verify-mutation-results.py capture-baseline \
  --candidate artifacts/mutation/mutation-baseline-candidate.json
```

After reviewing the HTML reports, accept it explicitly:

```bash
python3 eng/verify-mutation-results.py accept-baseline \
  --candidate artifacts/mutation/mutation-baseline-candidate.json \
  --output-baseline eng/mutation-baseline.json \
  --reviewed-by "<github-user>" \
  --review-notes "Reviewed both HTML reports and documented survivors."
```

An exported source archive has no `.git` directory. Configuration-only validation may use:

```bash
python3 eng/verify-mutation-results.py validate-config --skip-git-check
```

Normal clones and CI must not skip Git tracking validation.

## Generated outputs

```text
artifacts/mutation/MUTATION_SUMMARY.md
artifacts/mutation/mutation-summary.json
artifacts/mutation/mutation-baseline-candidate.json
artifacts/mutation/reports/TCJ.Core/run-metadata.json
artifacts/mutation/reports/TCJ.Core/stryker-console.log
artifacts/mutation/reports/TCJ.Core/reports/mutation-report.html
artifacts/mutation/reports/TCJ.Core/reports/mutation-report.json
artifacts/mutation/reports/TCJ.DependencyInjection/run-metadata.json
artifacts/mutation/reports/TCJ.DependencyInjection/stryker-console.log
artifacts/mutation/reports/TCJ.DependencyInjection/reports/mutation-report.html
artifacts/mutation/reports/TCJ.DependencyInjection/reports/mutation-report.json
```

Artifacts are retained for review but are not committed.

## Interpret outcomes

- **Killed:** a test detected the mutation.
- **Survived:** the changed behavior was not rejected by the tests.
- **Timeout:** the mutation caused a bounded timeout and counts as detected.
- **No coverage:** no test exercised the mutant when coverage analysis is enabled.
- **Ignored:** the mutation was intentionally excluded by Stryker.
- **Compile error:** the generated mutant could not compile.
- **Runtime error, pending, or not run:** the result is incomplete and is rejected by policy.

A survived mutant is not automatically a product bug. It is evidence that current assertions do not distinguish the original behavior from the mutation. Add a focused test when the difference matters. Do not lower the gate or add a broad exclusion merely to make CI green.

## Changing scope or thresholds

A pull request that changes mutation scope, runner mode, ignored mutation types, or thresholds must include:

- old and new measured results;
- report links or artifacts;
- technical justification;
- verifier tests;
- documentation updates;
- changelog entry;
- a new reviewed baseline when result comparability changes.

Changing the Stryker version, test runner, coverage-analysis mode, or mutation targets invalidates score comparability and should normally produce a new baseline.

## Merge and release rule

Step 29 is complete only when:

- `eng/mutation-baseline.json` is recorded, not pending;
- normal CI is green;
- the reusable mutation quality gate is green;
- the dedicated workflow is green;
- HTML and JSON reports are available;
- survived mutants have been reviewed;
- release checklist and changelog are updated.

A merged pull request or a syntactically valid report does not by itself satisfy the Definition of Done.
