using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.Outbox;
using TCJ.EntityFrameworkCore.Outbox.Extensions;

namespace TCJ.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("TCJ.Core", "TCJ.EntityFrameworkCore", "Outbox")]
public class OutboxBenchmarks
{
    private static readonly DateTimeOffset OccurredAt = DateTimeOffset.UnixEpoch;
    private ServiceProvider _withoutOutbox = null!;
    private ServiceProvider _withOutbox = null!;
    private IOutboxSerializer _serializer = null!;
    private string _payload = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var baselineServices = new ServiceCollection();
        baselineServices.AddTcjEntityFrameworkCore<BenchmarkDbContext>();
        _withoutOutbox = baselineServices.BuildServiceProvider();

        var outboxServices = new ServiceCollection();
        outboxServices.AddTcjEntityFrameworkCore<BenchmarkDbContext>();
        outboxServices.AddTcjOutboxEvent<BenchmarkDomainEvent>("benchmark.changed.v1");
        outboxServices.AddTcjOutbox<BenchmarkDbContext>();
        _withOutbox = outboxServices.BuildServiceProvider();
        _serializer = _withOutbox.GetRequiredService<IOutboxSerializer>();
        _payload = _serializer.Serialize(new BenchmarkDomainEvent(Guid.Empty, 1, OccurredAt));
    }

    [Benchmark(Baseline = true)]
    public int SaveChangesWithoutOutbox() => Save(_withoutOutbox, eventCount: 0);

    [Benchmark]
    public int SaveChangesWithOneEvent() => Save(_withOutbox, eventCount: 1);

    [Benchmark]
    public int SaveChangesWithFiveEvents() => Save(_withOutbox, eventCount: 5);

    [Benchmark]
    public string SerializeOneEvent() =>
        _serializer.Serialize(new BenchmarkDomainEvent(Guid.Empty, 1, OccurredAt));

    [Benchmark]
    public IDomainEvent DeserializeOneEvent() =>
        _serializer.Deserialize(typeof(BenchmarkDomainEvent), _payload);

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _withoutOutbox.Dispose();
        _withOutbox.Dispose();
    }

    private static int Save(ServiceProvider provider, int eventCount)
    {
        using IServiceScope scope = provider.CreateScope();
        var optionsBuilder = new DbContextOptionsBuilder<BenchmarkDbContext>();
        optionsBuilder.UseInMemoryDatabase("tcj-outbox-benchmark", new InMemoryDatabaseRoot());
        optionsBuilder.AddTcjPersistenceInterceptors(scope.ServiceProvider);
        using var context = new BenchmarkDbContext(optionsBuilder.Options);
        var entity = new BenchmarkEntity(Guid.NewGuid());
        entity.Raise(eventCount);
        context.Entities.Add(entity);
        return context.SaveChanges();
    }

    private sealed class BenchmarkDbContext(DbContextOptions<BenchmarkDbContext> options)
        : DbContext(options), IReadDbContext, IWriteDbContext
    {
        internal DbSet<BenchmarkEntity> Entities => Set<BenchmarkEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BenchmarkEntity>(builder =>
            {
                builder.HasKey(entity => entity.Id);
                builder.Ignore(entity => entity.DomainEvents);
            });
            modelBuilder.AddTcjOutbox();
        }
    }

    private sealed class BenchmarkEntity(Guid id) : Entity<Guid>(id)
    {
        internal void Raise(int count)
        {
            for (int index = 0; index < count; index++)
            {
                AddDomainEvent(new BenchmarkDomainEvent(Id, index, OccurredAt));
            }
        }
    }

    private sealed record BenchmarkDomainEvent(Guid EntityId, int Sequence, DateTimeOffset OccurredOn) : IDomainEvent;
}
