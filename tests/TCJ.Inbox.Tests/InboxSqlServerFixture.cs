using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TCJ.Core.DomainEvents;
using TCJ.Core.Entities;
using TCJ.Core.Inbox;
using TCJ.Core.Outbox;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Inbox;
using TCJ.EntityFrameworkCore.Inbox.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Inbox.Extensions;
using TCJ.EntityFrameworkCore.Outbox.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Outbox.Extensions;
using Testcontainers.MsSql;

namespace TCJ.Inbox.Tests;

public sealed class InboxSqlServerFixture : IAsyncLifetime
{
    internal const string Image = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
    private readonly MsSqlContainer _container = new MsSqlBuilder(Image).WithPassword(CreatePassword()).WithLabel("tcj.inbox.tests", "true").WithCleanUp(true).Build();
    internal FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 2, 4, 0, 0, TimeSpan.Zero));
    internal InboxTestBehavior Behavior { get; } = new();
    internal ServiceProvider InlineProvider { get; private set; } = null!;
    internal ServiceProvider DeferredProvider { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await _container.StartAsync(timeout.Token).ConfigureAwait(false);
        InlineProvider = await CreateProviderAsync("TCJ_Inbox_Inline_" + Guid.NewGuid().ToString("N"), InboxProcessingMode.Inline, timeout.Token).ConfigureAwait(false);
        DeferredProvider = await CreateProviderAsync("TCJ_Inbox_Deferred_" + Guid.NewGuid().ToString("N"), InboxProcessingMode.Deferred, timeout.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (InlineProvider is not null) await InlineProvider.DisposeAsync().ConfigureAwait(false);
        if (DeferredProvider is not null) await DeferredProvider.DisposeAsync().ConfigureAwait(false);
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    internal async Task ResetAsync()
    {
        Behavior.Reset();
        Time.Advance(TimeSpan.FromHours(3));
        await ResetProviderAsync(InlineProvider).ConfigureAwait(false);
        await ResetProviderAsync(DeferredProvider).ConfigureAwait(false);
    }

    internal IncomingMessageEnvelope Envelope(string id, string value = "ok", string type = "test.command", int version = 1, IReadOnlyDictionary<string, string>? headers = null, string? correlationId = null) =>
        new(id, type, version, "orders-api", System.Text.Json.JsonSerializer.Serialize(new TestInboxCommand(value)), Time.GetUtcNow(), correlationId, null, headers);

    private async Task<ServiceProvider> CreateProviderAsync(string databaseName, InboxProcessingMode mode, CancellationToken cancellationToken)
    {
        var cs = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = databaseName, TrustServerCertificate = true };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Time);
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(Behavior);
        services.AddScoped<IDomainEventDispatcher, NoopDispatcher>();
        services.AddTcjSqlServer<InboxTestDbContext>(cs.ConnectionString, options => { options.EnableRetryOnFailure = false; options.CommandTimeout = 30; });
        services.AddTcjOutboxEvent<TestInboxChangedEvent>("test.inbox.changed.v1");
        services.AddTcjSqlServerOutbox<InboxTestDbContext>(options => { options.BatchSize = 10; options.LockDuration = TimeSpan.FromSeconds(10); });
        services.AddTcjInboxMessage<TestInboxCommand>("test.command", 1);
        services.AddTcjInboxHandler<TestInboxCommand, TestInboxHandler>();
        services.AddTcjSqlServerInbox<InboxTestDbContext>(options =>
        {
            options.ConsumerName = "orders-api";
            options.ProcessingMode = mode;
            options.BatchSize = 10;
            options.LockDuration = TimeSpan.FromSeconds(10);
            options.MaxRetryAttempts = 2;
            options.BaseRetryDelay = TimeSpan.FromSeconds(1);
            options.MaxRetryDelay = TimeSpan.FromSeconds(4);
            options.RetentionPeriod = TimeSpan.FromHours(1);
            options.CleanupBatchSize = 5;
        });
        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<InboxTestDbContext>().Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        return provider;
    }

    private static async Task ResetProviderAsync(ServiceProvider provider)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        InboxTestDbContext db = scope.ServiceProvider.GetRequiredService<InboxTestDbContext>();
        await db.Set<TCJ.EntityFrameworkCore.Outbox.OutboxMessage>().ExecuteDeleteAsync().ConfigureAwait(false);
        await db.Set<InboxMessage>().ExecuteDeleteAsync().ConfigureAwait(false);
        await db.BusinessRows.ExecuteDeleteAsync().ConfigureAwait(false);
    }

    private static string CreatePassword() { Span<byte> bytes = stackalloc byte[18]; RandomNumberGenerator.Fill(bytes); return $"H!{Convert.ToHexString(bytes)}a9"; }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InboxSqlServerCollection : ICollectionFixture<InboxSqlServerFixture> { public const string Name = "Transactional Inbox SQL Server"; }

