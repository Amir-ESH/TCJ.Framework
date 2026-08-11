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


## Native AOT and trimming

`TCJ.AspNetCore` declares `IsAotCompatible=true` for the supported Minimal API path. TCJ-owned health-response
JSON uses source-generated `System.Text.Json` metadata, and `AddTcjAspNetCore()` adds TCJ Problem Details metadata
to the ASP.NET Core JSON resolver chain without replacing application-provided serializer contexts.

For Native AOT applications, use `WebApplication.CreateSlimBuilder()` (or another upstream-supported equivalent),
register source-generated JSON metadata for application request/response DTOs, and stay inside ASP.NET Core feature
families that support Native AOT. TCJ does **not** claim to make MVC controllers Native-AOT-compatible.
Custom object types placed in `ResultError.Metadata` also remain application serialization contracts and must be
covered by the application's `JsonSerializerContext` when they are emitted in HTTP Problem Details.

See [Native AOT and trimming compatibility](../guides/native-aot-and-trimming.md) for the verified path and limitations.

## Health integration

See [Health checks and startup diagnostics](../health-checks.md) for the Step 43 APIs and operational contracts supported by this package.
