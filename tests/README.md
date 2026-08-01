# TCJ tests

The suite uses xUnit.net v3.

The test suite is split by production package so failures remain easy to locate:

- `TCJ.Core.Tests`: result invariants, domain entities and UUID v7 generation.
- `TCJ.DependencyInjection.Tests`: marker-based registrations and domain-event dispatch.
- `TCJ.EntityFrameworkCore.Tests`: auditing, soft delete, specifications, entity search and unit of work.
- `TCJ.EntityFrameworkCore.SqlServer.Tests`: SQL Server rowversion conventions and provider registration.
- `TCJ.AspNetCore.Tests`: current-user resolution and Result-to-HTTP mapping.

Run all tests:

```powershell
dotnet test .\TCJ.slnx -c Release
```

Run with coverage:

```powershell
dotnet test .\TCJ.slnx `
  -c Release `
  --collect:"XPlat Code Coverage" `
  --settings .\tests\coverlet.runsettings
```
