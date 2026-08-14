# TCJ.Core

`TCJ.Core` contains framework-neutral primitives and has no ASP.NET Core or Entity Framework Core dependency.

## Entities

Choose the smallest base class required by the model:

| Type | Adds |
| --- | --- |
| `Entity<TKey>` | Strongly typed key and pending domain events |
| `AuditedEntity<TKey>` | Creation and modification audit fields |
| `FullAuditedEntity<TKey>` | Soft-delete fields |
| `RowVersionAuditedEntity<TKey>` | Audit fields plus binary rowversion |
| `RowVersionFullAuditedEntity<TKey>` | Audit, soft delete, and rowversion |

Entity equality semantics are not supplied; domain models remain responsible for their own equality rules.

## Result pattern

```csharp
Result success = Result.Success();
Result<ProductDto> value = Result.Success(productDto);

Result failure = Result.Failure(
    CommonErrors.ValidationForField("Name", "Name is required."));
```

A successful generic result exposes `Value`. Reading `Value` from a failed result throws `InvalidOperationException`.

Useful operations include:

- `Map`
- `Bind`
- `Ensure`
- `Match`
- `Switch`
- `Tap`
- `TapFailure`
- `Result.Combine`

Errors are immutable `ResultError` instances with a code, message, semantic `ResultErrorType`, and read-only metadata.

## Common errors

`CommonErrors` creates consistent errors for:

- general failure
- validation and field validation
- not found
- conflict
- unauthorized
- forbidden
- unexpected failures

## Domain events

`IDomainEvent` defines `OccurredOn`. Entities derived from `Entity<TKey>` can add protected pending events. Dispatching is performed through `IDomainEventDispatcher` supplied by `TCJ.DependencyInjection`.

## Identifiers

`IGuidGenerator.CreateVersion7()` creates time-ordered UUID version 7 values through the default `GuidGenerator` implementation.

## Current user abstraction

`ICurrentUserProvider` exposes a nullable numeric `long` user identifier. The abstraction is transport-neutral; `TCJ.AspNetCore` supplies the HTTP implementation.

## Guards and extensions

The package contains focused guard and extension helpers. Prefer .NET built-in guard APIs when they already express the requirement, and use TCJ helpers for project-specific gaps.
