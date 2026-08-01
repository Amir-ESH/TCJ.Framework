# TCJ.Empty sample

This sample is a small Product API demonstrating the TCJ modules together:

- convention-based dependency injection
- immutable Result values mapped to HTTP Problem Details
- SQL Server registration
- generic repositories and specifications
- Unit of Work
- auditing and soft deletion
- deterministic data seeding

## Run

The default connection string uses SQL Server LocalDB on Windows:

```text
Server=(localdb)\MSSQLLocalDB;Database=TCJ.Empty;Trusted_Connection=True;TrustServerCertificate=True
```

Change `ConnectionStrings:Default` in `appsettings.json` when using a different SQL Server instance.

```powershell
dotnet run --project .\samples\TCJ.Empty\TCJ.Empty.csproj
```

In Development, the sample creates the database with `EnsureCreatedAsync`, seeds three products, and exposes OpenAPI at `/openapi/v1.json`.

Use `TCJ.Empty.http` to exercise create, read, update, soft-delete, and restore operations.
