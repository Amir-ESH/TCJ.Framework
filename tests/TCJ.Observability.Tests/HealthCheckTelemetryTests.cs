using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.Core.Diagnostics;
using TCJ.DependencyInjection.HealthChecks;

namespace TCJ.Observability.Tests;

public sealed class HealthCheckTelemetryTests : IDisposable
{
    public HealthCheckTelemetryTests()
    {
        TcjTelemetry.ResetForTests();
        TcjTelemetry.Configure(options => { options.EnableTracing = true; options.EnableMetrics = true; });
    }

    [Fact]
    public async Task Health_check_activity_uses_stable_bounded_tags()
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TcjDiagnosticNames.Sources.Core,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        using ServiceProvider provider = CreateProvider();

        await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Name == "tcj.core");

        Activity activity = Assert.Single(stopped, item => item.OperationName == TcjDiagnosticNames.Activities.HealthCheckExecute);
        Assert.Equal("tcj.core", activity.GetTagItem(TcjDiagnosticNames.Tags.HealthCheckName));
        Assert.Equal("liveness", activity.GetTagItem(TcjDiagnosticNames.Tags.HealthCheckCategory));
        Assert.Equal("healthy", activity.GetTagItem(TcjDiagnosticNames.Tags.HealthCheckStatus));
        Assert.Equal(TcjTelemetry.FrameworkVersion, activity.GetTagItem(TcjDiagnosticNames.Tags.PackageVersion));
        Assert.DoesNotContain(activity.TagObjects, item => item.Key == TcjDiagnosticNames.Tags.ExceptionMessage);
    }

    [Fact]
    public async Task Health_check_metrics_include_required_instruments_without_secret_dimensions()
    {
        var metricNames = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == TcjDiagnosticNames.Sources.Core
                && instrument.Name.StartsWith("tcj.health_checks.", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => metricNames.Add(instrument.Name));
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => metricNames.Add(instrument.Name));
        listener.Start();
        using ServiceProvider provider = CreateProvider();

        await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Name == "tcj.core");

        Assert.Contains(TcjDiagnosticNames.Metrics.HealthChecksExecuted, metricNames);
        Assert.Contains(TcjDiagnosticNames.Metrics.HealthCheckDuration, metricNames);
        Assert.Contains(TcjDiagnosticNames.Metrics.HealthCheckStatus, metricNames);
        Assert.DoesNotContain(metricNames, name => name.Contains("password", StringComparison.OrdinalIgnoreCase));
    }


    [Fact]
    public async Task Unhealthy_health_check_increments_failure_metric_without_diagnostic_message_dimension()
    {
        var records = new ConcurrentBag<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == TcjDiagnosticNames.Sources.Core
                && instrument.Name == TcjDiagnosticNames.Metrics.HealthCheckFailures)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => records.Add((instrument.Name, tags.ToArray())));
        listener.Start();
        using ServiceProvider provider = CreateProvider();
        const string secret = "TCJ_TEST_SECRET_HEALTH_DIAGNOSTIC";
        provider.GetRequiredService<TCJ.Core.HealthChecks.TcjStartupDiagnostics>()
            .Report("TCJ.Test.Unhealthy", $"secret={secret}");

        await provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Name == "tcj.startup");

        var record = Assert.Single(records, item => item.Name == TcjDiagnosticNames.Metrics.HealthCheckFailures);
        Assert.DoesNotContain(secret, string.Join('\n', record.Tags.Select(tag => tag.Value?.ToString())), StringComparison.Ordinal);
        Assert.All(record.Tags, tag => Assert.Contains(tag.Key, new[]
        {
            TcjDiagnosticNames.Tags.HealthCheckName,
            TcjDiagnosticNames.Tags.HealthCheckCategory,
            TcjDiagnosticNames.Tags.HealthCheckStatus,
            TcjDiagnosticNames.Tags.OperationOutcome
        }));
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
