using Microsoft.EntityFrameworkCore;
using TCJ.AspNetCore.Extensions;
using TCJ.AspNetCore.Options;
using TCJ.Core.Entities;
#if TCJ_OUTBOX_SMOKE
using TCJ.Core.DomainEvents;
using TCJ.Core.Outbox;
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
#if TCJ_OUTBOX_SMOKE
using TCJ.EntityFrameworkCore.Outbox;
using TCJ.EntityFrameworkCore.Outbox.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Outbox.Extensions;
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
        builder.Services.AddTransient<IDomainEventHandler<SmokeChanged>, SmokeChangedHandler>();
#endif
#if TCJ_HEALTH_CHECK_SMOKE
        builder.Services.AddTcjHealthChecks()
            .AddTcjDependencyInjection()
            .AddTcjDomainEvents();
#endif

        string sqlServerConnection = Environment.GetEnvironmentVariable("TCJ_OUTBOX_SMOKE_CONNECTION")
            ?? "Server=localhost;Database=TCJ_PublishedPackageSmoke;User Id=sa;Password=NotUsed_123!;TrustServerCertificate=True";

        builder.Services.AddTcjSqlServer<SmokeDbContext>(
            sqlServerConnection,
            configureTcjSqlServer: options =>
                options.EnableRetryOnFailure = false);
#if TCJ_OUTBOX_SMOKE
        builder.Services.AddTcjOutboxEvent<SmokeChanged>("smoke.changed.v1");
        builder.Services.AddTcjSqlServerOutbox<SmokeDbContext>(options =>
        {
            options.BatchSize = 10;
            options.PollingInterval = TimeSpan.FromMilliseconds(50);
            options.LockDuration = TimeSpan.FromSeconds(10);
            options.MaxRetryAttempts = 1;
        });
#endif

        await using WebApplication app = builder.Build();

        app.UseTcjAspNetCore();
#if TCJ_HEALTH_CHECK_SMOKE
        app.MapTcjLivenessChecks();
        app.MapTcjReadinessChecks();
#endif

        await using AsyncServiceScope scope =
            app.Services.CreateAsyncScope();

        IServiceProvider services = scope.ServiceProvider;

        IGuidGenerator guidGenerator =
            services.GetRequiredService<IGuidGenerator>();

        Guid id = guidGenerator.CreateVersion7();
        Result<Guid> result = Result.Success(id);

        if (result.IsFailure || result.Value == Guid.Empty)
        {
            throw new InvalidOperationException(
                "TCJ.Core Result or GUID generation smoke check failed.");
        }

        _ = services.GetRequiredService<
            IReadRepository<SmokeEntity, Guid>>();

        _ = services.GetRequiredService<IUnitOfWork>();

        SmokeDbContext dbContext =
            services.GetRequiredService<SmokeDbContext>();

        if (dbContext.Model.FindEntityType(typeof(SmokeEntity)) is null)
        {
            throw new InvalidOperationException(
                "TCJ Entity Framework Core model smoke check failed.");
        }

        Type[] packageMarkerTypes =
        [
            typeof(Result),
            typeof(TcjDependencyInjectionOptions),
            typeof(IUnitOfWork),
            typeof(TcjSqlServerOptions),
            typeof(TcjAspNetCoreOptions)
        ];

        foreach (Type markerType in packageMarkerTypes)
        {
            string assemblyName =
                markerType.Assembly.GetName().Name
                ?? throw new InvalidOperationException(
                    "A package assembly has no name.");

            Console.WriteLine($"Loaded {assemblyName}");
        }


#if TCJ_RESILIENCE_SMOKE
        await VerifyPublishedResilienceAsync();
#endif

#if TCJ_OUTBOX_SMOKE
        await VerifyPublishedOutboxAsync(services);
#endif

#if TCJ_HEALTH_CHECK_SMOKE
        await VerifyPublishedHealthChecksAsync(app);
#endif

        Console.WriteLine(
            $"Published package smoke test succeeded. Generated UUID: {id}");
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
        OutboxProcessingResult result = await processor.ProcessBatchAsync().ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        OutboxMessage processed = await dbContext.Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Id == persisted.Id).ConfigureAwait(false);
        SmokeDeliveryProbe probe = services.GetRequiredService<SmokeDeliveryProbe>();
        if (result.ProcessedCount != 1 || processed.ProcessedAtUtc is null || probe.Count != 1)
        {
            throw new InvalidOperationException("Published transactional outbox processing smoke failed.");
        }

        Console.WriteLine("TCJ_OUTBOX_SMOKE succeeded for TCJ_OutboxMessages.");
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
            {
                throw new PublishedSmokeTransientException();
            }

            return Task.FromResult(42);
        }, "operation");

        if (result != 42 || attempts != 2 || detector.IsTransient(new ArgumentException("permanent")))
        {
            throw new InvalidOperationException("Published resilience retry/classification smoke check failed.");
        }

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
    public DbSet<SmokeEntity> SmokeEntities =>
        Set<SmokeEntity>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplySoftDeleteQueryFilters();
        modelBuilder.ApplyTcjSqlServerConventions();
#if TCJ_OUTBOX_SMOKE
        modelBuilder.AddTcjOutbox();
#endif
    }
}

public sealed class SmokeEntity
    : RowVersionFullAuditedEntity<Guid>
{
    private SmokeEntity()
    {
    }

    public SmokeEntity(Guid id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Name { get; private set; } =
        string.Empty;

#if TCJ_OUTBOX_SMOKE
    public void RaiseChanged(string marker, DateTimeOffset occurredOn)
        => AddDomainEvent(new SmokeChanged(Id, marker, occurredOn));
#endif
}

#if TCJ_OUTBOX_SMOKE
public sealed record SmokeChanged(Guid EntityId, string Marker, DateTimeOffset OccurredOn) : IDomainEvent;

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