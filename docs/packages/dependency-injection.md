# TCJ.DependencyInjection

This package adds explicit assembly scanning, lifetime markers, framework defaults, and sequential domain-event dispatching.

## Registration

```csharp
builder.Services.AddTcjDependencyInjection(typeof(Program).Assembly);
```

Or configure the scan set explicitly:

```csharp
builder.Services.AddTcjDependencyInjection(options =>
{
    options.AddAssemblyContaining<Program>();
    options.AddAssemblyContaining<ApplicationAssemblyMarker>();
});
```

Only public, concrete types in the supplied assemblies are scanned.

## Lifetime markers

Register an implementation through its service interfaces:

```csharp
public interface IOrderService;

public sealed class OrderService : IOrderService, IScopedDependency;
```

Available interface-registration markers:

- `ITransientDependency`
- `IScopedDependency`
- `ISingletonDependency`

Register a concrete type as itself:

```csharp
public sealed class CacheWarmer : ISelfSingletonDependency;
```

Available self-registration markers:

- `ISelfTransientDependency`
- `ISelfScopedDependency`
- `ISelfSingletonDependency`

A type must not implement more than one TCJ lifetime marker. A non-self marker also requires at least one service interface.

## Framework services

With `RegisterFrameworkServices = true`, the package registers:

- `TimeProvider.System`
- `IGuidGenerator` as a singleton
- `IDomainEventDispatcher` as scoped

Disable these defaults only when the host supplies replacements:

```csharp
builder.Services.AddTcjDependencyInjection(options =>
{
    options.RegisterFrameworkServices = false;
    options.AddAssemblyContaining<Program>();
});
```

## Domain-event handlers

Public implementations of `IDomainEventHandler<TEvent>` are registered as transient services when `RegisterDomainEventHandlers` is enabled.

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

The dispatcher invokes events in collection order and handlers sequentially. An exception stops the current dispatch; the dispatcher does not swallow failures or execute handlers in parallel.
