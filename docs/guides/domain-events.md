# Domain events

## Define an event

```csharp
public sealed record ProductCreated(
    Guid ProductId,
    DateTimeOffset OccurredOn) : IDomainEvent;
```

## Raise an event from an entity

```csharp
public sealed class Product : Entity<Guid>
{
    public Product(Guid id, string name)
        : base(id)
    {
        Name = name;
        AddDomainEvent(new ProductCreated(id, DateTimeOffset.UtcNow));
    }

    public string Name { get; }
}
```

`AddDomainEvent` and `RemoveDomainEvent` are protected. Pending events are exposed through `DomainEvents` as a read-only collection.

## Handle an event

```csharp
public sealed class ProductCreatedHandler
    : IDomainEventHandler<ProductCreated>
{
    public Task HandleAsync(
        ProductCreated domainEvent,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

In regular JIT/non-trimmed applications, handlers can be discovered when their public assembly is supplied to the convention-scanning `AddTcjDependencyInjection` overload.

For Native AOT or trimming, use the explicit path instead:

```csharp
services.AddTcjDependencyInjection();
services.AddTcjDomainEvent<ProductCreated>();
services.AddTransient<IDomainEventHandler<ProductCreated>, ProductCreatedHandler>();
```

`AddTcjDomainEvent<TEvent>()` declares a closed dispatch route only; handler lifetime remains controlled by the normal Microsoft DI registration. Convention scanning stays available but is explicitly restricted for trimming and Native AOT.

## Dispatch explicitly

```csharp
await dispatcher.DispatchAsync(
    aggregate.DomainEvents,
    cancellationToken);

aggregate.ClearDomainEvents();
```

The current preview does not dispatch events automatically from EF Core. Decide at the application boundary whether events are dispatched before or after persistence and how failures are handled.

## Execution semantics

- Events are dispatched in collection order.
- Handlers for each event run sequentially.
- Cancellation is propagated.
- Handler exceptions are not swallowed.
- Clearing pending events remains the caller's responsibility.

For durable cross-process delivery, add an outbox implementation in the consuming application; TCJ does not currently provide one.
