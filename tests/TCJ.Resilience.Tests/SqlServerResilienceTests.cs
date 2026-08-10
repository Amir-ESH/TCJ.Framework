using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.Resilience.Tests.Infrastructure;

namespace TCJ.Resilience.Tests;

[Collection("Resilience SQL Server")]
public sealed class SqlServerResilienceTests(SqlServerResilienceFixture fixture)
{
    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Transaction")]
    public async Task SqlServer_transient_execution_strategy_recreates_context_and_transaction_and_commits_once()
    {
        await using ServiceProvider provider = await fixture.CreateServicesAsync();
        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var contextIds = new List<Guid>();
        int attempts = 0;

        await scopeFactory.ExecuteTcjSqlServerTransactionAsync<ResilienceSqlDbContext>(async (db, token) =>
        {
            contextIds.Add(db.InstanceId);
            attempts++;
            if (attempts == 1)
            {
                // Dirty the first context so the retry proves that failed state is discarded.
                db.Rows.Add(new ResilienceRow { Value = "must-not-survive-retry" });
                throw new InjectedSqlTransientException();
            }

            db.Rows.Add(new ResilienceRow { Value = "committed-once" });
            await db.SaveChangesAsync(token);
        });

        Assert.Equal(2, attempts);
        Assert.Equal(2, contextIds.Distinct().Count());
        using IServiceScope verificationScope = provider.CreateScope();
        ResilienceSqlDbContext verification = verificationScope.ServiceProvider.GetRequiredService<ResilienceSqlDbContext>();
        ResilienceRow row = Assert.Single(await verification.Rows.AsNoTracking().ToListAsync());
        Assert.Equal("committed-once", row.Value);
        ResilienceTrace.Write(nameof(SqlServer_transient_execution_strategy_recreates_context_and_transaction_and_commits_once), new { attempts, distinctContexts = contextIds.Distinct().Count(), rows = 1 });
    }

    [Fact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Transaction")]
    public async Task SqlServer_permanent_failure_is_not_retried_and_transaction_rolls_back()
    {
        await using ServiceProvider provider = await fixture.CreateServicesAsync();
        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        int attempts = 0;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            scopeFactory.ExecuteTcjSqlServerTransactionAsync<ResilienceSqlDbContext>(async (db, token) =>
            {
                attempts++;
                db.Rows.Add(new ResilienceRow { Value = "must-roll-back" });
                await db.SaveChangesAsync(token);
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT * FROM [TCJ_Definitely_Missing_Table]",
                    token);
            }));

        Assert.Equal(1, attempts);
        using IServiceScope verificationScope = provider.CreateScope();
        ResilienceSqlDbContext verification = verificationScope.ServiceProvider.GetRequiredService<ResilienceSqlDbContext>();
        Assert.Equal(0, await verification.Rows.CountAsync());
    }
}

[CollectionDefinition("Resilience SQL Server")]
public sealed class ResilienceSqlServerCollection : ICollectionFixture<SqlServerResilienceFixture>
{
}
