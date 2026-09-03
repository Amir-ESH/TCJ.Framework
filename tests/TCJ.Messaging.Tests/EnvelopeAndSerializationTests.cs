using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Serialization;

namespace TCJ.Messaging.Tests;

public sealed class EnvelopeAndSerializationTests
{
    [Fact]
    public void Typed_envelope_requires_stable_id() =>
        Assert.Throws<ArgumentException>(() => new MessageEnvelope<TestMessage>("", "test.message", 1, new("x"), DateTimeOffset.UtcNow));

    [Fact]
    public void Typed_envelope_rejects_non_positive_version() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MessageEnvelope<TestMessage>("id", "test.message", 0, new("x"), DateTimeOffset.UtcNow));

    [Fact]
    public void Typed_envelope_rejects_assembly_qualified_type_name() =>
        Assert.Throws<ArgumentException>(() => new MessageEnvelope<TestMessage>("id", "A.Type, Assembly", 1, new("x"), DateTimeOffset.UtcNow));

    [Fact]
    public void Typed_envelope_copies_headers()
    {
        var headers = new Dictionary<string, string> { ["tenant"] = "a" };
        var envelope = new MessageEnvelope<TestMessage>("id", "test.message", 1, new("x"), DateTimeOffset.UtcNow, headers: headers);
        headers["tenant"] = "b";
        Assert.Equal("a", envelope.Headers["tenant"]);
    }

    [Fact]
    public void Raw_envelope_does_not_copy_body()
    {
        byte[] body = [1, 2, 3];
        var envelope = new TransportMessageEnvelope("id", "test.message", 1, body, "application/json", DateTimeOffset.UtcNow);
        body[0] = 9;
        Assert.Equal(9, envelope.Body.Span[0]);
    }

    [Fact]
    public void Raw_envelope_accepts_syntactically_valid_unknown_content_type()
    {
        TransportMessageEnvelope envelope = TestServices.Envelope(contentType: "application/xml");
        Assert.Equal("application/xml", envelope.ContentType);
    }

    [Fact]
    public void Header_policy_removes_forbidden_headers()
    {
        var policy = new MessagingHeaderPolicy(new TcjMessagingOptions());
        IReadOnlyDictionary<string, string> filtered = policy.Filter(new Dictionary<string, string>
        {
            ["authorization"] = "Bearer secret",
            ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
        });
        Assert.DoesNotContain("authorization", filtered.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("traceparent", filtered.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Header_policy_removes_malformed_traceparent()
    {
        var policy = new MessagingHeaderPolicy(new TcjMessagingOptions());
        IReadOnlyDictionary<string, string> filtered = policy.Filter(new Dictionary<string, string> { ["traceparent"] = "invalid" });
        Assert.False(filtered.ContainsKey("traceparent"));
    }

    [Fact]
    public void Header_policy_drops_tracestate_without_traceparent()
    {
        var policy = new MessagingHeaderPolicy(new TcjMessagingOptions());
        IReadOnlyDictionary<string, string> filtered = policy.Filter(new Dictionary<string, string> { ["tracestate"] = "vendor=value" });
        Assert.False(filtered.ContainsKey("tracestate"));
    }

    [Fact]
    public void Additional_allowlist_rejects_sensitive_header()
    {
        var options = new TcjMessagingOptions();
        options.AdditionalAllowedHeaders.Add("x-secret-value");
        Assert.Throws<ArgumentException>(() => new MessagingHeaderPolicy(options));
    }

    [Fact]
    public void Serializer_uses_explicit_contract()
    {
        using ServiceProvider provider = TestServices.Create();
        IMessageSerializer serializer = provider.GetRequiredService<IMessageSerializer>();
        IMessageContractRegistry registry = provider.GetRequiredService<IMessageContractRegistry>();
        var envelope = new MessageEnvelope<TestMessage>("id", "test.message", 1, new("value"), DateTimeOffset.UtcNow);
        TransportMessageEnvelope raw = serializer.Serialize(envelope, registry.Resolve(typeof(TestMessage), "test.message", 1));
        TestMessage result = Assert.IsType<TestMessage>(serializer.Deserialize(raw, registry.Resolve("test.message", 1)));
        Assert.Equal("value", result.Value);
    }

    [Fact]
    public void Serializer_rejects_unknown_content_type()
    {
        using ServiceProvider provider = TestServices.Create();
        IMessageSerializer serializer = provider.GetRequiredService<IMessageSerializer>();
        IMessageContractRegistry registry = provider.GetRequiredService<IMessageContractRegistry>();
        Assert.Throws<NotSupportedException>(() => serializer.Deserialize(TestServices.Envelope(contentType: "application/xml"), registry.Resolve("test.message", 1)));
    }

    [Fact]
    public void Registry_rejects_duplicate_wire_contracts()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        services.AddTcjMessage("test.message", 1, TestJsonContext.Default.TestMessage);
        services.AddTcjMessage("test.message", 1, TestJsonContext.Default.TestMessage);
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMessageContractRegistry>());
    }

    [Fact]
    public void Upcaster_advances_payload_to_registered_target_version()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        services.AddTcjMessage("test.upcast", 2, TestJsonContext.Default.TestMessageV2);
        services.AddTcjMessageUpcaster<V1ToV2Upcaster>();
        using ServiceProvider provider = services.BuildServiceProvider();
        IMessageSerializer serializer = provider.GetRequiredService<IMessageSerializer>();
        IMessageContractRegistry registry = provider.GetRequiredService<IMessageContractRegistry>();
        var raw = new TransportMessageEnvelope("id", "test.upcast", 1, System.Text.Encoding.UTF8.GetBytes("{\"value\":\"old\"}"), "application/json", DateTimeOffset.UtcNow);
        TestMessageV2 result = Assert.IsType<TestMessageV2>(serializer.Deserialize(raw, registry.Resolve("test.upcast", 2)));
        Assert.Equal("upcast", result.Extra);
    }

    [Fact]
    public void Missing_upcaster_chain_fails_closed()
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        services.AddTcjMessage("test.upcast", 2, TestJsonContext.Default.TestMessageV2);
        using ServiceProvider provider = services.BuildServiceProvider();
        IMessageSerializer serializer = provider.GetRequiredService<IMessageSerializer>();
        IMessageContractRegistry registry = provider.GetRequiredService<IMessageContractRegistry>();
        var raw = new TransportMessageEnvelope("id", "test.upcast", 1, System.Text.Encoding.UTF8.GetBytes("{\"value\":\"old\"}"), "application/json", DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => serializer.Deserialize(raw, registry.Resolve("test.upcast", 2)));
    }
}
