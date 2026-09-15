using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;

namespace TCJ.Messaging.Sagas.Tests;

public sealed class SagaDefinitionTests
{
    [Fact]
    public void Registration_requires_stable_positive_explicit_contracts()
    {
        var services = NewServices();
        Assert.ThrowsAny<ArgumentException>(() => services.AddTcjSaga<TestDbContext, OrderSaga, OrderState>("", 1, 1, TestJsonContext.Default.OrderState, static () => new(), ValidDefinition));
        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddTcjSaga<TestDbContext, OrderSaga, OrderState>("orders", 0, 1, TestJsonContext.Default.OrderState, static () => new(), ValidDefinition));
        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddTcjSaga<TestDbContext, OrderSaga, OrderState>("orders", 1, 0, TestJsonContext.Default.OrderState, static () => new(), ValidDefinition));
    }

    [Fact]
    public void Duplicate_saga_type_fails_registration()
    {
        var services = NewServices();
        services.AddTcjSaga<TestDbContext, OrderSaga, OrderState>("orders", 1, 1, TestJsonContext.Default.OrderState, static () => new(), ValidDefinition);
        Assert.Throws<InvalidOperationException>(() => services.AddTcjSaga<TestDbContext, OtherOrderSaga, OrderState>("orders", 1, 1, TestJsonContext.Default.OrderState, static () => new(), builder =>
        {
            builder.TerminalStates("completed", "failed", "compensating", "compensated")
                .StartsWith<OrderStarted>("order.started", 1, "started", "orderId", static message => SagaCorrelationKey.From(message.OrderId));
        }));
    }

    [Fact]
    public void Duplicate_message_handler_across_sagas_is_rejected()
    {
        var services = NewServices();
        services.AddTcjSaga<TestDbContext, OrderSaga, OrderState>("orders", 1, 1, TestJsonContext.Default.OrderState, static () => new(), ValidDefinition);
        Assert.Throws<InvalidOperationException>(() => services.AddTcjSaga<TestDbContext, OtherOrderSaga, OrderState>("other-orders", 1, 1, TestJsonContext.Default.OrderState, static () => new(), builder =>
        {
            builder.TerminalStates("completed", "failed", "compensating", "compensated")
                .StartsWith<OrderStarted>("order.started", 1, "started", "orderId", static message => SagaCorrelationKey.From(message.OrderId));
        }));
    }

    [Fact]
    public void Conflicting_start_and_continue_correlation_names_are_rejected()
    {
        var services = NewServices();
        Assert.Throws<InvalidOperationException>(() => services.AddTcjSaga<TestDbContext, SameMessageSaga, OrderState>("same-message", 1, 1, TestJsonContext.Default.OrderState, static () => new(), builder =>
        {
            builder.TerminalStates("completed", "failed", "compensating", "compensated")
                .StartsWith<OrderStarted>("order.started", 1, "started", "orderId", static message => SagaCorrelationKey.From(message.OrderId))
                .Handles<OrderStarted>("order.started", 1, "different", static message => SagaCorrelationKey.From(message.OrderId), ["started"]);
        }));
    }

    [Fact]
    public void Incomplete_state_migration_graph_fails_startup_definition_validation()
    {
        var services = NewServices();
        Assert.Throws<InvalidOperationException>(() => services.AddTcjSaga<TestDbContext, OrderSaga, OrderState>("migration", 3, 3, TestJsonContext.Default.OrderState, static () => new(), builder =>
        {
            builder.TerminalStates("completed", "failed", "compensating", "compensated")
                .StartsWith<OrderStarted>("order.started.v3", 1, "started", "orderId", static message => SagaCorrelationKey.From(message.OrderId))
                .SupportsDefinitionVersion(1)
                .AddStateMigrator<OneToTwoMigrator>(1, 2);
        }));
    }

    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddTcjSagas<TestDbContext>();
        return services;
    }

    private static void ValidDefinition(SagaDefinitionBuilder<OrderSaga, OrderState> builder) => builder
        .TerminalStates("completed", "failed", "compensating", "compensated")
        .StartsWith<OrderStarted>("order.started", 1, "started", "orderId", static message => SagaCorrelationKey.From(message.OrderId))
        .Handles<OrderContinued>("order.continued", 1, "orderId", static message => SagaCorrelationKey.From(message.OrderId), ["started"])
        .HandlesTimeout("payment", "order.payment.timeout", ["started"])
        .Compensates();
}

internal sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options), IReadDbContext, IWriteDbContext;
internal sealed class OrderState : ISagaState { public Guid OrderId { get; set; } public int Step { get; set; } }
internal sealed record OrderStarted(Guid OrderId);
internal sealed record OrderContinued(Guid OrderId);

internal sealed class OrderSaga : ISagaStartsWith<OrderState, OrderStarted>, ISagaHandles<OrderState, OrderContinued>, ISagaHandlesTimeout<OrderState>, ISagaCompensates<OrderState>
{
    public Task HandleAsync(OrderState state, OrderStarted message, SagaContext context, CancellationToken cancellationToken = default) { state.OrderId = message.OrderId; context.Stay(); return Task.CompletedTask; }
    public Task HandleAsync(OrderState state, OrderContinued message, SagaContext context, CancellationToken cancellationToken = default) { state.Step++; context.Complete(); return Task.CompletedTask; }
    public Task HandleTimeoutAsync(OrderState state, SagaTimeout timeout, SagaContext context, CancellationToken cancellationToken = default) { context.Fail(); return Task.CompletedTask; }
    public Task CompensateAsync(OrderState state, SagaContext context, CancellationToken cancellationToken = default) { context.CompleteCompensation(); return Task.CompletedTask; }
}
internal sealed class OtherOrderSaga : ISagaStartsWith<OrderState, OrderStarted>
{
    public Task HandleAsync(OrderState state, OrderStarted message, SagaContext context, CancellationToken cancellationToken = default) { context.Stay(); return Task.CompletedTask; }
}
internal sealed class SameMessageSaga : ISagaStartsWith<OrderState, OrderStarted>, ISagaHandles<OrderState, OrderStarted>
{
    public Task HandleAsync(OrderState state, OrderStarted message, SagaContext context, CancellationToken cancellationToken = default) { context.Stay(); return Task.CompletedTask; }
}
internal sealed class OneToTwoMigrator : TCJ.Messaging.Sagas.Migration.ISagaStateMigrator
{
    public int FromVersion => 1;
    public int ToVersion => 2;
    public string Migrate(string payload) => payload;
}

[JsonSerializable(typeof(OrderState))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
