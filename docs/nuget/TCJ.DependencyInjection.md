# TCJ.DependencyInjection

`TCJ.DependencyInjection` adds TCJ service registration and sequential domain-event dispatching on top of Microsoft.Extensions.DependencyInjection. It supports both an explicit reflection-free bootstrap for Native AOT-sensitive applications and opt-in convention scanning for normal JIT applications.

## Install

```bash
dotnet add package TCJ.DependencyInjection --prerelease
```

TCJ Framework is currently pre-1.0. Pin the exact preview version used by your application when reproducibility matters.

## Highlights

- Explicit `AddTcjDependencyInjection()` bootstrap without assembly scanning.
- Closed domain-event registration with `AddTcjDomainEvent<TEvent>()`.
- Sequential domain-event dispatching through standard Microsoft DI lifetimes.
- Optional convention scanning for non-trimmed applications.
- Health-check registration support used by the TCJ health contract.
- Full Native AOT/trimming support on the explicit reflection-free path.

## Example

```csharp
using TCJ.DependencyInjection.Extensions;

services.AddTcjDependencyInjection();
services.AddTcjDomainEvent<OrderPlaced>();
services.AddScoped<IOrderService, OrderService>();
services.AddTransient<IDomainEventHandler<OrderPlaced>, OrderPlacedHandler>();
```

For convention-based discovery in a normal JIT application:

```csharp
services.AddTcjDependencyInjection(typeof(Program).Assembly);
```

## Dependencies

This package builds on `TCJ.Core` and Microsoft dependency-injection abstractions.

## Documentation

- [TCJ.DependencyInjection package documentation](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/packages/tcj-dependencyinjection.md)
- [Domain events guide](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/guides/domain-events.md)
- [Native AOT and trimming guide](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/guides/native-aot-and-trimming.md)
- [Validated examples](https://github.com/Amir-ESH/TCJ.Framework/blob/v0.1.0-preview.5/docs/examples.md)
- [Repository](https://github.com/Amir-ESH/TCJ.Framework)
- [Issues](https://github.com/Amir-ESH/TCJ.Framework/issues)

## License

TCJ Framework is licensed under GNU LGPL v3.0 only (`LGPL-3.0-only`). See the repository license for details.
