using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace TCJ.Resilience.Tests.Infrastructure;

public sealed class SqlServerResilienceFixture : IAsyncLifetime
{
    private const string Image = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
    private readonly MsSqlContainer _container = new MsSqlBuilder(Image)
        .WithPassword(CreatePassword())
        .WithCleanUp(true)
        .Build();

    public async ValueTask InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await _container.StartAsync(timeout.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    internal async Task<ServiceProvider> CreateServicesAsync()
    {
        string databaseName = $"TCJ_Resilience_{Guid.NewGuid():N}";
        string connectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = databaseName,
            TrustServerCertificate = true
        }.ConnectionString;

        var masterBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "master",
            TrustServerCertificate = true
        };
        var masterOptions = new DbContextOptionsBuilder<DbContext>()
            .UseSqlServer(masterBuilder.ConnectionString)
            .Options;
        await using (var master = new DbContext(masterOptions))
        {
            await master.Database.ExecuteSqlRawAsync($"CREATE DATABASE [{databaseName}]").ConfigureAwait(false);
        }

        var services = new ServiceCollection();
        services.AddDbContext<ResilienceSqlDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
                sql.ExecutionStrategy(dependencies => new InjectedSqlExecutionStrategy(dependencies))));

        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        using IServiceScope scope = provider.CreateScope();
        ResilienceSqlDbContext context = scope.ServiceProvider.GetRequiredService<ResilienceSqlDbContext>();
        await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        return provider;
    }

    private static string CreatePassword()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return $"T!{Convert.ToHexString(bytes)}a7";
    }
}

internal sealed class ResilienceSqlDbContext(DbContextOptions<ResilienceSqlDbContext> options) : DbContext(options)
{
    internal Guid InstanceId { get; } = Guid.NewGuid();
    internal DbSet<ResilienceRow> Rows => Set<ResilienceRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ResilienceRow>(builder =>
        {
            builder.HasKey(row => row.Id);
            builder.Property(row => row.Value).HasMaxLength(128).IsRequired();
        });
    }
}

internal sealed class ResilienceRow
{
    public int Id { get; set; }
    public string Value { get; set; } = string.Empty;
}

internal sealed class InjectedSqlTransientException : Exception
{
    internal InjectedSqlTransientException() : base("controlled SQL execution-strategy transient failure") { }
}

internal sealed class InjectedSqlExecutionStrategy(ExecutionStrategyDependencies dependencies)
    : ExecutionStrategy(dependencies, maxRetryCount: 2, maxRetryDelay: TimeSpan.Zero)
{
    protected override bool ShouldRetryOn(Exception exception) => exception is InjectedSqlTransientException;
}
