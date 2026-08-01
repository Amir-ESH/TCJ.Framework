using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.Repositories;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TCJ.EntityFrameworkCore.Tests;

public sealed class RegistrationAndUnitOfWorkTests
{
    [Fact]
    public async Task Registration_resolves_repository_and_unit_of_work_for_the_same_context()
    {
        var services = new ServiceCollection();
        string databaseName = Guid.NewGuid().ToString(format: "N");

        services.AddTcjEntityFrameworkCore<TestDbContext>(options => options.UseInMemoryDatabase(databaseName));

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        IRepository<TestProduct, Guid> repository = scope.ServiceProvider.GetRequiredService<IRepository<TestProduct, Guid>>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var product = new TestProduct(id: Guid.NewGuid(), name: "Persisted");
        await repository.AddAsync(product, CancellationToken.None);

        int affected = await unitOfWork.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(1, affected);
        Assert.True(await repository.AnyAsync(testProduct => testProduct.Id == product.Id, CancellationToken.None));
    }
}
