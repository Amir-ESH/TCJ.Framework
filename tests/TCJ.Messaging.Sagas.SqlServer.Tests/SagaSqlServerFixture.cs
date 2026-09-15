using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TCJ.Core.DomainEvents;
using TCJ.Core.Inbox;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Inbox.Extensions;
using TCJ.EntityFrameworkCore.Outbox.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Inbox.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Outbox.Extensions;
using TCJ.Messaging.Sagas.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.Messaging.Sagas.EntityFrameworkCore.Registration;
using Testcontainers.MsSql;

namespace TCJ.Messaging.Sagas.SqlServer.Tests;

public sealed class SagaSqlServerFixture : IAsyncLifetime
{
    internal const string Image = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
    private readonly MsSqlContainer _container = new MsSqlBuilder(Image).WithPassword(CreatePassword()).WithLabel("tcj.saga.tests", "true").WithCleanUp(true).Build();
    internal FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero));
    internal SagaTestBehavior Behavior { get; } = new();
    internal ServiceProvider Provider { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await _container.StartAsync(timeout.Token).ConfigureAwait(false);
        Provider = await CreateProviderAsync("TCJ_Saga_" + Guid.NewGuid().ToString("N"), timeout.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Provider is not null) await Provider.DisposeAsync().ConfigureAwait(false);
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    internal async Task ResetAsync()
    {
        Behavior.Reset();
        Time.Advance(TimeSpan.FromHours(2));
        await using AsyncServiceScope scope = Provider.CreateAsyncScope();
        SagaTestDbContext db = scope.ServiceProvider.GetRequiredService<SagaTestDbContext>();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [TCJ_SagaTimers]; DELETE FROM [TCJ_SagaCorrelations]; DELETE FROM [TCJ_SagaInstances]; DELETE FROM [TCJ_OutboxMessages]; DELETE FROM [TCJ_InboxMessages]; DELETE FROM [SagaBusinessRows];").ConfigureAwait(false);
    }

    internal IncomingMessageEnvelope Envelope<T>(string id, string type, T message) =>
        new(id, type, 1, "saga-tests", System.Text.Json.JsonSerializer.Serialize(message), Time.GetUtcNow(), "trace-correlation", null, null);

    private async Task<ServiceProvider> CreateProviderAsync(string databaseName, CancellationToken cancellationToken)
    {
        var connection = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = databaseName, TrustServerCertificate = true };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Time);
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(Behavior);
        services.AddScoped<IDomainEventDispatcher, SagaNoopDomainEventDispatcher>();
        services.AddTcjSqlServer<SagaTestDbContext>(connection.ConnectionString, options => { options.EnableRetryOnFailure = false; options.CommandTimeout = 30; });
        services.AddTcjOutboxEvent<SagaStartedEvent>("saga.started.v1");
        services.AddTcjOutboxEvent<SagaIncrementedEvent>("saga.incremented.v1");
        services.AddTcjOutboxEvent<SagaTimedOutEvent>("saga.timedout.v1");
        services.AddTcjOutboxEvent<SagaCompensatedEvent>("saga.compensated.v1");
        services.AddTcjSqlServerOutbox<SagaTestDbContext>(options => { options.BatchSize = 20; options.LockDuration = TimeSpan.FromSeconds(10); });
        services.AddTcjSqlServerInbox<SagaTestDbContext>(options =>
        {
            options.ConsumerName = "saga-tests";
            options.ProcessingMode = InboxProcessingMode.Inline;
            options.MaxRetryAttempts = 4;
            options.BaseRetryDelay = TimeSpan.FromMilliseconds(100);
            options.MaxRetryDelay = TimeSpan.FromSeconds(2);
            options.LockDuration = TimeSpan.FromSeconds(10);
        });
        services.AddTcjSqlServerSagas<SagaTestDbContext>(options =>
        {
            options.TimerLeaseDuration = TimeSpan.FromSeconds(5);
            options.TimerBatchSize = 10;
            options.MaxTimerAttempts = 3;
            options.TimerRetryBaseDelay = TimeSpan.FromMilliseconds(100);
            options.MaxCompensationAttempts = 3;
            options.CompensationRetryBaseDelay = TimeSpan.FromMilliseconds(100);
            options.TerminalRetentionPeriod = TimeSpan.FromMinutes(30);
            options.CleanupBatchSize = 10;
        });
        services.AddTcjSaga<SagaTestDbContext, OrderProcessSaga, OrderSagaState>(
            "order-process", 2, 2, SagaSqlJsonContext.Default.OrderSagaState, static () => new(), builder => builder
                .TerminalStates("completed", "failed", "compensating", "compensated")
                .StartsWith<StartOrder>("order.start", 1, "waiting", "orderId", static m => SagaCorrelationKey.From(m.OrderId))
                .Handles<IncrementOrder>("order.increment", 1, "orderId", static m => SagaCorrelationKey.From(m.OrderId), ["waiting"])
                .Handles<RequestCompensation>("order.compensate", 1, "orderId", static m => SagaCorrelationKey.From(m.OrderId), ["waiting"])
                .HandlesTimeout("payment", "order.payment.timeout", ["waiting"])
                .Compensates()
                .SupportsDefinitionVersion(1)
                .AddStateMigrator<StateV1ToV2Migrator>(1, 2));

        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SagaTestDbContext>().Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        return provider;
    }

    private static string CreatePassword() { Span<byte> bytes = stackalloc byte[18]; RandomNumberGenerator.Fill(bytes); return $"H!{Convert.ToHexString(bytes)}a9"; }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SagaSqlServerCollection : ICollectionFixture<SagaSqlServerFixture> { public const string Name = "Durable Saga SQL Server"; }

