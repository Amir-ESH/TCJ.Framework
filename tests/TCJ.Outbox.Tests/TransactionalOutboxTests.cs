using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using TCJ.AspNetCore.Outbox.Extensions;
using TCJ.Core.Diagnostics;
using TCJ.Core.DomainEvents;
using TCJ.Core.HealthChecks;
using TCJ.Core.Outbox;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Outbox;
using TCJ.EntityFrameworkCore.Outbox.Extensions;
using TCJ.EntityFrameworkCore.Outbox.Serialization;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Outbox.Extensions;

namespace TCJ.Outbox.Tests;

[Collection(OutboxSqlServerCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "SqlServer")]
[Trait("Category", "Outbox")]
public sealed class TransactionalOutboxTests(OutboxSqlServerFixture fixture)
{
    [Fact]
    public async Task Business_state_and_outbox_message_commit_together()
    {
        await fixture.ResetAsync();
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>();
        var entity = new OutboxTestEntity("commit-together");
        entity.Change("commit", fixture.Time.GetUtcNow());
        context.Entities.Add(entity);

        await context.SaveChangesAsync();

        Assert.Equal(1, await context.Entities.CountAsync());
        Assert.Equal(1, await context.Set<OutboxMessage>().CountAsync());
        Assert.Empty(entity.DomainEvents);
    }

    [Fact]
    public async Task Explicit_transaction_clears_domain_events_only_after_commit()
    {
        await fixture.ResetAsync();
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>();
        var entity = new OutboxTestEntity("explicit-commit");
        entity.Change("explicit", fixture.Time.GetUtcNow());
        context.Entities.Add(entity);
        await using var transaction = await context.Database.BeginTransactionAsync();

        await context.SaveChangesAsync();
        Assert.Single(entity.DomainEvents);
        Assert.Single(await context.Set<OutboxMessage>().ToArrayAsync());

        await transaction.CommitAsync();
        Assert.Empty(entity.DomainEvents);
    }

    [Fact]
    public async Task Transaction_rollback_persists_neither_business_state_nor_outbox_and_keeps_event_for_retry()
    {
        await fixture.ResetAsync();
        Guid entityId;
        await using (AsyncServiceScope scope = fixture.Provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>();
            var entity = new OutboxTestEntity("rollback");
            entityId = entity.Id;
            entity.Change("rollback", fixture.Time.GetUtcNow());
            context.Entities.Add(entity);
            await using var transaction = await context.Database.BeginTransactionAsync();
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
            Assert.Single(entity.DomainEvents);
        }

        await using AsyncServiceScope verificationScope = fixture.Provider.CreateAsyncScope();
        var verification = verificationScope.ServiceProvider.GetRequiredService<OutboxTestDbContext>();
        Assert.False(await verification.Entities.AnyAsync(entity => entity.Id == entityId));
        Assert.False(await verification.Set<OutboxMessage>().AnyAsync());
    }

    [Fact]
    public async Task Rolled_back_delete_is_reattached_for_retry_with_the_same_outbox_message_id()
    {
        await fixture.ResetAsync();
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>();
        var entity = new OutboxTestEntity("delete-rollback");
        context.Entities.Add(entity);
        await context.SaveChangesAsync();

        entity.Change("delete-rollback", fixture.Time.GetUtcNow());
        context.Entities.Remove(entity);
        Guid messageId;
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await context.SaveChangesAsync();
            messageId = Assert.Single(context.Set<OutboxMessage>().Local).Id;
            await transaction.RollbackAsync();
            Assert.Equal(EntityState.Deleted, context.Entry(entity).State);
            Assert.Single(entity.DomainEvents);
            Assert.Equal(EntityState.Added, context.Entry(context.Set<OutboxMessage>().Local.Single()).State);
        }

        await context.SaveChangesAsync();

