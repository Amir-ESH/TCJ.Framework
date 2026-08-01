# Specifications and repositories

## Define a read specification

```csharp
public sealed class ActiveProductsByNameSpecification
    : Specification<Product>
{
    public ActiveProductsByNameSpecification(int skip, int take)
        : base(product => product.IsActive)
    {
        ApplyOrderBy(product => product.Name);
        ApplyThenBy(product => product.Id);
        ApplyPaging(skip, take);
    }
}
```

Paging is zero-based and must be paired with ordering.

## Define an update specification

```csharp
public sealed class ProductForUpdateSpecification
    : Specification<Product>
{
    public ProductForUpdateSpecification(Guid id)
        : base(product => product.Id == id)
    {
        AsTracking();
    }
}
```

Read specifications are no-tracking by default. Request tracking only when the loaded entity will be modified.

## Include soft-deleted rows

```csharp
public sealed class DeletedProductSpecification
    : Specification<Product>
{
    public DeletedProductSpecification(Guid id)
        : base(product => product.Id == id)
    {
        IgnoreGlobalQueryFilters();
        AsTracking();
    }
}
```

## Query through a repository

```csharp
IReadOnlyList<Product> products =
    await repository.ListAsync(specification, cancellationToken);

bool exists =
    await repository.AnyAsync(specification, cancellationToken);

int count =
    await repository.CountAsync(specification, cancellationToken);
```

For `AnyAsync` and `CountAsync`, ordering, includes, and paging do not affect the operation.

## Stage and persist changes

```csharp
await repository.AddAsync(product, cancellationToken);
await unitOfWork.SaveChangesAsync(cancellationToken);
```

Write methods do not call `SaveChangesAsync` automatically.

## Explicit transaction

```csharp
await using IUnitOfWorkTransaction transaction =
    await unitOfWork.BeginTransactionAsync(cancellationToken);

try
{
    await repository.AddAsync(product, cancellationToken);
    await unitOfWork.SaveChangesAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
}
catch
{
    await transaction.RollbackAsync(cancellationToken);
    throw;
}
```

Coordinate explicit transactions with the configured provider execution strategy.
