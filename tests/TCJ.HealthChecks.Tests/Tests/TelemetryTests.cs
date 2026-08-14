using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Diagnostics;
using TCJ.DependencyInjection.HealthChecks;

namespace TCJ.HealthChecks.Tests.Tests;

[Trait("Category", "Integration")]
[Trait("Category", "HealthChecks")]
[Trait("Category", "Observability")]
public sealed class TelemetryTests : IDisposable
{
    public TelemetryTests()
    {
        TcjTelemetry.ResetForTests();
        TcjTelemetry.Configure(options => { options.EnableTracing = true; options.EnableMetrics = true; });
    }

    [Fact]
    public async Task Health_check_emits_one_bounded_activity()
    {
        var activities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TcjDiagnosticNames.Sources.Core,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        using ServiceProvider provider = CreateProvider();
        await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(registration => registration.Name == "tcj.core");

        Activity activity = Assert.Single(activities, item => item.OperationName == TcjDiagnosticNames.Activities.HealthCheckExecute);
        Assert.Equal("tcj.core", activity.GetTagItem(TcjDiagnosticNames.Tags.HealthCheckName));
        Assert.Equal("liveness", activity.GetTagItem(TcjDiagnosticNames.Tags.HealthCheckCategory));
        Assert.Equal("healthy", activity.GetTagItem(TcjDiagnosticNames.Tags.HealthCheckStatus));
        Assert.Equal(TcjTelemetry.FrameworkVersion, activity.GetTagItem(TcjDiagnosticNames.Tags.PackageVersion));
        Assert.DoesNotContain(activity.TagObjects, item => item.Key == TcjDiagnosticNames.Tags.ExceptionMessage);
    }

    [Fact]
    public async Task Health_check_emits_execution_duration_status_and_no_duplicate_metric_names()
    {
        var names = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == TcjDiagnosticNames.Sources.Core && instrument.Name.StartsWith("tcj.health_checks.", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => names.Add(instrument.Name));
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => names.Add(instrument.Name));
        listener.Start();

        using ServiceProvider provider = CreateProvider();
        await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(registration => registration.Name == "tcj.core");

        Assert.Contains(TcjDiagnosticNames.Metrics.HealthChecksExecuted, names);
        Assert.Contains(TcjDiagnosticNames.Metrics.HealthCheckDuration, names);
        Assert.Contains(TcjDiagnosticNames.Metrics.HealthCheckStatus, names);
        Assert.Equal(1, names.Count(name => name == TcjDiagnosticNames.Metrics.HealthChecksExecuted));
    }

    [Fact]
    public void Public_health_metric_dimensions_are_bounded_contract_names()
    {
        string[] dimensions =
        [
            TcjDiagnosticNames.Tags.HealthCheckName,
            TcjDiagnosticNames.Tags.HealthCheckCategory,
            TcjDiagnosticNames.Tags.HealthCheckStatus,
            TcjDiagnosticNames.Tags.OperationOutcome
        ];
        Assert.Equal(dimensions.Length, dimensions.Distinct(StringComparer.Ordinal).Count());
        Assert.All(dimensions, value => Assert.True(value.StartsWith("tcj.", StringComparison.Ordinal)));
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTcjHealthChecks();
        return services.BuildServiceProvider();
    }

    public void Dispose() => TcjTelemetry.ResetForTests();
}
