using TCJ.Core.DomainEvents;

namespace TCJ.Messaging.Sagas.Tests;

public sealed class SagaContextTests
{
    [Fact]
    public void Correlation_key_is_bounded_and_redacted()
    {
        SagaCorrelationKey key = SagaCorrelationKey.From("Customer-ABC");
        Assert.Equal("[redacted-correlation]", key.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => SagaCorrelationKey.From(new string('x', 257)));
    }

    [Fact]
    public void Lifecycle_is_explicit_and_only_one_terminal_action_is_allowed()
    {
        SagaContext context = CreateContext();
        context.Complete();
        Assert.Equal(SagaLifecycleAction.Complete, context.LifecycleAction);
        Assert.Throws<InvalidOperationException>(() => context.Fail());
    }

    [Fact]
    public void Timer_deadline_must_be_future_and_output_is_outbox_compatible_domain_event()
    {
        SagaContext context = CreateContext();
        Assert.Throws<ArgumentOutOfRangeException>(() => context.ScheduleTimer("payment", context.UtcNow));
        context.ScheduleTimer("payment", context.UtcNow.AddMinutes(1));
        context.Emit(new TestEvent(context.UtcNow));
        Assert.Single(context.TimerMutations);
        Assert.Single(context.OutgoingEvents);
    }

    [Fact]
    public void Compensation_completion_is_explicit_business_action()
    {
        SagaContext context = CreateContext();
        context.CompleteCompensation();
        Assert.Equal(SagaLifecycleAction.CompleteCompensation, context.LifecycleAction);
    }

    private static SagaContext CreateContext() => new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "orders", 1, "started", "m1", "order.started", 1, 1, "corr-sensitive", "cause", new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero));
    private sealed record TestEvent(DateTimeOffset OccurredOn) : IDomainEvent;
}
