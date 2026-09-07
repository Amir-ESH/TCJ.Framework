using System.Diagnostics;
using System.Text;
using Azure.Messaging.ServiceBus;
using BenchmarkDotNet.Attributes;
using TCJ.Messaging.AzureServiceBus.Configuration;
using TCJ.Messaging.AzureServiceBus.Diagnostics;
using TCJ.Messaging.AzureServiceBus.Publishing;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Publishing;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Messaging.AzureServiceBus", "Messaging", "AzureServiceBus")]
public class AzureServiceBusBenchmarks
{
    private AzureServiceBusMessageMapper _mapper = null!;
    private TransportMessageEnvelope _message = null!;
    private PublishContext _publish = null!;
    private PublishContext _scheduled = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var messaging = new TcjMessagingOptions();
        _mapper = new AzureServiceBusMessageMapper(
            new MessagingHeaderPolicy(messaging),
            new TcjAzureServiceBusOptions
            {
                ConnectionString = "Endpoint=sb://localhost/;SharedAccessKeyName=local;SharedAccessKey=local"
            },
            TimeProvider.System);
        _message = new TransportMessageEnvelope(
            "00000000000000000000000000000001",
            "benchmark.azure-service-bus",
            1,
            Encoding.UTF8.GetBytes("{\"value\":42}"),
            "application/json",
            DateTimeOffset.UtcNow,
            correlationId: "correlation",
            causationId: "causation",
            headers: new Dictionary<string, string>
            {
                ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
            });
        _publish = new PublishContext { Destination = "orders", TimeToLive = TimeSpan.FromMinutes(5) };
        _scheduled = new PublishContext { Destination = "orders", ScheduledAtUtc = DateTimeOffset.UtcNow.AddHours(1) };
    }

    [Benchmark(Baseline = true)]
    public ServiceBusMessage MapPublishMessage() => _mapper.ToServiceBusMessage(_message, _publish);

    [Benchmark]
    public ServiceBusMessage MapScheduledPublishMessage() => _mapper.ToServiceBusMessage(_message, _scheduled);

    [Benchmark]
    public ActivityContext ExtractTraceContext() => AzureServiceBusDiagnostics.ExtractParent(_message);

    [Benchmark]
    public void TelemetryDisabled()
    {
        using System.Diagnostics.Activity? activity = AzureServiceBusDiagnostics.Start(
            TcjAzureServiceBusDiagnosticNames.PublishActivity,
            "publish",
            "orders",
            message: _message);
        AzureServiceBusDiagnostics.MessagePublished();
    }
}
