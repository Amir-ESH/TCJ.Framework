using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using TCJ.DependencyInjection.Extensions;
using TCJ.DependencyInjection.HealthChecks;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.HealthChecks;

namespace TCJ.HealthChecks.Tests.Infrastructure;

public sealed class SqlServerHealthFixture : IAsyncLifetime
{
    internal const string Image = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
    private readonly MsSqlContainer _container = new MsSqlBuilder(Image).WithPassword(CreatePassword()).WithCleanUp(true).Build();

    public async ValueTask InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await _container.StartAsync(timeout.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    internal ServiceProvider CreateProvider(bool migrations = false, TimeSpan? timeout = null, TimeSpan? cache = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTcjDependencyInjection();
        services.AddTcjSqlServer<HealthTestDbContext>(_container.GetConnectionString(), options =>
        {
            options.EnableRetryOnFailure = false;
            options.MigrationsAssembly = typeof(SqlServerHealthFixture).Assembly.GetName().Name;
        });
        services.AddTcjHealthChecks(options =>
        {
            if (timeout is not null) options.DatabaseTimeout = timeout.Value;
            if (cache is not null) options.CacheDuration = cache.Value;
        }).AddTcjSqlServer<HealthTestDbContext>(migrations);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    internal string UnavailableConnectionString()
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            DataSource = "127.0.0.1,1",
            ConnectTimeout = 30,
            TrustServerCertificate = true
        };
        return builder.ConnectionString;
    }

    private static string CreatePassword()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return $"H!{Convert.ToHexString(bytes)}a9";
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerHealthCollection : ICollectionFixture<SqlServerHealthFixture>
{
    public const string Name = "Health SQL Server";
}
