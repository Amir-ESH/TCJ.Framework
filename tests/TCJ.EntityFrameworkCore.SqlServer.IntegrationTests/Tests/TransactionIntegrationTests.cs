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
public sealed class TransactionIntegrationTests(SqlServerContainerFixture fixture)
    : SqlServerIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Unit_of_work_commit_persists_changes()
    {
        Guid id = Guid.NewGuid();

        using (IServiceScope scope = Database.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using IUnitOfWorkTransaction transaction = await unitOfWork.BeginTransactionAsync();

            await repository.AddAsync(NewEntity(id, "commit"));
            await unitOfWork.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        using IServiceScope verificationScope = Database.CreateScope();
        var verificationRepository = verificationScope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
        Assert.NotNull(await verificationRepository.GetByIdAsync(id));
    }

    [Fact]
    public async Task Transaction_rollback_removes_uncommitted_changes()
    {
        Guid id = Guid.NewGuid();

        using (IServiceScope scope = Database.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using IUnitOfWorkTransaction transaction = await unitOfWork.BeginTransactionAsync();

            await repository.AddAsync(NewEntity(id, "rollback"));
            await unitOfWork.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        using IServiceScope verificationScope = Database.CreateScope();
        var verificationRepository = verificationScope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
        Assert.Null(await verificationRepository.GetByIdAsync(id));
    }

    [Fact]
    public async Task Multiple_repository_operations_participate_in_one_transaction()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        using (IServiceScope scope = Database.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using IUnitOfWorkTransaction transaction = await unitOfWork.BeginTransactionAsync();

            await repository.AddAsync(NewEntity(first, "tx-first"));
            await repository.AddAsync(NewEntity(second, "tx-second"));
            await unitOfWork.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        using IServiceScope verificationScope = Database.CreateScope();
        var verificationRepository = verificationScope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
        Assert.Equal(2, await verificationRepository.CountAsync());
    }


    [Fact]
    public async Task Failed_transaction_does_not_partially_persist_changes_after_rollback()
    {
        Guid firstId = Guid.NewGuid();

        using (IServiceScope scope = Database.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using IUnitOfWorkTransaction transaction = await unitOfWork.BeginTransactionAsync();

            await repository.AddAsync(NewEntity(firstId, "transaction-failure"));
            await unitOfWork.SaveChangesAsync();
            await repository.AddAsync(NewEntity(Guid.NewGuid(), "transaction-failure"));
            await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => unitOfWork.SaveChangesAsync());
            await transaction.RollbackAsync();
        }

        using IServiceScope verificationScope = Database.CreateScope();
        var verificationRepository = verificationScope.ServiceProvider.GetRequiredService<IRepository<SqlServerTestEntity, Guid>>();
        Assert.Null(await verificationRepository.GetByIdAsync(firstId));
        Assert.Equal(0, await verificationRepository.CountAsync());
    }

    [Fact]
    public async Task Starting_a_nested_transaction_is_rejected_and_disposal_is_safe()
    {
        using IServiceScope scope = Database.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IUnitOfWorkTransaction transaction = await unitOfWork.BeginTransactionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.BeginTransactionAsync());
        await transaction.RollbackAsync();
        await transaction.DisposeAsync();
        await transaction.DisposeAsync();
    }

    private static SqlServerTestEntity NewEntity(Guid id, string name) =>
        new(id, name, 1.0000m, DateTimeOffset.UtcNow);
}
