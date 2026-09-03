using Microsoft.EntityFrameworkCore;
using TCJ.AspNetCore.Extensions;
using TCJ.AspNetCore.Options;
using TCJ.Core.Entities;
#if TCJ_OUTBOX_SMOKE || TCJ_INBOX_SMOKE
using TCJ.Core.DomainEvents;
using TCJ.Core.Outbox;
#endif
#if TCJ_INBOX_SMOKE
using System.Text.Json;
using TCJ.Core.Inbox;
#endif
#if TCJ_MESSAGING_SMOKE
using System.Text.Json.Serialization;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Integration;
using TCJ.Messaging.Publishing;
using TCJ.Messaging.Receiving;
#endif
using TCJ.Core.Identifiers;
using TCJ.Core.Results;
#if TCJ_RESILIENCE_SMOKE
using TCJ.Core.Resilience;
#endif
using TCJ.DependencyInjection.Extensions;
#if TCJ_HEALTH_CHECK_SMOKE
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TCJ.DependencyInjection.HealthChecks;
#endif
using TCJ.DependencyInjection.Registration;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;
#if TCJ_OUTBOX_SMOKE || TCJ_INBOX_SMOKE
using TCJ.EntityFrameworkCore.Outbox;
using TCJ.EntityFrameworkCore.Outbox.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Outbox.Extensions;
#endif
#if TCJ_INBOX_SMOKE
using TCJ.EntityFrameworkCore.Inbox;
using TCJ.EntityFrameworkCore.Inbox.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Inbox.Extensions;
#endif
using TCJ.EntityFrameworkCore.Repositories;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Options;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TCJ.PublishedPackages.SmokeTest;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                Args = args,
                EnvironmentName = Environments.Production
            });

        builder.Services.AddTcjDependencyInjection(typeof(Program).Assembly);
        builder.Services.AddTcjAspNetCore();
#if TCJ_OUTBOX_SMOKE
        builder.Services.AddSingleton<SmokeDeliveryProbe>();
#endif
#if TCJ_HEALTH_CHECK_SMOKE
        IHealthChecksBuilder healthChecks = builder.Services.AddTcjHealthChecks()
            .AddTcjDependencyInjection()
            .AddTcjDomainEvents();
#endif

        string sqlServerConnection = Environment.GetEnvironmentVariable("TCJ_OUTBOX_SMOKE_CONNECTION")
            ?? "Server=localhost;Database=TCJ_PublishedPackageSmoke;User Id=sa;Password=NotUsed_123!;TrustServerCertificate=True";

        builder.Services.AddTcjSqlServer<SmokeDbContext>(
            sqlServerConnection,
            configureTcjSqlServer: options =>
                options.EnableRetryOnFailure = false);
#if TCJ_OUTBOX_SMOKE || TCJ_INBOX_SMOKE
        builder.Services.AddTcjOutboxEvent<SmokeChanged>("smoke.changed.v1");
        builder.Services.AddTcjSqlServerOutbox<SmokeDbContext>(options =>
        {
            options.BatchSize = 10;
            options.PollingInterval = TimeSpan.FromMilliseconds(50);
            options.LockDuration = TimeSpan.FromSeconds(10);
            options.MaxRetryAttempts = 1;
        });
#endif
#if TCJ_INBOX_SMOKE
        builder.Services.AddTcjInboxMessage<SmokeInboundCommand>("smoke.inbound", version: 1);
        builder.Services.AddTcjInboxHandler<SmokeInboundCommand, SmokeInboundHandler>();
        builder.Services.AddTcjSqlServerInbox<SmokeDbContext>(options =>
        {
            options.ConsumerName = "published-package-smoke";
            options.ProcessingMode = InboxProcessingMode.Inline;
            options.MaxRetryAttempts = 1;
        });
#endif
#if TCJ_MESSAGING_SMOKE
        builder.Services.AddTcjMessaging(options => options.EnableConsumer = true);
        builder.Services.AddTcjMessage("smoke.inbound", 1, SmokeMessagingJsonContext.Default.SmokeInboundCommand);
        builder.Services.AddTcjMessage("smoke.changed", 1, SmokeMessagingJsonContext.Default.SmokeChanged);
        builder.Services.AddTcjInMemoryMessaging();
        builder.Services.AddTcjMessagingOutboxBridge();
#if TCJ_HEALTH_CHECK_SMOKE
        healthChecks.AddTcjMessagingHealthChecks();
