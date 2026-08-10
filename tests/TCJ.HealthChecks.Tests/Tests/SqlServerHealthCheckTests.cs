using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.HealthChecks;
using TCJ.DependencyInjection.Extensions;
using TCJ.DependencyInjection.HealthChecks;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.HealthChecks;
using TCJ.HealthChecks.Tests.Infrastructure;

namespace TCJ.HealthChecks.Tests.Tests;

[Collection(SqlServerHealthCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "HealthChecks")]
[Trait("Category", "SqlServer")]
public sealed class SqlServerHealthCheckTests(SqlServerHealthFixture fixture)
{
    [Fact]
    public async Task Available_sql_server_is_healthy()
    {
        using ServiceProvider provider = fixture.CreateProvider();
        HealthReport report = await RunAsync(provider, TcjHealthCheckNames.Checks.SqlServer);
        Assert.Equal(HealthStatus.Healthy, report.Status);
    }

    [Fact]
    public async Task Unavailable_sql_server_is_unhealthy_without_affecting_core_liveness()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTcjDependencyInjection();
        services.AddTcjSqlServer<HealthTestDbContext>(fixture.UnavailableConnectionString(), options => options.EnableRetryOnFailure = false);
        services.AddTcjHealthChecks(options => options.DatabaseTimeout = TimeSpan.FromSeconds(1))
            .AddTcjSqlServer<HealthTestDbContext>();
        using ServiceProvider provider = services.BuildServiceProvider();
        HealthReport sql = await RunAsync(provider, TcjHealthCheckNames.Checks.SqlServer);
        HealthReport live = await RunAsync(provider, TcjHealthCheckNames.Checks.Core);
        Assert.Equal(HealthStatus.Unhealthy, sql.Status);
        Assert.Equal(HealthStatus.Healthy, live.Status);
    }

    [Fact]
    public async Task Pending_migrations_report_configured_degraded_status_without_applying_them()
    {
        using ServiceProvider provider = fixture.CreateProvider(migrations: true);
        using IServiceScope scope = provider.CreateScope();
        HealthTestDbContext db = scope.ServiceProvider.GetRequiredService<HealthTestDbContext>();
        string[] before = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        HealthReport report = await RunAsync(provider, TcjHealthCheckNames.Checks.SqlServerMigrations);
        string[] after = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Equal(before, after);
        Assert.Empty(after);
    }

    [Fact]
    public async Task Canceled_sql_server_health_request_propagates_cancellation()
    {
        using ServiceProvider provider = fixture.CreateProvider(cache: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(provider, TcjHealthCheckNames.Checks.SqlServer, cancellation.Token));
    }

    [Fact]
    public async Task Database_timeout_is_bounded_by_configured_health_timeout()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTcjDependencyInjection();
        services.AddTcjSqlServer<HealthTestDbContext>(fixture.UnavailableConnectionString(), options => options.EnableRetryOnFailure = false);
        services.AddTcjHealthChecks(options => { options.DatabaseTimeout = TimeSpan.FromMilliseconds(500); options.CacheDuration = TimeSpan.Zero; })
            .AddTcjSqlServer<HealthTestDbContext>();
        using ServiceProvider provider = services.BuildServiceProvider();
        var started = System.Diagnostics.Stopwatch.StartNew();
        HealthReport report = await RunAsync(provider, TcjHealthCheckNames.Checks.SqlServer);
        started.Stop();
        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"Health check took {started.Elapsed}.");
    }

    [Fact]
    public async Task Sql_server_health_result_does_not_expose_connection_string()
    {
        string connection = fixture.UnavailableConnectionString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTcjDependencyInjection();
        services.AddTcjSqlServer<HealthTestDbContext>(connection, options => options.EnableRetryOnFailure = false);
        services.AddTcjHealthChecks(options => options.DatabaseTimeout = TimeSpan.FromMilliseconds(500)).AddTcjSqlServer<HealthTestDbContext>();
        using ServiceProvider provider = services.BuildServiceProvider();
        HealthReport report = await RunAsync(provider, TcjHealthCheckNames.Checks.SqlServer);
        HealthReportEntry entry = report.Entries[TcjHealthCheckNames.Checks.SqlServer];
        Assert.DoesNotContain(connection, entry.Description ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
    }

    private static Task<HealthReport> RunAsync(IServiceProvider provider, string name, CancellationToken token = default)
        => provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(registration => registration.Name == name, token);
}
