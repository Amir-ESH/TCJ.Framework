using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Security;
using TCJ.EntityFrameworkCore.Interceptors;
using TCJ.EntityFrameworkCore.Repositories;

namespace TCJ.EntityFrameworkCore.Tests;

public sealed class AuditingAndSoftDeleteTests
{
    [Fact]
    public async Task SaveChanges_applies_creation_and_modification_audits()
    {
        DateTimeOffset createdAt = new(year: 2026, month: 8, day: 1, hour:8, minute: 0, second: 0, offset: TimeSpan.Zero);
        DateTimeOffset modifiedAt = createdAt.AddMinutes(15);
        var timeProvider = new MutableTimeProvider(createdAt);

        await using ServiceProvider serviceProvider = new ServiceCollection().AddSingleton<ICurrentUserProvider>(new StubCurrentUserProvider(42))
                                                                             .BuildServiceProvider();

        var interceptor = new AuditingSaveChangesInterceptor(serviceProvider, timeProvider);
        DbContextOptions<TestDbContext> options = CreateOptions(interceptor);

        await using var context = new TestDbContext(options);
        var product = new TestProduct(id: Guid.NewGuid(), name: "Alpha");

        context.Add(product);
        await context.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(createdAt, product.CreatedOn);
        Assert.Equal(42L, product.CreatedBy);
        Assert.Null(product.ModifiedOn);
        Assert.Null(product.ModifiedBy);

        timeProvider.UtcNow = modifiedAt;
        product.Name = "Alpha updated";
        await context.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(createdAt, product.CreatedOn);
        Assert.Equal(42L, product.CreatedBy);
        Assert.Equal(modifiedAt, product.ModifiedOn);
        Assert.Equal(42L, product.ModifiedBy);
    }

    [Fact]
    public async Task Soft_delete_hides_entity_and_restore_makes_it_visible_again()
    {
        DateTimeOffset deletedAt = new(year: 2026, month: 8, day: 1, hour: 9, minute: 0, second: 0, offset: TimeSpan.Zero);
        var timeProvider = new MutableTimeProvider(deletedAt);

        await using ServiceProvider serviceProvider = new ServiceCollection().AddSingleton<ICurrentUserProvider>(new StubCurrentUserProvider(7))
                                                                             .BuildServiceProvider();

        var interceptor = new AuditingSaveChangesInterceptor(serviceProvider, timeProvider);
        DbContextOptions<TestDbContext> options = CreateOptions(interceptor);

        await using var context = new TestDbContext(options);
        var product = new TestProduct(id: Guid.NewGuid(), name: "Archived");
        context.Add(product);
        await context.SaveChangesAsync(CancellationToken.None);

        var repository = new EfSoftDeleteRepository<TestProduct, Guid>(context);
        repository.SoftDelete(product);
        await context.SaveChangesAsync(CancellationToken.None);

        Assert.True(product.IsDeleted);
        Assert.Equal(deletedAt, product.DeletedOn);
        Assert.Equal(7L, product.DeletedBy);
        Assert.Empty(await context.Products.ToListAsync(CancellationToken.None));
        Assert.Single(await context.Products.IgnoreQueryFilters().ToListAsync(CancellationToken.None));

        repository.Restore(product);
        await context.SaveChangesAsync(CancellationToken.None);

        Assert.False(product.IsDeleted);
        Assert.Null(product.DeletedOn);
        Assert.Null(product.DeletedBy);
        Assert.Single(await context.Products.ToListAsync(CancellationToken.None));
    }

    private static DbContextOptions<TestDbContext> CreateOptions(AuditingSaveChangesInterceptor interceptor) =>
        new DbContextOptionsBuilder<TestDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString(format: "N"))
                                                    .AddInterceptors(interceptor).Options;

    private sealed class StubCurrentUserProvider(long? userId) : ICurrentUserProvider
    {
        public long? UserId { get; } = userId;
    }
}
