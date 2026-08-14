using System.Diagnostics;
using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Diagnostics;
using TCJ.Core.DomainEvents;
using TCJ.DependencyInjection.DomainEvents;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Core", "TCJ.DependencyInjection", "Observability")]
public class ObservabilityBenchmarks
{
    private readonly IReadOnlyCollection<IDomainEvent> _events =
        [new BenchmarkDomainEvent(DateTimeOffset.UnixEpoch)];

    private ServiceProvider _provider = null!;
    private IDomainEventDispatcher _dispatcher = null!;
    private ActivityListener _activityListener = null!;
    private MeterListener _meterListener = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventDispatcher, DomainEventDispatcher>();
        services.AddSingleton<IDomainEventHandler<BenchmarkDomainEvent>>(new BenchmarkDomainEventHandler());
        _provider = services.BuildServiceProvider();
        _dispatcher = _provider.GetRequiredService<IDomainEventDispatcher>();

        _activityListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name.StartsWith("TCJ.", StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name.StartsWith("TCJ.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _meterListener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        _meterListener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        _meterListener.Start();
    }

    [IterationSetup(Target = nameof(TelemetryDisabled))]
    public void DisableTelemetry() => Configure(tracing: false, metrics: false);

    [IterationSetup(Target = nameof(TracingListenerEnabled))]
    public void EnableTracingOnly() => Configure(tracing: true, metrics: false);

    [IterationSetup(Target = nameof(MetricsListenerEnabled))]
    public void EnableMetricsOnly() => Configure(tracing: false, metrics: true);

    [IterationSetup(Target = nameof(TracingAndMetricsEnabled))]
    public void EnableTracingAndMetrics() => Configure(tracing: true, metrics: true);

    [Benchmark(Baseline = true)]
    public void TelemetryDisabled() => Dispatch();

    [Benchmark]
    public void TracingListenerEnabled() => Dispatch();

    [Benchmark]
    public void MetricsListenerEnabled() => Dispatch();

    [Benchmark]
    public void TracingAndMetricsEnabled() => Dispatch();

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
        _provider.Dispose();
        Configure(tracing: true, metrics: true);
    }

    private void Dispatch() =>
        _dispatcher.DispatchAsync(_events, CancellationToken.None).GetAwaiter().GetResult();

    private static void Configure(bool tracing, bool metrics) =>
        TcjTelemetry.Configure(options =>
        {
            options.EnableTracing = tracing;
            options.EnableMetrics = metrics;
            options.RecordExceptionMessages = false;
            options.RecordEntityTypeNames = true;
            options.RecordHandlerTypeNames = true;
        });

    private sealed record BenchmarkDomainEvent(DateTimeOffset OccurredOn) : IDomainEvent;

    private sealed class BenchmarkDomainEventHandler : IDomainEventHandler<BenchmarkDomainEvent>
    {
        public Task HandleAsync(BenchmarkDomainEvent domainEvent, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
