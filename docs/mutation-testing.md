# Mutation testing quality gate

Mutation testing measures whether the test suite detects deliberate behavioral changes in production code. Stryker.NET creates small changes called mutants and runs the relevant tests against them. A mutant is **killed** when a test detects the change and **survives** when the tests still pass.

Code coverage and mutation testing answer different questions:

- code coverage asks whether code executed;
- mutation testing asks whether the assertions would detect a meaningful change in that code.

Both checks are required. A high coverage percentage does not prove that assertions are strong.

## Why the earlier runs were invalid

The first the initial mutation-testing integration run used the VSTest path with xUnit v3. Stryker could not reliably control the active mutant, so every tested mutant was reported as survived and the aggregate score was `0.00%`.

PR #53 then introduced a different failure: normal CI required `eng/mutation-baseline.json` to be recorded **before** Stryker was allowed to run. Because the first real baseline can only be produced after Stryker completes, that workflow created a bootstrap deadlock. Restore, build, tests, and Stryker were skipped.

The current workflow fixes both problems:

1. xUnit v3 mutation runs use Stryker's Microsoft Testing Platform runner;
2. the test projects are standalone executables;
3. Stryker runs before the baseline state is enforced;
4. a pending baseline produces a candidate after a valid run instead of preventing the run;
5. a recorded baseline is required only after the candidate has been reviewed and accepted.

## Controlled initial scope

The initial baseline covers selected foundational files in:

- `TCJ.Core` through `tests/TCJ.Core.Tests`;
- `TCJ.DependencyInjection` through `tests/TCJ.DependencyInjection.Tests`.

The exact projects, mutation targets, thresholds, exclusions, report paths, and runner-health rules are defined in `eng/mutation-policy.json`.

Generated code, migrations, samples, smoke tests, test projects, build output, and assembly metadata are excluded. Test projects are never mutated.

## xUnit v3 and MTP configuration

The shared Stryker configuration uses:

```json
{
  "test-runner": "mtp",
  "coverage-analysis": "off",
  "concurrency": 1,
  "disable-mix-mutants": true
}
```

`tests/TestProject.props` sets the test projects to `OutputType=Exe` and enables the Microsoft Testing Platform command-line runner. The mutation workflow runs the unmutated test executables with `dotnet run` before Stryker starts.

The repository's normal `CI` workflow intentionally keeps its existing `dotnet test`/VSTest coverage path so Coverlet collection and the established code-coverage gate are not changed by mutation testing.

Coverage-based mutation optimization is disabled for the initial baseline because the earlier xUnit v3 coverage capture was invalid. All relevant tests are therefore run against each tested mutant. This is slower but safer.

Stryker.NET 4.16 reuses MTP test hosts. Two process-wide static initializers in the controlled scope have narrow, documented `Stryker disable once all` comments because mutating them could contaminate later mutant sessions. These exclusions are listed in `sourceLevelExclusions` and validated by the repository verifier. Broad mutation-type exclusions remain prohibited unless they have a specific policy justification.

## Quality policy

The initial aggregate policy requires at least:

- mutation score: `50.0%`;
- tested mutants: `20`;
- killed mutants: `2`;
- killed mutants per project: `1`;
- compile-error rate: no more than `10.0%`;
- runtime-error, pending, and not-run mutants: `0`.

The verifier rejects a report before score enforcement when any of these conditions occurs:

- a Stryker process failed;
- an expected JSON or HTML report is missing;
- the report schema or project identity is wrong;
- report, policy, log, or source-revision metadata does not match;
- a known invalid-run marker appears in the runner log;
- too few mutants were tested or killed;
- every tested mutant survived and no mutant was killed;
- compile errors exceed policy;
- runtime-error, pending, or not-run mutants exist;
- the two project reports were produced from different source revisions.

This prevents another structurally valid but meaningless `0%` report from becoming the baseline.

## Workflow behavior

The workflow is named **Mutation testing** and its stable required-check name is:

```text
Mutation testing / Run mutation tests
```

It supports:

- pull requests targeting `main` or `develop`;
- pushes to `main` or `develop`;
- weekly scheduled execution;
- manual execution with `verify` or `capture-baseline` mode.

