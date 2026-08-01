using HttpResults = Microsoft.AspNetCore.Http.Results;
using TCJ.AspNetCore.Results;
using TCJ.Core.Results;

namespace TCJ.Empty.Products;

public static class ProductEndpoints
{
    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints.MapGroup(prefix: "/api/products").WithTags("Products");

        group.MapGet(pattern: "/", async (IProductService service, CancellationToken cancellationToken) =>
                          {
                              Result<IReadOnlyList<ProductDto>> result = await service.GetAllAsync(cancellationToken);

                              return result.ToHttpResult();
                          });

        group.MapGet(pattern: "/{id:guid}", async (Guid id, IProductService service, CancellationToken cancellationToken) =>
                                            {
                                                Result<ProductDto> result = await service.GetByIdAsync(id, cancellationToken);

                                                return result.ToHttpResult();
                                            });

        group.MapPost(pattern: "/", async (CreateProductRequest request, IProductService service, CancellationToken cancellationToken) =>
                                    {
                                        Result<ProductDto> result = await service.CreateAsync(request, cancellationToken);

                                        return result.ToHttpResult(product => HttpResults.Created($"/api/products/{product.Id}", product));
                                    });

        group.MapPut(pattern: "/{id:guid}", async (Guid id, UpdateProductRequest request, IProductService service, CancellationToken cancellationToken) =>
                                            {
                                                Result<ProductDto> result = await service.UpdateAsync(id, request, cancellationToken);

                                                return result.ToHttpResult();
                                            });

        group.MapDelete(pattern: "/{id:guid}", async (Guid id, IProductService service, CancellationToken cancellationToken) =>
                                      {
                                          Result result = await service.DeleteAsync(id, cancellationToken);

                                          return result.ToHttpResult();
                                      });

        group.MapPost(pattern: "/{id:guid}/restore", async (Guid id, IProductService service, CancellationToken cancellationToken) =>
                                            {
                                                Result<ProductDto> result = await service.RestoreAsync(id, cancellationToken);

                                                return result.ToHttpResult();
                                            });

        return endpoints;
    }
}
