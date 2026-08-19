# Domain events

Domain events let aggregates record facts that occurred inside the domain without directly invoking infrastructure or application services. TCJ keeps event recording separate from delivery so applications can choose either immediate in-process dispatch or the optional transactional-outbox path.

## Define an event

```csharp
public sealed record OrderPlaced(Guid OrderId) : IDomainEvent
{
    public DateTimeOffset OccurredOn { get; init; } = DateTimeOffset.UtcNow;
}
```

## Raise events from an entity

Entities derived from `Entity<TKey>` can record pending events through the protected domain-event API. Recording an event does not dispatch it and does not perform I/O.

```csharp
public sealed class Order : Entity<Guid>
{
    public void Place()
    {
        // Apply domain state changes first.
        AddDomainEvent(new OrderPlaced(Id));
    }
}
```

## Direct in-process dispatch

Use direct dispatch when the application deliberately owns the persistence/dispatch boundary and durable delivery is not required.

```csharp
await dispatcher.DispatchAsync(
    aggregate.DomainEvents,
    cancellationToken);

aggregate.ClearDomainEvents();
```

Direct dispatch has these semantics:

- events are dispatched in collection order;
- handlers for each event run sequentially;
- cancellation is propagated;
- handler exceptions are not swallowed;
- clearing pending events remains the caller's responsibility after the application has decided the event lifecycle is complete.

Direct dispatch is intentionally not coupled to EF Core `SaveChanges`. An application can dispatch before or after persistence, but it must decide what a handler failure means for its own consistency boundary.

## Handler registration and lifetime

TCJ has two supported handler-registration paths:

- With convention scanning, effectively public concrete `IDomainEventHandler<TEvent>` implementations are discovered by the dedicated handler pipeline and registered as transient services. The later dependency-marker pass skips handler types, so TCJ lifetime markers such as `ITransientDependency`, `IScopedDependency`, `ISingletonDependency`, and their self-registration variants do not control handler lifetime. `TCJ0004` reports those misleading markers.
- With the reflection-free path, `AddTcjDomainEvent<TEvent>()` declares the event dispatch route but does not discover handlers. Register each handler explicitly with normal `IServiceCollection` methods (`AddTransient`, `AddScoped`, or `AddSingleton`) and let that explicit registration define the handler lifetime.

A handler may implement `IDomainEventHandler<TEvent>` for more than one event type. The same registration contract applies to each handler interface. Do not add TCJ dependency lifetime markers to communicate handler lifetime; use the handler-registration path itself.

## Transactional outbox

For durable delivery, TCJ provides an **optional transactional outbox**. When enabled for an EF Core context, pending domain events are serialized into `TCJ_OutboxMessages` during `SaveChanges` and committed in the same database transaction as the business changes. A separate processor dispatches only committed messages later.

The outbox changes event ownership semantics:

- the persistence interceptor captures the pending events as outbox messages;
- a successful persistence/transaction boundary clears the captured pending events;
- consumers do not manually dispatch the same pending event collection after it has been persisted to the outbox;
- delivery is **at least once**, so handlers with non-idempotent side effects need an idempotency strategy.

Register persisted event contracts explicitly with stable logical names:

```csharp
services.AddTcjOutboxEvent<OrderPlaced>("order.placed.v1");
```

Then enable the provider-specific outbox integration. SQL Server applications use `AddTcjSqlServerOutbox<TDbContext>` and own the migration that creates `TCJ_OutboxMessages`.

See [Transactional outbox](../outbox.md) for schema ownership, processing, retries, dead-letter behavior, replay, cleanup, health checks, and telemetry.

## Choosing a delivery model

Use **direct dispatch** when all handlers intentionally run inside the application's current execution path and the application can tolerate or explicitly coordinate persistence/handler failures.

Use the **transactional outbox** when a domain event must survive process failure after the business transaction commits or when dispatch should happen asynchronously outside the request/command transaction.

Do not use both paths for the same pending event instance; doing so can cause duplicate application-level effects outside the outbox's normal at-least-once contract.
