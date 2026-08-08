# Code coverage quality gate

TCJ Framework collects cross-platform coverage for every production package during CI, release preflight, and tagged release validation. Coverage is a regression signal, not a substitute for assertions or behavior-focused tests.

## Policy

The repository policy lives in [`eng/coverage-policy.json`](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/eng/coverage-policy.json):

```json
{
  "minimumLineCoverage": 15.0,
  "minimumBranchCoverage": 10.0,
  "minimumReportCount": 6
}
```

These deliberately conservative initial minimums protect the preview codebase from losing broad test execution while the suite grows. Raise them only after the current default branch is measured and the higher value is demonstrated to pass consistently. Lowering either threshold requires an explicit explanation in the pull request.

The expected package list must remain identical to `eng/release-manifest.json`. The expected report count must remain identical to the number of non-excluded test projects included in `TCJ.slnx`; dedicated SQL Server and ASP.NET Core integration projects are validated by their own gates and are listed in `excludedTestProjects`.

## Collection

All test projects inherit `coverlet.collector` from `tests/TestProject.props`. The shared `tests/coverlet.runsettings` configuration:

- emits Cobertura XML;
- includes `TCJ.*` production assemblies;
- excludes test assemblies and compiler-generated code;
- uses Source Link paths when available.

Run the same collection locally:

```bash
dotnet test TCJ.slnx \
  -c Release \
  --filter "Category!=SqlServer&Category!=AspNetCore" \
  --collect:"XPlat Code Coverage" \
  --settings tests/coverlet.runsettings \
  --results-directory TestResults
```

## Verification

Validate repository wiring without running tests:

```bash
python3 eng/verify-coverage.py validate-config
```

After collecting reports, merge and enforce the gate:

```bash
python3 eng/verify-coverage.py verify
```

The verifier discovers `TestResults/**/coverage.cobertura.xml`, merges duplicate source lines across test projects using the highest observed hit count, and computes branch coverage from Cobertura condition data. It fails when:

- fewer than six reports exist;
- any TCJ production package is absent from the reports;
- no production or branch data exists;
- line or branch coverage is below policy;
- coverage policy, runsettings, test-project setup, or workflow integration is weakened.

Generated outputs are local build artifacts:

```text
artifacts/coverage/COVERAGE_SUMMARY.md
artifacts/coverage/coverage-summary.json
```

CI appends the Markdown summary to the GitHub Actions run summary and uploads the raw Cobertura reports plus both merged summaries.

## Review expectations

New behavior and bug fixes should add focused assertions in the nearest package-specific test project. Do not add meaningless execution-only tests solely to increase a percentage. Exclusions must be narrow, technically justified, and reviewed alongside the affected production code.

The SQL Server integration project is intentionally excluded from the general coverage-report count in `eng/coverage-policy.json`. Its tests require Docker and run in the dedicated SQL Server integration workflow, while the normal coverage gate continues to measure the six non-container test projects. This exclusion prevents the coverage job from starting the same database suite a second time and does not exclude any production TCJ package from coverage measurement.
