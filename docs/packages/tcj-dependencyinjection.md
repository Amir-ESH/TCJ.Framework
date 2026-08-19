# TCJ.DependencyInjection

`TCJ.DependencyInjection` provides a reflection-free bootstrap for TCJ framework services plus an opt-in convention scanner for non-trimmed applications. It preserves normal Microsoft dependency-injection semantics and sequential domain-event dispatching.

## Install

```bash
dotnet add package TCJ.DependencyInjection --version 0.1.0-preview.3
```

- **Target framework:** `net10.0`
- **Main namespaces:** `TCJ.DependencyInjection.Extensions`, `TCJ.DependencyInjection.Lifetimes`, `TCJ.DependencyInjection.Registration`
- **Primary entry points:** `AddTcjDependencyInjection`, `AddTcjDomainEvent<TEvent>`, lifetime marker interfaces, and `TcjDependencyInjectionOptions`

## Reflection-free bootstrap

Use the parameterless overload when application code must remain trimming-aware or Native AOT friendly:

```csharp
builder.Services.AddTcjDependencyInjection();
builder.Services.AddTcjDomainEvent<ProductCreated>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddTransient<IDomainEventHandler<ProductCreated>, ProductCreatedHandler>();
```

`AddTcjDependencyInjection()` registers only TCJ framework defaults. It does not enumerate or scan application assemblies. `AddTcjDomainEvent<TEvent>()` declares the closed event type used by the reflection-free dispatcher and does not discover handlers. Application services and handlers are registered explicitly through normal `IServiceCollection` APIs.

The framework defaults are:

- `TimeProvider.System`
- `IGuidGenerator` as a singleton
- `IDomainEventDispatcher` as scoped

Repeated bootstrap and closed event-route registrations are safe because they use duplicate protection.

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

Only effectively public, concrete types in the supplied assemblies are scanned. A top-level dependency must be declared `public`; a nested dependency and every one of its containing types must also be `public`. A nested `public` class inside an `internal`, `private`, or otherwise non-public container is not part of the convention-scan surface. `TCJ0003` reports convention-marked concrete types that violate this accessibility rule and offers an automatic fix only when making the marked type itself `public` is sufficient.

The assembly/options overloads are annotated with both `RequiresUnreferencedCode` and `RequiresDynamicCode` because arbitrary runtime assembly discovery and the scanner-compatible runtime-generic dispatch fallback are not reliable trimming/Native AOT contracts. Those overloads remain available for regular JIT/non-trimmed applications.

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

Effectively public implementations of `IDomainEventHandler<TEvent>` are registered as transient services when convention scanning is used and `RegisterDomainEventHandlers` is enabled.

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

On the reflection-free path, declare every dispatched event type with `AddTcjDomainEvent<TEvent>()` and register handlers explicitly with `IServiceCollection`, as shown above. Handler lifetimes remain exactly the lifetime chosen by the application. The dispatcher invokes events in collection order and handlers sequentially. An exception stops the current dispatch; the dispatcher does not swallow failures or execute handlers in parallel.

See [Native AOT and trimming](../guides/native-aot-and-trimming.md) for the supported and restricted DI paths.

## Health integration

See [Health checks and startup diagnostics](../health-checks.md) for the the health-check feature set APIs and operational contracts supported by this package.

Related packages: [TCJ.Core](tcj-core.md). See [Domain events](../guides/domain-events.md), [Health checks](../health-checks.md), [Native AOT and trimming](../guides/native-aot-and-trimming.md), [validated examples](../examples.md), and the [generated API reference](../api/index.md).