#endif
#endif

        await using WebApplication app = builder.Build();

        app.UseTcjAspNetCore();
#if TCJ_HEALTH_CHECK_SMOKE
        app.MapTcjLivenessChecks();
        app.MapTcjReadinessChecks();
#endif

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;

#if TCJ_OUTBOX_SMOKE
        int smokeHandlerCount = services
            .GetServices<IDomainEventHandler<SmokeChanged>>()
            .Count();
        if (smokeHandlerCount != 1)
        {
            throw new InvalidOperationException(
                $"Published outbox smoke expected exactly one SmokeChanged handler registration, but found {smokeHandlerCount}.");
        }
#endif

        IGuidGenerator guidGenerator = services.GetRequiredService<IGuidGenerator>();
        Guid id = guidGenerator.CreateVersion7();
        Result<Guid> result = Result.Success(id);

        if (result.IsFailure || result.Value == Guid.Empty)
        {
            throw new InvalidOperationException("TCJ.Core Result or GUID generation smoke check failed.");
        }

        _ = services.GetRequiredService<IReadRepository<SmokeEntity, Guid>>();
        _ = services.GetRequiredService<IUnitOfWork>();

        SmokeDbContext dbContext = services.GetRequiredService<SmokeDbContext>();
        if (dbContext.Model.FindEntityType(typeof(SmokeEntity)) is null)
        {
            throw new InvalidOperationException("TCJ Entity Framework Core model smoke check failed.");
        }

        Type[] packageMarkerTypes =
        [
            typeof(Result),
            typeof(TcjDependencyInjectionOptions),
            typeof(IUnitOfWork),
            typeof(TcjSqlServerOptions),
            typeof(TcjAspNetCoreOptions)
#if TCJ_MESSAGING_SMOKE
            , typeof(TCJ.Messaging.Configuration.TcjMessagingOptions)
#endif
        ];

        foreach (Type markerType in packageMarkerTypes)
        {
            string assemblyName = markerType.Assembly.GetName().Name
                ?? throw new InvalidOperationException("A package assembly has no name.");
            Console.WriteLine($"Loaded {assemblyName}");
        }

#if TCJ_RESILIENCE_SMOKE
        await VerifyPublishedResilienceAsync();
#endif

#if TCJ_OUTBOX_SMOKE
        await VerifyPublishedOutboxAsync(services);
#endif

#if TCJ_INBOX_SMOKE
        await VerifyPublishedInboxAsync(services);
#endif

#if TCJ_MESSAGING_SMOKE
        await VerifyPublishedMessagingAsync(services);
#endif

#if TCJ_HEALTH_CHECK_SMOKE
        await VerifyPublishedHealthChecksAsync(app);
#endif

        Console.WriteLine($"Published package smoke test succeeded. Generated UUID: {id}");
    }

#if TCJ_OUTBOX_SMOKE
    private static async Task VerifyPublishedOutboxAsync(IServiceProvider services)
    {
        SmokeDbContext dbContext = services.GetRequiredService<SmokeDbContext>();
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        var entity = new SmokeEntity(Guid.CreateVersion7(), "outbox-smoke");
        entity.RaiseChanged("published", DateTimeOffset.UtcNow);
        dbContext.SmokeEntities.Add(entity);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        OutboxMessage persisted = await dbContext.Set<OutboxMessage>().AsNoTracking().SingleAsync().ConfigureAwait(false);
        if (persisted.EventType != "smoke.changed.v1" || persisted.Id == Guid.Empty)
        {
            throw new InvalidOperationException("Published transactional outbox persistence smoke failed for TCJ_OutboxMessages.");
        }

        IOutboxProcessor processor = services.GetRequiredService<IOutboxProcessor>();
        OutboxProcessingResult processing = await processor.ProcessBatchAsync().ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        OutboxMessage processed = await dbContext.Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Id == persisted.Id).ConfigureAwait(false);
