# TCJ.Core

`TCJ.Core` provides the framework-neutral foundation for TCJ Framework: results, structured errors, entities, domain events, guards, UUID v7 identifiers, current-user contracts, resilience primitives, health contracts, observability contracts, and transactional-outbox abstractions.

## Install

```bash
dotnet add package TCJ.Core --prerelease
```

TCJ Framework is currently pre-1.0. Pin the exact preview version used by your application when reproducibility matters.

## Highlights

- `Result` and `Result<T>` for explicit success/failure flows.
- Entity and domain-event primitives for modular domain models.
- Guard helpers and UUID v7 identifier generation.
- Framework-neutral current-user and security contracts.
- Resilience, health, observability, and outbox contracts shared by higher-level TCJ packages.
- `net10.0` target with the repository's Full Native AOT/trimming support tier for this package.

## Example

```csharp
using TCJ.Core.Results;

Result<int> result = int.TryParse(input, out int value)
    ? Result.Success(value)
    : Result.Failure<int>(CommonErrors.Validation("A number is required."));
```

## Documentation

- [TCJ.Core package documentation](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/packages/tcj-core.md)
- [Result and HTTP guide](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/guides/results-and-http.md)
- [Domain events guide](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/guides/domain-events.md)
- [Native AOT and trimming guide](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/guides/native-aot-and-trimming.md)
- [Repository](https://github.com/Amir-ESH/TCJ.Framework)
- [Issues](https://github.com/Amir-ESH/TCJ.Framework/issues)

## License

TCJ Framework is licensed under GNU LGPL v3.0 only (`LGPL-3.0-only`). See the repository license for details.