        Assert.False(await context.Entities.AsNoTracking().AnyAsync(item => item.Id == entity.Id));
        OutboxMessage persisted = Assert.Single(await context.Set<OutboxMessage>().AsNoTracking().ToArrayAsync());
        Assert.Equal(messageId, persisted.Id);
        Assert.Empty(entity.DomainEvents);
    }

    [Fact]
    public async Task Failed_save_retry_reuses_the_same_message_id_and_does_not_duplicate_outbox_rows()
    {
        await fixture.ResetAsync();
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>();
        context.Entities.Add(new OutboxTestEntity("unique-name"));
        await context.SaveChangesAsync();

        var retryEntity = new OutboxTestEntity("unique-name");
        retryEntity.Change("retry-save", fixture.Time.GetUtcNow());
        context.Entities.Add(retryEntity);
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Guid stableId = Assert.Single(context.Set<OutboxMessage>().Local).Id;

        retryEntity.Name = "unique-name-fixed";
        await context.SaveChangesAsync();

        OutboxMessage persisted = Assert.Single(await context.Set<OutboxMessage>().AsNoTracking().ToArrayAsync());
        Assert.Equal(stableId, persisted.Id);
        Assert.Empty(retryEntity.DomainEvents);
    }

    [Fact]
    public async Task Synchronous_save_path_persists_outbox_and_clears_events_consistently()
    {
        await fixture.ResetAsync();
        using IServiceScope scope = fixture.Provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>();
        var entity = new OutboxTestEntity("sync-save");
        entity.Change("sync", fixture.Time.GetUtcNow());
        context.Entities.Add(entity);

        context.SaveChanges();

        Assert.Single(context.Set<OutboxMessage>().AsNoTracking().ToArray());
        Assert.Empty(entity.DomainEvents);
    }

    [Fact]
    public async Task Manual_batch_processing_dispatches_and_marks_message_processed()
    {
        await fixture.ResetAsync();
        Guid id = await fixture.PersistEventAsync("manual");
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        IOutboxProcessor processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();

        OutboxProcessingResult result = await processor.ProcessBatchAsync();

        Assert.Equal(1, result.ProcessedCount);
        OutboxMessage row = await scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>().Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Id == id);
        Assert.NotNull(row.ProcessedAtUtc);
        Assert.Equal(1, row.AttemptCount);
        Assert.Contains("manual", fixture.Behavior.DeliveredMarkers);
    }

    [Fact]
    public async Task Transient_failure_schedules_bounded_retry_then_succeeds()
    {
        await fixture.ResetAsync();
        fixture.Behavior.FailTransiently("transient", 1);
        Guid id = await fixture.PersistEventAsync("transient");
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        IOutboxProcessor processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();

        OutboxProcessingResult first = await processor.ProcessBatchAsync();
        OutboxMessage retry = await scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>().Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Id == id);
        Assert.Equal(1, first.RetryScheduledCount);
        Assert.Equal(1, retry.AttemptCount);
        Assert.True(retry.NextAttemptAtUtc > fixture.Time.GetUtcNow());
        Assert.Null(retry.DeadLetteredAtUtc);

        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        OutboxProcessingResult second = await processor.ProcessBatchAsync();
        Assert.Equal(1, second.ProcessedCount);
        Assert.Equal(2, fixture.Behavior.AttemptCount(id));
    }

    [Fact]
    public async Task Permanent_failure_is_dead_lettered_without_blocking_later_messages()
    {
        await fixture.ResetAsync();
        fixture.Behavior.FailPermanently("poison");
        Guid poisonId = await fixture.PersistEventAsync("poison", "a-poison");
        Guid goodId = await fixture.PersistEventAsync("good", "b-good");
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();

        OutboxProcessingResult result = await scope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessBatchAsync();

        var context = scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>();
        OutboxMessage poison = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Id == poisonId);
        OutboxMessage good = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Id == goodId);
        Assert.Equal(1, result.DeadLetteredCount);
        Assert.Equal(1, result.ProcessedCount);
        Assert.NotNull(poison.DeadLetteredAtUtc);
        Assert.NotNull(good.ProcessedAtUtc);
    }

    [Fact]
    public async Task Poison_message_stops_after_maximum_transient_retries()
    {
        await fixture.ResetAsync();
        fixture.Behavior.FailTransiently("retry-poison", 10);
        Guid id = await fixture.PersistEventAsync("retry-poison");
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        IOutboxProcessor processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();

        Assert.Equal(1, (await processor.ProcessBatchAsync()).RetryScheduledCount);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, (await processor.ProcessBatchAsync()).RetryScheduledCount);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, (await processor.ProcessBatchAsync()).DeadLetteredCount);

        OutboxMessage row = await scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>().Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Id == id);
        Assert.Equal(3, row.AttemptCount);
        Assert.NotNull(row.DeadLetteredAtUtc);
        fixture.Time.Advance(TimeSpan.FromHours(1));
        Assert.False((await processor.ProcessBatchAsync()).HasWork);
    }

    [Fact]
    public async Task Concurrent_processors_claim_each_message_once_in_normal_path()
    {
        await fixture.ResetAsync();
        await using (AsyncServiceScope seedScope = fixture.Provider.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<OutboxTestDbContext>();
            for (int index = 0; index < 20; index++)
            {
                var entity = new OutboxTestEntity($"concurrent-{index:D2}");
                entity.Change($"concurrent-{index:D2}", fixture.Time.GetUtcNow());
                context.Entities.Add(entity);
            }
            await context.SaveChangesAsync();
        }

        Task<OutboxProcessingResult>[] workers = Enumerable.Range(0, 4).Select(async _ =>
        {
            await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessBatchAsync();
        }).ToArray();
        OutboxProcessingResult[] results = await Task.WhenAll(workers);

        Assert.Equal(20, results.Sum(result => result.ProcessedCount));
        await using AsyncServiceScope verifyScope = fixture.Provider.CreateAsyncScope();
        OutboxMessage[] rows = await verifyScope.ServiceProvider.GetRequiredService<OutboxTestDbContext>().Set<OutboxMessage>().AsNoTracking().ToArrayAsync();
        Assert.Equal(20, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.NotNull(row.ProcessedAtUtc);
            Assert.Equal(1, row.AttemptCount);
            Assert.Null(row.LockId);
        });
    }

    [Fact]
    public async Task Expired_lease_is_reclaimed_and_idempotent_side_effect_remains_single()
    {
        await fixture.ResetAsync();
        Guid id = await fixture.PersistEventAsync("lease");
        await using (AsyncServiceScope claimScope = fixture.Provider.CreateAsyncScope())
        {
            IOutboxStorage storage = claimScope.ServiceProvider.GetRequiredService<IOutboxStorage>();
            OutboxMessage claimed = Assert.Single(await storage.ClaimBatchAsync(fixture.Time.GetUtcNow(), default));
            Assert.Equal(id, claimed.Id);
            fixture.Behavior.RecordExternalDelivery(id); // simulates success before the worker can persist status
        }

        fixture.Time.Advance(TimeSpan.FromSeconds(11));
        await using AsyncServiceScope recoveryScope = fixture.Provider.CreateAsyncScope();
        OutboxProcessingResult recovered = await recoveryScope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessBatchAsync();

        Assert.Equal(1, recovered.ProcessedCount);
        Assert.Equal(2, fixture.Behavior.AttemptCount(id));
        Assert.Equal(1, fixture.Behavior.SideEffectCount);
    }

    [Fact]
    public async Task Cancellation_after_handler_side_effect_leaves_recoverable_lease()
    {
        await fixture.ResetAsync();
        Guid id = await fixture.PersistEventAsync("cancel");
        using var cancellation = new CancellationTokenSource();
        fixture.Behavior.BeforeDispatch = cancellation.Cancel;
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        IOutboxProcessor processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessBatchAsync(cancellation.Token));
        OutboxMessage row = await scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>().Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Id == id);
        Assert.Null(row.ProcessedAtUtc);
        Assert.NotNull(row.LockId);

        fixture.Behavior.BeforeDispatch = null;
        fixture.Time.Advance(TimeSpan.FromSeconds(11));
        await using AsyncServiceScope recoveryScope = fixture.Provider.CreateAsyncScope();
        Assert.Equal(1, (await recoveryScope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessBatchAsync()).ProcessedCount);
        Assert.Equal(2, fixture.Behavior.AttemptCount(id));
        Assert.Equal(1, fixture.Behavior.SideEffectCount);
    }

    [Fact]
    public async Task Explicit_replay_preserves_message_identity_and_resets_attempt_metadata()
    {
        await fixture.ResetAsync();
        fixture.Behavior.FailPermanently("replay");
        Guid id = await fixture.PersistEventAsync("replay");
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        IOutboxProcessor processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
        await processor.ProcessBatchAsync();

        OutboxReplayResult replay = await scope.ServiceProvider.GetRequiredService<IOutboxReplayService>().ReplayAsync(id);
        OutboxMessage row = await scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>().Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Id == id);

        Assert.True(replay.Replayed);
        Assert.Equal(id, replay.MessageId);
        Assert.Equal(id, row.Id);
        Assert.Null(row.DeadLetteredAtUtc);
        Assert.Equal(0, row.AttemptCount);
        Assert.Equal(1, row.ReplayCount);
    }

    [Fact]
    public async Task Replay_is_rejected_while_message_has_an_active_claim()
    {
        await fixture.ResetAsync();
        fixture.Behavior.FailPermanently("active-replay");
        Guid id = await fixture.PersistEventAsync("active-replay");
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        IOutboxProcessor processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
        await processor.ProcessBatchAsync();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IOutboxReplayService>().ReplayAsync(id)).Replayed);

        IOutboxStorage storage = scope.ServiceProvider.GetRequiredService<IOutboxStorage>();
        Assert.Single(await storage.ClaimBatchAsync(fixture.Time.GetUtcNow(), default));
        Assert.False((await scope.ServiceProvider.GetRequiredService<IOutboxReplayService>().ReplayAsync(id)).Replayed);
    }

    [Fact]
    public async Task Cleanup_removes_only_old_processed_records_and_preserves_pending_and_dead_letters()
    {
        await fixture.ResetAsync();
        Guid processedId = await fixture.PersistEventAsync("processed-old", "processed-old");
        await using (AsyncServiceScope processScope = fixture.Provider.CreateAsyncScope())
        {
            await processScope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessBatchAsync();
        }

        fixture.Behavior.FailPermanently("dead");
        _ = await fixture.PersistEventAsync("dead", "dead");
        await using (AsyncServiceScope deadScope = fixture.Provider.CreateAsyncScope())
        {
            await deadScope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessBatchAsync();
        }

        // Persist the pending row only after the poison batch has been processed so it
        // remains genuinely pending and the cleanup assertion tests retention semantics.
        _ = await fixture.PersistEventAsync("pending", "pending");
        fixture.Time.Advance(TimeSpan.FromHours(2));

        await using AsyncServiceScope cleanupScope = fixture.Provider.CreateAsyncScope();
        OutboxCleanupResult result = await cleanupScope.ServiceProvider.GetRequiredService<IOutboxCleanupService>().CleanupAsync();
        OutboxMessage[] remaining = await cleanupScope.ServiceProvider.GetRequiredService<OutboxTestDbContext>().Set<OutboxMessage>().AsNoTracking().ToArrayAsync();
        Assert.Equal(1, result.DeletedCount);
        Assert.DoesNotContain(remaining, message => message.Id == processedId);
        Assert.Contains(remaining, message => message.ProcessedAtUtc is null && message.DeadLetteredAtUtc is null);
        Assert.Contains(remaining, message => message.DeadLetteredAtUtc is not null);
    }

    [Fact]
    public async Task Cleanup_respects_configured_batch_limit()
    {
        await fixture.ResetAsync();
        for (int index = 0; index < 7; index++)
        {
            await fixture.PersistEventAsync($"cleanup-{index}", $"cleanup-{index}");
        }

        await using (AsyncServiceScope processScope = fixture.Provider.CreateAsyncScope())
        {
            IOutboxProcessor processor = processScope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
            while ((await processor.ProcessBatchAsync()).HasWork) { }
        }
        fixture.Time.Advance(TimeSpan.FromHours(2));

        await using AsyncServiceScope cleanupScope = fixture.Provider.CreateAsyncScope();
        OutboxCleanupResult first = await cleanupScope.ServiceProvider.GetRequiredService<IOutboxCleanupService>().CleanupAsync();
        OutboxCleanupResult second = await cleanupScope.ServiceProvider.GetRequiredService<IOutboxCleanupService>().CleanupAsync();
        Assert.Equal(5, first.DeletedCount);
        Assert.Equal(2, second.DeletedCount);
    }

    [Fact]
    public async Task Sensitive_payload_marker_is_not_persisted_in_error_diagnostics()
    {
        await fixture.ResetAsync();
        const string secret = "TCJ_SECRET_MARKER_4F62E";
        fixture.Behavior.FailPermanently(secret);
        Guid id = await fixture.PersistEventAsync(secret);
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessBatchAsync();

        OutboxMessage row = await scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>().Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Id == id);
        Assert.Contains(secret, row.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, row.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, row.LastErrorType ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_event_type_has_bounded_failure_and_is_dead_lettered()
    {
        await fixture.ResetAsync();
        Guid id = Guid.CreateVersion7(fixture.Time.GetUtcNow());
        DateTimeOffset now = fixture.Time.GetUtcNow();
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>();
        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO [TCJ_OutboxMessages]
            ([Id],[OccurredAtUtc],[EventType],[Payload],[AttemptCount],[NextAttemptAtUtc],[CreatedAtUtc],[UpdatedAtUtc],[ReplayCount])
            VALUES ({{id}},{{now}},{{"unknown.contract.v1"}},{{"{}"}},0,{{now}},{{now}},{{now}},0)
            """);

        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessBatchAsync()).DeadLetteredCount);
        OutboxMessage row = await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Id == id);
        Assert.NotNull(row.DeadLetteredAtUtc);
        Assert.Contains("InvalidOperationException", row.LastErrorType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outbox_tracing_uses_stable_safe_tags_without_payload_values()
    {
        await fixture.ResetAsync();
        TcjTelemetryOptions original = TcjTelemetry.GetOptions();
        TcjTelemetry.Configure(options =>
        {
            options.EnableTracing = true;
            options.EnableMetrics = original.EnableMetrics;
            options.RecordExceptionMessages = true; // Outbox must still never emit exception messages.
            options.RecordEntityTypeNames = original.RecordEntityTypeNames;
            options.RecordHandlerTypeNames = original.RecordHandlerTypeNames;
        });

        const string secret = "TCJ_OUTBOX_TRACE_SECRET_91A7";
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TcjDiagnosticNames.Sources.EntityFrameworkCore,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        try
        {
            fixture.Behavior.FailPermanently(secret);
            _ = await fixture.PersistEventAsync(secret);
            await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
            Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessBatchAsync()).DeadLetteredCount);

            Assert.Contains(stopped, activity => activity.OperationName == TcjDiagnosticNames.Activities.OutboxPersist);
            Assert.Contains(stopped, activity => activity.OperationName == TcjDiagnosticNames.Activities.OutboxClaim);
            Assert.Contains(stopped, activity => activity.OperationName == TcjDiagnosticNames.Activities.OutboxProcess);
            Assert.Contains(stopped, activity => activity.OperationName == TcjDiagnosticNames.Activities.OutboxDeadLetter);
            string allTags = string.Join('\n', stopped.SelectMany(activity => activity.TagObjects).Select(tag => $"{tag.Key}={tag.Value}"));
            Assert.DoesNotContain(secret, allTags, StringComparison.Ordinal);
            Assert.DoesNotContain(TcjDiagnosticNames.Tags.ExceptionMessage, allTags, StringComparison.Ordinal);
        }
        finally
        {
            RestoreTelemetry(original);
        }
    }

    [Fact]
    public async Task Outbox_metrics_cover_persistence_processing_failure_and_backlog_without_payload_dimensions()
    {
        await fixture.ResetAsync();
        TcjTelemetryOptions original = TcjTelemetry.GetOptions();
        TcjTelemetry.Configure(options =>
        {
            options.EnableTracing = original.EnableTracing;
            options.EnableMetrics = true;
            options.RecordExceptionMessages = original.RecordExceptionMessages;
            options.RecordEntityTypeNames = original.RecordEntityTypeNames;
            options.RecordHandlerTypeNames = original.RecordHandlerTypeNames;
        });

        var records = new ConcurrentBag<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == TcjDiagnosticNames.Sources.EntityFrameworkCore
                && instrument.Name.StartsWith("tcj.outbox.", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => records.Add((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => records.Add((instrument.Name, tags.ToArray())));
        listener.Start();

        const string secret = "TCJ_OUTBOX_METRIC_SECRET_7C2D";
        try
        {
            fixture.Behavior.FailPermanently(secret);
            _ = await fixture.PersistEventAsync(secret);
            _ = await fixture.PersistEventAsync("metrics-success");
            await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessBatchAsync();
            await scope.ServiceProvider.GetRequiredService<HealthCheckService>().CheckHealthAsync(
                registration => registration.Name == TcjHealthCheckNames.Checks.OutboxBacklog);

            string[] names = records.Select(record => record.Name).Distinct(StringComparer.Ordinal).ToArray();
            Assert.Contains(TcjDiagnosticNames.Metrics.OutboxMessagesPersisted, names);
            Assert.Contains(TcjDiagnosticNames.Metrics.OutboxMessagesProcessed, names);
            Assert.Contains(TcjDiagnosticNames.Metrics.OutboxMessagesFailed, names);
            Assert.Contains(TcjDiagnosticNames.Metrics.OutboxMessagesDeadLettered, names);
            Assert.Contains(TcjDiagnosticNames.Metrics.OutboxProcessingDuration, names);
            Assert.Contains(TcjDiagnosticNames.Metrics.OutboxPendingCount, names);
            Assert.Contains(TcjDiagnosticNames.Metrics.OutboxOldestPendingAge, names);
            string allTags = string.Join('\n', records.SelectMany(record => record.Tags).Select(tag => $"{tag.Key}={tag.Value}"));
            Assert.DoesNotContain(secret, allTags, StringComparison.Ordinal);
        }
        finally
        {
            RestoreTelemetry(original);
        }
    }

    [Fact]
    public async Task Health_checks_expose_safe_processor_backlog_and_dead_letter_status()
    {
        await fixture.ResetAsync();
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IOutboxProcessor>().ProcessBatchAsync();
        HealthReport report = await scope.ServiceProvider.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            registration => registration.Name.StartsWith("tcj.outbox.", StringComparison.Ordinal));

        Assert.Contains(TcjHealthCheckNames.Checks.OutboxProcessor, report.Entries.Keys);
        Assert.Contains(TcjHealthCheckNames.Checks.OutboxBacklog, report.Entries.Keys);
        Assert.Contains(TcjHealthCheckNames.Checks.OutboxDeadLetters, report.Entries.Keys);
        Assert.All(report.Entries.Values, entry => Assert.DoesNotContain("Payload", entry.Description ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Model_contains_required_primary_key_and_processing_indexes()
    {
        await fixture.ResetAsync();
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>();
        var entity = context.Model.FindEntityType(typeof(OutboxMessage))!;

        Assert.Equal("TCJ_OutboxMessages", entity.GetTableName());
        Assert.Equal(nameof(OutboxMessage.Id), Assert.Single(entity.FindPrimaryKey()!.Properties).Name);
        string[] indexNames = entity.GetIndexes().Select(index => index.GetDatabaseName()).OfType<string>().ToArray();
        Assert.Contains("IX_TCJ_OutboxMessages_ProcessedAtUtc_NextAttemptAtUtc", indexNames);
        Assert.Contains("IX_TCJ_OutboxMessages_LockExpiresAtUtc", indexNames);
        Assert.Contains("IX_TCJ_OutboxMessages_OccurredAtUtc", indexNames);
        Assert.Contains("IX_TCJ_OutboxMessages_EventType", indexNames);
    }

    [Fact]
    public async Task Stable_registered_event_name_is_persisted_without_assembly_qualified_name()
    {
        await fixture.ResetAsync();
        Guid id = await fixture.PersistEventAsync("stable-name");
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        OutboxMessage row = await scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>().Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Id == id);

        Assert.Equal("test.changed.v1", row.EventType);
        Assert.DoesNotContain(typeof(TestDomainEvent).Assembly.GetName().Name!, row.EventType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_serializer_deserializes_only_pre_resolved_domain_event_type()
    {
        await fixture.ResetAsync();
        await using AsyncServiceScope scope = fixture.Provider.CreateAsyncScope();
        IOutboxSerializer serializer = scope.ServiceProvider.GetRequiredService<IOutboxSerializer>();
        IOutboxEventTypeResolver resolver = scope.ServiceProvider.GetRequiredService<IOutboxEventTypeResolver>();
        var domainEvent = new TestDomainEvent(Guid.NewGuid(), "serialization", fixture.Time.GetUtcNow());

        string payload = serializer.Serialize(domainEvent);
        Type resolved = resolver.Resolve("test.changed.v1");
        IDomainEvent roundTrip = serializer.Deserialize(resolved, payload);

        Assert.IsType<TestDomainEvent>(roundTrip);
        Assert.DoesNotContain("$type", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Assembly", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Custom_serializer_can_replace_default_registration()
    {
        var services = new ServiceCollection();
        var custom = new TestOutboxSerializer();
        services.AddSingleton<IOutboxSerializer>(custom);
        services.AddTcjOutbox<OutboxTestDbContext>();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(custom, provider.GetRequiredService<IOutboxSerializer>());
    }

    [Fact]
    public void Invalid_outbox_configuration_is_rejected_at_registration()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddTcjOutbox<OutboxTestDbContext>(options => options.BatchSize = 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddTcjOutbox<OutboxTestDbContext>(options => options.LockDuration = TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => services.AddTcjOutbox<OutboxTestDbContext>(options =>
        {
            options.BaseRetryDelay = TimeSpan.FromMinutes(2);
            options.MaxRetryDelay = TimeSpan.FromSeconds(1);
        }));
    }

    [Fact]
    public void Hosted_processing_is_optional_and_does_not_register_until_explicitly_requested()
    {
        var services = new ServiceCollection();
        services.AddTcjOutbox<OutboxTestDbContext>();
        Assert.DoesNotContain(services, IsOutboxHostedServiceRegistration);

        services.AddTcjOutboxProcessor();

        Assert.Contains(services, IsOutboxHostedServiceRegistration);
    }

    private static bool IsOutboxHostedServiceRegistration(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IHostedService)
        && descriptor.ImplementationType is Type implementationType
        && implementationType.Assembly == typeof(OutboxHostedServiceCollectionExtensions).Assembly
        && string.Equals(implementationType.Name, "OutboxHostedService", StringComparison.Ordinal);

    private static void RestoreTelemetry(TcjTelemetryOptions options)
    {
        TcjTelemetry.Configure(current =>
        {
            current.EnableTracing = options.EnableTracing;
            current.EnableMetrics = options.EnableMetrics;
            current.RecordExceptionMessages = options.RecordExceptionMessages;
            current.RecordEntityTypeNames = options.RecordEntityTypeNames;
            current.RecordHandlerTypeNames = options.RecordHandlerTypeNames;
        });
    }

    private sealed class TestOutboxSerializer : IOutboxSerializer
    {
        public string Serialize(IDomainEvent domainEvent) => "custom";
        public IDomainEvent Deserialize(Type eventType, string payload) => new TestDomainEvent(Guid.Empty, payload, DateTimeOffset.UnixEpoch);
    }
}
