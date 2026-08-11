# TCJ.DependencyInjection

This package provides a reflection-free TCJ framework bootstrap, opt-in convention scanning, lifetime markers, framework defaults, and sequential domain-event dispatching.

## Reflection-free bootstrap

Use the parameterless overload when application code must remain trimming-aware or Native AOT friendly:

```csharp
builder.Services.AddTcjDependencyInjection();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddTransient<IDomainEventHandler<ProductCreated>, ProductCreatedHandler>();
```

`AddTcjDependencyInjection()` registers only TCJ framework defaults. It does not enumerate or scan application assemblies. Application services and domain-event handlers are registered explicitly through normal `IServiceCollection` APIs.

The framework defaults are:

- `TimeProvider.System`
- `IGuidGenerator` as a singleton
- `IDomainEventDispatcher` as scoped

Repeated calls are safe because these framework registrations use duplicate protection.

## Convention scanning

Existing non-trimmed applications can continue to scan explicitly supplied assemblies:

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

Only public, concrete types in the supplied assemblies are scanned. The assembly/options overloads are annotated with `RequiresUnreferencedCode` because arbitrary runtime assembly discovery is not a reliable trimming contract. Those overloads remain available for regular JIT/non-trimmed applications.

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

## Framework services with scanning options

`RegisterFrameworkServices` remains available on the convention-scanning options for existing consumers. Disable these defaults only when the host supplies replacements:

```csharp
builder.Services.AddTcjDependencyInjection(options =>
{
    options.RegisterFrameworkServices = false;
    options.AddAssemblyContaining<Program>();
});
```

## Domain-event handlers

Public implementations of `IDomainEventHandler<TEvent>` are registered as transient services when convention scanning is used and `RegisterDomainEventHandlers` is enabled.

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

On the reflection-free path, register handlers explicitly with `IServiceCollection`, as shown above. The dispatcher invokes events in collection order and handlers sequentially. An exception stops the current dispatch; the dispatcher does not swallow failures or execute handlers in parallel.

See [Native AOT and trimming](../guides/native-aot-and-trimming.md) for the supported and restricted DI paths.
