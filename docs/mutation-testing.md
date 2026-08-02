# Mutation testing quality gate

Mutation testing measures whether the test suite detects deliberate behavioral defects in production code. Stryker.NET creates small compiled-code changes, called mutants, and reruns the relevant tests. A mutant is **killed** when a test fails because of the change and **survives** when the tests still pass.

Code coverage and mutation testing answer different questions:

- code coverage asks whether a line or branch executed;
- mutation testing asks whether the assertions would detect a meaningful change in that executed behavior.

High coverage remains useful, but it does not prove that assertions are strong. The repository therefore keeps coverage and mutation testing as separate quality gates.

## Initial baseline

The first controlled baseline mutates:

- `TCJ.Core`, using `tests/TCJ.Core.Tests`;
- `TCJ.DependencyInjection`, using `tests/TCJ.DependencyInjection.Tests`.

The repository policy is stored in `eng/mutation-policy.json`. The initial aggregate requirements are:

- mutation score: at least `50.0%`;
- tested mutants: at least `20`.

This is a starting baseline, not the final quality target. Add focused tests first, then raise the minimum score in a dedicated pull request after a stable report has been reviewed.

## Configuration and reports

`stryker-config.json` contains the shared Stryker.NET settings. Stryker runs once per production project because one Stryker invocation mutates one project under test. The repository verifier then combines the two JSON reports and applies one aggregate policy.

Generated outputs are written to:

```text
artifacts/mutation/mutation-summary.json
artifacts/mutation/MUTATION_SUMMARY.md
artifacts/mutation/reports/TCJ.Core/reports/mutation-report.html
artifacts/mutation/reports/TCJ.Core/reports/mutation-report.json
artifacts/mutation/reports/TCJ.DependencyInjection/reports/mutation-report.html
artifacts/mutation/reports/TCJ.DependencyInjection/reports/mutation-report.json
```

`artifacts/`, local Stryker output directories, and the local tool directory are ignored by Git. The policy, verifier, workflow, and shared Stryker configuration must remain tracked.

## Run locally

Prerequisites are the .NET SDK selected by `global.json`, Python 3, and Git. From the repository root:

```bash
dotnet restore TCJ.slnx
dotnet build tests/TCJ.Core.Tests/TCJ.Core.Tests.csproj -c Release --no-restore
dotnet build tests/TCJ.DependencyInjection.Tests/TCJ.DependencyInjection.Tests.csproj -c Release --no-restore

dotnet tool install --tool-path .tools dotnet-stryker --version 4.16.0

python3 -m unittest discover \
  --start-directory eng/tests \
  --pattern "test_*.py"
python3 eng/verify-mutation-results.py validate-config

rm -rf artifacts/mutation
mkdir -p artifacts/mutation/reports

(
  cd tests/TCJ.Core.Tests
  ../../.tools/dotnet-stryker \
    --config-file ../../stryker-config.json \
    --project TCJ.Core.csproj \
    --output ../../artifacts/mutation/reports/TCJ.Core \
    --skip-version-check
)

(
  cd tests/TCJ.DependencyInjection.Tests
  ../../.tools/dotnet-stryker \
    --config-file ../../stryker-config.json \
    --project TCJ.DependencyInjection.csproj \
    --output ../../artifacts/mutation/reports/TCJ.DependencyInjection \
    --skip-version-check
)

python3 eng/verify-mutation-results.py verify
```

On Windows, invoke `.tools\dotnet-stryker.exe` instead of `.tools/dotnet-stryker`. An exported source archive has no `.git` directory; configuration-only validation can use `--skip-git-check` there. Normal cloned repositories and CI must keep the Git tracking check enabled.

Open each generated HTML report to inspect individual mutants. The JSON and Markdown summaries are intended for automation and pull-request review.

## Read the results

- **Killed:** a test detected the mutation.
- **Survived:** tests executed the changed behavior but did not reject it. Add or strengthen an assertion when the mutation represents a real behavioral requirement.
- **Timeout:** the mutation caused the test run to exceed its limit. Stryker counts this as detected, but recurring timeouts should still be investigated.
- **No coverage:** no test executed the mutant. Add a test or document why the code is intentionally outside the current scope.
- **Ignored:** Stryker or repository configuration excluded the mutant.
- **Compile error:** the generated mutation could not produce a valid build and is excluded from the score.

A survived mutant is not automatically a production bug. It is evidence that the current tests do not distinguish the original behavior from that specific change. Review the affected code, decide whether the difference matters, and prefer a focused behavioral test over a broader exclusion.

## Exclusion policy

The baseline excludes generated files, build output, migrations, samples, smoke tests, assembly metadata, test projects, and files outside the selected production projects. Exclusions must be narrow and must describe code that cannot provide meaningful mutation feedback.

When adding an exclusion:

1. reproduce and review the mutant;
2. explain why a focused test is not the correct solution;
3. add the pattern to `eng/mutation-policy.json` and `stryker-config.json`;
4. update this page or the pull-request description with the justification;
5. run the verifier tests and configuration validation.

Globally disabling a mutation type is discouraged because it can hide useful defects across unrelated code. Use file- or line-level exclusions when possible. If a global mutation type must be ignored, add it to `ignoredMutationTypes` and provide a matching technical reason in `ignoredMutationJustifications`; the verifier rejects unjustified entries.

## Update the baseline

Do not lower the threshold only to make a run pass. First inspect survived and no-coverage mutants, add tests for meaningful behavior, and rerun both projects. When the score is stable and repeatable:

1. update `minimumMutationScore` or `minimumTestedMutants` in `eng/mutation-policy.json`;
2. keep the Stryker `thresholds.low` value aligned with the policy score;
3. record the change in `CHANGELOG.md`;
4. include the old score, new score, and reviewed report in the pull request;
5. run `python3 eng/verify-mutation-results.py validate-config` and the complete mutation workflow.

The weekly **Mutation testing** workflow catches test-effectiveness regressions even when no release is in progress. Pull requests and pushes that change the selected production or test projects trigger the same workflow, and the HTML/JSON reports are uploaded as artifacts.
