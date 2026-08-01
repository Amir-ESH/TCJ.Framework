using TCJ.Core.Identifiers;
using TCJ.EntityFrameworkCore.Repositories;
using TCJ.EntityFrameworkCore.Seeding;

namespace TCJ.Empty.Products;

public sealed class ProductSeedContributor(IRepository<Product, Guid> repository, IGuidGenerator guidGenerator) : IDataSeedContributor
{
    public int Order => 100;

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await repository.AnyAsync(cancellationToken))
        {
            return;
        }

        Product[] products =
        [
            new(id: guidGenerator.CreateVersion7(), name: "Mechanical Keyboard", price: 129.90m),
            new(id: guidGenerator.CreateVersion7(), name: "USB-C Dock", price: 89.50m),
            new(id: guidGenerator.CreateVersion7(), name: "Wireless Mouse", price: 54.00m),
        ];

        await repository.AddRangeAsync(products, cancellationToken);
    }
}
