# TCJ.DependencyInjection

`TCJ.DependencyInjection` provides explicit, convention-based registration for application services and domain-event handlers while preserving normal Microsoft dependency-injection semantics.

## Install

```bash
dotnet add package TCJ.DependencyInjection --version 0.1.0-preview.2
```

- **Target framework:** `net10.0`
- **Main namespaces:** `TCJ.DependencyInjection.Extensions`, `TCJ.DependencyInjection.Lifetimes`, `TCJ.DependencyInjection.Registration`
- **Primary entry points:** `AddTcjDependencyInjection`, lifetime marker interfaces, and `TcjDependencyInjectionOptions`

```csharp
services.AddTcjDependencyInjection(typeof(Program).Assembly);
```

Related packages: [TCJ.Core](tcj-core.md). See [validated examples](../examples.md) and the [generated API reference](../api/index.md).

## Health integration

See [Health checks and startup diagnostics](../health-checks.md) for the Step 43 APIs and operational contracts supported by this package.
