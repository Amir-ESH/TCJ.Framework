# TCJ.AspNetCore

`TCJ.AspNetCore` integrates TCJ Framework with ASP.NET Core applications. It provides current-user resolution, Result-to-HTTP mapping, Problem Details, centralized exception handling, health endpoints, and the optional hosted transactional-outbox processing path.

## Install

```bash
dotnet add package TCJ.AspNetCore --prerelease
```

TCJ Framework is currently pre-1.0. Pin the exact preview version used by your application when reproducibility matters.

## Highlights

- `AddTcjAspNetCore()` service registration.
- `UseTcjAspNetCore()` application middleware setup.
- Result-to-HTTP conversion and Problem Details integration.
- Centralized exception handling with production-safe responses.
- Current-user/request-scope integration.
- Liveness/readiness endpoint mapping.
- Full Native AOT support for the verified Minimal API path; MVC controllers are not claimed as Native-AOT-compatible by TCJ.

## Example

```csharp
using TCJ.AspNetCore.Extensions;

builder.Services.AddTcjAspNetCore();

var app = builder.Build();
app.UseTcjAspNetCore();
```

## Dependencies

This package builds on `TCJ.Core` and the ASP.NET Core shared framework.

## Documentation

- [TCJ.AspNetCore package documentation](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/packages/tcj-aspnetcore.md)
- [Result and HTTP guide](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/guides/results-and-http.md)
- [ASP.NET Core integration testing](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/aspnetcore-integration-testing.md)
- [Health checks and startup diagnostics](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/health-checks.md)
- [Native AOT and trimming guide](https://github.com/Amir-ESH/TCJ.Framework/blob/main/docs/guides/native-aot-and-trimming.md)
- [Repository](https://github.com/Amir-ESH/TCJ.Framework)
- [Issues](https://github.com/Amir-ESH/TCJ.Framework/issues)

## License

TCJ Framework is licensed under GNU LGPL v3.0 only (`LGPL-3.0-only`). See the repository license for details.
