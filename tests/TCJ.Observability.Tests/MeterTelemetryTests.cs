using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TCJ.Core.Diagnostics;
using TCJ.Core.DomainEvents;
using TCJ.DependencyInjection.DomainEvents;

namespace TCJ.Observability.Tests;

public sealed class MeterTelemetryTests : IDisposable
{
    public MeterTelemetryTests() => TcjTelemetry.ResetForTests();

    [Fact]
    public async Task Domain_event_metrics_have_stable_names_units_and_bounded_dimensions()
    {
        var measurements = new ConcurrentQueue<Measurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == TcjDiagnosticNames.Sources.Core)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new Measurement(instrument.Name, instrument.Unit, value, CopyTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue(new Measurement(instrument.Name, instrument.Unit, value, CopyTags(tags))));
        listener.Start();

        var invoker = new DomainEventHandlerInvoker<TestEvent>(
            new IDomainEventHandler<TestEvent>[] { new SuccessHandler() });
        await invoker.InvokeAsync(new TestEvent(DateTimeOffset.UtcNow), CancellationToken.None);

        Measurement dispatched = Assert.Single(
            measurements,
            measurement => measurement.Name == TcjDiagnosticNames.Metrics.DomainEventsDispatched);
        Measurement handlerCompleted = Assert.Single(
            measurements,
            measurement => measurement.Name == TcjDiagnosticNames.Metrics.DomainEventHandlersCompleted);
        Measurement dispatchDuration = Assert.Single(
            measurements,
            measurement => measurement.Name == TcjDiagnosticNames.Metrics.DomainEventDispatchDuration);
        Measurement handlerDuration = Assert.Single(
            measurements,
            measurement => measurement.Name == TcjDiagnosticNames.Metrics.DomainEventHandlerDuration);

        Assert.Equal("{event}", dispatched.Unit);
        Assert.Equal("{event}", handlerCompleted.Unit);
        Assert.Equal("s", dispatchDuration.Unit);
        Assert.Equal("s", handlerDuration.Unit);
        Assert.True(dispatchDuration.Value >= 0);
        Assert.True(handlerDuration.Value >= 0);

        Assert.All(measurements, measurement =>
        {
            Assert.All(measurement.Tags.Keys, key =>
                Assert.Contains(key, new[] { TcjDiagnosticNames.Tags.OperationOutcome }));
        });
    }

    private static IReadOnlyDictionary<string, object?> CopyTags(
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            result[tag.Key] = tag.Value;
        }

        return result;
    }

    public void Dispose() => TcjTelemetry.ResetForTests();

    private sealed record TestEvent(DateTimeOffset OccurredOn) : IDomainEvent;

    private sealed class SuccessHandler : IDomainEventHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed record Measurement(
        string Name,
        string? Unit,
        double Value,
        IReadOnlyDictionary<string, object?> Tags);
}
