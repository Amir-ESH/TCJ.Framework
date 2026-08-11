# TCJ.DependencyInjection

`TCJ.DependencyInjection` provides a reflection-free bootstrap for TCJ framework services plus an opt-in convention scanner for non-trimmed applications. It preserves normal Microsoft dependency-injection semantics and sequential domain-event dispatching.

## Install

```bash
dotnet add package TCJ.DependencyInjection --version 0.1.0-preview.2
```

- **Target framework:** `net10.0`
- **Main namespaces:** `TCJ.DependencyInjection.Extensions`, `TCJ.DependencyInjection.Lifetimes`, `TCJ.DependencyInjection.Registration`
- **Primary entry points:** `AddTcjDependencyInjection`, lifetime marker interfaces, and `TcjDependencyInjectionOptions`

## Reflection-free bootstrap

For trimming-aware or Native AOT application code, register only TCJ framework defaults through the parameterless overload and register application services normally with `IServiceCollection`:

```csharp
services.AddTcjDependencyInjection();
services.AddScoped<IOrderService, OrderService>();
services.AddTransient<IDomainEventHandler<OrderPlaced>, OrderPlacedHandler>();
```

The parameterless overload registers `TimeProvider`, `IGuidGenerator`, and `IDomainEventDispatcher`. It does not enumerate or scan application assemblies.

## Convention scanning

Existing non-trimmed applications can continue to opt into lifetime-marker and domain-event-handler discovery by supplying assemblies:

```csharp
services.AddTcjDependencyInjection(typeof(Program).Assembly);
```

The assembly/options overloads use runtime reflection and are annotated with `RequiresUnreferencedCode`. Trimming-aware callers should use the parameterless bootstrap instead of relying on convention scanning.

Related packages: [TCJ.Core](tcj-core.md). See [dependency injection](dependency-injection.md), the [Native AOT and trimming guide](../guides/native-aot-and-trimming.md), [validated examples](../examples.md), and the [generated API reference](../api/index.md).

## Health integration

See [Health checks and startup diagnostics](../health-checks.md) for the Step 43 APIs and operational contracts supported by this package.
