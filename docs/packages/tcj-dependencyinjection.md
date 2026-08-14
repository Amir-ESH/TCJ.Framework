# TCJ.DependencyInjection

`TCJ.DependencyInjection` provides a reflection-free bootstrap for TCJ framework services plus an opt-in convention scanner for non-trimmed applications. It preserves normal Microsoft dependency-injection semantics and sequential domain-event dispatching.

## Install

```bash
dotnet add package TCJ.DependencyInjection --version 0.1.0-preview.2
```

- **Target framework:** `net10.0`
- **Main namespaces:** `TCJ.DependencyInjection.Extensions`, `TCJ.DependencyInjection.Lifetimes`, `TCJ.DependencyInjection.Registration`
- **Primary entry points:** `AddTcjDependencyInjection`, `AddTcjDomainEvent<TEvent>`, lifetime marker interfaces, and `TcjDependencyInjectionOptions`

## Reflection-free bootstrap

For trimming-aware or Native AOT application code, use the explicit registration path:

```csharp
services.AddTcjDependencyInjection();
services.AddTcjDomainEvent<OrderPlaced>();
services.AddScoped<IOrderService, OrderService>();
services.AddTransient<IDomainEventHandler<OrderPlaced>, OrderPlacedHandler>();
```

The parameterless bootstrap registers `TimeProvider`, `IGuidGenerator`, and `IDomainEventDispatcher`. It does not enumerate or scan application assemblies. `AddTcjDomainEvent<TEvent>()` declares the closed event type that the dispatcher may handle; it does not discover or register handlers. Register handlers through normal Microsoft DI methods so transient, scoped, and singleton lifetimes stay under application control. Repeating the same event-route registration is idempotent.

`TCJ.DependencyInjection` declares `IsAotCompatible=true`, and the package-only AOT fixture exercises this explicit bootstrap, closed event route, manual handler registration, and dispatch with SDK trim/AOT analysis enabled.

## Convention scanning

Existing non-trimmed applications can continue to opt into lifetime-marker and domain-event-handler discovery by supplying assemblies:

```csharp
services.AddTcjDependencyInjection(typeof(Program).Assembly);
```

The assembly/options overloads use runtime reflection and a runtime-generic dispatch fallback. They are annotated with both `RequiresUnreferencedCode` and `RequiresDynamicCode`. Trimming/Native AOT callers should use the parameterless bootstrap, declare dispatched types with `AddTcjDomainEvent<TEvent>()`, and register handlers explicitly instead of relying on convention scanning.

Related packages: [TCJ.Core](tcj-core.md). See [dependency injection](dependency-injection.md), the [Native AOT and trimming guide](../guides/native-aot-and-trimming.md), [validated examples](../examples.md), and the [generated API reference](../api/index.md).

## Health integration

See [Health checks and startup diagnostics](../health-checks.md) for the Step 43 APIs and operational contracts supported by this package.
