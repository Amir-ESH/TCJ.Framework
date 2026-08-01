using TCJ.Core.Entities;

namespace TCJ.Empty.Products;

public sealed class Product : FullAuditedEntity<Guid>
{
    private Product() { }

    public Product(Guid id, string name, decimal price)
    {
        Id = id;
        UpdateDetails(name, price);
    }

    public string Name { get; private set; } = string.Empty;

    public decimal Price { get; private set; }

    public void UpdateDetails(string name, decimal price)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(price);

        Name = name.Trim();
        Price = price;
    }
}
