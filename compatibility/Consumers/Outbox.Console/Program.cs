using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;
using TCJ.Core.Entities;
using TCJ.Core.Outbox;
using TCJ.DependencyInjection.Extensions;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.Outbox;
using TCJ.EntityFrameworkCore.Outbox.Extensions;

namespace TcjCompatibility.OutboxConsumer;

public static class Program
{
    public static async Task Main()
    {
        var services = new ServiceCollection();
        services.AddSingleton<OutboxConsumerProbe>();
        services.AddTcjDependencyInjection(typeof(Program).Assembly);
        services.AddTcjEntityFrameworkCore<OutboxConsumerDbContext>(options =>
            options.UseInMemoryDatabase($"tcj-outbox-compat-{Guid.NewGuid():N}"));
        services.AddTcjOutbox<OutboxConsumerDbContext>(options =>
        {
            options.BatchSize = 10;
            options.LockDuration = TimeSpan.FromSeconds(30);
        });
        services.AddTcjOutboxEvent<ConsumerCreatedEvent>("compatibility.consumer.created.v1");
        services.AddScoped<IOutboxStorage, InMemoryCompatibilityOutboxStorage>();

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        using IServiceScope scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OutboxConsumerDbContext>();
        var aggregate = new OutboxConsumerAggregate(Guid.NewGuid());
        aggregate.RaiseCreated();
        dbContext.Aggregates.Add(aggregate);

        await dbContext.SaveChangesAsync(CancellationToken.None);

        OutboxMessage message = await dbContext.Set<OutboxMessage>().SingleAsync();
        if (aggregate.DomainEvents.Count != 0 ||
            message.EventType != "compatibility.consumer.created.v1" ||
            message.ProcessedAtUtc is not null)
        {
            throw new InvalidOperationException("Package-only outbox persistence failed.");
        }

        IOutboxProcessor processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
        OutboxProcessingResult result = await processor.ProcessBatchAsync(CancellationToken.None);
        OutboxConsumerProbe probe = scope.ServiceProvider.GetRequiredService<OutboxConsumerProbe>();
        OutboxMessage processed = await dbContext.Set<OutboxMessage>().SingleAsync();

        if (result.ProcessedCount != 1 ||
            probe.HandledCount != 1 ||
            probe.LastAggregateId != aggregate.Id ||
            processed.Id != message.Id ||
            processed.ProcessedAtUtc is null ||
            processed.AttemptCount != 1)
        {
            throw new InvalidOperationException("Package-only outbox processing failed.");
        }

        Console.WriteLine("TCJ transactional outbox consumer passed");
    }
}

public sealed class OutboxConsumerDbContext(DbContextOptions<OutboxConsumerDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
    public DbSet<OutboxConsumerAggregate> Aggregates => Set<OutboxConsumerAggregate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<OutboxConsumerAggregate>(builder => builder.HasKey(aggregate => aggregate.Id));
        modelBuilder.AddTcjOutbox();
    }
}

public sealed class OutboxConsumerAggregate : Entity<Guid>
{
    private OutboxConsumerAggregate() { }

    public OutboxConsumerAggregate(Guid id) : base(id) { }

    public void RaiseCreated() => AddDomainEvent(new ConsumerCreatedEvent(Id, DateTimeOffset.UtcNow));
}

public sealed record ConsumerCreatedEvent(Guid AggregateId, DateTimeOffset OccurredOn) : IDomainEvent;

public sealed class ConsumerCreatedEventHandler(OutboxConsumerProbe probe) : IDomainEventHandler<ConsumerCreatedEvent>
{
    public Task HandleAsync(ConsumerCreatedEvent domainEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        probe.HandledCount++;
        probe.LastAggregateId = domainEvent.AggregateId;
        return Task.CompletedTask;
    }
}

public sealed class OutboxConsumerProbe
{
    public int HandledCount { get; set; }
    public Guid LastAggregateId { get; set; }
}