internal sealed class SagaTestDbContext(DbContextOptions<SagaTestDbContext> options) : DbContext(options), IReadDbContext, IWriteDbContext
{
    internal DbSet<SagaBusinessRow> BusinessRows => Set<SagaBusinessRow>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<SagaBusinessRow>(builder => { builder.ToTable("SagaBusinessRows"); builder.HasKey(x => x.OrderId); builder.Property(x => x.Value).HasMaxLength(128).IsRequired(); });
        modelBuilder.AddTcjInbox();
        modelBuilder.AddTcjOutbox();
        modelBuilder.AddTcjSqlServerSagas();
    }
}

internal sealed class SagaBusinessRow
{
    private SagaBusinessRow() { }
    internal SagaBusinessRow(Guid orderId, string value) { OrderId = orderId; Value = value; }
    public Guid OrderId { get; private set; }
    public string Value { get; internal set; } = string.Empty;
}

internal sealed class OrderSagaState : ISagaState
{
    public Guid OrderId { get; set; }
    public int Step { get; set; }
    public int SchemaMarker { get; set; } = 2;
}

internal sealed record StartOrder(Guid OrderId, bool FailAfterMutation = false);
internal sealed record IncrementOrder(Guid OrderId);
internal sealed record RequestCompensation(Guid OrderId);
internal sealed record SagaStartedEvent(Guid OrderId, DateTimeOffset OccurredOn) : IDomainEvent;
internal sealed record SagaIncrementedEvent(Guid OrderId, int Step, DateTimeOffset OccurredOn) : IDomainEvent;
internal sealed record SagaTimedOutEvent(Guid OrderId, DateTimeOffset OccurredOn) : IDomainEvent;
internal sealed record SagaCompensatedEvent(Guid OrderId, DateTimeOffset OccurredOn) : IDomainEvent;

