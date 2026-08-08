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
public sealed class RepositoryIntegrationTests(SqlServerContainerFixture fixture)
    : SqlServerIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Repository_inserts_and_retrieves_entity_by_identifier()
    {
        Guid id = Guid.NewGuid();

        using (IServiceScope scope = Database.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await repository.AddAsync(NewEntity(id, "repo-insert"));
            Assert.Equal(1, await unitOfWork.SaveChangesAsync());
        }

        using (IServiceScope scope = Database.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
            SqlServerTestEntity? loaded = await repository.GetByIdAsync(id);
            Assert.NotNull(loaded);
            Assert.Equal("repo-insert", loaded.Name);
        }
    }

    [Fact]
    public async Task Repository_query_uses_specification_against_sql_server()
    {
        using IServiceScope scope = Database.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await repository.AddRangeAsync(
        [
            NewEntity(Guid.NewGuid(), "alpha-2"),
            NewEntity(Guid.NewGuid(), "beta-1"),
            NewEntity(Guid.NewGuid(), "alpha-1")
        ]);
        await unitOfWork.SaveChangesAsync();

        IReadOnlyList<SqlServerTestEntity> results = await repository.ListAsync(new NamePrefixSpecification("alpha-"));

        Assert.Equal(new[] { "alpha-1", "alpha-2" }, results.Select(entity => entity.Name).ToArray());
    }

    [Fact]
    public async Task Repository_updates_entity_and_preserves_persisted_state()
    {
        Guid id = Guid.NewGuid();

        using IServiceScope scope = Database.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await repository.AddAsync(NewEntity(id, "before-update"));
        await unitOfWork.SaveChangesAsync();

        SqlServerTestEntity entity = await repository.TrackedQuery().SingleAsync(value => value.Id == id);
        entity.Name = "after-update";
        repository.Update(entity);
        await unitOfWork.SaveChangesAsync();

        using IServiceScope verificationScope = Database.CreateScope();
        var verificationRepository = verificationScope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
        SqlServerTestEntity? reloaded = await verificationRepository.GetByIdAsync(id);
        Assert.NotNull(reloaded);
        Assert.Equal("after-update", reloaded.Name);
    }

    [Fact]
    public async Task Soft_delete_hides_entity_but_keeps_row_in_sql_server()
    {
        Guid id = Guid.NewGuid();

        using IServiceScope scope = Database.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
        var softDelete = scope.ServiceProvider.GetRequiredService<ISoftDeleteRepository<SqlServerTestEntity, Guid>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var context = scope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();

        var entity = NewEntity(id, "soft-delete");
        await repository.AddAsync(entity);
        await unitOfWork.SaveChangesAsync();

        softDelete.SoftDelete(entity);
        await unitOfWork.SaveChangesAsync();

        Assert.Null(await repository.GetByIdAsync(id));
        SqlServerTestEntity stored = await context.TestEntities.IgnoreQueryFilters().SingleAsync(value => value.Id == id);
        Assert.True(stored.IsDeleted);
        Assert.NotNull(stored.DeletedOn);
    }

    [Fact]
    public async Task Repository_honors_cancellation_tokens()
    {
        using IServiceScope scope = Database.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.ListAsync(cancellation.Token));
    }

    [Fact]
    public async Task Missing_entity_returns_null()
    {
        using IServiceScope scope = Database.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();

        Assert.Null(await repository.GetByIdAsync(Guid.NewGuid()));
    }

    private static SqlServerTestEntity NewEntity(Guid id, string name) =>
        new(id, name, 12.3400m, DateTimeOffset.UtcNow);
}
