using Microsoft.EntityFrameworkCore;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.Specifications;

namespace TCJ.EntityFrameworkCore.Tests;

internal sealed class TestDbContext(DbContextOptions<TestDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
    public DbSet<TestProduct> Products => Set<TestProduct>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TestProduct>(builder =>
        {
            builder.HasKey(product => product.Id);
            builder.Property(product => product.Name).IsRequired();
        });

        modelBuilder.ApplySoftDeleteQueryFilters();
    }
}

internal sealed class TestProduct : FullAuditedEntity<Guid>
{
    private TestProduct()
    {
    }

    public TestProduct(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Name { get; set; } = string.Empty;
}

internal sealed class PagedProductsSpecification : Specification<TestProduct>
{
    public PagedProductsSpecification(int skip, int take)
        : base(criteria: product => product.Name.StartsWith('A'))
    {
        ApplyOrderBy(product => product.Name);
        ApplyThenBy(product => product.Id);
        ApplyPaging(skip, take);
    }
}

internal sealed class InvalidPagedProductsSpecification : Specification<TestProduct>
{
    public InvalidPagedProductsSpecification()
    {
        ApplyPaging(skip: 0, take: 10);
    }
}

internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
