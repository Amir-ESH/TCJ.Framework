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
public sealed class SqlServerStorageIntegrationTests(SqlServerContainerFixture fixture)
    : SqlServerIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Guid_decimal_datetimeoffset_and_nullable_values_round_trip()
    {
        Guid id = Guid.Parse("018f7e4e-8123-7a65-bc12-123456789abc");
        decimal amount = 123456789.4321m;
        var occurredOn = new DateTimeOffset(2026, 8, 8, 9, 10, 11, TimeSpan.FromHours(3.5));

        using (IServiceScope scope = Database.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await repository.AddAsync(new SqlServerTestEntity(id, "storage-roundtrip", amount, occurredOn));
            await unitOfWork.SaveChangesAsync();
        }

        using IServiceScope verificationScope = Database.CreateScope();
        var verificationRepository = verificationScope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
        SqlServerTestEntity? stored = await verificationRepository.GetByIdAsync(id);

        Assert.NotNull(stored);
        Assert.Equal(id, stored.Id);
        Assert.Equal(amount, stored.Amount);
        Assert.Equal(occurredOn, stored.OccurredOn);
        Assert.Null(stored.OptionalText);
        Assert.NotEmpty(stored.RowVersion);
    }

    [Fact]
    public async Task Unique_index_is_enforced_by_sql_server()
    {
        using IServiceScope scope = Database.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await repository.AddAsync(NewEntity(Guid.NewGuid(), "unique-name"));
        await unitOfWork.SaveChangesAsync();
        await repository.AddAsync(NewEntity(Guid.NewGuid(), "unique-name"));

        await Assert.ThrowsAsync<DbUpdateException>(() => unitOfWork.SaveChangesAsync());
    }

    [Fact]
    public async Task Foreign_key_restrict_behavior_is_enforced_by_sql_server()
    {
        int parentId;

        using (IServiceScope scope = Database.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();
            var parent = new SqlServerParent("parent");
            context.Parents.Add(parent);
            context.Children.Add(new SqlServerChild("child", parent));
            await context.SaveChangesAsync();
            parentId = parent.Id;
        }

        using IServiceScope deletionScope = Database.CreateScope();
        var deletionContext = deletionScope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();
        SqlServerParent parentToDelete = await deletionContext.Parents.SingleAsync(value => value.Id == parentId);
        deletionContext.Parents.Remove(parentToDelete);

        await Assert.ThrowsAsync<DbUpdateException>(() => deletionContext.SaveChangesAsync());
    }

    [Fact]
    public async Task Sql_server_generates_identity_values()
    {
        using IServiceScope scope = Database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();
        var parent = new SqlServerParent("identity");

        context.Parents.Add(parent);
        await context.SaveChangesAsync();

        Assert.True(parent.Id > 0);
    }

    [Fact]
    public async Task Configured_string_length_is_enforced_by_sql_server()
    {
        using IServiceScope scope = Database.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        string tooLong = new('x', 81);

        await repository.AddAsync(NewEntity(Guid.NewGuid(), tooLong));

        await Assert.ThrowsAsync<DbUpdateException>(() => unitOfWork.SaveChangesAsync());
    }

    private static SqlServerTestEntity NewEntity(Guid id, string name) =>
        new(id, name, 7.6543m, DateTimeOffset.UtcNow);
}
