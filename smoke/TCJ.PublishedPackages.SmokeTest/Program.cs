using Microsoft.EntityFrameworkCore;
using TCJ.AspNetCore.Extensions;
using TCJ.AspNetCore.Options;
using TCJ.Core.Entities;
using TCJ.Core.Identifiers;
using TCJ.Core.Results;
using TCJ.DependencyInjection.Extensions;
using TCJ.DependencyInjection.Registration;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.Repositories;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Options;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TCJ.PublishedPackages.SmokeTest;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                Args = args,
                EnvironmentName = Environments.Production
            });

        builder.Services.AddTcjDependencyInjection(typeof(Program).Assembly);
        builder.Services.AddTcjAspNetCore();

        builder.Services.AddTcjSqlServer<SmokeDbContext>(
            "Server=localhost;Database=TCJ_PublishedPackageSmoke;User Id=sa;Password=NotUsed_123!;TrustServerCertificate=True",
            configureTcjSqlServer: options =>
                options.EnableRetryOnFailure = false);

        await using WebApplication app = builder.Build();

        app.UseTcjAspNetCore();

        await using AsyncServiceScope scope =
            app.Services.CreateAsyncScope();

        IServiceProvider services = scope.ServiceProvider;

        IGuidGenerator guidGenerator =
            services.GetRequiredService<IGuidGenerator>();

        Guid id = guidGenerator.CreateVersion7();
        Result<Guid> result = Result.Success(id);

        if (result.IsFailure || result.Value == Guid.Empty)
        {
            throw new InvalidOperationException(
                "TCJ.Core Result or GUID generation smoke check failed.");
        }

        _ = services.GetRequiredService<
            IReadRepository<SmokeEntity, Guid>>();

        _ = services.GetRequiredService<IUnitOfWork>();

        SmokeDbContext dbContext =
            services.GetRequiredService<SmokeDbContext>();

        if (dbContext.Model.FindEntityType(typeof(SmokeEntity)) is null)
        {
            throw new InvalidOperationException(
                "TCJ Entity Framework Core model smoke check failed.");
        }

        Type[] packageMarkerTypes =
        [
            typeof(Result),
            typeof(TcjDependencyInjectionOptions),
            typeof(IUnitOfWork),
            typeof(TcjSqlServerOptions),
            typeof(TcjAspNetCoreOptions)
        ];

        foreach (Type markerType in packageMarkerTypes)
        {
            string assemblyName =
                markerType.Assembly.GetName().Name
                ?? throw new InvalidOperationException(
                    "A package assembly has no name.");

            Console.WriteLine($"Loaded {assemblyName}");
        }

        Console.WriteLine(
            $"Published package smoke test succeeded. Generated UUID: {id}");
    }
}

public sealed class SmokeDbContext(
    DbContextOptions<SmokeDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
    public DbSet<SmokeEntity> SmokeEntities =>
        Set<SmokeEntity>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplySoftDeleteQueryFilters();
        modelBuilder.ApplyTcjSqlServerConventions();
    }
}

public sealed class SmokeEntity
    : RowVersionFullAuditedEntity<Guid>
{
    private SmokeEntity()
    {
    }

    public SmokeEntity(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Name { get; private set; } =
        string.Empty;
}