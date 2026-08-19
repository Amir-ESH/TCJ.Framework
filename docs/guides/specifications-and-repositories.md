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

Paging is zero-based and must be paired with deterministic primary ordering. Use `ApplyOrderBy` or `ApplyOrderByDescending` to establish the primary order, then use `ApplyThenBy`/`ApplyThenByDescending` for deterministic tie-breakers when needed. Secondary ordering alone does not establish the primary order.

`TCJ2000` reports clearly unordered `ApplyPaging` calls when the analyzer can prove the construction path has no primary ordering. The rule is intentionally conservative around helper-heavy or ambiguous construction paths and does not offer a code fix because TCJ cannot safely guess the domain ordering key. See [`TCJ2000`](../analyzers/TCJ2000.md).

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

Repository implementations must not take ownership of the persistence boundary by calling Entity Framework Core `DbContext.SaveChanges`/`SaveChangesAsync` or TCJ `IUnitOfWork.SaveChangesAsync`. Commit after composing repository operations at the application/use-case boundary. `TCJ1000` reports repository-owned commits when the analyzer is enabled; it intentionally offers no code fix because moving a commit boundary requires application and transaction semantics that cannot be inferred safely. See [`TCJ1000`](../analyzers/TCJ1000.md).

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
