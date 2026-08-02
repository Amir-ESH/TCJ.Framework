# Tests

The repository separates tests by production package:

```text
TCJ.Core.Tests
TCJ.DependencyInjection.Tests
TCJ.EntityFrameworkCore.Tests
TCJ.EntityFrameworkCore.SqlServer.Tests
TCJ.AspNetCore.Tests
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

## Mutation testing

`TCJ.Core.Tests` and `TCJ.DependencyInjection.Tests` are the first mutation-testing test projects. The policy selects a controlled set of production files; test code is never mutated. xUnit v3 runs through Microsoft Testing Platform, and coverage-based mutation optimization is disabled until its results are proven trustworthy. A result with zero killed mutants, all tested mutants surviving, incomplete statuses, excessive compile errors, mismatched hashes, or a pending baseline is rejected. Run and record the baseline as documented in [`docs/mutation-testing.md`](../docs/mutation-testing.md). Survived mutants should normally be addressed with focused behavioral assertions rather than broad exclusions.


A generated baseline candidate has status `candidate`. It becomes a valid recorded baseline only after both HTML reports are reviewed and `accept-baseline` records reviewer identity and notes.
