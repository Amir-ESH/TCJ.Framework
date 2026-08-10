# TCJ.AspNetCore

`TCJ.AspNetCore` integrates TCJ results, current-user resolution, Problem Details, and exception handling into ASP.NET Core applications.

## Install

```bash
dotnet add package TCJ.AspNetCore --version 0.1.0-preview.2
```

- **Target framework:** `net10.0`
- **Main namespaces:** `TCJ.AspNetCore.Extensions`, `TCJ.AspNetCore.Results`, `TCJ.AspNetCore.Options`, `TCJ.AspNetCore.Security`
- **Primary entry points:** `AddTcjAspNetCore`, `UseTcjAspNetCore`, `ToHttpResult`, and `TcjAspNetCoreOptions`

```csharp
builder.Services.AddTcjAspNetCore();
app.UseTcjAspNetCore();
```

Related packages: [TCJ.Core](tcj-core.md). See [Result and HTTP](../guides/results-and-http.md), [validated examples](../examples.md), and the [generated API reference](../api/index.md).

## Health integration

See [Health checks and startup diagnostics](../health-checks.md) for the Step 43 APIs and operational contracts supported by this package.
