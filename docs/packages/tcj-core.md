# TCJ.Core

`TCJ.Core` contains framework-neutral domain primitives: results, structured errors, entities, domain events, guards, identifiers, current-user abstractions, and common extensions.

## Install

```bash
dotnet add package TCJ.Core --version 0.1.0-preview.2
```

- **Target framework:** `net10.0`
- **Native AOT/trimming:** library-level **Full** compatibility (`IsAotCompatible=true`), verified through the package-only analyzer fixture; end-to-end Native AOT support-tier promotion remains gated by packed publish/run evidence
- **Main namespaces:** `TCJ.Core.Results`, `TCJ.Core.Entities`, `TCJ.Core.DomainEvents`, `TCJ.Core.Guards`, `TCJ.Core.Identifiers`
- **Primary entry points:** `Result`, `Result<T>`, `ResultError`, `Entity<TKey>`, `IDomainEventDispatcher`, `Check`

```csharp
Result<int> result = int.TryParse(input, out int value)
    ? Result.Success(value)
    : Result.Failure<int>(CommonErrors.Validation("A number is required."));
```

Related guides: [Result and HTTP](../guides/results-and-http.md), [Domain events](../guides/domain-events.md), [Native AOT and trimming compatibility](../guides/native-aot-and-trimming.md), and the [generated API reference](../api/index.md).

## Health integration

See [Health checks and startup diagnostics](../health-checks.md) for the Step 43 APIs and operational contracts supported by this package.
