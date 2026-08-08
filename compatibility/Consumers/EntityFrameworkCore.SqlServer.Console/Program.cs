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
        if (!string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal) ||
            dbContext.Database.GetConnectionString() != ConnectionString ||
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>() is null)
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
