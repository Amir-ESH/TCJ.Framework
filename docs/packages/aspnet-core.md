# TCJ.AspNetCore

This package connects TCJ Result values and the current-user abstraction to ASP.NET Core.

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
