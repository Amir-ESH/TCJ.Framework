using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Entities;
using TCJ.DependencyInjection.Extensions;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.Repositories;
using TCJ.EntityFrameworkCore.Specifications;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TcjCompatibility.EntityFrameworkCoreConsumer;

public static class Program
{
    public static async Task Main()
    {
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(typeof(Program).Assembly);
        services.AddTcjEntityFrameworkCore<ConsumerDbContext>(options =>
            options.UseInMemoryDatabase($"tcj-compat-{Guid.NewGuid():N}"));

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        using IServiceScope scope = provider.CreateScope();
        IRepository<ConsumerProduct, Guid> repository = scope.ServiceProvider.GetRequiredService<IRepository<ConsumerProduct, Guid>>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var product = new ConsumerProduct(Guid.NewGuid(), "Alpha");
        await repository.AddAsync(product, CancellationToken.None);
        if (await unitOfWork.SaveChangesAsync(CancellationToken.None) != 1 || product.CreatedOn is null)
        {
            throw new InvalidOperationException("Repository/unit-of-work/auditing integration failed.");
        }

        IReadOnlyList<ConsumerProduct> matches = await repository.ListAsync(new AlphaProductsSpecification(), CancellationToken.None);
        if (matches.Count != 1 || matches[0].Id != product.Id)
        {
            throw new InvalidOperationException("Specification integration failed.");
        }

        product.IsDeleted = true;
        repository.Update(product);
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        if (await repository.CountAsync(CancellationToken.None) != 0)
        {
            throw new InvalidOperationException("Soft-delete query filter integration failed.");
        }

        Console.WriteLine("TCJ.EntityFrameworkCore consumer passed");
    }
}

public sealed class ConsumerDbContext(DbContextOptions<ConsumerDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
    public DbSet<ConsumerProduct> Products => Set<ConsumerProduct>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ConsumerProduct>(builder =>
        {
            builder.HasKey(product => product.Id);
            builder.Property(product => product.Name).IsRequired();
        });
        modelBuilder.ApplySoftDeleteQueryFilters();
    }
}

public sealed class ConsumerProduct : FullAuditedEntity<Guid>
{
    private ConsumerProduct() { }

    public ConsumerProduct(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Name { get; private set; } = string.Empty;
}

public sealed class AlphaProductsSpecification : Specification<ConsumerProduct>
{
    public AlphaProductsSpecification() : base(product => product.Name.StartsWith("A"))
    {
        ApplyOrderBy(product => product.Name);
    }
}
