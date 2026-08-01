using TCJ.EntityFrameworkCore.Specifications;

namespace TCJ.Empty.Products;

public sealed class ProductsOrderedByNameSpecification : Specification<Product>
{
    public ProductsOrderedByNameSpecification()
    {
        ApplyOrderBy(product => product.Name);
        ApplyThenBy(product => product.Id);
    }
}

public sealed class ProductByIdForUpdateSpecification : Specification<Product>
{
    public ProductByIdForUpdateSpecification(Guid id) : base(criteria: product => product.Id == id)
    {
        AsTracking();
    }
}

public sealed class ProductByIdIncludingDeletedSpecification : Specification<Product>
{
    public ProductByIdIncludingDeletedSpecification(Guid id) : base(criteria: product => product.Id == id)
    {
        IgnoreGlobalQueryFilters();
        AsTracking();
    }
}

public sealed class ProductWithNameIncludingDeletedSpecification : Specification<Product>
{
    public ProductWithNameIncludingDeletedSpecification(string name) : base(criteria: product => product.Name == name)
    {
        IgnoreGlobalQueryFilters();
    }

    public ProductWithNameIncludingDeletedSpecification(string name, Guid excludedId) : base(criteria: product => product.Id != excludedId && product.Name == name)
    {
        IgnoreGlobalQueryFilters();
    }
}
