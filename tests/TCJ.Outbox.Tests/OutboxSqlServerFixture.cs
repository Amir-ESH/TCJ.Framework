using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TCJ.Core.DomainEvents;
using TCJ.Core.Entities;
using TCJ.Core.Outbox;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Outbox.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Outbox.Extensions;
using Testcontainers.MsSql;

namespace TCJ.Outbox.Tests;

public sealed class OutboxSqlServerFixture : IAsyncLifetime
{
    internal const string Image = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
    private readonly MsSqlContainer _container = new MsSqlBuilder(Image)
        .WithPassword(CreatePassword())
        .WithLabel("tcj.outbox.tests", "true")
        .WithCleanUp(true)
        .Build();
    private ServiceProvider? _provider;
    private string? _databaseName;

    internal FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
    internal TestDeliveryBehavior Behavior { get; } = new();
    internal ServiceProvider Provider => _provider ?? throw new InvalidOperationException("Fixture is not initialized.");

    public async ValueTask InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await _container.StartAsync(timeout.Token).ConfigureAwait(false);

        _databaseName = $"TCJ_Outbox_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = _databaseName,
            TrustServerCertificate = true
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Time);
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(Behavior);
        services.AddScoped<IDomainEventDispatcher, TestDomainEventDispatcher>();
        services.AddTcjSqlServer<OutboxTestDbContext>(builder.ConnectionString, options =>
        {
            options.EnableRetryOnFailure = false;
            options.CommandTimeout = 30;
        });
        services.AddTcjOutboxEvent<TestDomainEvent>("test.changed.v1");
        services.AddTcjSqlServerOutbox<OutboxTestDbContext>(options =>
        {
            options.BatchSize = 5;
            options.PollingInterval = TimeSpan.FromMilliseconds(10);
            options.LockDuration = TimeSpan.FromSeconds(10);
            options.MaxRetryAttempts = 2;
            options.BaseRetryDelay = TimeSpan.FromSeconds(1);
            options.MaxRetryDelay = TimeSpan.FromSeconds(4);
            options.RetentionPeriod = TimeSpan.FromHours(1);
            options.CleanupBatchSize = 5;
            options.CleanupInterval = TimeSpan.FromMinutes(5);
        });

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using AsyncServiceScope scope = Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>();
        await context.Database.EnsureCreatedAsync(timeout.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
            _provider = null;
        }

        await _container.DisposeAsync().ConfigureAwait(false);
    }

    internal async Task ResetAsync()
    {
        Behavior.Reset();
        Time.Advance(TimeSpan.FromHours(3));
        await using AsyncServiceScope scope = Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>();
        await context.Set<TCJ.EntityFrameworkCore.Outbox.OutboxMessage>().ExecuteDeleteAsync().ConfigureAwait(false);
        await context.Entities.ExecuteDeleteAsync().ConfigureAwait(false);
    }

    internal async Task<Guid> PersistEventAsync(string marker = "ok", string? name = null)
    {
        await using AsyncServiceScope scope = Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OutboxTestDbContext>();
        var entity = new OutboxTestEntity(name ?? $"entity-{Guid.NewGuid():N}");
        entity.Change(marker, Time.GetUtcNow());
        context.Entities.Add(entity);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return context.Set<TCJ.EntityFrameworkCore.Outbox.OutboxMessage>()
            .Local
            .Single(message => message.EventType == "test.changed.v1")
            .Id;
    }

    private static string CreatePassword()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return $"H!{Convert.ToHexString(bytes)}a9";
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OutboxSqlServerCollection : ICollectionFixture<OutboxSqlServerFixture>
{
    public const string Name = "Transactional outbox SQL Server";
}

internal sealed class OutboxTestDbContext(DbContextOptions<OutboxTestDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
    internal DbSet<OutboxTestEntity> Entities => Set<OutboxTestEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<OutboxTestEntity>(builder =>
        {
            builder.ToTable("OutboxTestEntities");
            builder.HasKey(entity => entity.Id);
            builder.Property(entity => entity.Name).HasMaxLength(160).IsRequired();
            builder.HasIndex(entity => entity.Name).IsUnique();
            builder.Ignore(entity => entity.DomainEvents);
        });
        modelBuilder.AddTcjOutbox();
    }
}

internal sealed class OutboxTestEntity : Entity<Guid>
{
    private OutboxTestEntity() { }

    internal OutboxTestEntity(string name) : base(Guid.NewGuid())
    {
        Name = name;
    }

    public string Name { get; set; } = string.Empty;

    internal void Change(string marker, DateTimeOffset now) => AddDomainEvent(new TestDomainEvent(Id, marker, now));
}

public sealed record TestDomainEvent(Guid EntityId, string Marker, DateTimeOffset OccurredOn) : IDomainEvent;

internal sealed class TestDeliveryBehavior
{
    private readonly ConcurrentDictionary<string, int> _transientFailures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _permanentFailures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, int> _attempts = new();
    private readonly ConcurrentDictionary<Guid, byte> _sideEffects = new();
    private readonly ConcurrentBag<string> _deliveredMarkers = [];

    internal Action? BeforeDispatch { get; set; }
    internal int SideEffectCount => _sideEffects.Count;
    internal IReadOnlyCollection<string> DeliveredMarkers => _deliveredMarkers.ToArray();
    internal int AttemptCount(Guid messageId) => _attempts.TryGetValue(messageId, out int count) ? count : 0;
    internal bool HasSideEffect(Guid messageId) => _sideEffects.ContainsKey(messageId);
    internal void RecordExternalDelivery(Guid messageId)
    {
        _attempts.AddOrUpdate(messageId, 1, static (_, count) => count + 1);
        _sideEffects.TryAdd(messageId, 0);
    }
    internal void FailTransiently(string marker, int count) => _transientFailures[marker] = count;
    internal void FailPermanently(string marker) => _permanentFailures[marker] = 0;

    internal void Reset()
    {
        _transientFailures.Clear();
        _permanentFailures.Clear();
        _attempts.Clear();
        _sideEffects.Clear();
        while (_deliveredMarkers.TryTake(out _)) { }
        BeforeDispatch = null;
    }

    internal void Dispatch(TestDomainEvent domainEvent, Guid messageId)
    {
        _attempts.AddOrUpdate(messageId, 1, static (_, count) => count + 1);
        BeforeDispatch?.Invoke();

        if (_permanentFailures.ContainsKey(domainEvent.Marker))
        {
            throw new InvalidOperationException($"Permanent test failure for {domainEvent.Marker}.");
        }

        if (_transientFailures.TryGetValue(domainEvent.Marker, out int remaining) && remaining > 0)
        {
            _transientFailures[domainEvent.Marker] = remaining - 1;
            throw new TimeoutException($"Transient test failure for {domainEvent.Marker}.");
        }

        _sideEffects.TryAdd(messageId, 0);
        _deliveredMarkers.Add(domainEvent.Marker);
    }
}

internal sealed class TestDomainEventDispatcher(
    TestDeliveryBehavior behavior,
    IOutboxMessageContextAccessor contextAccessor) : IDomainEventDispatcher
{
    public Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OutboxMessageContext context = contextAccessor.Current
            ?? throw new InvalidOperationException("Outbox message context must be available during delivery.");

        foreach (IDomainEvent domainEvent in domainEvents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            behavior.Dispatch((TestDomainEvent)domainEvent, context.MessageId);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return Task.CompletedTask;
    }
}
