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
public sealed class AuditingAndDomainEventsIntegrationTests(SqlServerContainerFixture fixture)
    : SqlServerIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Auditing_stores_creation_and_modification_metadata_in_utc()
    {
        Guid id = Guid.NewGuid();
        DateTimeOffset createdOn;

        using (IServiceScope scope = Database.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var entity = NewEntity(id, "audit-create");

            await repository.AddAsync(entity);
            await unitOfWork.SaveChangesAsync();

            Assert.NotNull(entity.CreatedOn);
            Assert.Equal(TimeSpan.Zero, entity.CreatedOn!.Value.Offset);
            Assert.Equal(7001, entity.CreatedBy);
            Assert.Null(entity.ModifiedOn);
            createdOn = entity.CreatedOn.Value;
        }

        using (IServiceScope scope = Database.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            SqlServerTestEntity entity = await repository.TrackedQuery().SingleAsync(value => value.Id == id);

            entity.Name = "audit-update";
            await unitOfWork.SaveChangesAsync();

            Assert.Equal(createdOn, entity.CreatedOn);
            Assert.Equal(7001, entity.CreatedBy);
            Assert.NotNull(entity.ModifiedOn);
            Assert.Equal(TimeSpan.Zero, entity.ModifiedOn!.Value.Offset);
            Assert.Equal(7001, entity.ModifiedBy);
        }
    }

    [Fact]
    public async Task Soft_delete_metadata_survives_a_sql_server_round_trip()
    {
        Guid id = Guid.NewGuid();

        using (IServiceScope scope = Database.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
            var softDelete = scope.ServiceProvider.GetRequiredService<ISoftDeleteRepository<SqlServerTestEntity, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var entity = NewEntity(id, "audit-soft-delete");

            await repository.AddAsync(entity);
            await unitOfWork.SaveChangesAsync();
            softDelete.SoftDelete(entity);
            await unitOfWork.SaveChangesAsync();
        }

        using IServiceScope verificationScope = Database.CreateScope();
        var context = verificationScope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();
        SqlServerTestEntity stored = await context.TestEntities.IgnoreQueryFilters().SingleAsync(value => value.Id == id);

        Assert.True(stored.IsDeleted);
        Assert.NotNull(stored.DeletedOn);
        Assert.Equal(TimeSpan.Zero, stored.DeletedOn!.Value.Offset);
        Assert.Equal(7001, stored.DeletedBy);
    }

    [Fact]
    public async Task Persistence_keeps_domain_events_pending_until_the_application_dispatches_and_clears_them()
    {
        using IServiceScope scope = Database.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var entity = NewEntity(Guid.NewGuid(), "domain-event");
        entity.RaisePersistenceMarker();

        Assert.Single(entity.DomainEvents);
        await repository.AddAsync(entity);
        await unitOfWork.SaveChangesAsync();

        // TCJ currently documents explicit dispatch: EF persistence must not silently clear pending events.
        Assert.Single(entity.DomainEvents);
        entity.ClearDomainEvents();
        Assert.Empty(entity.DomainEvents);
    }


    [Fact]
    [Trait("Category", "Transaction")]
    public async Task Rolled_back_persistence_keeps_domain_events_pending_and_leaves_no_row()
    {
        Guid id = Guid.NewGuid();
        var entity = NewEntity(id, "domain-event-rollback");
        entity.RaisePersistenceMarker();

        using (IServiceScope scope = Database.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using IUnitOfWorkTransaction transaction = await unitOfWork.BeginTransactionAsync();

            await repository.AddAsync(entity);
            await unitOfWork.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        Assert.Single(entity.DomainEvents);

        using IServiceScope verificationScope = Database.CreateScope();
        var verificationRepository = verificationScope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
        Assert.Null(await verificationRepository.GetByIdAsync(id));
    }

    private static SqlServerTestEntity NewEntity(Guid id, string name) =>
        new(id, name, 9.8765m, DateTimeOffset.UtcNow);
}
