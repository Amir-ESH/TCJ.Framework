using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.DependencyInjection.Extensions;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TcjUpgrade.SqlServerConsumer;

public static class Program
{
    private static readonly JsonSerializerOptions BehaviorJsonOptions = new() { WriteIndented = true };

    private const string ConnectionString = "Server=127.0.0.1,1433;Database=TcjUpgrade;Integrated Security=True;Encrypt=False;TrustServerCertificate=True";

    public static async Task Main()
    {
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(typeof(Program).Assembly);
        services.AddTcjSqlServer<UpgradeSqlServerDbContext>(ConnectionString, options =>
        {
            options.EnableRetryOnFailure = false;
            options.CommandTimeout = 5;
        });

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();
        UpgradeSqlServerDbContext dbContext = scope.ServiceProvider.GetRequiredService<UpgradeSqlServerDbContext>();
        DbConnection connection = dbContext.Database.GetDbConnection();
        IReadDbContext readDbContext = scope.ServiceProvider.GetRequiredService<IReadDbContext>();
        IWriteDbContext writeDbContext = scope.ServiceProvider.GetRequiredService<IWriteDbContext>();
        _ = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var behavior = new
        {
            schemaVersion = 1,
            scenario = "EntityFrameworkCore.SqlServerConsumer",
            checks = new
            {
                providerConfigured = dbContext.Database.IsSqlServer(),
                readContextResolved = ReferenceEquals(dbContext, readDbContext),
                writeContextResolved = ReferenceEquals(dbContext, writeDbContext),
                dataSourceConfigured = string.Equals(connection.DataSource, "127.0.0.1,1433", StringComparison.OrdinalIgnoreCase),
                databaseConfigured = string.Equals(connection.Database, "TcjUpgrade", StringComparison.Ordinal),
                commandTimeoutConfigured = dbContext.Database.GetCommandTimeout() == 5,
            },
        };

        await WriteBehaviorAsync(behavior);
        Console.WriteLine("TCJ.EntityFrameworkCore.SqlServer upgrade scenario passed");
    }

    private static async Task WriteBehaviorAsync<T>(T value)
    {
        string path = Environment.GetEnvironmentVariable("TCJ_UPGRADE_BEHAVIOR_PATH")
            ?? throw new InvalidOperationException("TCJ_UPGRADE_BEHAVIOR_PATH is required.");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Behavior path has no directory."));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, BehaviorJsonOptions));
    }
}

public sealed class UpgradeSqlServerDbContext(DbContextOptions<UpgradeSqlServerDbContext> options) : DbContext(options), IReadDbContext, IWriteDbContext { }
