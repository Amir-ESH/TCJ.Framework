using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.DependencyInjection.Extensions;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TcjCompatibility.SqlServerConsumer;

public static class Program
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=TcjCompatibility;Integrated Security=True;Encrypt=False;TrustServerCertificate=True";

    public static void Main()
    {
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(typeof(Program).Assembly);
        services.AddTcjSqlServer<SqlServerConsumerDbContext>(ConnectionString, options =>
        {
            options.EnableRetryOnFailure = false;
            options.CommandTimeout = 5;
        });

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        using IServiceScope scope = provider.CreateScope();

        SqlServerConsumerDbContext dbContext = scope.ServiceProvider.GetRequiredService<SqlServerConsumerDbContext>();
        DbConnection connection = dbContext.Database.GetDbConnection();
        IReadDbContext readDbContext = scope.ServiceProvider.GetRequiredService<IReadDbContext>();
        IWriteDbContext writeDbContext = scope.ServiceProvider.GetRequiredService<IWriteDbContext>();
        _ = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        if (!dbContext.Database.IsSqlServer() ||
            !ReferenceEquals(dbContext, readDbContext) ||
            !ReferenceEquals(dbContext, writeDbContext) ||
            !string.Equals(connection.DataSource, "127.0.0.1,1433", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(connection.Database, "TcjCompatibility", StringComparison.Ordinal) ||
            dbContext.Database.GetCommandTimeout() != 5)
        {
            throw new InvalidOperationException("SQL Server package registration is invalid.");
        }

        Console.WriteLine("TCJ.EntityFrameworkCore.SqlServer consumer passed");
    }
}

public sealed class SqlServerConsumerDbContext(DbContextOptions<SqlServerConsumerDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
}
