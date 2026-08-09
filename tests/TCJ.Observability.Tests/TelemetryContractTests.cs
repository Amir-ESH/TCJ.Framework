using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TCJ.AspNetCore.Diagnostics;
using TCJ.Core.Diagnostics;
using TCJ.DependencyInjection.Diagnostics;
using TCJ.DependencyInjection.Extensions;
using TCJ.EntityFrameworkCore.Diagnostics;
using TCJ.EntityFrameworkCore.SqlServer.Diagnostics;

namespace TCJ.Observability.Tests;

public sealed class TelemetryContractTests : IDisposable
{
    public TelemetryContractTests() => TcjTelemetry.ResetForTests();

    [Fact]
    public void Stable_sources_and_meters_use_package_versions()
    {
        Assert.Equal(TcjDiagnosticNames.Sources.Core, CoreTelemetryDiagnostics.ActivitySource.Name);
        Assert.Equal(TcjDiagnosticNames.Sources.DependencyInjection, DependencyInjectionTelemetryDiagnostics.ActivitySource.Name);
        Assert.Equal(TcjDiagnosticNames.Sources.EntityFrameworkCore, EntityFrameworkCoreTelemetryDiagnostics.ActivitySource.Name);
        Assert.Equal(TcjDiagnosticNames.Sources.EntityFrameworkCoreSqlServer, SqlServerTelemetryDiagnostics.ActivitySource.Name);
        Assert.Equal(TcjDiagnosticNames.Sources.AspNetCore, AspNetCoreTelemetryDiagnostics.ActivitySource.Name);

        Assert.Equal(CoreTelemetryDiagnostics.PackageVersion, CoreTelemetryDiagnostics.ActivitySource.Version);
        Assert.Equal(DependencyInjectionTelemetryDiagnostics.PackageVersion, DependencyInjectionTelemetryDiagnostics.ActivitySource.Version);
        Assert.Equal(EntityFrameworkCoreTelemetryDiagnostics.PackageVersion, EntityFrameworkCoreTelemetryDiagnostics.ActivitySource.Version);
        Assert.Equal(SqlServerTelemetryDiagnostics.PackageVersion, SqlServerTelemetryDiagnostics.ActivitySource.Version);
        Assert.Equal(AspNetCoreTelemetryDiagnostics.PackageVersion, AspNetCoreTelemetryDiagnostics.ActivitySource.Version);

        Assert.Equal(TcjDiagnosticNames.Sources.Core, CoreTelemetryDiagnostics.Meter.Name);
        Assert.Equal(TcjDiagnosticNames.Sources.DependencyInjection, DependencyInjectionTelemetryDiagnostics.Meter.Name);
        Assert.Equal(TcjDiagnosticNames.Sources.EntityFrameworkCore, EntityFrameworkCoreTelemetryDiagnostics.Meter.Name);
        Assert.Equal(TcjDiagnosticNames.Sources.EntityFrameworkCoreSqlServer, SqlServerTelemetryDiagnostics.Meter.Name);
        Assert.Equal(TcjDiagnosticNames.Sources.AspNetCore, AspNetCoreTelemetryDiagnostics.Meter.Name);

        Assert.Equal(CoreTelemetryDiagnostics.PackageVersion, CoreTelemetryDiagnostics.Meter.Version);
        Assert.Equal(DependencyInjectionTelemetryDiagnostics.PackageVersion, DependencyInjectionTelemetryDiagnostics.Meter.Version);
        Assert.Equal(EntityFrameworkCoreTelemetryDiagnostics.PackageVersion, EntityFrameworkCoreTelemetryDiagnostics.Meter.Version);
        Assert.Equal(SqlServerTelemetryDiagnostics.PackageVersion, SqlServerTelemetryDiagnostics.Meter.Version);
        Assert.Equal(AspNetCoreTelemetryDiagnostics.PackageVersion, AspNetCoreTelemetryDiagnostics.Meter.Version);

        Assert.DoesNotContain("unknown", new[]
        {
            CoreTelemetryDiagnostics.PackageVersion,
            DependencyInjectionTelemetryDiagnostics.PackageVersion,
            EntityFrameworkCoreTelemetryDiagnostics.PackageVersion,
            SqlServerTelemetryDiagnostics.PackageVersion,
            AspNetCoreTelemetryDiagnostics.PackageVersion
        });
    }

    [Fact]
    public void No_listener_does_not_allocate_activity_and_configuration_is_production_safe()
    {
        Activity? activity = TcjTelemetry.StartActivity(
            CoreTelemetryDiagnostics.ActivitySource,
            TcjDiagnosticNames.Activities.DomainEventDispatch,
            TcjDiagnosticNames.Sources.Core,
            CoreTelemetryDiagnostics.PackageVersion,
            "dispatch");

        Assert.Null(activity);

        TcjTelemetryOptions options = TcjTelemetry.GetOptions();
        Assert.True(options.EnableTracing);
        Assert.True(options.EnableMetrics);
        Assert.False(options.RecordExceptionMessages);
        Assert.True(options.RecordEntityTypeNames);
        Assert.True(options.RecordHandlerTypeNames);
    }

    [Fact]
    public void Telemetry_registration_is_idempotent_and_does_not_register_exporters()
    {
        var services = new ServiceCollection();
        int before = services.Count;

        services.AddTcjTelemetry();
        services.AddTcjTelemetry(options => options.RecordExceptionMessages = false);
        services.AddTcjTelemetry();

        Assert.Equal(before, services.Count);
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType.FullName?.Contains("Exporter", StringComparison.OrdinalIgnoreCase) == true);
    }

    public void Dispose() => TcjTelemetry.ResetForTests();
}
