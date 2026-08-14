# Getting started

## 1. Choose the modules

Use only the modules required by the application:

| Need | Module |
| --- | --- |
| Entities, Result values, domain-event contracts | `TCJ.Core` |
| Convention-based dependency registration | `TCJ.DependencyInjection` |
| EF Core repositories and persistence conventions | `TCJ.EntityFrameworkCore` |
| SQL Server provider defaults | `TCJ.EntityFrameworkCore.SqlServer` |
| Problem Details and Result-to-HTTP mapping | `TCJ.AspNetCore` |

Install the exact preview versions required by the application:

```bash
dotnet add package TCJ.Core --version 0.1.0-preview.2
dotnet add package TCJ.DependencyInjection --version 0.1.0-preview.2
dotnet add package TCJ.EntityFrameworkCore --version 0.1.0-preview.2
dotnet add package TCJ.EntityFrameworkCore.SqlServer --version 0.1.0-preview.2
dotnet add package TCJ.AspNetCore --version 0.1.0-preview.2
```

The sample continues to use project references so it always exercises the current repository source.

The repository development version may be newer than the latest published preview. Consumer installation examples intentionally remain pinned to the latest verified NuGet.org release.

## 2. Define a DbContext

The context must implement the read and write abstractions:

```csharp
using Microsoft.EntityFrameworkCore;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        modelBuilder.ApplySoftDeleteQueryFilters();
        modelBuilder.ApplyTcjSqlServerConventions();
    }
}
```

`ApplySoftDeleteQueryFilters` adds a global filter for entities implementing `ISoftDelete`. `ApplyTcjSqlServerConventions` configures `IRowVersion.RowVersion` properties as SQL Server rowversion concurrency tokens.

## 3. Register services

```csharp
using TCJ.AspNetCore.Extensions;
using TCJ.DependencyInjection.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration
    .GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "Connection string 'Default' was not found.");

builder.Services.AddTcjDependencyInjection(options =>
{
    options.AddAssemblyContaining<Program>();
});

builder.Services.AddTcjAspNetCore(options =>
{
    options.IncludeExceptionDetails = builder.Environment.IsDevelopment();
});

builder.Services.AddTcjSqlServer<AppDbContext>(
    connectionString,
    configureTcjSqlServer: options =>
    {
        options.CommandTimeout = 30;
        options.MaxRetryCount = 6;
        options.MaxRetryDelay = TimeSpan.FromSeconds(30);
    });
```

For trimming-aware or Native AOT application code, do not use convention scanning. Call `builder.Services.AddTcjDependencyInjection()` with no assemblies, declare every dispatched event type with `builder.Services.AddTcjDomainEvent<TEvent>()`, and register application services and domain-event handlers explicitly through normal `IServiceCollection` methods. See [Native AOT and trimming](guides/native-aot-and-trimming.md) for the exact supported boundary.

`AddTcjSqlServer` also registers the services from `TCJ.EntityFrameworkCore`, including repositories, the unit of work, auditing, seeding, and entity search.

## 4. Configure the request pipeline

```csharp
var app = builder.Build();

app.UseTcjAspNetCore();
app.UseHttpsRedirection();

app.MapProductEndpoints();
app.Run();
```

Call `UseTcjAspNetCore` early. It enables the registered exception handler and Problem Details responses for otherwise-empty error status codes.

## 5. Define an entity

```csharp
using TCJ.Core.Entities;

public sealed class Product : FullAuditedEntity<Guid>
{
    private Product() { }

    public Product(Guid id, string name, decimal price)
    {
        Id = id;
        Rename(name);
        Price = price;
    }

    public string Name { get; private set; } = string.Empty;
    public decimal Price { get; private set; }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }
}
```

`FullAuditedEntity<TKey>` includes creation, modification, deletion, and soft-delete state. Audit values are populated by the EF Core interceptor when changes are saved.

## 6. Use Result values at the application boundary

```csharp
using TCJ.Core.Results;

public async Task<Result<ProductDto>> GetAsync(
    Guid id,
    CancellationToken cancellationToken)
{
    Product? product = await repository.GetByIdAsync(id, cancellationToken);

    return product is null
        ? Result.Failure<ProductDto>(
            CommonErrors.NotFound(nameof(Product), id))
        : Result.Success(
            new ProductDto(
                product.Id,
                product.Name,
                product.Price));
}
```

In a Minimal API endpoint:

```csharp
using TCJ.AspNetCore.Results;

app.MapGet("/api/products/{id:guid}",
    async (Guid id, IProductService service, CancellationToken ct) =>
    {
        Result<ProductDto> result = await service.GetAsync(id, ct);
        return result.ToHttpResult();
    });
```

## 7. Run the repository checks

```bash
dotnet restore TCJ.slnx
dotnet build TCJ.slnx -c Release --no-restore
dotnet test TCJ.slnx -c Release --no-build --filter "Category!=SqlServer&Category!=AspNetCore&Category!=Concurrency"
```

Continue with the [package reference](README.md#package-reference) and the [Product API sample](https://github.com/Amir-ESH/TCJ.Framework/blob/develop/samples/TCJ.Empty/README.md).

## Package and API reference

Use the [package landing pages](packages/index.md) to choose modules and locate their main entry points. The [API reference](api/index.md) is generated from the exact production projects and XML documentation comments. Consumer examples that are part of the quality gate are collected in [Validated consumer examples](examples.md).
