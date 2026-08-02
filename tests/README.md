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

`TCJ.Core.Tests` and `TCJ.DependencyInjection.Tests` are the first mutation-testing test projects. Stryker mutates only their corresponding production projects; test code is never mutated. Run the local baseline and verifier as documented in [`docs/mutation-testing.md`](../docs/mutation-testing.md). Survived mutants should normally be addressed with focused behavioral assertions rather than broad exclusions.

