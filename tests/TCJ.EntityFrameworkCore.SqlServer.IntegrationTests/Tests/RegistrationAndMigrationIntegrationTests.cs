using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.UnitOfWork;
using TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Tests;

[Collection(SqlServerIntegrationCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "SqlServer")]
[Trait("Category", "Database")]
public sealed class RegistrationAndMigrationIntegrationTests(SqlServerContainerFixture fixture)
    : SqlServerIntegrationTestBase(fixture)
{
    [Fact]
    public void Registration_resolves_context_abstractions_and_sql_server_provider()
    {
        using IServiceScope scope = Database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();

        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", context.Database.ProviderName);
        Assert.Same(context, scope.ServiceProvider.GetRequiredService<IReadDbContext>());
        Assert.Same(context, scope.ServiceProvider.GetRequiredService<IWriteDbContext>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        Assert.Equal(Fixture.Policy.CommandTimeoutSeconds, context.Database.GetCommandTimeout());
        Assert.False(context.Database.CreateExecutionStrategy().RetriesOnFailure);
    }

    [Fact]
    public void Configured_connection_string_targets_the_isolated_database_without_external_configuration()
    {
        using IServiceScope scope = Database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();
        var expected = new SqlConnectionStringBuilder(Database.ConnectionString);
        var actual = new SqlConnectionStringBuilder(context.Database.GetConnectionString());

        Assert.Equal(expected.DataSource, actual.DataSource);
        Assert.Equal(Database.DatabaseName, actual.InitialCatalog);
        Assert.False(string.IsNullOrWhiteSpace(actual.UserID));
        Assert.True(actual.TrustServerCertificate);
    }


    [Fact]
    public void Duplicate_registration_resolves_a_single_valid_context_contract()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TCJ.Core.Security.ICurrentUserProvider>(new TestCurrentUserProvider(7001));
        services.AddTcjSqlServer<SqlServerTestDbContext>(Database.ConnectionString, options => options.EnableRetryOnFailure = false);
        services.AddTcjSqlServer<SqlServerTestDbContext>(Database.ConnectionString, options => options.EnableRetryOnFailure = false);

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        using IServiceScope scope = provider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", context.Database.ProviderName);
        Assert.Same(context, scope.ServiceProvider.GetRequiredService<IReadDbContext>());
        Assert.Same(context, scope.ServiceProvider.GetRequiredService<IWriteDbContext>());
    }

    [Fact]
    public void Invalid_sql_server_options_fail_with_a_useful_error()
    {
        var services = new ServiceCollection();
        services.AddTcjSqlServer<SqlServerTestDbContext>(
            Database.ConnectionString,
            options => options.CommandTimeout = 0);

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>());

        Assert.Contains("CommandTimeout must be greater than zero", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Container_is_ready_and_database_accepts_real_connections()
    {
        Assert.True(Fixture.IsReady);

        using IServiceScope scope = Database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();

        Assert.True(await context.Database.CanConnectAsync());
    }

    [Fact]
    [Trait("Category", "Migration")]
    public async Task Migration_creates_required_tables_indexes_and_history()
    {
        using IServiceScope scope = Database.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SqlServerTestDbContext>();

        string[] migrations = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Contains("202608080001_InitialSqlServerIntegration", migrations);

        DbConnection connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand tableCommand = connection.CreateCommand();
        tableCommand.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name IN ('IntegrationEntities','IntegrationParents','IntegrationChildren')";
        Assert.Equal(3, Convert.ToInt32(await tableCommand.ExecuteScalarAsync()));

        await using DbCommand indexCommand = connection.CreateCommand();
        indexCommand.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_IntegrationEntities_Name' AND is_unique = 1";
        Assert.Equal(1, Convert.ToInt32(await indexCommand.ExecuteScalarAsync()));
        Assert.False(context.Database.HasPendingModelChanges());
    }
}
