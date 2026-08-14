using TCJ.Core.DomainEvents;
using TCJ.Core.Entities;
using TCJ.Core.Identifiers;

namespace TCJ.Core.Tests;

public sealed class EntityAndGuidBehaviorTests
{
    [Fact]
    public void Entity_exposes_key_and_supports_removing_one_event()
    {
        Guid id = Guid.NewGuid();
        var entity = new TestEntity(id);
        var first = new TestDomainEvent(DateTimeOffset.UtcNow);
        var second = new TestDomainEvent(DateTimeOffset.UtcNow.AddSeconds(1));

        entity.Raise(first);
        entity.Raise(second);

        Assert.Equal(id, entity.Id);
        Assert.Equal(id, entity.GetKey());
        Assert.True(entity.Remove(first));
        Assert.False(entity.Remove(first));
        Assert.Same(second, Assert.Single(entity.DomainEvents));
    }

    [Fact]
    public void Entity_rejects_null_domain_events()
    {
        var entity = new TestEntity(Guid.NewGuid());

        Assert.Throws<ArgumentNullException>(() => entity.Raise(null!));
        Assert.Throws<ArgumentNullException>(() => entity.Remove(null!));
    }

    [Fact]
    public void Entity_dto_has_mutable_identifier()
    {
        Guid id = Guid.NewGuid();
        var dto = new TestEntityDto { Id = id };

        Assert.Equal(id, dto.Id);
    }

    [Fact]
    public void Guid_generator_rejects_null_time_provider()
    {
        Assert.Throws<ArgumentNullException>(() => new GuidGenerator(null!));
    }

    [Fact]
    public void Create_returns_a_version_four_guid()
    {
        var generator = new GuidGenerator();

        Guid value = generator.Create();

        Assert.NotEqual(Guid.Empty, value);
        Assert.Equal('4', value.ToString("D")[14]);
    }

    [Fact]
    public void Version_seven_generation_uses_the_supplied_time_provider()
    {
        var firstGenerator = new GuidGenerator(new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 1, 12, 30, 0, TimeSpan.Zero)));
        var secondGenerator = new GuidGenerator(new FixedTimeProvider(
            new DateTimeOffset(2026, 8, 2, 12, 30, 0, TimeSpan.Zero)));

        Guid first = firstGenerator.CreateVersion7();
        Guid second = secondGenerator.CreateVersion7();

        Assert.Equal('7', first.ToString("D")[14]);
        Assert.Equal('7', second.ToString("D")[14]);
        Assert.True(string.CompareOrdinal(first.ToString("N"), second.ToString("N")) < 0);
    }

    private sealed class TestEntity(Guid id) : Entity<Guid>(id)
    {
        public void Raise(IDomainEvent domainEvent) => AddDomainEvent(domainEvent);
        public bool Remove(IDomainEvent domainEvent) => RemoveDomainEvent(domainEvent);
    }

    private sealed class TestEntityDto : EntityDto<Guid>
    {
    }

    private sealed record TestDomainEvent(DateTimeOffset OccurredOn) : IDomainEvent;

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