#if TCJ_MESSAGING_SMOKE
        IMessageReceiver receiver = services.GetRequiredService<IMessageReceiver>();
        using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ReceivedMessage> enumerator = receiver
            .ReceiveAsync(new ReceiveContext { Source = "smoke.changed.v1" }, receiveTimeout.Token)
            .GetAsyncEnumerator(receiveTimeout.Token);
        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("Published TCJ.Messaging Outbox bridge produced no transport delivery.");
        }
        ReceivedMessage received = enumerator.Current;
        await received.Settlement.CompleteAsync(receiveTimeout.Token).ConfigureAwait(false);
        if (processing.ProcessedCount != 1 || processed.ProcessedAtUtc is null
            || received.Envelope.MessageId != persisted.Id.ToString("D")
            || received.Envelope.MessageType != "smoke.changed"
            || received.Envelope.MessageVersion != 1)
        {
            throw new InvalidOperationException("Published TCJ.Messaging Outbox publishing smoke failed.");
        }
        Console.WriteLine("TCJ_MESSAGING_SMOKE published a persisted Outbox message through the neutral transport contract.");
#else
        SmokeDeliveryProbe probe = services.GetRequiredService<SmokeDeliveryProbe>();
        if (processing.ProcessedCount != 1 || processed.ProcessedAtUtc is null || probe.Count != 1)
        {
            throw new InvalidOperationException("Published transactional outbox processing smoke failed.");
        }
#endif

        Console.WriteLine("TCJ_OUTBOX_SMOKE succeeded for TCJ_OutboxMessages.");
    }
#endif

#if TCJ_INBOX_SMOKE
    private static async Task VerifyPublishedInboxAsync(IServiceProvider services)
    {
        SmokeDbContext dbContext = services.GetRequiredService<SmokeDbContext>();
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        const string messageId = "published-inbox-message-1";
        string payload = JsonSerializer.Serialize(new SmokeInboundCommand("inbox-smoke"));
        var envelope = new IncomingMessageEnvelope(
            messageId,
            "smoke.inbound",
            1,
            "published-package-smoke",
            payload,
            DateTimeOffset.UtcNow,
            correlationId: "published-inbox-correlation");

        IInboxPipeline pipeline = services.GetRequiredService<IInboxPipeline>();
        InboxHandlingResult first = await pipeline.ProcessAsync(envelope).ConfigureAwait(false);
        InboxHandlingResult duplicate = await pipeline.ProcessAsync(envelope).ConfigureAwait(false);

        dbContext.ChangeTracker.Clear();
        int businessRows = await dbContext.SmokeEntities.AsNoTracking().CountAsync(entity => entity.Name == "inbox-smoke").ConfigureAwait(false);
        InboxMessage inbox = await dbContext.Set<InboxMessage>().AsNoTracking().SingleAsync(message =>
            message.ConsumerName == "published-package-smoke" && message.MessageId == messageId).ConfigureAwait(false);
        OutboxMessage outbound = await dbContext.Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.CausationId == messageId).ConfigureAwait(false);

        if (first.Outcome != InboxHandlingOutcome.Acknowledge
            || duplicate.Outcome != InboxHandlingOutcome.IgnoreDuplicate
            || businessRows != 1
            || inbox.ProcessedAtUtc is null
            || outbound.CorrelationId != "published-inbox-correlation"
            || outbound.CausationId != messageId)
        {
            throw new InvalidOperationException("Published transactional Inbox/Outbox idempotency smoke failed.");
        }

        Console.WriteLine("TCJ_INBOX_SMOKE succeeded for TCJ_InboxMessages with one business effect and one causal Outbox row.");
    }
#endif