internal sealed class SagaTestBehavior
{
    private int _timeoutFailures;
    private volatile bool _raceEnabled;
    private int _raceSequence;
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _raceArrivals = new();
    internal void Reset() { _timeoutFailures = 0; _raceEnabled = false; _raceSequence = 0; _raceArrivals.Clear(); }
    internal void FailNextTimeout() => Interlocked.Exchange(ref _timeoutFailures, 1);
    internal bool ConsumeTimeoutFailure() => Interlocked.Exchange(ref _timeoutFailures, 0) == 1;
    internal void EnableTwoPartyRace() { _raceArrivals.Clear(); _raceSequence = 0; _raceEnabled = true; }
    internal void DisableRace() => _raceEnabled = false;
    internal async Task ArriveAtRaceAsync()
    {
        if (!_raceEnabled) return;
        int index = Interlocked.Increment(ref _raceSequence);
        var current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _raceArrivals.TryAdd(index, current);
        if (_raceArrivals.Count >= 2)
        {
            foreach (TaskCompletionSource source in _raceArrivals.Values) source.TrySetResult();
        }
        await current.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }
}

internal sealed class OrderProcessSaga(SagaTestDbContext dbContext, SagaTestBehavior behavior) :
    ISagaStartsWith<OrderSagaState, StartOrder>,
    ISagaHandles<OrderSagaState, IncrementOrder>,
    ISagaHandles<OrderSagaState, RequestCompensation>,
    ISagaHandlesTimeout<OrderSagaState>,
    ISagaCompensates<OrderSagaState>
{
    public Task HandleAsync(OrderSagaState state, StartOrder message, SagaContext context, CancellationToken cancellationToken = default)
    {
        state.OrderId = message.OrderId;
        dbContext.BusinessRows.Add(new SagaBusinessRow(message.OrderId, "started"));
        context.ScheduleTimer("payment", context.UtcNow.AddMinutes(1));
        context.Emit(new SagaStartedEvent(message.OrderId, context.UtcNow));
        context.Stay();
        if (message.FailAfterMutation) throw new SagaTestValidationException();
        return Task.CompletedTask;
    }

    public async Task HandleAsync(OrderSagaState state, IncrementOrder message, SagaContext context, CancellationToken cancellationToken = default)
    {
        await behavior.ArriveAtRaceAsync().ConfigureAwait(false);
        state.Step++;
        SagaBusinessRow row = await dbContext.BusinessRows.SingleAsync(x => x.OrderId == message.OrderId, cancellationToken).ConfigureAwait(false);
        row.Value = $"step-{state.Step}";
        context.Emit(new SagaIncrementedEvent(message.OrderId, state.Step, context.UtcNow));
        context.Stay();
    }

    public Task HandleAsync(OrderSagaState state, RequestCompensation message, SagaContext context, CancellationToken cancellationToken = default)
    {
        context.RequestCompensation();
        return Task.CompletedTask;
    }

    public Task HandleTimeoutAsync(OrderSagaState state, SagaTimeout timeout, SagaContext context, CancellationToken cancellationToken = default)
    {
        if (behavior.ConsumeTimeoutFailure()) throw new TimeoutException("test timeout");
        context.Emit(new SagaTimedOutEvent(state.OrderId, context.UtcNow));
        context.Fail();
        return Task.CompletedTask;
    }

    public Task CompensateAsync(OrderSagaState state, SagaContext context, CancellationToken cancellationToken = default)
    {
        context.Emit(new SagaCompensatedEvent(state.OrderId, context.UtcNow));
        context.CompleteCompensation();
        return Task.CompletedTask;
    }
}

internal sealed class SagaTestValidationException : Exception;

internal sealed class SagaNoopDomainEventDispatcher : IDomainEventDispatcher
{
    public Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal sealed class StateV1ToV2Migrator : TCJ.Messaging.Sagas.Migration.ISagaStateMigrator
{
    public int FromVersion => 1;
    public int ToVersion => 2;
    public string Migrate(string payload)
    {
        OrderSagaState state = System.Text.Json.JsonSerializer.Deserialize(payload, SagaSqlJsonContext.Default.OrderSagaState) ?? throw new InvalidOperationException();
        state.SchemaMarker = 2;
        return System.Text.Json.JsonSerializer.Serialize(state, SagaSqlJsonContext.Default.OrderSagaState);
    }
}

[JsonSerializable(typeof(OrderSagaState))]
internal sealed partial class SagaSqlJsonContext : JsonSerializerContext;