The workflow does not use a top-level pull-request path filter. It always creates the stable check, then performs an internal scope check. Unrelated changes finish successfully as `not-applicable`, so a required check cannot remain permanently pending. The scope detector treats `.config/dotnet-tools.json` as mutation-relevant only when the pinned `dotnet-stryker` definition changes; adding or updating unrelated repository tools such as DocFX does not start an expensive Stryker run. Changes to the scope detector itself are covered by the repository Python test suite, while controlled production, test, Stryker, policy, or baseline changes still require the full mutation run.

## First baseline bootstrap

`eng/mutation-baseline.json` initially has status `pending`. There are two safe ways to produce the first candidate.

### Recommended: manual capture

1. Push the fix branch.
2. Open **Actions → Mutation testing → Run workflow**.
3. Select the fix branch.
4. Select mode `capture-baseline`.
5. Run the workflow.
6. Download the `mutation-reports-...-capture-baseline` artifact.

The capture run still enforces execution health, minimum score, tested-mutant count, and killed-mutant count. It does not bypass the quality policy. It only allows a pending baseline to produce an unreviewed candidate.

### Normal pull-request verification while pending

A normal `verify` run also executes Stryker and generates a candidate when the result is valid. It then fails deliberately because an unreviewed candidate is not yet a recorded baseline. This is expected only during the one-time bootstrap.

## Review and accept the candidate

Extract the artifact so this file exists:

```text
artifacts/mutation/mutation-baseline-candidate.json
```

Review both HTML reports, especially every survived mutant:

```text
artifacts/mutation/reports/TCJ.Core/reports/mutation-report.html
artifacts/mutation/reports/TCJ.DependencyInjection/reports/mutation-report.html
```

Then accept the candidate with explicit reviewer information:

```bash
python3 eng/verify-mutation-results.py accept-baseline \
  --candidate artifacts/mutation/mutation-baseline-candidate.json \
  --output-baseline eng/mutation-baseline.json \
  --reviewed-by "<github-user>" \
  --review-notes "Reviewed both HTML reports and documented meaningful survivors in the pull request."
```

Commit the recorded baseline:

```bash
git add eng/mutation-baseline.json
git commit -m "test: record mutation testing baseline"
git push
```

The next normal `verify` run enforces both the repository minimum and the recorded baseline score. The allowed score regression is `0.0` by default.

Never rename or copy the candidate directly over `eng/mutation-baseline.json`; the acceptance command records the review attestation and validates the candidate.

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

dotnet run --project tests/TCJ.Core.Tests/TCJ.Core.Tests.csproj \
  --configuration Release \
  --no-build \
  --no-restore
dotnet run --project tests/TCJ.DependencyInjection.Tests/TCJ.DependencyInjection.Tests.csproj \
  --configuration Release \
  --no-build \
  --no-restore

python3 -m unittest discover \
  --start-directory eng/tests \
  --pattern "test_*.py"
python3 eng/verify-mutation-results.py validate-config

rm -rf artifacts/mutation
mkdir -p artifacts/mutation/reports
python3 eng/run-mutation-testing.py --project TCJ.Core
python3 eng/run-mutation-testing.py --project TCJ.DependencyInjection
```

For a pending baseline candidate:

```bash
python3 eng/verify-mutation-results.py capture-baseline
```

For a recorded baseline:

```bash
python3 eng/verify-mutation-results.py validate-baseline
python3 eng/verify-mutation-results.py verify
```

An exported source archive has no `.git` directory. Configuration-only validation may use `--skip-git-check`; normal clones and CI must keep Git tracking validation enabled.

## Generated outputs

```text
artifacts/mutation/mutation-summary.json
artifacts/mutation/MUTATION_SUMMARY.md
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

Mutation output is ignored by Git. Policy, baseline, runner, verifier, workflow, tool manifest, and shared Stryker configuration remain tracked.

## Reading survived mutants

A survived mutant is not automatically a production bug. It means the current assertions do not distinguish the original behavior from that specific change. Review the affected behavior and add a focused test when the difference matters.

Do not lower the score, broaden exclusions, or globally disable mutation types merely to make the workflow green. Global mutation-type exclusions can hide meaningful defects across unrelated code and therefore require a specific justification in policy.

## Updating the baseline

A baseline update requires a new valid run and report review when any of these changes affects comparability:

- Stryker version;
- test runner or coverage-analysis mode;
- mutation targets or exclusions;
- meaningful test behavior;
- score thresholds.

Record the old score, new score, report artifact, review notes, and reason in the pull request. Raise the baseline gradually as focused tests are added.