internal sealed class InboxTestDbContext(DbContextOptions<InboxTestDbContext> options) : DbContext(options), IReadDbContext, IWriteDbContext
{
    internal DbSet<InboxBusinessRow> BusinessRows => Set<InboxBusinessRow>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<InboxBusinessRow>(builder =>
        {
            builder.ToTable("InboxBusinessRows"); builder.HasKey(row => row.Id); builder.Property(row => row.MessageId).HasMaxLength(256).IsRequired(); builder.Property(row => row.Value).HasMaxLength(160).IsRequired(); builder.HasIndex(row => row.MessageId).IsUnique(); builder.Ignore(row => row.DomainEvents);
        });
        modelBuilder.AddTcjInbox();
        modelBuilder.AddTcjOutbox();
    }
}

internal sealed class InboxBusinessRow : Entity<Guid>
{
    private InboxBusinessRow() { }
    internal InboxBusinessRow(string messageId, string value, DateTimeOffset now) : base(Guid.NewGuid()) { MessageId = messageId; Value = value; AddDomainEvent(new TestInboxChangedEvent(Id, messageId, now)); }
    public string MessageId { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;
}

public sealed record TestInboxCommand(string Value);
public sealed record TestInboxChangedEvent(Guid EntityId, string MessageId, DateTimeOffset OccurredOn) : IDomainEvent;

internal sealed class InboxTestBehavior
{
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _transient = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _permanent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _saveThenFail = new(StringComparer.Ordinal);
    internal Action<string>? AfterCalled { get; set; }
    internal OutboxMessageContext? LastOutboxContext { get; private set; }
    internal int Calls(string messageId) => _calls.TryGetValue(messageId, out int value) ? value : 0;
    internal void Called(string messageId) { _calls.AddOrUpdate(messageId, 1, static (_, count) => count + 1); AfterCalled?.Invoke(messageId); }
    internal void FailTransiently(string value, int count) => _transient[value] = count;
    internal void FailPermanently(string value) => _permanent[value] = 0;
    internal void SaveThenFail(string value) => _saveThenFail[value] = 0;
    internal bool ShouldFailPermanent(string value) => _permanent.ContainsKey(value);
    internal bool ShouldSaveThenFail(string value) => _saveThenFail.ContainsKey(value);
    internal bool ConsumeTransient(string value)
    {
        while (_transient.TryGetValue(value, out int remaining) && remaining > 0)
        {
            if (_transient.TryUpdate(value, remaining - 1, remaining)) return true;
        }
        return false;
    }
    internal void RecordOutboxContext(OutboxMessageContext context) => LastOutboxContext = context;
    internal void Reset() { _calls.Clear(); _transient.Clear(); _permanent.Clear(); _saveThenFail.Clear(); AfterCalled = null; LastOutboxContext = null; }
}

internal sealed class TestInboxHandler(InboxTestDbContext db, InboxTestBehavior behavior, TimeProvider timeProvider) : IInboxMessageHandler<TestInboxCommand>
{
    public async Task HandleAsync(TestInboxCommand message, InboxMessageContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        behavior.Called(context.MessageId);
        cancellationToken.ThrowIfCancellationRequested();
        var row = new InboxBusinessRow(context.MessageId, message.Value, timeProvider.GetUtcNow());
        db.BusinessRows.Add(row);
        if (behavior.ShouldSaveThenFail(message.Value)) { await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); throw new TimeoutException("synthetic"); }
        if (behavior.ConsumeTransient(message.Value)) throw new TimeoutException("synthetic");
        if (behavior.ShouldFailPermanent(message.Value)) throw new InvalidOperationException("synthetic");
    }
}

internal sealed class NoopDispatcher(InboxTestBehavior behavior, IOutboxMessageContextAccessor contextAccessor) : IDomainEventDispatcher
{
    public Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OutboxMessageContext context = contextAccessor.Current ?? throw new InvalidOperationException("Outbox delivery context must be available.");
        behavior.RecordOutboxContext(context);
        return Task.CompletedTask;
    }
}
