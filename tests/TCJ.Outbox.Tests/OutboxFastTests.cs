using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TCJ.AspNetCore.Outbox.Extensions;
using TCJ.Core.DomainEvents;
using TCJ.Core.Outbox;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.Outbox;
using TCJ.EntityFrameworkCore.Outbox.Extensions;
using TCJ.EntityFrameworkCore.Outbox.Serialization;

namespace TCJ.Outbox.Tests;

[Trait("Category", "OutboxFast")]
public sealed class OutboxFastTests
{
    [Fact]
    public void Default_options_are_bounded_and_valid()
    {
        var options = new TcjOutboxOptions();
        options.Validate();
        Assert.Equal(100, options.BatchSize);
        Assert.Equal(TimeSpan.FromSeconds(30), options.LockDuration);
        Assert.Equal(10, options.MaxRetryAttempts);
        Assert.Equal(TimeSpan.FromDays(7), options.RetentionPeriod);
    }

    [Fact]
    public void Invalid_options_are_rejected_before_processing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TcjOutboxOptions { BatchSize = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TcjOutboxOptions { LockDuration = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentException>(() => new TcjOutboxOptions
        {
            BaseRetryDelay = TimeSpan.FromSeconds(2),
            MaxRetryDelay = TimeSpan.FromSeconds(1)
        }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TcjOutboxOptions
        {
            BaseRetryDelay = TimeSpan.Zero
        }.Validate());
    }

    [Fact]
    public void Explicit_event_name_is_stable_and_resolves_without_assembly_identity()
    {
        var services = new ServiceCollection();
        services.AddTcjOutbox<FastDbContext>();
        services.AddTcjOutboxEvent<FastEvent>("fast.changed.v1");

        using ServiceProvider provider = services.BuildServiceProvider();
        IOutboxEventTypeResolver resolver = provider.GetRequiredService<IOutboxEventTypeResolver>();

        Assert.Equal("fast.changed.v1", resolver.GetName(typeof(FastEvent)));
        Assert.Equal(typeof(FastEvent), resolver.Resolve("fast.changed.v1"));
        Assert.DoesNotContain(typeof(FastEvent).Assembly.GetName().Name!, resolver.GetName(typeof(FastEvent)), StringComparison.Ordinal);
    }

    [Fact]
    public void Default_json_serializer_does_not_emit_polymorphic_type_metadata()
    {
        var serializer = new SystemTextJsonOutboxSerializer(new TcjOutboxOptions());
        var value = new FastEvent("safe", DateTimeOffset.UnixEpoch);
        string payload = serializer.Serialize(value);
        IDomainEvent roundTrip = serializer.Deserialize(typeof(FastEvent), payload);
        Assert.IsType<FastEvent>(roundTrip);
        Assert.DoesNotContain("$type", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AssemblyQualifiedName", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Consumer_registered_serializer_replaces_default()
    {
        var services = new ServiceCollection();
        var custom = new FastSerializer();
        services.AddSingleton<IOutboxSerializer>(custom);
        services.AddTcjOutbox<FastDbContext>();
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Same(custom, provider.GetRequiredService<IOutboxSerializer>());
    }

    [Fact]
    public void Hosted_processor_is_opt_in()
    {
        var services = new ServiceCollection();
        services.AddTcjOutbox<FastDbContext>();
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        services.AddTcjOutboxProcessor();
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public async Task Conflicting_provider_storage_registrations_are_rejected_at_startup()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDomainEventDispatcher, NoopDispatcher>();
        services.AddTcjEntityFrameworkCore<FastDbContext>(options =>
            options.UseInMemoryDatabase($"outbox-fast-{Guid.NewGuid():N}"));
        services.AddTcjOutbox<FastDbContext>();
        services.AddScoped<IOutboxStorage, FastOutboxStorage>();
        services.AddScoped<IOutboxStorage, SecondFastOutboxStorage>();

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IOutboxProcessor processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessBatchAsync());
        Assert.Contains("exactly one provider-specific IOutboxStorage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_outbox_context_registration_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddTcjOutbox<FastDbContext>();
        Assert.Throws<InvalidOperationException>(() => services.AddTcjOutbox<SecondFastDbContext>());
    }

    private sealed record FastEvent(string Value, DateTimeOffset OccurredOn) : IDomainEvent;

    private sealed class FastSerializer : IOutboxSerializer
    {
        public string Serialize(IDomainEvent domainEvent) => "fast";
        public IDomainEvent Deserialize(Type eventType, string payload) => new FastEvent(payload, DateTimeOffset.UnixEpoch);
    }

    private sealed class FastDbContext(DbContextOptions<FastDbContext> options) : DbContext(options), IReadDbContext, IWriteDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.AddTcjOutbox();
        }
    }

    private sealed class SecondFastDbContext(DbContextOptions<SecondFastDbContext> options) : DbContext(options), IReadDbContext, IWriteDbContext;

    private sealed class NoopDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class FastOutboxStorage : IOutboxStorage
    {
        public string ProviderName => "Microsoft.EntityFrameworkCore.InMemory";
        public Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<OutboxMessage>>([]);
        public Task MarkProcessedAsync(Guid messageId, Guid lockId, int attempt, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ScheduleRetryAsync(Guid messageId, Guid lockId, int attempt, DateTimeOffset nextAttemptAtUtc, string errorType, string error, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeadLetterAsync(Guid messageId, Guid lockId, int attempt, string errorType, string error, DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ReplayAsync(Guid messageId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<int> CleanupAsync(DateTimeOffset processedBeforeUtc, int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<OutboxHealthSnapshot> GetHealthSnapshotAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(new OutboxHealthSnapshot(0, 0, TimeSpan.Zero));
    }

    private sealed class SecondFastOutboxStorage : FastOutboxStorage { }
}
