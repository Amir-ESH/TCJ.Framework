# Data seeding

## Create an idempotent contributor

```csharp
public sealed class ProductSeedContributor(
    IRepository<Product, Guid> repository,
    IUnitOfWork unitOfWork,
    IGuidGenerator guidGenerator)
    : IDataSeedContributor
{
    public int Order => 100;

    public async Task SeedAsync(
        CancellationToken cancellationToken = default)
    {
        if (await repository.AnyAsync(cancellationToken))
        {
            return;
        }

        await repository.AddAsync(
            new Product(
                guidGenerator.CreateVersion7(),
                "Sample product",
                10m),
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
```

A contributor must be safe to execute more than once.

## Register contributors

```csharp
builder.Services
    .AddTcjDataSeedContributor<ProductSeedContributor>();
```

Register each contributor explicitly. The seeder orders contributors by `Order`, then by full type name when orders match.

## Run seeding

```csharp
await app.Services.SeedTcjDataAsync();
```

Run seeding during a controlled startup or deployment phase. Avoid allowing multiple application instances to race through non-idempotent seed logic.

## Transaction note

The current `DataSeeder` owns an explicit transaction. If SQL Server retry-on-failure is enabled, coordinate the transaction with the provider execution strategy. The sample disables retry for its local seed path until that orchestration is added.
