# TCJ.Empty Product API sample

This sample demonstrates the current TCJ modules together in a small Minimal API:

- convention-based dependency injection
- immutable Result values mapped to HTTP Problem Details
- SQL Server registration
- generic repositories and specifications
- Unit of Work
- auditing and soft deletion
- deterministic, idempotent data seeding

For framework setup and package details, start with the [documentation hub](../../docs/README.md).

## Prerequisites

- .NET SDK selected by the repository `global.json`
- SQL Server LocalDB on Windows, or another reachable SQL Server instance

## Connection string

The default Development connection string uses SQL Server LocalDB:

```text
Server=(localdb)\MSSQLLocalDB;Database=TCJ.Empty;Trusted_Connection=True;TrustServerCertificate=True
```

Change `ConnectionStrings:Default` in `appsettings.json`, environment variables, or user secrets when using a different server. Do not commit production credentials.

## Run

```powershell
dotnet run --project .\samples\TCJ.Empty\TCJ.Empty.csproj
```

In Development, the sample:

1. creates the database with `EnsureCreatedAsync`;
2. seeds three products;
3. exposes OpenAPI at `/openapi/v1.json`.

`EnsureCreatedAsync` is used only to keep the sample self-contained. Production applications should use an explicit migration and deployment strategy.

## Endpoints

```text
GET    /api/products
GET    /api/products/{id}
POST   /api/products
PUT    /api/products/{id}
DELETE /api/products/{id}
POST   /api/products/{id}/restore
```

Use `TCJ.Empty.http` to exercise create, read, update, soft-delete, and restore operations.

## Retry note

The sample data seeder owns an explicit transaction, so SQL Server retry-on-failure is disabled in `Program.cs` until execution-strategy orchestration is added. This is a sample-specific choice, not a general production recommendation.
