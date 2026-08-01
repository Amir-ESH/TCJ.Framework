using Microsoft.EntityFrameworkCore;
using TCJ.EntityFrameworkCore.Searching;
using TCJ.EntityFrameworkCore.Specifications;

namespace TCJ.EntityFrameworkCore.Tests;

public sealed class SpecificationAndSearcherTests
{
    [Fact]
    public async Task Specification_applies_filter_order_and_paging()
    {
        DbContextOptions<TestDbContext> options = CreateOptions();

        await using (var seedContext = new TestDbContext(options))
        {
            seedContext.AddRange(new TestProduct(id: Guid.NewGuid(), name: "Azure"),
                                 new TestProduct(id: Guid.NewGuid(), name: "Beta"),
                                 new TestProduct(id: Guid.NewGuid(), name: "Alpha"));

            await seedContext.SaveChangesAsync(CancellationToken.None);
        }

        await using var context = new TestDbContext(options);
        IQueryable<TestProduct> query = SpecificationEvaluator.GetQuery(context.Products, new PagedProductsSpecification(skip: 0, take: 1));

        TestProduct product = Assert.Single(await query.ToListAsync(CancellationToken.None));

        Assert.Equal("Alpha", product.Name);
        Assert.Empty(collection: context.ChangeTracker.Entries());
    }

    [Fact]
    public void Paged_specification_without_ordering_is_rejected()
    {
        using var context = new TestDbContext(CreateOptions());

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            SpecificationEvaluator.GetQuery(context.Products, new InvalidPagedProductsSpecification()));

        Assert.True(exception.Message.Contains("ordering", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Entity_searcher_uses_model_metadata_and_primary_key_conversion()
    {
        DbContextOptions<TestDbContext> options = CreateOptions();
        var id = Guid.NewGuid();

        await using var context = new TestDbContext(options);
        context.Add(new TestProduct(id, name: "Searchable"));
        await context.SaveChangesAsync(CancellationToken.None);

        var searcher = new EntitySearcher(context);
        var input = EntityRecordInput.ForSingleKey(nameof(TestProduct), nameof(TestProduct.Id), keyValue: id.ToString());

        Assert.True(await searcher.ExistsAsync(input, CancellationToken.None));
        Assert.IsType<TestProduct>(await searcher.FindAsync(input, CancellationToken.None));

        EntityPropertyMetadata metadata = searcher.GetPropertyMetadata(new EntityPropertyInput(nameof(TestProduct), nameof(TestProduct.Id)));

        Assert.True(metadata.IsPrimaryKey);
        Assert.Equal(typeof(Guid).FullName, metadata.ClrTypeName);
    }

    private static DbContextOptions<TestDbContext> CreateOptions() => new DbContextOptionsBuilder<TestDbContext>()
                                                                      .UseInMemoryDatabase(Guid.NewGuid().ToString(format: "N")).Options;
}
