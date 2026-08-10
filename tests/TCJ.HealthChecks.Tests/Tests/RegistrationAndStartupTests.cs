using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TCJ.Core.HealthChecks;
using TCJ.DependencyInjection.Extensions;
using TCJ.DependencyInjection.HealthChecks;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.HealthChecks;
using TCJ.EntityFrameworkCore.SqlServer.HealthChecks;
using TCJ.HealthChecks.Tests.Infrastructure;

namespace TCJ.HealthChecks.Tests.Tests;

[Trait("Category", "Integration")]
[Trait("Category", "HealthChecks")]
public sealed class RegistrationAndStartupTests
{
    [Fact]
    public void Registration_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTcjDependencyInjection();
        services.AddTcjHealthChecks().AddTcjDependencyInjection().AddTcjDomainEvents();
        services.AddTcjHealthChecks().AddTcjDependencyInjection().AddTcjDomainEvents();
        using ServiceProvider provider = services.BuildServiceProvider();

        string[] names = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.Select(r => r.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(TcjHealthCheckNames.Checks.Core, names);
        Assert.Contains(TcjHealthCheckNames.Checks.Startup, names);
        Assert.Contains(TcjHealthCheckNames.Checks.DependencyInjection, names);
        Assert.Contains(TcjHealthCheckNames.Checks.DomainEvents, names);
    }

    [Fact]
    public async Task Core_liveness_is_healthy_with_framework_services()
    {
        using ServiceProvider provider = CreateHealthyProvider();
        HealthReport report = await RunAsync(provider, TcjHealthCheckNames.Checks.Core);
        Assert.Equal(HealthStatus.Healthy, report.Status);
    }

    [Fact]
    public async Task Dependency_injection_readiness_is_healthy_when_framework_registered()
    {
        using ServiceProvider provider = CreateHealthyProvider();
        HealthReport report = await RunAsync(provider, TcjHealthCheckNames.Checks.DependencyInjection);
        Assert.Equal(HealthStatus.Healthy, report.Status);
    }

    [Fact]
    public async Task Dependency_injection_readiness_is_unhealthy_when_framework_registration_missing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTcjHealthChecks().AddTcjDependencyInjection();
        using ServiceProvider provider = services.BuildServiceProvider();
        HealthReport report = await RunAsync(provider, TcjHealthCheckNames.Checks.DependencyInjection);
        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Contains(provider.GetRequiredService<TcjStartupDiagnostics>().GetSnapshot(), d => d.Code.StartsWith("TCJ.DI.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Domain_event_readiness_is_healthy_without_dispatching_synthetic_events()
    {
        using ServiceProvider provider = CreateHealthyProvider();
        HealthReport report = await RunAsync(provider, TcjHealthCheckNames.Checks.DomainEvents);
        Assert.Equal(HealthStatus.Healthy, report.Status);
    }

    [Fact]
    public async Task Startup_diagnostic_error_makes_readiness_unhealthy_but_not_liveness()
    {
        using ServiceProvider provider = CreateHealthyProvider();
        provider.GetRequiredService<TcjStartupDiagnostics>().Report("TCJ.Test.Invalid", "TCJ test configuration requires a valid setting.");
        HealthReport startup = await RunAsync(provider, TcjHealthCheckNames.Checks.Startup);
        HealthReport live = await RunAsync(provider, TcjHealthCheckNames.Checks.Core);
        Assert.Equal(HealthStatus.Unhealthy, startup.Status);
        Assert.Equal(HealthStatus.Healthy, live.Status);
    }

    [Fact]
    public async Task Startup_warning_is_degraded()
    {
        using ServiceProvider provider = CreateHealthyProvider();
        provider.GetRequiredService<TcjStartupDiagnostics>().Report("TCJ.Test.Warning", "TCJ test warning.", TcjStartupDiagnosticSeverity.Warning);
        HealthReport report = await RunAsync(provider, TcjHealthCheckNames.Checks.Startup);
        Assert.Equal(HealthStatus.Degraded, report.Status);
    }

    [Fact]
    public void Startup_diagnostics_are_actionable_and_ordered()
    {
        var diagnostics = new TcjStartupDiagnostics();
        diagnostics.Report("TCJ.Z", "Configure the required provider.");
        diagnostics.Report("TCJ.A", "Register the required framework service.");
        IReadOnlyList<TcjStartupDiagnostic> snapshot = diagnostics.GetSnapshot();
        Assert.Equal(["TCJ.A", "TCJ.Z"], snapshot.Select(d => d.Code).ToArray());
        Assert.All(snapshot, d => Assert.True(d.Message.Length > 10));
    }

    [Fact]
    public void Invalid_database_timeout_fails_during_registration()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddTcjHealthChecks(options => options.DatabaseTimeout = TimeSpan.FromSeconds(11)));
    }

