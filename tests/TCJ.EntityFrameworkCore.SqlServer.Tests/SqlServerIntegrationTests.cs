using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TCJ.EntityFrameworkCore.SqlServer.Tests;

public sealed class SqlServerIntegrationTests
{
    private const string ConnectionString = "Server=(localdb)\\MSSQLLocalDB;Database=TCJ.Tests;Trusted_Connection=True;TrustServerCertificate=True";

    [Fact]
    public void Rowversion_convention_configures_database_generated_concurrency_token()
    {
        DbContextOptions<SqlServerTestDbContext> options = new DbContextOptionsBuilder<SqlServerTestDbContext>().UseSqlServer(ConnectionString).Options;

        using var context = new SqlServerTestDbContext(options);
        IProperty property = context.Model.FindEntityType(typeof(ConcurrencyEntity))!.FindProperty(nameof(IRowVersion.RowVersion))!;

        Assert.True(property.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, property.ValueGenerated);
        Assert.False(property.IsNullable);
        Assert.Equal("rowversion", property.GetColumnType());
    }

    [Fact]
    public void Provider_registration_rejects_retry_count_above_resilience_bound()
    {
        var services = new ServiceCollection();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddTcjSqlServer<SqlServerTestDbContext>(ConnectionString,
                configureTcjSqlServer: options => options.MaxRetryCount = 11));
        Assert.Contains("between 1 and 10", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_registration_rejects_retry_delay_above_resilience_bound()
    {
        var services = new ServiceCollection();
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddTcjSqlServer<SqlServerTestDbContext>(ConnectionString,
                configureTcjSqlServer: options => options.MaxRetryDelay = TimeSpan.FromSeconds(31)));
        Assert.Contains("no more than 30 seconds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_registration_exposes_context_abstractions_and_unit_of_work()
    {
        var services = new ServiceCollection();

        services.AddTcjSqlServer<SqlServerTestDbContext>(ConnectionString,
                                                         configureTcjSqlServer: options =>
                                                             {
                                                                 options.EnableRetryOnFailure = false;
                                                                 options.CommandTimeout = 45;
                                                             });

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        SqlServerTestDbContext context = scope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();

        Assert.Same(context, scope.ServiceProvider.GetRequiredService<IReadDbContext>());
        Assert.Same(context, scope.ServiceProvider.GetRequiredService<IWriteDbContext>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        Assert.Equal(45, context.Database.GetCommandTimeout());
    }
}

internal sealed class SqlServerTestDbContext(DbContextOptions<SqlServerTestDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ConcurrencyEntity>().HasKey(entity => entity.Id);
        modelBuilder.ApplyTcjSqlServerConventions();
    }
}

internal sealed class ConcurrencyEntity : RowVersionFullAuditedEntity<Guid>
{
    public ConcurrencyEntity()
    {
        Id = Guid.NewGuid();
    }
}