#if TCJ_MESSAGING_SMOKE
    private static async Task VerifyPublishedMessagingAsync(IServiceProvider services)
    {
        const string transportSource = "published-messaging-inbox";
        const string messageId = "published-messaging-inbox-1";
        const string correlationId = "published-messaging-correlation";
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new SmokeInboundCommand("messaging-inbox-smoke"),
            SmokeMessagingJsonContext.Default.SmokeInboundCommand);
        var envelope = new TransportMessageEnvelope(
            messageId,
            "smoke.inbound",
            1,
            payload,
            "application/json",
            DateTimeOffset.UtcNow,
            correlationId: correlationId,
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
                ["authorization"] = "must-not-propagate"
            });

        IMessagePublisher publisher = services.GetRequiredService<IMessagePublisher>();
        PublishResult firstPublish = await publisher.PublishAsync(envelope, new PublishContext { Destination = transportSource }).ConfigureAwait(false);
        PublishResult duplicatePublish = await publisher.PublishAsync(envelope, new PublishContext { Destination = transportSource }).ConfigureAwait(false);
        if (!firstPublish.IsSuccess || !duplicatePublish.IsSuccess)
        {
            throw new InvalidOperationException("Published TCJ.Messaging in-memory publish smoke failed.");
        }

        IMessageReceiver receiver = services.GetRequiredService<IMessageReceiver>();
        InboxTransportBridge bridge = services.GetRequiredService<InboxTransportBridge>();
        using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ReceivedMessage> enumerator = receiver
            .ReceiveAsync(new ReceiveContext { Source = transportSource }, receiveTimeout.Token)
            .GetAsyncEnumerator(receiveTimeout.Token);

        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Published TCJ.Messaging first Inbox delivery was not received.");
        InboxTransportBridgeResult first = await bridge.ProcessAsync(enumerator.Current, receiveTimeout.Token).ConfigureAwait(false);

        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            throw new InvalidOperationException("Published TCJ.Messaging duplicate Inbox delivery was not received.");
        InboxTransportBridgeResult duplicate = await bridge.ProcessAsync(enumerator.Current, receiveTimeout.Token).ConfigureAwait(false);

        SmokeDbContext dbContext = services.GetRequiredService<SmokeDbContext>();
        dbContext.ChangeTracker.Clear();
        int businessRows = await dbContext.SmokeEntities.AsNoTracking()
            .CountAsync(entity => entity.Name == "messaging-inbox-smoke")
            .ConfigureAwait(false);
        InboxMessage inbox = await dbContext.Set<InboxMessage>().AsNoTracking().SingleAsync(message =>
            message.ConsumerName == "published-package-smoke" && message.MessageId == messageId).ConfigureAwait(false);
        OutboxMessage outbound = await dbContext.Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.CausationId == messageId).ConfigureAwait(false);

        if (first.InboxResult.Outcome != InboxHandlingOutcome.Acknowledge
            || first.Settlement != MessageSettlement.Complete
            || duplicate.InboxResult.Outcome != InboxHandlingOutcome.IgnoreDuplicate
            || duplicate.Settlement != MessageSettlement.Complete
            || businessRows != 1
            || inbox.ProcessedAtUtc is null
            || outbound.CorrelationId != correlationId)
        {
            throw new InvalidOperationException("Published TCJ.Messaging transport-to-Inbox duplicate smoke failed.");
        }

        IOutboxProcessor processor = services.GetRequiredService<IOutboxProcessor>();
        OutboxProcessingResult outboxResult = await processor.ProcessBatchAsync().ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        OutboxMessage processed = await dbContext.Set<OutboxMessage>().AsNoTracking()
            .SingleAsync(message => message.Id == outbound.Id)
            .ConfigureAwait(false);
        if (outboxResult.ProcessedCount < 1 || processed.ProcessedAtUtc is null)
        {
            throw new InvalidOperationException("Published TCJ.Messaging Inbox-to-Outbox publishing smoke did not mark the persisted message processed.");
        }

        bool foundOutbound = false;
        using var outboxReceiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ReceivedMessage> outboxEnumerator = receiver
            .ReceiveAsync(new ReceiveContext { Source = "smoke.changed.v1" }, outboxReceiveTimeout.Token)
            .GetAsyncEnumerator(outboxReceiveTimeout.Token);
        for (int attempt = 0; attempt < outboxResult.ProcessedCount; attempt++)
        {
            if (!await outboxEnumerator.MoveNextAsync().ConfigureAwait(false))
                break;
            ReceivedMessage received = outboxEnumerator.Current;
            await received.Settlement.CompleteAsync(outboxReceiveTimeout.Token).ConfigureAwait(false);
            if (received.Envelope.MessageId == outbound.Id.ToString("D"))
            {
                foundOutbound = received.Envelope.CorrelationId == correlationId
                    && received.Envelope.CausationId == messageId
                    && !received.Envelope.Headers.ContainsKey("authorization");
            }
        }
        if (!foundOutbound)
        {
            throw new InvalidOperationException("Published TCJ.Messaging Inbox-to-Outbox transport envelope did not preserve safe identity/correlation metadata.");
        }

        IMessageConsumerRunner runner = services.GetRequiredService<IMessageConsumerRunner>();
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await runner.RunAsync(new ReceiveContext { Source = "published-messaging-graceful-shutdown" }, shutdown.Token).ConfigureAwait(false);

        Console.WriteLine("TCJ_MESSAGING_SMOKE succeeded for package restore, in-memory publish/receive, Inbox duplicate settlement, Outbox publishing, safe headers, and graceful shutdown.");
    }
#endif