    [Fact]
    public void Invalid_cache_duration_fails_during_registration()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddTcjHealthChecks(options => options.CacheDuration = TimeSpan.FromSeconds(61)));
    }

    [Fact]
    public async Task Provider_independent_entity_framework_check_initializes_model_without_connecting()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTcjEntityFrameworkCore<HealthTestDbContext>(options => options.UseInMemoryDatabase("health-model"));
        var health = services.AddTcjHealthChecks();
        health.AddTcjEntityFrameworkCore<HealthTestDbContext>();
        using ServiceProvider provider = services.BuildServiceProvider();
        HealthReport report = await RunAsync(provider, TcjHealthCheckNames.Checks.EntityFrameworkCore);
        Assert.Equal(HealthStatus.Healthy, report.Status);
    }


    [Fact]
    public async Task Missing_sql_server_DbContext_produces_actionable_startup_diagnostic_without_connecting()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        IHealthChecksBuilder health = services.AddTcjHealthChecks();
        health.AddTcjSqlServer<HealthTestDbContext>();
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        HealthReport report = await RunAsync(provider, TcjHealthCheckNames.Checks.SqlServer);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        TcjStartupDiagnostic diagnostic = Assert.Single(
            provider.GetRequiredService<TcjStartupDiagnostics>().GetSnapshot(),
            item => item.Code == "TCJ.SqlServer.MissingDbContext");
        Assert.Contains(nameof(HealthTestDbContext), diagnostic.Message, StringComparison.Ordinal);
    }


    [Fact]
    public async Task Malformed_sql_server_configuration_records_sanitized_actionable_diagnostic()
    {
        const string malformed = "TCJ_TEST_SECRET_MALFORMED_CONNECTION";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTcjDependencyInjection();
        TCJ.EntityFrameworkCore.SqlServer.Extensions.SqlServerServiceCollectionExtensions.AddTcjSqlServer<HealthTestDbContext>(services, malformed, options => options.EnableRetryOnFailure = false);
        services.AddTcjHealthChecks(options => options.CacheDuration = TimeSpan.Zero)
            .AddTcjSqlServer<HealthTestDbContext>();
        using ServiceProvider provider = services.BuildServiceProvider();

        HealthReport report = await RunAsync(provider, TcjHealthCheckNames.Checks.SqlServer);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        TcjStartupDiagnostic diagnostic = Assert.Single(
            provider.GetRequiredService<TcjStartupDiagnostics>().GetSnapshot(),
            item => item.Code == "TCJ.SqlServer.InvalidConfiguration");
        Assert.Contains("Verify", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(malformed, diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(malformed, report.Entries[TcjHealthCheckNames.Checks.SqlServer].Description ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_server_health_registration_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        IHealthChecksBuilder health = services.AddTcjHealthChecks();
        health.AddTcjSqlServer<HealthTestDbContext>();
        health.AddTcjSqlServer<HealthTestDbContext>();
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        string[] names = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Select(registration => registration.Name)
            .ToArray();
        Assert.Equal(1, names.Count(name => name == TcjHealthCheckNames.Checks.SqlServer));
    }

    private static ServiceProvider CreateHealthyProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTcjDependencyInjection();
        services.AddTcjHealthChecks().AddTcjDependencyInjection().AddTcjDomainEvents();
        return services.BuildServiceProvider();
    }

    private static Task<HealthReport> RunAsync(IServiceProvider provider, string name, CancellationToken token = default)
        => provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(registration => registration.Name == name, token);
}
