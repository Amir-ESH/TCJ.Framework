# TCJ Framework

TCJ is a modular foundation for building .NET 10 applications with explicit boundaries between domain primitives, dependency injection, persistence, SQL Server, and ASP.NET Core integration.

> **Status:** `0.1.0-preview.1` is a preview release. Public APIs may change before `1.0.0`.

Source code and issue tracking: https://github.com/Amir-ESH/TCJ.Framework

## Packages

| Package | Purpose |
| --- | --- |
| `TCJ.Core` | Entities, Result pattern, domain-event contracts, guards, identifiers, and security abstractions. |
| `TCJ.DependencyInjection` | Convention-based service registration and sequential domain-event dispatching. |
| `TCJ.EntityFrameworkCore` | Repositories, specifications, unit of work, auditing, soft delete, seeding, and entity search. |
| `TCJ.EntityFrameworkCore.SqlServer` | SQL Server registration options and rowversion conventions. |
| `TCJ.AspNetCore` | Current-user resolution, Result-to-HTTP mapping, Problem Details, and exception handling. |

## Installation

Install only the packages required by your application:

```bash
dotnet add package TCJ.Core --prerelease
dotnet add package TCJ.DependencyInjection --prerelease
dotnet add package TCJ.EntityFrameworkCore --prerelease
dotnet add package TCJ.EntityFrameworkCore.SqlServer --prerelease
dotnet add package TCJ.AspNetCore --prerelease
```

## Minimal setup

```csharp
using TCJ.AspNetCore.Extensions;
using TCJ.DependencyInjection.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTcjDependencyInjection(typeof(Program).Assembly);
builder.Services.AddTcjAspNetCore();
builder.Services.AddTcjSqlServer<AppDbContext>(
    builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException(
            "Connection string 'Default' was not found."));

var app = builder.Build();

app.UseTcjAspNetCore();
app.Run();
```

A complete Product API sample is available in `samples/TCJ.Empty`.

## Build, test, and pack

```bash
dotnet restore TCJ.slnx
dotnet build TCJ.slnx -c Release --no-restore
dotnet test TCJ.slnx -c Release --no-build
dotnet pack TCJ.slnx -c Release --no-build
```

NuGet packages and symbol packages are written to `artifacts/packages`.

## License

TCJ is licensed under the MIT License. See `LICENSE.txt`.