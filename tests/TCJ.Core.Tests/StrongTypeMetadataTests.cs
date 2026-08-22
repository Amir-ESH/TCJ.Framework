using TCJ.Core.StrongTypes;

namespace TCJ.Core.Tests;

public sealed class StrongTypeMetadataTests
{
    [Fact]
    public void Strongly_typed_id_attribute_can_describe_value_type()
    {
        var attribute = new StronglyTypedIdAttribute<Guid>();

        Assert.NotNull(attribute);
    }

    [Fact]
    public void Value_object_attribute_can_describe_value_type()
    {
        var attribute = new ValueObjectAttribute<string>();

        Assert.NotNull(attribute);
    }

    private readonly struct TestValue(Guid value) : IStronglyTypedValue<Guid>
    {
        public Guid Value { get; } = value;
    }
}
