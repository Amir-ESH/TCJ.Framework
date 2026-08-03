# Tests

The repository separates tests by production package:

```text
TCJ.Core.Tests
TCJ.DependencyInjection.Tests
TCJ.EntityFrameworkCore.Tests
TCJ.EntityFrameworkCore.SqlServer.Tests
TCJ.AspNetCore.Tests
TCJ.Architecture.Tests
```

The suite uses xUnit v3, Microsoft.NET.Test.Sdk, the Visual Studio runner adapter, and Coverlet collection settings shared through `TestProject.props`.

Run all tests:

```bash
dotnet test TCJ.slnx -c Release
```

Run coverage:

```bash
dotnet test TCJ.slnx \
  -c Release \
  --collect:"XPlat Code Coverage" \
  --settings tests/coverlet.runsettings \
  --results-directory TestResults
python3 eng/verify-coverage.py verify
```

The verifier merges all package-specific Cobertura reports and enforces the repository thresholds from `eng/coverage-policy.json`. Tests should focus on public behavior and high-risk integration boundaries. Bug fixes should include a regression test in the closest package-specific project.

## Architecture tests

`TCJ.Architecture.Tests` references all five production projects for inspection and enforces the dependency graph, forbidden infrastructure references, namespace ownership, public API boundaries, and stable naming/visibility rules declared in `eng/architecture-policy.json`.

Run only the architecture category:

```bash
dotnet test tests/TCJ.Architecture.Tests/TCJ.Architecture.Tests.csproj \
  -c Release \
  -- --filter-trait "Category=Architecture"
```

Failures identify the assembly or type, the unexpected dependency or namespace, the expected rule, and the policy/documentation that must be updated for an intentional architecture change.

## Mutation testing

`TCJ.Core.Tests` and `TCJ.DependencyInjection.Tests` are the first mutation-testing test projects. Stryker uses the Microsoft Testing Platform runner for xUnit v3 and mutates only the controlled production files listed in `eng/mutation-policy.json`; test code is never mutated.

The verifier rejects missing reports, runner failures, mismatched hashes, excessive compile errors, incomplete statuses, zero-killed/all-survived executions, and score regressions. A pending baseline does not prevent Stryker from running: a valid run produces a candidate, which becomes a recorded baseline only after both HTML reports are reviewed and `accept-baseline` records reviewer identity and notes. See [`docs/mutation-testing.md`](../docs/mutation-testing.md).
