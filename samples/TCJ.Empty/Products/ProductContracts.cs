namespace TCJ.Empty.Products;

public sealed record CreateProductRequest(string Name, decimal Price);

public sealed record UpdateProductRequest(string Name, decimal Price);

public sealed record ProductDto(Guid Id,
                                string Name,
                                decimal Price,
                                DateTimeOffset? CreatedOn,
                                DateTimeOffset? ModifiedOn);
