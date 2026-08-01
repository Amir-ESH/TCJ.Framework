using TCJ.Core.DomainEvents;
using TCJ.Core.Entities;
using TCJ.Core.Identifiers;

namespace TCJ.Core.Tests;

public sealed class EntityAndIdentifierTests
{
    [Fact]
    public void Entity_collects_and_clears_domain_events()
    {
        var entity = new TestEntity(id: Guid.NewGuid());
        var domainEvent = new TestDomainEvent(DateTimeOffset.UtcNow);

        entity.Raise(domainEvent);

        Assert.Same(domainEvent, Assert.Single(entity.DomainEvents));

        entity.ClearDomainEvents();

        Assert.Empty(entity.DomainEvents);
    }

    [Fact]
    public void Version7_guid_uses_the_expected_version_nibble()
    {
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(year: 2026,
                                                                    month: 8,
                                                                    day: 1,
                                                                    hour: 12,
                                                                    minute: 30,
                                                                    second: 0,
                                                                    offset: TimeSpan.Zero));
        var generator = new GuidGenerator(timeProvider);

        Guid value = generator.CreateVersion7();

        Assert.Equal('7', value.ToString(format: "D")[14]);
    }

    private sealed class TestEntity(Guid id) : Entity<Guid>(id)
    {
        public void Raise(IDomainEvent domainEvent) => AddDomainEvent(domainEvent);
    }

    private sealed record TestDomainEvent(DateTimeOffset OccurredOn) : IDomainEvent;

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
