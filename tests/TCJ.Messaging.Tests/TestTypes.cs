using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;
using TCJ.Core.Inbox;
using TCJ.Core.Outbox;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;

namespace TCJ.Messaging.Tests;

internal sealed record TestMessage(string Value);
internal sealed record TestMessageV2(string Value, string? Extra);
internal sealed record TestDomainEvent(string Value, DateTimeOffset OccurredOn) : IDomainEvent;

[JsonSerializable(typeof(TestMessage))]
[JsonSerializable(typeof(TestMessageV2))]
[JsonSerializable(typeof(TestDomainEvent))]
internal sealed partial class TestJsonContext : JsonSerializerContext;

internal static class TestServices
{
    public static ServiceProvider Create(Action<TCJ.Messaging.Configuration.TcjMessagingOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging(configure);
        services.AddTcjMessage("test.message", 1, TestJsonContext.Default.TestMessage);
        services.AddTcjInMemoryMessaging();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public static TransportMessageEnvelope Envelope(
        string id = "message-1",
        string type = "test.message",
        int version = 1,
        string body = "{\"value\":\"ok\"}",
        string contentType = "application/json",
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(id, type, version, System.Text.Encoding.UTF8.GetBytes(body), contentType,
            DateTimeOffset.Parse("2026-09-02T00:00:00Z"), headers: headers);
}

internal sealed class RecordingSettlement : IMessageSettlement
{
    public int CompleteCount { get; private set; }
    public int RetryCount { get; private set; }
    public int DeadLetterCount { get; private set; }
    public int AbandonCount { get; private set; }
    public int DeferCount { get; private set; }
    public RetrySettlementOptions? RetryOptions { get; private set; }
    public DeadLetterOptions? DeadLetterOptions { get; private set; }

    public Task CompleteAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); CompleteCount++; return Task.CompletedTask; }
    public Task RetryAsync(RetrySettlementOptions options, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); RetryOptions = options; RetryCount++; return Task.CompletedTask; }
    public Task DeadLetterAsync(DeadLetterOptions options, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); DeadLetterOptions = options; DeadLetterCount++; return Task.CompletedTask; }
    public Task AbandonAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); AbandonCount++; return Task.CompletedTask; }
    public Task DeferAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); DeferCount++; return Task.CompletedTask; }
}

internal sealed class StubInboxPipeline : IInboxPipeline
{
    private readonly Func<IncomingMessageEnvelope, CancellationToken, Task<InboxHandlingResult>> _handler;
    public StubInboxPipeline(Func<IncomingMessageEnvelope, CancellationToken, Task<InboxHandlingResult>> handler) => _handler = handler;
    public IncomingMessageEnvelope? LastEnvelope { get; private set; }
    public async Task<InboxHandlingResult> ProcessAsync(IncomingMessageEnvelope envelope, CancellationToken cancellationToken = default)
    { LastEnvelope = envelope; return await _handler(envelope, cancellationToken); }
}

internal sealed class StubDispatcher : IDomainEventDispatcher
{
    public int Calls { get; private set; }
    public Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.CompletedTask; }
}

internal sealed class StubOutboxContextAccessor : IOutboxMessageContextAccessor
{
    public OutboxMessageContext? Current { get; set; }
}

internal sealed class V1ToV2Upcaster : TCJ.Messaging.Serialization.IMessageUpcaster
{
    public string MessageType => "test.upcast";
    public int SourceVersion => 1;
    public int TargetVersion => 2;
    public ReadOnlyMemory<byte> Upcast(ReadOnlyMemory<byte> payload)
    {
        using JsonDocument doc = JsonDocument.Parse(payload);
        string value = doc.RootElement.GetProperty("value").GetString()!;
        return JsonSerializer.SerializeToUtf8Bytes(new TestMessageV2(value, "upcast"), TestJsonContext.Default.TestMessageV2);
    }
}


internal sealed class StubMessagingStartupValidator : TCJ.Messaging.Configuration.IMessagingStartupValidator
{
    public Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
