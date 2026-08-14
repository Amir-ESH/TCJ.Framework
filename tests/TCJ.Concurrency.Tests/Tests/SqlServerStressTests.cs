using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Concurrency.Tests.Fixtures;
using TCJ.Concurrency.Tests.Infrastructure;
using TCJ.EntityFrameworkCore.Repositories;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TCJ.Concurrency.Tests.Tests;

[Collection(SqlServerStressCollection.Name)]
[Trait("Category", "Concurrency")]
[Trait("Category", "Stress")]
[Trait("Category", "Transactions")]
[Trait("Category", "SqlServer")]
[Trait("Category", "ScheduledStress")]
public sealed class SqlServerStressTests(SqlServerStressFixture fixture)
{
    [Fact]
    public async Task IndependentSqlServerTransactionsCommitWithoutInterference()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await StressRunner.RunAsync(nameof(IndependentSqlServerTransactionsCommitWithoutInterference), "sqlserver", async context =>
        {
            using IServiceScope scope = database.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<StressEntity, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using IUnitOfWorkTransaction transaction = await unitOfWork.BeginTransactionAsync(context.CancellationToken);
            await repository.AddAsync(new StressEntity(Guid.NewGuid(), $"commit-{context.OperationId}"), context.CancellationToken);
            await unitOfWork.SaveChangesAsync(context.CancellationToken);
            await transaction.CommitAsync(context.CancellationToken);
        }, async () =>
        {
            using IServiceScope scope = database.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<StressEntity, Guid>>();
            int expected = StressSettings.Load("sqlserver").Workers * StressSettings.Load("sqlserver").Iterations;
            Assert.Equal(expected, await repository.CountAsync());
        });
    }

    [Fact]
    public async Task RollbackDoesNotRemoveAnotherTransactionCommit()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var committed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        await StressRunner.RunAsync(nameof(RollbackDoesNotRemoveAnotherTransactionCommit), "sqlserver", async context =>
        {
            using IServiceScope scope = database.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<StressEntity, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using IUnitOfWorkTransaction transaction = await unitOfWork.BeginTransactionAsync(context.CancellationToken);
            string name = $"tx-{context.OperationId}";
            await repository.AddAsync(new StressEntity(Guid.NewGuid(), name), context.CancellationToken);
            await unitOfWork.SaveChangesAsync(context.CancellationToken);
            if ((context.Worker + context.Iteration) % 2 == 0)
            {
                await transaction.CommitAsync(context.CancellationToken);
                committed.TryAdd(name, 0);
            }
            else
            {
                await transaction.RollbackAsync(context.CancellationToken);
            }
        }, async () =>
        {
            using IServiceScope scope = database.CreateScope();
            StressDbContext context = scope.ServiceProvider.GetRequiredService<StressDbContext>();
            string[] names = await context.Entities.Select(entity => entity.Name).ToArrayAsync();
            Assert.Equal(committed.Keys.OrderBy(value => value, StringComparer.Ordinal), names.OrderBy(value => value, StringComparer.Ordinal));
        });
    }

    [Fact]
    public async Task ConcurrentUniqueConstraintAllowsSingleWinner()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var winners = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        await StressRunner.RunAsync(nameof(ConcurrentUniqueConstraintAllowsSingleWinner), "sqlserver", async context =>
        {
            string key = $"unique-{context.Iteration % 5}";
            using IServiceScope scope = database.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<StressEntity, Guid>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await repository.AddAsync(new StressEntity(Guid.NewGuid(), key), context.CancellationToken);
            try
            {
                await unitOfWork.SaveChangesAsync(context.CancellationToken);
                winners.AddOrUpdate(key, 1, static (_, count) => count + 1);
            }
            catch (DbUpdateException)
            {
                // Expected losers prove the unique constraint remains authoritative under concurrency.
            }
        }, () =>
        {
            Assert.NotEmpty(winners);
            Assert.All(winners, pair => Assert.Equal(1, pair.Value));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SameDbContextConcurrentOperationsFailPredictably()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await StressRunner.RunAsync(nameof(SameDbContextConcurrentOperationsFailPredictably), "sqlserver", async context =>
        {
            using IServiceScope scope = database.CreateScope();
            DbContextOptions<StressDbContext> baseOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<StressDbContext>>();
            var gate = new DeterministicCommandGateInterceptor();
            DbContextOptions<StressDbContext> options = new DbContextOptionsBuilder<StressDbContext>(baseOptions)
                .AddInterceptors(gate)
                .Options;
            await using var db = new StressDbContext(options);

            Task<int> first = db.Database.ExecuteSqlRawAsync(
                "/* TCJ_CONCURRENCY_GATE */ SELECT 1",
                context.CancellationToken);
            Exception? secondFailure = null;
            try
            {
                await gate.WaitUntilBlockedAsync(context.CancellationToken);
                secondFailure = await Record.ExceptionAsync(() =>
                    db.Database.ExecuteSqlRawAsync("SELECT 1", context.CancellationToken));
            }
            finally
            {
                gate.Release();
                await first;
            }

            Assert.IsType<InvalidOperationException>(secondFailure);
        });
    }

    [Fact]
    public async Task OptimisticConcurrencyConflictsAreDetected()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await StressRunner.RunAsync(nameof(OptimisticConcurrencyConflictsAreDetected), "sqlserver", async context =>
        {
            Guid id = Guid.NewGuid();
            using (IServiceScope seed = database.CreateScope())
            {
                StressDbContext db = seed.ServiceProvider.GetRequiredService<StressDbContext>();
                db.Add(new StressRowVersionEntity(id, $"seed-{context.OperationId}"));
                await db.SaveChangesAsync(context.CancellationToken);
            }

            using IServiceScope firstScope = database.CreateScope();
            using IServiceScope secondScope = database.CreateScope();
            StressDbContext firstDb = firstScope.ServiceProvider.GetRequiredService<StressDbContext>();
            StressDbContext secondDb = secondScope.ServiceProvider.GetRequiredService<StressDbContext>();
            StressRowVersionEntity first = await firstDb.Set<StressRowVersionEntity>().SingleAsync(entity => entity.Id == id, context.CancellationToken);
            StressRowVersionEntity second = await secondDb.Set<StressRowVersionEntity>().SingleAsync(entity => entity.Id == id, context.CancellationToken);
            first.Name = $"first-{context.OperationId}";
            await firstDb.SaveChangesAsync(context.CancellationToken);
            second.Name = $"second-{context.OperationId}";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync(context.CancellationToken));
        });
    }
}