/// <summary>
/// Package-consumer fixture storage. Production SQL Server applications use
/// TCJ.EntityFrameworkCore.SqlServer's provider-specific storage instead.
/// </summary>
public sealed class InMemoryCompatibilityOutboxStorage(
    OutboxConsumerDbContext dbContext,
    TcjOutboxOptions options) : IOutboxStorage
{
    public string ProviderName => "Microsoft.EntityFrameworkCore.InMemory";

    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<OutboxMessage> messages = await dbContext.Set<OutboxMessage>()
            .Where(message =>
                message.ProcessedAtUtc == null &&
                message.DeadLetteredAtUtc == null &&
                message.NextAttemptAtUtc <= now &&
                (message.LockExpiresAtUtc == null || message.LockExpiresAtUtc <= now))
            .OrderBy(message => message.NextAttemptAtUtc)
            .ThenBy(message => message.OccurredAtUtc)
            .ThenBy(message => message.Id)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        Guid lockId = Guid.NewGuid();
        DateTimeOffset expiresAt = now + options.LockDuration;
        foreach (OutboxMessage message in messages)
        {
            Set(message, nameof(OutboxMessage.LockId), lockId);
            Set(message, nameof(OutboxMessage.LockedAtUtc), now);
            Set(message, nameof(OutboxMessage.LockExpiresAtUtc), expiresAt);
            Set(message, nameof(OutboxMessage.UpdatedAtUtc), now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return messages;
    }

    public async Task MarkProcessedAsync(Guid messageId, Guid lockId, int attempt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        OutboxMessage message = await GetOwnedAsync(messageId, lockId, cancellationToken);
        Set(message, nameof(OutboxMessage.AttemptCount), attempt);
        Set(message, nameof(OutboxMessage.ProcessedAtUtc), now);
        ClearLease(message, now);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ScheduleRetryAsync(Guid messageId, Guid lockId, int attempt, DateTimeOffset nextAttemptAtUtc, string errorType, string error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        OutboxMessage message = await GetOwnedAsync(messageId, lockId, cancellationToken);
        Set(message, nameof(OutboxMessage.AttemptCount), attempt);
        Set(message, nameof(OutboxMessage.NextAttemptAtUtc), nextAttemptAtUtc);
        Set(message, nameof(OutboxMessage.LastErrorType), errorType);
        Set(message, nameof(OutboxMessage.LastError), error);
        ClearLease(message, now);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeadLetterAsync(Guid messageId, Guid lockId, int attempt, string errorType, string error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        OutboxMessage message = await GetOwnedAsync(messageId, lockId, cancellationToken);
        Set(message, nameof(OutboxMessage.AttemptCount), attempt);
        Set(message, nameof(OutboxMessage.DeadLetteredAtUtc), now);
        Set(message, nameof(OutboxMessage.LastErrorType), errorType);
        Set(message, nameof(OutboxMessage.LastError), error);
        ClearLease(message, now);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ReplayAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        OutboxMessage? message = await dbContext.Set<OutboxMessage>().SingleOrDefaultAsync(item => item.Id == messageId, cancellationToken);
        if (message is null || message.DeadLetteredAtUtc is null || (message.LockExpiresAtUtc is not null && message.LockExpiresAtUtc > now))
        {
            return false;
        }

        Set(message, nameof(OutboxMessage.AttemptCount), 0);
        Set(message, nameof(OutboxMessage.NextAttemptAtUtc), now);
        Set<DateTimeOffset?>(message, nameof(OutboxMessage.DeadLetteredAtUtc), null);
        Set<string?>(message, nameof(OutboxMessage.LastErrorType), null);
        Set<string?>(message, nameof(OutboxMessage.LastError), null);
        Set(message, nameof(OutboxMessage.ReplayCount), message.ReplayCount + 1);
        Set(message, nameof(OutboxMessage.LastReplayedAtUtc), now);
        ClearLease(message, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> CleanupAsync(DateTimeOffset processedBeforeUtc, int batchSize, CancellationToken cancellationToken)
    {
        List<OutboxMessage> messages = await dbContext.Set<OutboxMessage>()
            .Where(message => message.ProcessedAtUtc != null && message.ProcessedAtUtc < processedBeforeUtc && message.LockId == null)
            .OrderBy(message => message.ProcessedAtUtc)
            .ThenBy(message => message.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        dbContext.RemoveRange(messages);
        await dbContext.SaveChangesAsync(cancellationToken);
        return messages.Count;
    }

    public async Task<OutboxHealthSnapshot> GetHealthSnapshotAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<OutboxMessage> messages = await dbContext.Set<OutboxMessage>().ToListAsync(cancellationToken);
        List<OutboxMessage> pending = messages
            .Where(message => message.ProcessedAtUtc is null && message.DeadLetteredAtUtc is null)
            .ToList();
        DateTimeOffset? oldest = pending.Count == 0 ? null : pending.Min(message => message.CreatedAtUtc);
        return new OutboxHealthSnapshot(
            pending.Count,
            messages.Count(message => message.DeadLetteredAtUtc is not null),
            oldest is null ? TimeSpan.Zero : now - oldest.Value);
    }

    private async Task<OutboxMessage> GetOwnedAsync(Guid messageId, Guid lockId, CancellationToken cancellationToken)
    {
        OutboxMessage? message = await dbContext.Set<OutboxMessage>()
            .SingleOrDefaultAsync(item => item.Id == messageId && item.LockId == lockId, cancellationToken);
        return message ?? throw new InvalidOperationException("The compatibility outbox lease was lost.");
    }

    private void ClearLease(OutboxMessage message, DateTimeOffset now)
    {
        Set<Guid?>(message, nameof(OutboxMessage.LockId), null);
        Set<DateTimeOffset?>(message, nameof(OutboxMessage.LockedAtUtc), null);
        Set<DateTimeOffset?>(message, nameof(OutboxMessage.LockExpiresAtUtc), null);
        Set(message, nameof(OutboxMessage.UpdatedAtUtc), now);
    }

    private void Set<T>(OutboxMessage message, string propertyName, T value) =>
        dbContext.Entry(message).Property<T>(propertyName).CurrentValue = value;
}
