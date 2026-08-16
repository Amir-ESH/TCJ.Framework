# TCJ.AspNetCore

`TCJ.AspNetCore` integrates TCJ results, current-user resolution, Problem Details, and exception handling into ASP.NET Core applications.

## Install

```bash
dotnet add package TCJ.AspNetCore --version 0.1.0-preview.3
```

- **Target framework:** `net10.0`
- **Main namespaces:** `TCJ.AspNetCore.Extensions`, `TCJ.AspNetCore.Results`, `TCJ.AspNetCore.Options`, `TCJ.AspNetCore.Security`
- **Primary entry points:** `AddTcjAspNetCore`, `UseTcjAspNetCore`, `ToHttpResult`, and `TcjAspNetCoreOptions`

```csharp
builder.Services.AddTcjAspNetCore();
app.UseTcjAspNetCore();
```

## Registration

```csharp
builder.Services.AddTcjAspNetCore(options =>
{
    options.UserIdClaimType = ClaimTypes.NameIdentifier;
    options.IncludeExceptionDetails = builder.Environment.IsDevelopment();
    options.UnexpectedErrorTitle = "An unexpected server error occurred.";
    options.UnexpectedErrorDetail = "The server could not process the request.";
});
```

A custom numeric user resolver can replace claim parsing:

```csharp
builder.Services.AddTcjAspNetCore(options =>
{
    options.UserIdResolver = principal =>
    {
        string? value = principal.FindFirst("user_id")?.Value;
        return long.TryParse(value, out long id) ? id : null;
    };
});
```

## Middleware

```csharp
app.UseTcjAspNetCore();
```

This enables the registered exception handler and produces Problem Details for otherwise-empty error status responses. Problem Details receive a `traceId` extension when one is not already present.

## Result mapping

```csharp
Result<ProductDto> result = await service.GetAsync(id, cancellationToken);
return result.ToHttpResult();
```

A custom success result is supported:

```csharp
return result.ToHttpResult(product =>
    TypedResults.Created($"/api/products/{product.Id}", product));
```

Error mappings:

| Result error type | HTTP status |
| --- | ---: |
| `Validation` | 400 |
| `Failure` | 400 |
| `Unauthorized` | 401 |
| `Forbidden` | 403 |
| `NotFound` | 404 |
| `Conflict` | 409 |
| `Unexpected` | 500 |

When several errors have different types, mapping uses the package's severity precedence: Unexpected, Unauthorized, Forbidden, Conflict, NotFound, then Bad Request.

Validation errors with `FieldName` metadata are grouped into `HttpValidationProblemDetails` and include an `errorCodes` extension.

## Exception details

Keep `IncludeExceptionDetails` disabled outside trusted development environments. The default handler logs the exception and returns a safe Problem Details response.

## Transactional outbox hosting

`TCJ.AspNetCore` provides the optional hosted polling loop for applications that want background outbox processing. Persistence and provider-specific claiming remain owned by the EF Core packages. See [Transactional outbox](../outbox.md).

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

See [Health checks and startup diagnostics](../health-checks.md) for the the health-check feature set APIs and operational contracts supported by this package.

Related packages: [TCJ.Core](tcj-core.md). See [Result and HTTP](../guides/results-and-http.md), [health checks](../health-checks.md), [transactional outbox](../outbox.md), [Native AOT and trimming](../guides/native-aot-and-trimming.md), [validated examples](../examples.md), and the [generated API reference](../api/index.md).
