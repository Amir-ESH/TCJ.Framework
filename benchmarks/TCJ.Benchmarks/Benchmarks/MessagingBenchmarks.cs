using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Serialization;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Messaging", "Messaging")]
public class MessagingBenchmarks
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.UnixEpoch;
    private ServiceProvider _provider = null!;
    private IMessageSerializer _serializer = null!;
    private MessagingMessageContract _contract = null!;
    private MessagingHeaderPolicy _headerPolicy = null!;
    private MessageEnvelope<BenchmarkMessage> _typed = null!;
    private TransportMessageEnvelope _transport = null!;
    private IReadOnlyDictionary<string, string> _headers = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging(options => options.AdditionalAllowedHeaders.Add("tenant"));
        services.AddTcjMessage("benchmark.message", 1, MessagingBenchmarkJsonContext.Default.BenchmarkMessage);
        services.AddTcjInMemoryMessaging();
        _provider = services.BuildServiceProvider();
        _serializer = _provider.GetRequiredService<IMessageSerializer>();
        _contract = _provider.GetRequiredService<IMessageContractRegistry>().Resolve("benchmark.message", 1);
        _headerPolicy = _provider.GetRequiredService<MessagingHeaderPolicy>();
        _headers = new Dictionary<string, string>
        {
            ["tenant"] = "north",
            ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            ["authorization"] = "filtered"
        };
        _typed = CreateEnvelope();
        _transport = _serializer.Serialize(_typed, _contract);
    }

    [Benchmark(Baseline = true)]
    public MessageEnvelope<BenchmarkMessage> CreateEnvelope() =>
        new(
            "00000000000000000000000000000001",
            "benchmark.message",
            1,
            new BenchmarkMessage("value", 42),
            CreatedAt,
            "correlation",
            "causation",
            headers: _headers);

    [Benchmark]
    public TransportMessageEnvelope Serialize() => _serializer.Serialize(_typed, _contract);

    [Benchmark]
    public object Deserialize() => _serializer.Deserialize(_transport, _contract);

    [Benchmark]
    public IReadOnlyDictionary<string, string> FilterHeaders() => _headerPolicy.Filter(_headers);

    [GlobalCleanup]
    public void GlobalCleanup() => _provider.Dispose();
}

public sealed record BenchmarkMessage(string Value, int Number);

[JsonSerializable(typeof(BenchmarkMessage))]
internal sealed partial class MessagingBenchmarkJsonContext : JsonSerializerContext;
