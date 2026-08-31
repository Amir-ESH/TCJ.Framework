using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TCJ.Concurrency.Tests.Infrastructure;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using Testcontainers.MsSql;

namespace TCJ.Concurrency.Tests.Fixtures;

public sealed class SqlServerStressFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public async ValueTask InitializeAsync()
    {
        ConcurrencyPolicy policy = ConcurrencyPolicy.Load();
        _container = new MsSqlBuilder(policy.SqlServerContainerImage)
            .WithPassword(CreatePassword())
            .WithLabel("tcj.concurrency.stress", "true")
            .WithCleanUp(true)
            .Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await _container.StartAsync(timeout.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await WriteSanitizedContainerLogAsync().ConfigureAwait(false);
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async Task<SqlStressDatabase> CreateDatabaseAsync()
    {
        string databaseName = $"TCJ_Concurrency_{Guid.NewGuid():N}";
        string master = BuildConnectionString("master");
        await using (var connection = new SqlConnection(master))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = "DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@databaseName); EXEC sys.sp_executesql @sql;";
            command.Parameters.Add(new SqlParameter("@databaseName", databaseName));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        string connectionString = BuildConnectionString(databaseName);
        ServiceProvider services = BuildServices(connectionString);
        try
        {
            using IServiceScope scope = services.CreateScope();
            StressDbContext context = scope.ServiceProvider.GetRequiredService<StressDbContext>();
            await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
            return new SqlStressDatabase(this, databaseName, services);
        }
        catch
        {
            await services.DisposeAsync().ConfigureAwait(false);
            await DropDatabaseAsync(databaseName).ConfigureAwait(false);
            throw;
        }
    }


    private async Task WriteSanitizedContainerLogAsync()
    {
        if (_container is null)
        {
            return;
        }

        string directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "concurrency", "logs");
        Directory.CreateDirectory(directory);
        try
        {
            var (stdout, stderr) = await _container.GetLogsAsync().ConfigureAwait(false);
            string content = $"STDOUT{Environment.NewLine}{stdout}{Environment.NewLine}STDERR{Environment.NewLine}{stderr}";
            await File.WriteAllTextAsync(
                Path.Combine(directory, "sanitized-sqlserver.log"),
                Sanitize(content)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "sqlserver-log-collection-error.log"),
                Sanitize(exception.GetType().Name + ": " + exception.Message)).ConfigureAwait(false);
        }
    }

    private static string Sanitize(string value) =>
        Regex.Replace(value, @"(?i)((?:Password|Pwd)\s*=\s*)[^;\s]+", "$1***");

    private static string CreatePassword()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return $"T!{Convert.ToHexString(bytes)}a7";
    }

    private ServiceProvider BuildServices(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddTcjSqlServer<StressDbContext>(connectionString, options =>
        {
            options.EnableRetryOnFailure = false;
            options.CommandTimeout = 20;
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private string BuildConnectionString(string databaseName)
    {
        string value = _container?.GetConnectionString() ?? throw new InvalidOperationException("SQL Server container is not started.");
        var builder = new SqlConnectionStringBuilder(value)
        {
            InitialCatalog = databaseName,
            TrustServerCertificate = true
        };
        return builder.ConnectionString;
    }

    private async Task DropDatabaseAsync(string databaseName)
    {
        if (_container is null)
        {
            return;
        }
        await using var connection = new SqlConnection(BuildConnectionString("master"));
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            IF DB_ID(@databaseName) IS NOT NULL
            BEGIN
                DECLARE @sql nvarchar(max) =
                    N'ALTER DATABASE ' + QUOTENAME(@databaseName)
                    + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE '
                    + QUOTENAME(@databaseName) + N';';
                EXEC sys.sp_executesql @sql;
            END
            """;
        command.Parameters.Add(new SqlParameter("@databaseName", databaseName));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    internal sealed class SqlStressDatabase(SqlServerStressFixture fixture, string databaseName, ServiceProvider services) : IAsyncDisposable
    {
        public IServiceScope CreateScope() => services.CreateScope();
        public ServiceProvider Services => services;
        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync().ConfigureAwait(false);
            await fixture.DropDatabaseAsync(databaseName).ConfigureAwait(false);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerStressCollection : ICollectionFixture<SqlServerStressFixture>
{
    public const string Name = "SQL Server concurrency stress";
}
