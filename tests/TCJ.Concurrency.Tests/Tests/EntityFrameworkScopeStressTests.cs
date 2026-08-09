using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Concurrency.Tests.Fixtures;
using TCJ.Concurrency.Tests.Infrastructure;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.Repositories;

namespace TCJ.Concurrency.Tests.Tests;

[Trait("Category", "Concurrency")]
[Trait("Category", "Stress")]
[Trait("Category", "Transactions")]
[Trait("Category", "RequestScope")]
public sealed class EntityFrameworkScopeStressTests
{
    [Fact]
    public async Task IndependentDbContextScopesDoNotShareContext()
    {
        using ServiceProvider provider = BuildProvider(nameof(IndependentDbContextScopesDoNotShareContext));
        await StressRunner.RunAsync(nameof(IndependentDbContextScopesDoNotShareContext), "core", _ =>
        {
            using IServiceScope first = provider.CreateScope();
            using IServiceScope second = provider.CreateScope();
            StressDbContext firstContext = first.ServiceProvider.GetRequiredService<StressDbContext>();
            StressDbContext secondContext = second.ServiceProvider.GetRequiredService<StressDbContext>();
            Assert.NotSame(firstContext, secondContext);
            Assert.Same(firstContext, first.ServiceProvider.GetRequiredService<IWriteDbContext>());
            Assert.Same(secondContext, second.ServiceProvider.GetRequiredService<IWriteDbContext>());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RepositoryResolutionRemainsScopeCorrect()
    {
        using ServiceProvider provider = BuildProvider(nameof(RepositoryResolutionRemainsScopeCorrect));
        await StressRunner.RunAsync(nameof(RepositoryResolutionRemainsScopeCorrect), "core", _ =>
        {
            using IServiceScope first = provider.CreateScope();
            using IServiceScope second = provider.CreateScope();
            IRepository<StressEntity, Guid> firstRepository = first.ServiceProvider.GetRequiredService<IRepository<StressEntity, Guid>>();
            IRepository<StressEntity, Guid> sameRepository = first.ServiceProvider.GetRequiredService<IRepository<StressEntity, Guid>>();
            IRepository<StressEntity, Guid> secondRepository = second.ServiceProvider.GetRequiredService<IRepository<StressEntity, Guid>>();
            Assert.Same(firstRepository, sameRepository);
            Assert.NotSame(firstRepository, secondRepository);
            Assert.NotSame(first.ServiceProvider.GetRequiredService<StressDbContext>(), second.ServiceProvider.GetRequiredService<StressDbContext>());
            return Task.CompletedTask;
        });
    }

    private static ServiceProvider BuildProvider(string databaseName)
    {
        var services = new ServiceCollection();
        services.AddTcjEntityFrameworkCore<StressDbContext>(options => options.UseInMemoryDatabase(databaseName));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
