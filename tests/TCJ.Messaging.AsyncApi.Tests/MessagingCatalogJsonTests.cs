using System.Text.Json;

namespace TCJ.Messaging.AsyncApi.Tests;

public sealed class MessagingCatalogJsonTests
{
    [Fact]
    public void Strongly_typed_catalog_round_trips_through_json()
    {
        var catalog = MessagingCatalogTestData.CreateValidCatalog();

        var json = MessagingCatalogJson.Serialize(catalog);
        var roundTrip = MessagingCatalogJson.Deserialize(json);

        Assert.True(MessagingCatalogValidator.Validate(roundTrip).IsValid);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"deliverySemantics\": \"AtLeastOnce\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("$type", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_property_is_rejected()
    {
        var json = """{"schemaVersion":1,"application":{"name":"app","version":"1"},"document":{"title":"doc","version":"1"},"credential":"secret"}""";
        Assert.Throws<JsonException>(() => MessagingCatalogJson.Deserialize(json));
    }

    [Fact]
    public void Unsupported_enum_value_is_rejected()
    {
        var json = """{"schemaVersion":1,"application":{"name":"app","version":"1"},"document":{"title":"doc","version":"1"},"transports":[{"id":"t","kind":"broker","protocol":"amqp","deliverySemantics":"ExactlyOnce"}]}""";
        Assert.Throws<JsonException>(() => MessagingCatalogJson.Deserialize(json));
    }

    [Theory]
    [InlineData("deliverySemantics", "ExactlyOnce")]
    [InlineData("orderingSemantics", "Global")]
    [InlineData("deadLetterSemantics", "Automatic")]
    public void Unsupported_transport_semantics_are_rejected(string property, string value)
    {
        var json = $$"""{"schemaVersion":1,"application":{"name":"app","version":"1"},"document":{"title":"doc","version":"1"},"transports":[{"id":"t","kind":"broker","protocol":"amqp","{{property}}":"{{value}}"}]}""";
        Assert.Throws<JsonException>(() => MessagingCatalogJson.Deserialize(json));
    }

    [Fact]
    public void Unsupported_lifecycle_is_rejected()
    {
        var json = """{"schemaVersion":1,"application":{"name":"app","version":"1"},"document":{"title":"doc","version":"1"},"channels":[{"id":"c","address":"orders","transportId":"t","lifecycle":"Removed"}]}""";
        Assert.Throws<JsonException>(() => MessagingCatalogJson.Deserialize(json));
    }

    [Theory]
    [InlineData(MessagingLifecycle.Draft)]
    [InlineData(MessagingLifecycle.Published)]
    [InlineData(MessagingLifecycle.Deprecated)]
    [InlineData(MessagingLifecycle.Retired)]
    public void Governed_lifecycle_values_round_trip(MessagingLifecycle lifecycle)
    {
        var catalog = MessagingCatalogTestData.CreateValidCatalog();
        catalog = catalog with { Channels = [catalog.Channels[0] with { Lifecycle = lifecycle }] };
        var roundTrip = MessagingCatalogJson.Deserialize(MessagingCatalogJson.Serialize(catalog));
        Assert.Equal(lifecycle, roundTrip.Channels[0].Lifecycle);
    }

    [Fact]
    public void Security_mechanism_wire_names_are_governed()
    {
        var catalog = MessagingCatalogTestData.CreateValidCatalog() with
        {
            SecuritySchemes = [new MessagingSecurityScheme { Id = "sasl", Mechanism = MessagingSecurityMechanism.SaslScram }]
        };

        Assert.Contains("\"SASL/SCRAM\"", MessagingCatalogJson.Serialize(catalog), StringComparison.Ordinal);
    }
}
