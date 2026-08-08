using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Entities;
using TCJ.DependencyInjection.Extensions;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.Repositories;
using TCJ.EntityFrameworkCore.Specifications;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TcjUpgrade.EntityFrameworkCoreConsumer;

public static class Program
{
    public static async Task Main()
    {
        string phase = Environment.GetEnvironmentVariable("TCJ_UPGRADE_PHASE")
            ?? throw new InvalidOperationException("TCJ_UPGRADE_PHASE is required.");
        string dataDirectory = Environment.GetEnvironmentVariable("TCJ_UPGRADE_DATA_PATH")
            ?? throw new InvalidOperationException("TCJ_UPGRADE_DATA_PATH is required.");
        Directory.CreateDirectory(dataDirectory);
        string databasePath = Path.Combine(dataDirectory, "upgrade.db");

        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(typeof(Program).Assembly);
        services.AddTcjEntityFrameworkCore<UpgradeDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();
        UpgradeDbContext dbContext = scope.ServiceProvider.GetRequiredService<UpgradeDbContext>();
        await dbContext.Database.EnsureCreatedAsync();
        IRepository<UpgradeProduct, Guid> repository = scope.ServiceProvider.GetRequiredService<IRepository<UpgradeProduct, Guid>>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Guid persistedId = Guid.Parse("01900000-0000-7000-8000-000000000001");
        bool persistedDataCompatible;
        UpgradeProduct? product = await repository.GetByIdAsync(persistedId, CancellationToken.None);
        if (string.Equals(phase, "baseline", StringComparison.Ordinal))
        {
            if (product is null)
            {
                product = new UpgradeProduct(persistedId, "Alpha");
                await repository.AddAsync(product, CancellationToken.None);
                _ = await unitOfWork.SaveChangesAsync(CancellationToken.None);
            }
            persistedDataCompatible = product is not null && product.Name == "Alpha";
        }
        else
        {
            persistedDataCompatible = product is not null && product.Name == "Alpha";
            if (product is null)
            {
                throw new InvalidOperationException("The target package could not read data written by the baseline package.");
            }
        }

        if (product is null)
        {
            throw new InvalidOperationException("Upgrade product is unavailable.");
        }
        IReadOnlyList<UpgradeProduct> matches = await repository.ListAsync(new AlphaProductsSpecification(), CancellationToken.None);
        bool modelCreated = dbContext.Model.FindEntityType(typeof(UpgradeProduct)) is not null;

        product.IsDeleted = true;
        repository.Update(product);
        int saved = await unitOfWork.SaveChangesAsync(CancellationToken.None);
        int visibleCount = await repository.CountAsync(CancellationToken.None);
        product.IsDeleted = false;
        repository.Update(product);
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        var behavior = new
        {
            schemaVersion = 1,
            scenario = "EntityFrameworkCoreConsumer",
            checks = new
            {
                repositorySaved = saved == 1,
                auditingApplied = product.CreatedOn is not null,
                specificationMatched = matches.Count == 1 && matches[0].Id == product.Id,
                softDeleteFiltered = visibleCount == 0,
                modelCreated,
                persistedDataCompatible,
            },
        };

        await WriteBehaviorAsync(behavior);
        Console.WriteLine("TCJ.EntityFrameworkCore upgrade scenario passed");
    }

    private static async Task WriteBehaviorAsync<T>(T value)
    {
        string path = Environment.GetEnvironmentVariable("TCJ_UPGRADE_BEHAVIOR_PATH")
            ?? throw new InvalidOperationException("TCJ_UPGRADE_BEHAVIOR_PATH is required.");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Behavior path has no directory."));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class UpgradeDbContext(DbContextOptions<UpgradeDbContext> options) : DbContext(options), IReadDbContext, IWriteDbContext
{
    public DbSet<UpgradeProduct> Products => Set<UpgradeProduct>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<UpgradeProduct>(builder =>
        {
            builder.HasKey(product => product.Id);
            builder.Property(product => product.Name).IsRequired();
        });
        modelBuilder.ApplySoftDeleteQueryFilters();
    }
}

public sealed class UpgradeProduct : FullAuditedEntity<Guid>
{
    private UpgradeProduct() { }
    public UpgradeProduct(Guid id, string name) { Id = id; Name = name; }
    public string Name { get; private set; } = string.Empty;
}

public sealed class AlphaProductsSpecification : Specification<UpgradeProduct>
{
    public AlphaProductsSpecification() : base(product => product.Name.StartsWith('A')) => ApplyOrderBy(product => product.Name);
}
