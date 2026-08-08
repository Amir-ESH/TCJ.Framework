using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.EntityFrameworkCore.Repositories;
using TCJ.EntityFrameworkCore.UnitOfWork;
using TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Tests;

[Collection(SqlServerIntegrationCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "SqlServer")]
[Trait("Category", "Database")]
[Trait("Category", "Transaction")]
public sealed class ConcurrencyIntegrationTests(SqlServerContainerFixture fixture)
    : SqlServerIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Rowversion_detects_conflicting_updates()
    {
        Guid id = Guid.NewGuid();

        using (IServiceScope seedScope = Database.CreateScope())
        {
            var repository = seedScope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
            var unitOfWork = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await repository.AddAsync(new SqlServerTestEntity(id, "concurrency", 1.0000m, DateTimeOffset.UtcNow));
            await unitOfWork.SaveChangesAsync();
        }

        using IServiceScope firstScope = Database.CreateScope();
        using IServiceScope secondScope = Database.CreateScope();
        var firstContext = firstScope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();
        var secondContext = secondScope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();

        SqlServerTestEntity first = await firstContext.TestEntities.SingleAsync(value => value.Id == id);
        SqlServerTestEntity second = await secondContext.TestEntities.SingleAsync(value => value.Id == id);

        first.Amount = 2.0000m;
        await firstContext.SaveChangesAsync();

        second.Amount = 3.0000m;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondContext.SaveChangesAsync());
    }
}
