using TCJ.Core.Identifiers;
using TCJ.Core.Results;
using TCJ.DependencyInjection.Lifetimes;
using TCJ.EntityFrameworkCore.Repositories;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TCJ.Empty.Products;

public interface IProductService
{
    Task<Result<IReadOnlyList<ProductDto>>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Result<ProductDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<ProductDto>> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken = default);

    Task<Result<ProductDto>> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<ProductDto>> RestoreAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class ProductService(IRepository<Product, Guid> repository,
                                   ISoftDeleteRepository<Product, Guid> softDeleteRepository,
                                   IUnitOfWork unitOfWork,
                                   IGuidGenerator guidGenerator)
    : IProductService, IScopedDependency
{
    public async Task<Result<IReadOnlyList<ProductDto>>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Product> products = await repository.ListAsync(new ProductsOrderedByNameSpecification(), cancellationToken);

        IReadOnlyList<ProductDto> response = products.Select(ToDto).ToArray();

        return Result.Success(response);
    }

    public async Task<Result<ProductDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Product? product = await repository.GetByIdAsync(id, cancellationToken);

        return product is null
                   ? Result.Failure<ProductDto>(error: CommonErrors.NotFound(nameof(Product), id))
                   : Result.Success(ToDto(product));
    }

    public async Task<Result<ProductDto>> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result validation = Validate(request.Name, request.Price);

        if (validation.IsFailure)
        {
            return Result.Failure<ProductDto>(validation.Errors);
        }

        string normalizedName = request.Name.Trim();

        bool duplicateExists = await repository.AnyAsync(new ProductWithNameIncludingDeletedSpecification(normalizedName), cancellationToken);

        if (duplicateExists)
        {
            return Result.Failure<ProductDto>(error: CommonErrors.Conflict(message: $"A product named '{normalizedName}' already exists."));
        }

        var product = new Product(id: guidGenerator.CreateVersion7(), normalizedName, request.Price);

        await repository.AddAsync(product, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(ToDto(product));
    }

    public async Task<Result<ProductDto>> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result validation = Validate(request.Name, request.Price);

        if (validation.IsFailure)
        {
            return Result.Failure<ProductDto>(validation.Errors);
        }

        Product? product = await repository.FirstOrDefaultAsync(new ProductByIdForUpdateSpecification(id), cancellationToken);

        if (product is null)
        {
            return Result.Failure<ProductDto>(error: CommonErrors.NotFound(nameof(Product), id));
        }

        string normalizedName = request.Name.Trim();

        bool duplicateExists = await repository.AnyAsync(new ProductWithNameIncludingDeletedSpecification(normalizedName, id), cancellationToken);

        if (duplicateExists)
        {
            return Result.Failure<ProductDto>(error: CommonErrors.Conflict(message: $"A product named '{normalizedName}' already exists."));
        }

        product.UpdateDetails(normalizedName, request.Price);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(ToDto(product));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Product? product = await repository.FirstOrDefaultAsync(new ProductByIdForUpdateSpecification(id), cancellationToken);

        if (product is null)
        {
            return Result.Failure(CommonErrors.NotFound(nameof(Product), id));
        }

        softDeleteRepository.SoftDelete(product);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    public async Task<Result<ProductDto>> RestoreAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Product? product = await repository.FirstOrDefaultAsync(new ProductByIdIncludingDeletedSpecification(id), cancellationToken);

        if (product is null)
        {
            return Result.Failure<ProductDto>(error: CommonErrors.NotFound(nameof(Product), id));
        }

        if (!product.IsDeleted)
        {
            return Result.Failure<ProductDto>(error: CommonErrors.Conflict(message: "The product is not deleted."));
        }

        softDeleteRepository.Restore(product);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(ToDto(product));
    }

    private static Result Validate(string name, decimal price)
    {
        var errors = new List<ResultError>();

        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add(CommonErrors.ValidationForField(nameof(CreateProductRequest.Name), message: "Product name is required."));
        }
        else if (name.Trim().Length > 200)
        {
            errors.Add(CommonErrors.ValidationForField(nameof(CreateProductRequest.Name), message: "Product name cannot exceed 200 characters."));
        }

        if (price < 0)
        {
            errors.Add(CommonErrors.ValidationForField(nameof(CreateProductRequest.Price), message: "Product price cannot be negative."));
        }

        return errors.Count == 0 ? Result.Success() : Result.Failure(errors);
    }

    private static ProductDto ToDto(Product product) =>
        new(product.Id,
            product.Name,
            product.Price,
            product.CreatedOn,
            product.ModifiedOn);
}
