# Result values and HTTP responses

## Create results

```csharp
Result completed = Result.Success();
Result<ProductDto> found = Result.Success(product);

Result invalid = Result.Failure(
    CommonErrors.ValidationForField(
        nameof(CreateProductRequest.Name),
        "Product name is required."));
```

## Compose operations

```csharp
Result<Product> result = FindProduct(productId)
    .Ensure(
        product => !product.IsDeleted,
        CommonErrors.Conflict("The product is deleted."))
    .Bind(product => RenameProduct(product, requestedName));
```

`Map` transforms a success value. `Bind` chains another Result-producing operation. Failures pass through without executing the success delegate.

## Aggregate validation errors

```csharp
Result validation = Result.Combine(
    ValidateName(request.Name),
    ValidatePrice(request.Price));
```

`Combine` returns all errors from failed inputs rather than only the first one.

## Attach metadata

```csharp
ResultError error = CommonErrors.Validation("Value is invalid.")
    .WithMetadata("FieldName", "Name")
    .WithMetadata("AttemptedValue", request.Name);
```

Metadata is copied into an immutable read-only dictionary.

## Map to HTTP

```csharp
app.MapPost("/api/products", async (
    CreateProductRequest request,
    IProductService service,
    CancellationToken cancellationToken) =>
{
    Result<ProductDto> result =
        await service.CreateAsync(request, cancellationToken);

    return result.ToHttpResult(product =>
        Results.Created($"/api/products/{product.Id}", product));
});
```

Expected business failures should use Result errors. Unexpected exceptions should flow to the centralized exception handler instead of being converted to generic Result failures at every boundary.