#if TCJ_HEALTH_CHECK_SMOKE
    private static async Task VerifyPublishedHealthChecksAsync(WebApplication app)
    {
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync().ConfigureAwait(false);
        try
        {
            IServer server = app.Services.GetRequiredService<IServer>();
            string address = server.Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("Published health-check smoke could not resolve its listening address.");
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            foreach (string path in new[] { "/health/live", "/health/ready" })
            {
                using HttpResponseMessage response = await client.GetAsync(path).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || !body.Contains("Healthy", StringComparison.Ordinal)
                    || body.Contains("Password=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Published health-check endpoint smoke failed for {path}.");
                }
            }
            Console.WriteLine("TCJ_HEALTH_CHECK_SMOKE succeeded.");
        }
        finally
        {
            await app.StopAsync().ConfigureAwait(false);
        }
    }
#endif

#if TCJ_RESILIENCE_SMOKE
    private static async Task VerifyPublishedResilienceAsync()
    {
        var detector = new TransientFailureDetector([new PublishedSmokeTransientClassifier()]);
        var policy = new TcjRetryPolicy(detector, new TcjRetryOptions
        {
            MaxRetryAttempts = 1,
            BaseDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero,
            UseJitter = false
        });
        int attempts = 0;
        int result = await policy.ExecuteAsync(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new PublishedSmokeTransientException();
            return Task.FromResult(42);
        }, "operation");

        if (result != 42 || attempts != 2 || detector.IsTransient(new ArgumentException("permanent")))
            throw new InvalidOperationException("Published resilience retry/classification smoke check failed.");

        Console.WriteLine("TCJ_RESILIENCE_SMOKE succeeded.");
    }

    private sealed class PublishedSmokeTransientException : Exception { }

    private sealed class PublishedSmokeTransientClassifier : ITransientFailureClassifier
    {
        public bool IsTransient(Exception exception) => exception is PublishedSmokeTransientException;
    }
#endif
}

public sealed class SmokeDbContext(
    DbContextOptions<SmokeDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
    public DbSet<SmokeEntity> SmokeEntities => Set<SmokeEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySoftDeleteQueryFilters();
        modelBuilder.ApplyTcjSqlServerConventions();
#if TCJ_OUTBOX_SMOKE || TCJ_INBOX_SMOKE
        modelBuilder.AddTcjOutbox();
#endif
#if TCJ_INBOX_SMOKE
        modelBuilder.AddTcjInbox();
#endif
    }
}

public sealed class SmokeEntity : RowVersionFullAuditedEntity<Guid>
{
    private SmokeEntity() { }

    public SmokeEntity(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Name { get; private set; } = string.Empty;

#if TCJ_OUTBOX_SMOKE || TCJ_INBOX_SMOKE
    public void RaiseChanged(string marker, DateTimeOffset occurredOn)
        => AddDomainEvent(new SmokeChanged(Id, marker, occurredOn));
#endif
}

#if TCJ_OUTBOX_SMOKE || TCJ_INBOX_SMOKE
public sealed record SmokeChanged(Guid EntityId, string Marker, DateTimeOffset OccurredOn) : IDomainEvent;

#if TCJ_OUTBOX_SMOKE
public sealed class SmokeDeliveryProbe
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public void Record() => Interlocked.Increment(ref _count);
}

public sealed class SmokeChangedHandler(SmokeDeliveryProbe probe) : IDomainEventHandler<SmokeChanged>
{
    public Task HandleAsync(SmokeChanged domainEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        probe.Record();
        return Task.CompletedTask;
    }
}
#endif

#if TCJ_INBOX_SMOKE
public sealed record SmokeInboundCommand(string Name);

public sealed class SmokeInboundHandler(SmokeDbContext dbContext) : IInboxMessageHandler<SmokeInboundCommand>
{
    public Task HandleAsync(SmokeInboundCommand message, InboxMessageContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entity = new SmokeEntity(Guid.CreateVersion7(), message.Name);
        entity.RaiseChanged("inbox", DateTimeOffset.UtcNow);
        dbContext.SmokeEntities.Add(entity);
        return Task.CompletedTask;
    }
}
#endif
#endif

#if TCJ_MESSAGING_SMOKE
[JsonSerializable(typeof(SmokeInboundCommand))]
[JsonSerializable(typeof(SmokeChanged))]
internal sealed partial class SmokeMessagingJsonContext : JsonSerializerContext;
#endif
