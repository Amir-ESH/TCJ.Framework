using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Inbox;
using TCJ.EntityFrameworkCore.Inbox;
using TCJ.EntityFrameworkCore.Outbox;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Persistence;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Processing;
using TCJ.Messaging.Sagas.Processing;
using TCJ.Messaging.Sagas.Remediation;

namespace TCJ.Messaging.Sagas.SqlServer.Tests;

[Collection(SagaSqlServerCollection.Name)]
[Trait("Category", "SqlServer")]
public sealed class DurableSagaSqlServerTests(SagaSqlServerFixture fixture)
{
    [Fact]
    public async Task Start_commits_inbox_saga_business_timer_and_outbox_atomically_and_duplicate_is_idempotent()
    {
        await fixture.ResetAsync();
        Guid orderId = Guid.NewGuid();
        IncomingMessageEnvelope envelope = fixture.Envelope("start-1", "order.start", new StartOrder(orderId));

        InboxHandlingResult first = await Pipeline().ProcessAsync(envelope);
        InboxHandlingResult duplicate = await Pipeline().ProcessAsync(envelope);

        Assert.Equal(InboxHandlingOutcome.Acknowledge, first.Outcome);
        Assert.Equal(InboxHandlingOutcome.IgnoreDuplicate, duplicate.Outcome);
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        SagaTestDbContext db = scope.ServiceProvider.GetRequiredService<SagaTestDbContext>();
        Assert.Equal(1, await db.Set<InboxMessage>().CountAsync());
        Assert.Equal(1, await db.Set<SagaInstance>().CountAsync());
        Assert.Equal(1, await db.Set<SagaCorrelation>().CountAsync());
        Assert.Equal(1, await db.Set<SagaTimer>().CountAsync());
        Assert.Equal(1, await db.BusinessRows.CountAsync());
        Assert.Equal(1, await db.Set<OutboxMessage>().CountAsync());
        Assert.DoesNotContain(orderId.ToString("D"), (await db.Set<SagaCorrelation>().SingleAsync()).ValueHash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handler_failure_rolls_back_saga_business_timer_correlation_and_outbox_state()
    {
        await fixture.ResetAsync();
        Guid orderId = Guid.NewGuid();
        InboxHandlingResult result = await Pipeline().ProcessAsync(fixture.Envelope("start-rollback", "order.start", new StartOrder(orderId, FailAfterMutation: true)));

        Assert.Equal(InboxHandlingOutcome.DeadLetter, result.Outcome);
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        SagaTestDbContext db = scope.ServiceProvider.GetRequiredService<SagaTestDbContext>();
        Assert.Equal(0, await db.Set<SagaInstance>().CountAsync());
        Assert.Equal(0, await db.Set<SagaCorrelation>().CountAsync());
        Assert.Equal(0, await db.Set<SagaTimer>().CountAsync());
        Assert.Equal(0, await db.BusinessRows.CountAsync());
        Assert.Equal(0, await db.Set<OutboxMessage>().CountAsync());
        Assert.Equal(InboxMessageStatus.DeadLettered, (await db.Set<InboxMessage>().SingleAsync()).Status);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task Concurrent_transitions_use_rowversion_retry_without_lost_update_or_duplicate_committed_outbox()
    {
        await fixture.ResetAsync();
        Guid orderId = Guid.NewGuid();
        Assert.Equal(InboxHandlingOutcome.Acknowledge, (await Pipeline().ProcessAsync(fixture.Envelope("race-start", "order.start", new StartOrder(orderId)))).Outcome);
        fixture.Behavior.EnableTwoPartyRace();
        IncomingMessageEnvelope first = fixture.Envelope("race-1", "order.increment", new IncrementOrder(orderId));
        IncomingMessageEnvelope second = fixture.Envelope("race-2", "order.increment", new IncrementOrder(orderId));

        InboxHandlingResult[] race = await Task.WhenAll(Pipeline().ProcessAsync(first), Pipeline().ProcessAsync(second));
        fixture.Behavior.DisableRace();
        Assert.Equal(1, race.Count(x => x.Outcome == InboxHandlingOutcome.Acknowledge));
        Assert.Equal(1, race.Count(x => x.Outcome == InboxHandlingOutcome.Retry));
        Assert.Contains(race, x => x.FailureType == InboxFailureType.ConcurrencyConflict);

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        IncomingMessageEnvelope retryEnvelope = race[0].Outcome == InboxHandlingOutcome.Retry ? first : second;
        Assert.Equal(InboxHandlingOutcome.Acknowledge, (await Pipeline().ProcessAsync(retryEnvelope)).Outcome);

        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        SagaTestDbContext db = scope.ServiceProvider.GetRequiredService<SagaTestDbContext>();
        SagaInstance saga = await db.Set<SagaInstance>().SingleAsync();
        OrderSagaState state = System.Text.Json.JsonSerializer.Deserialize(saga.StatePayload, SagaSqlJsonContext.Default.OrderSagaState)!;
        Assert.Equal(2, state.Step);
        Assert.Equal(3, await db.Set<OutboxMessage>().CountAsync());
        Assert.Equal("step-2", (await db.BusinessRows.SingleAsync()).Value);
    }

    [Fact]
    public async Task Due_timer_failure_rolls_back_transition_and_is_retried_with_bounded_attempts()
    {
        await fixture.ResetAsync();
        Guid orderId = Guid.NewGuid();
        await Pipeline().ProcessAsync(fixture.Envelope("timer-start", "order.start", new StartOrder(orderId)));
        fixture.Time.Advance(TimeSpan.FromMinutes(2));
        fixture.Behavior.FailNextTimeout();

        SagaTimerProcessingResult first = await TimerProcessor().ProcessBatchAsync();
        Assert.Equal(1, first.RetryScheduledCount);
        await using (AsyncServiceScope scope = fixture.Provider.CreateAsyncScope())
        {
            SagaTestDbContext db = scope.ServiceProvider.GetRequiredService<SagaTestDbContext>();
            Assert.Equal(SagaStatus.Active, (await db.Set<SagaInstance>().SingleAsync()).Status);
            Assert.Equal(1, await db.Set<OutboxMessage>().CountAsync());
            SagaTimer timer = await db.Set<SagaTimer>().SingleAsync();
            Assert.Equal(SagaTimerStatus.Scheduled, timer.Status);
            Assert.Equal(1, timer.AttemptCount);
        }

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        SagaTimerProcessingResult second = await TimerProcessor().ProcessBatchAsync();
        Assert.Equal(1, second.CompletedCount);
        await using AsyncServiceScope verify = fixture.Provider.CreateAsyncScope();
        SagaTestDbContext verifyDb = verify.ServiceProvider.GetRequiredService<SagaTestDbContext>();
        Assert.Equal(SagaStatus.Failed, (await verifyDb.Set<SagaInstance>().SingleAsync()).Status);
        Assert.Equal(SagaTimerStatus.Completed, (await verifyDb.Set<SagaTimer>().SingleAsync()).Status);
        Assert.Equal(2, await verifyDb.Set<OutboxMessage>().CountAsync());
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task Timer_claim_is_atomic_between_workers_and_expired_lease_recovers()
    {
        await fixture.ResetAsync();
        Guid orderId = Guid.NewGuid();
        await Pipeline().ProcessAsync(fixture.Envelope("lease-start", "order.start", new StartOrder(orderId)));
        fixture.Time.Advance(TimeSpan.FromMinutes(2));

        SagaTimerClaim claim;
        await using (AsyncServiceScope scope = fixture.Provider.CreateAsyncScope())
        {
            IReadOnlyList<SagaTimerClaim> claims = await scope.ServiceProvider.GetRequiredService<ISagaProviderStorage>().ClaimDueTimersAsync(fixture.Time.GetUtcNow(), default);
            claim = Assert.Single(claims);
        }
        await using (AsyncServiceScope competing = fixture.Provider.CreateAsyncScope())
        {
            Assert.Empty(await competing.ServiceProvider.GetRequiredService<ISagaProviderStorage>().ClaimDueTimersAsync(fixture.Time.GetUtcNow(), default));
        }

        fixture.Time.Advance(TimeSpan.FromSeconds(6));
        SagaTimerProcessingResult recovered = await TimerProcessor().ProcessBatchAsync();
        Assert.Equal(1, recovered.CompletedCount);
        await using AsyncServiceScope verify = fixture.Provider.CreateAsyncScope();
        SagaTimer timer = await verify.ServiceProvider.GetRequiredService<SagaTestDbContext>().Set<SagaTimer>().SingleAsync();
        Assert.Equal(2, timer.AttemptCount);
        Assert.NotEqual(Guid.Empty, claim.LockId);
    }

    [Fact]
    public async Task Explicit_compensation_is_durable_and_completes_as_new_business_action()
    {
        await fixture.ResetAsync();
        Guid orderId = Guid.NewGuid();
        await Pipeline().ProcessAsync(fixture.Envelope("comp-start", "order.start", new StartOrder(orderId)));
        Assert.Equal(InboxHandlingOutcome.Acknowledge, (await Pipeline().ProcessAsync(fixture.Envelope("comp-request", "order.compensate", new RequestCompensation(orderId)))).Outcome);

        Guid sagaId;
        await using (AsyncServiceScope before = fixture.Provider.CreateAsyncScope())
        {
            SagaInstance saga = await before.ServiceProvider.GetRequiredService<SagaTestDbContext>().Set<SagaInstance>().SingleAsync();
            sagaId = saga.SagaId;
            Assert.Equal(SagaStatus.Compensating, saga.Status);
            Assert.Equal(SagaCompensationStatus.Pending, saga.CompensationStatus);
        }
        SagaCompensationResult result = await fixture.Provider.GetRequiredService<ISagaRemediationService>().RetryCompensationAsync(sagaId);
        Assert.True(result.Completed);

        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        SagaTestDbContext db = scope.ServiceProvider.GetRequiredService<SagaTestDbContext>();
        SagaInstance final = await db.Set<SagaInstance>().SingleAsync();
        Assert.Equal(SagaStatus.Compensated, final.Status);
        Assert.Equal(SagaCompensationStatus.Completed, final.CompensationStatus);
        Assert.Equal(2, await db.Set<OutboxMessage>().CountAsync(x => x.EventType == "saga.started.v1" || x.EventType == "saga.compensated.v1"));
        Assert.NotNull((await db.Set<SagaCorrelation>().SingleAsync()).RemovedAtUtc);
    }

    [Fact]
    public async Task Supported_state_and_definition_version_migrate_transactionally_without_identity_change()
    {
        await fixture.ResetAsync();
        Guid sagaId = Guid.NewGuid();
        Guid orderId = Guid.NewGuid();
        DateTimeOffset now = fixture.Time.GetUtcNow();
        var oldState = new OrderSagaState { OrderId = orderId, Step = 4, SchemaMarker = 1 };
        string payload = System.Text.Json.JsonSerializer.Serialize(oldState, SagaSqlJsonContext.Default.OrderSagaState);
        await using (AsyncServiceScope seed = fixture.Provider.CreateAsyncScope())
        {
            SagaTestDbContext db = seed.ServiceProvider.GetRequiredService<SagaTestDbContext>();
            var instance = new SagaInstance(sagaId, "order-process", 1, 1, "waiting", payload, now);
            db.Set<SagaInstance>().Add(instance);
            db.Set<SagaCorrelation>().Add(new SagaCorrelation(sagaId, "order-process", "orderId", SagaStateRuntime.HashCorrelation(SagaCorrelationKey.From(orderId)), now));
            db.BusinessRows.Add(new SagaBusinessRow(orderId, "legacy"));
            await db.SaveChangesAsync();
        }

        Assert.Equal(InboxHandlingOutcome.Acknowledge, (await Pipeline().ProcessAsync(fixture.Envelope("migration-inc", "order.increment", new IncrementOrder(orderId)))).Outcome);
        await using AsyncServiceScope verify = fixture.Provider.CreateAsyncScope();
        SagaInstance migrated = await verify.ServiceProvider.GetRequiredService<SagaTestDbContext>().Set<SagaInstance>().SingleAsync();
        OrderSagaState state = System.Text.Json.JsonSerializer.Deserialize(migrated.StatePayload, SagaSqlJsonContext.Default.OrderSagaState)!;
        Assert.Equal(sagaId, migrated.SagaId);
        Assert.Equal(2, migrated.DefinitionVersion);
        Assert.Equal(2, migrated.StateSchemaVersion);
        Assert.Equal(2, state.SchemaMarker);
        Assert.Equal(5, state.Step);
    }

    [Fact]
    public async Task Cleanup_is_bounded_and_never_removes_active_saga()
    {
        await fixture.ResetAsync();
        Guid terminalOrder = Guid.NewGuid();
        await Pipeline().ProcessAsync(fixture.Envelope("cleanup-terminal-start", "order.start", new StartOrder(terminalOrder)));
        await Pipeline().ProcessAsync(fixture.Envelope("cleanup-comp", "order.compensate", new RequestCompensation(terminalOrder)));
        Guid terminalSagaId;
        await using (AsyncServiceScope find = fixture.Provider.CreateAsyncScope()) terminalSagaId = (await find.ServiceProvider.GetRequiredService<SagaTestDbContext>().Set<SagaInstance>().SingleAsync()).SagaId;
        await fixture.Provider.GetRequiredService<ISagaRemediationService>().RetryCompensationAsync(terminalSagaId);
        Guid activeOrder = Guid.NewGuid();
        await Pipeline().ProcessAsync(fixture.Envelope("cleanup-active", "order.start", new StartOrder(activeOrder)));
        fixture.Time.Advance(TimeSpan.FromHours(1));

        SagaCleanupResult cleanup = await fixture.Provider.GetRequiredService<ISagaRemediationService>().CleanupAsync();
        Assert.Equal(1, cleanup.DeletedCount);
        await using AsyncServiceScope verify = fixture.Provider.CreateAsyncScope();
        SagaInstance remaining = Assert.Single(await verify.ServiceProvider.GetRequiredService<SagaTestDbContext>().Set<SagaInstance>().ToListAsync());
        Assert.Equal(SagaStatus.Active, remaining.Status);
    }

    [Fact]
    public async Task Cleanup_never_removes_terminal_saga_with_active_timer()
    {
        await fixture.ResetAsync();
        DateTimeOffset old = fixture.Time.GetUtcNow().AddHours(-1);
        Guid sagaId = Guid.NewGuid();
        await using (AsyncServiceScope seed = fixture.Provider.CreateAsyncScope())
        {
            SagaTestDbContext db = seed.ServiceProvider.GetRequiredService<SagaTestDbContext>();
            var instance = new SagaInstance(sagaId, "order-process", 2, 2, "completed", "{}", old)
            {
                Status = SagaStatus.Completed,
                CompletedAtUtc = old,
                UpdatedAtUtc = old,
            };
            db.Set<SagaInstance>().Add(instance);
            db.Set<SagaTimer>().Add(new SagaTimer(Guid.NewGuid(), sagaId, "order-process", "payment", "order.payment.timeout", fixture.Time.GetUtcNow().AddMinutes(1), old));
            await db.SaveChangesAsync();
        }

        SagaCleanupResult cleanup = await fixture.Provider.GetRequiredService<ISagaRemediationService>().CleanupAsync();

        Assert.Equal(0, cleanup.DeletedCount);
        await using AsyncServiceScope verify = fixture.Provider.CreateAsyncScope();
        SagaTestDbContext verifyDb = verify.ServiceProvider.GetRequiredService<SagaTestDbContext>();
        Assert.Equal(1, await verifyDb.Set<SagaInstance>().CountAsync());
        Assert.Equal(1, await verifyDb.Set<SagaTimer>().CountAsync());
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task Stale_terminal_cleanup_delete_loses_rowversion_race_without_removing_saga()
    {
        await fixture.ResetAsync();
        Guid orderId = Guid.NewGuid();
        await Pipeline().ProcessAsync(fixture.Envelope("cleanup-race-start", "order.start", new StartOrder(orderId)));
        await Pipeline().ProcessAsync(fixture.Envelope("cleanup-race-comp", "order.compensate", new RequestCompensation(orderId)));
        Guid sagaId;
        await using (AsyncServiceScope find = fixture.Provider.CreateAsyncScope())
            sagaId = (await find.ServiceProvider.GetRequiredService<SagaTestDbContext>().Set<SagaInstance>().SingleAsync()).SagaId;
        await fixture.Provider.GetRequiredService<ISagaRemediationService>().RetryCompensationAsync(sagaId);

        await using AsyncServiceScope staleScope = fixture.Provider.CreateAsyncScope();
        SagaTestDbContext staleDb = staleScope.ServiceProvider.GetRequiredService<SagaTestDbContext>();
        SagaInstance stale = await staleDb.Set<SagaInstance>().SingleAsync(x => x.SagaId == sagaId);

        await using (AsyncServiceScope winnerScope = fixture.Provider.CreateAsyncScope())
        {
            SagaTestDbContext winnerDb = winnerScope.ServiceProvider.GetRequiredService<SagaTestDbContext>();
            SagaInstance winner = await winnerDb.Set<SagaInstance>().SingleAsync(x => x.SagaId == sagaId);
            winner.UpdatedAtUtc = winner.UpdatedAtUtc.AddSeconds(1);
            await winnerDb.SaveChangesAsync();
        }

        staleDb.Set<SagaInstance>().Remove(stale);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());

        await using AsyncServiceScope verify = fixture.Provider.CreateAsyncScope();
        Assert.True(await verify.ServiceProvider.GetRequiredService<SagaTestDbContext>().Set<SagaInstance>().AnyAsync(x => x.SagaId == sagaId));
    }

    private IInboxPipeline Pipeline() => fixture.Provider.GetRequiredService<IInboxPipeline>();
    private ISagaTimerProcessor TimerProcessor() => fixture.Provider.GetRequiredService<ISagaTimerProcessor>();
}
