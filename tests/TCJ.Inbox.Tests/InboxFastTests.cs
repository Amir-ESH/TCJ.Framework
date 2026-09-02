using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Inbox;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Inbox.Extensions;
using TCJ.EntityFrameworkCore.Inbox.Serialization;

namespace TCJ.Inbox.Tests;

public sealed class InboxFastTests
{
    [Fact]
    public void Envelope_requires_stable_message_id() => Assert.Throws<ArgumentException>(() => new IncomingMessageEnvelope("", "test.command", 1, "orders-api", "{}", DateTimeOffset.UtcNow));

    [Fact]
    public void Envelope_normalizes_received_time_to_utc()
    {
        var envelope = new IncomingMessageEnvelope("id", "test.command", 1, "orders-api", "{}", new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.FromHours(4)));
        Assert.Equal(TimeSpan.Zero, envelope.ReceivedAtUtc.Offset);
        Assert.Equal(4, envelope.ReceivedAtUtc.Hour);
    }

    [Fact]
    public void Envelope_copies_headers_immutably()
    {
        var headers = new Dictionary<string, string> { ["traceparent"] = "value" };
        var envelope = new IncomingMessageEnvelope("id", "test.command", 1, "orders-api", "{}", DateTimeOffset.UtcNow, headers: headers);
        headers["traceparent"] = "changed";
        Assert.Equal("value", envelope.Headers["traceparent"]);
    }

    [Fact]
    public void Deferred_mode_requires_payload_retention()
    {
        var options = new TcjInboxOptions { ConsumerName = "orders-api", ProcessingMode = InboxProcessingMode.Deferred, StorePayload = false };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Consumer_name_is_bounded_contract() => Assert.Throws<ArgumentException>(() => new TcjInboxOptions { ConsumerName = "orders api" }.Validate());

    [Fact]
    public void Sensitive_headers_cannot_be_added_to_persistence_allowlist()
    {
        var options = new TcjInboxOptions { ConsumerName = "orders-api" };
        options.HeaderAllowlist.Add("authorization");
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Repeated_identical_message_registration_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddTcjInboxMessage<TestInboxCommand>("test.command", 1);
        services.AddTcjInboxMessage<TestInboxCommand>("test.command", 1);
        Assert.Single(services.Where(descriptor => descriptor.ServiceType.Name == "InboxMessageRegistration"));
    }

    [Fact]
    public void Duplicate_wire_contract_with_different_clr_type_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddTcjInboxMessage<TestInboxCommand>("test.command", 1);
        Assert.Throws<InvalidOperationException>(() => services.AddTcjInboxMessage<OtherCommand>("test.command", 1));
    }

    [Fact]
    public void Ambiguous_handler_registration_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddTcjInboxHandler<TestInboxCommand, TestHandlerA>();
        Assert.Throws<InvalidOperationException>(() => services.AddTcjInboxHandler<TestInboxCommand, TestHandlerB>());
    }

    [Fact]
    public void Default_serializer_uses_registered_target_type_only()
    {
        var options = new TcjInboxOptions { ConsumerName = "orders-api" };
        var serializer = new SystemTextJsonInboxSerializer(options);
        var value = Assert.IsType<TestInboxCommand>(serializer.Deserialize(typeof(TestInboxCommand), "{\"value\":\"ok\"}"));
        Assert.Equal("ok", value.Value);
    }

    [Fact]
    public void Custom_serializer_registration_is_preserved()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInboxSerializer, StubSerializer>();
        services.AddTcjInbox<FastDbContext>(options => options.ConsumerName = "orders-api");
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<StubSerializer>(provider.GetRequiredService<IInboxSerializer>());
    }

    private sealed record OtherCommand(string Value);
    private sealed class TestHandlerA : IInboxMessageHandler<TestInboxCommand> { public Task HandleAsync(TestInboxCommand message, InboxMessageContext context, CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class TestHandlerB : IInboxMessageHandler<TestInboxCommand> { public Task HandleAsync(TestInboxCommand message, InboxMessageContext context, CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class StubSerializer : IInboxSerializer { public object Deserialize(Type messageType, string payload) => new TestInboxCommand("stub"); }
    private sealed class FastDbContext(DbContextOptions<FastDbContext> options) : DbContext(options), IReadDbContext, IWriteDbContext;
}
