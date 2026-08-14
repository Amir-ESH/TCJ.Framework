using System.Diagnostics;
using System.Diagnostics.Metrics;
using TCJ.Core.Diagnostics;
using TCJ.Core.Resilience;
using TCJ.Resilience.Tests.Infrastructure;

namespace TCJ.Resilience.Tests;

public sealed class ResilienceTelemetryTests : IDisposable
{
    public ResilienceTelemetryTests() => TcjTelemetry.ResetForTests();

    [Fact]
    [Trait("Category", "Retry")]
    public async Task Retry_telemetry_records_attempts_and_retry_with_bounded_dimensions_without_sensitive_messages()
    {
        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TcjDiagnosticNames.Sources.Core,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(activityListener);

        var measurements = new List<MeasurementRecord>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == TcjDiagnosticNames.Sources.Core &&
                instrument.Name.StartsWith("tcj.resilience.", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            measurements.Add(new MeasurementRecord(instrument.Name, measurement, tags.ToArray())));
        meterListener.Start();

        const string secret = "TCJ_RESILIENCE_SECRET_MARKER";
        int attempts = 0;
        var policy = new TcjRetryPolicy(
            new TransientFailureDetector([new InjectedTransientClassifier()]),
            new TcjRetryOptions
            {
                MaxRetryAttempts = 1,
                BaseDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                UseJitter = false
            });

        await policy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new InjectedTransientException(secret);
            }

            return Task.FromResult(42);
        }, "telemetry_test");

        Assert.Contains(activities, activity => activity.OperationName == TcjDiagnosticNames.Activities.ResilienceExecute);
        Assert.Contains(activities, activity => activity.OperationName == TcjDiagnosticNames.Activities.ResilienceRetry);
        Assert.DoesNotContain(secret, string.Join('\n', activities.SelectMany(activity => activity.TagObjects).Select(tag => tag.Value?.ToString())), StringComparison.Ordinal);
        Assert.Equal(2, measurements.Where(item => item.Name == TcjDiagnosticNames.Metrics.ResilienceAttempts).Sum(item => item.Value));
        Assert.Equal(1, measurements.Where(item => item.Name == TcjDiagnosticNames.Metrics.ResilienceRetries).Sum(item => item.Value));

        string[] allowedDimensions =
        [
            TcjDiagnosticNames.Tags.ResilienceStrategy,
            TcjDiagnosticNames.Tags.ResilienceOutcome,
            TcjDiagnosticNames.Tags.ResilienceAttempt,
            TcjDiagnosticNames.Tags.ResilienceFailureType,
            TcjDiagnosticNames.Tags.ResilienceCircuitState
        ];
        Assert.All(
            measurements.SelectMany(item => item.Tags),
            tag => Assert.Contains(tag.Key, allowedDimensions));
    }

    [Fact]
    [Trait("Category", "Timeout")]
    public async Task Timeout_telemetry_uses_timeout_category_without_recording_operation_payload()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TcjDiagnosticNames.Sources.Core,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        var fake = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var policy = new TcjTimeoutPolicy(
            new TcjTimeoutOptions { OperationTimeout = TimeSpan.FromSeconds(1) },
            fake);
        Task operation = policy.ExecuteAsync(
            token => Task.Delay(TimeSpan.FromMinutes(1), fake, token),
            "timeout_telemetry");
        fake.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<TcjTimeoutException>(() => operation);
    }

    public void Dispose() => TcjTelemetry.ResetForTests();

    private sealed record MeasurementRecord(
        string Name,
        long Value,
        KeyValuePair<string, object?>[] Tags);
}
