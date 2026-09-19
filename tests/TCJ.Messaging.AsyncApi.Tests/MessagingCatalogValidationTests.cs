namespace TCJ.Messaging.AsyncApi.Tests;

public sealed class MessagingCatalogValidationTests
{
    [Fact]
    public void Minimal_valid_catalog_passes()
    {
        var catalog = new MessagingCatalog
        {
            SchemaVersion = 1,
            Application = new MessagingApplication { Name = "app", Version = "1.0.0" },
            Document = new MessagingDocumentInfo { Title = "App Messaging", Version = "1.0.0" }
        };

        Assert.True(MessagingCatalogValidator.Validate(catalog).IsValid);
    }

    [Fact]
    public void Multi_transport_catalog_with_explicit_metadata_passes()
    {
        var catalog = MessagingCatalogTestData.CreateValidCatalog() with
        {
            Transports =
            [
                .. MessagingCatalogTestData.CreateValidCatalog().Transports,
                new MessagingTransport { Id = "commands", Kind = "broker", Protocol = "kafka", DeliverySemantics = MessagingDeliverySemantics.AtLeastOnce }
            ]
        };

        Assert.True(MessagingCatalogValidator.Validate(catalog).IsValid);
    }

    [Fact]
    public void Dynamic_destination_and_saga_metadata_pass()
    {
        var catalog = MessagingCatalogTestData.CreateValidCatalog();
        catalog = catalog with
        {
            Channels =
            [
                catalog.Channels[0] with
                {
                    DynamicDestination = new MessagingDynamicDestination { NamingStrategyId = "tenant-topic", Pattern = "tenant-{tenantId}.orders" }
                }
            ],
            Relationships =
            [
                new MessagingRelationship
                {
                    Id = "start-order-saga",
                    Kind = MessagingRelationshipKind.StartsSaga,
                    Source = new MessagingEntityReference { Kind = MessagingEntityKind.Consumer, Id = "billing-consumer" },
                    Target = new MessagingEntityReference { Kind = MessagingEntityKind.Channel, Id = "orders-created" },
                    Saga = new MessagingSagaReference { DefinitionId = "order-saga", DefinitionVersion = 2 }
                }
            ]
        };

        Assert.True(MessagingCatalogValidator.Validate(catalog).IsValid);
    }

    [Theory]
    [InlineData("producer")]
    [InlineData("consumer")]
    [InlineData("channel")]
    [InlineData("transport")]
    public void Duplicate_normalized_ids_are_rejected(string entity)
    {
        var catalog = MessagingCatalogTestData.CreateValidCatalog();
        catalog = entity switch
        {
            "producer" => catalog with { Producers = [catalog.Producers[0], catalog.Producers[0] with { Id = "ORDERS_PUBLISHER" }] },
            "consumer" => catalog with { Consumers = [catalog.Consumers[0], catalog.Consumers[0] with { Id = "BILLING_CONSUMER" }] },
            "channel" => catalog with { Channels = [catalog.Channels[0], catalog.Channels[0] with { Id = "ORDERS_CREATED" }] },
            _ => catalog with { Transports = [catalog.Transports[0], catalog.Transports[0] with { Id = "EVENTS" }] }
        };

        var result = MessagingCatalogValidator.Validate(catalog);

        Assert.Contains(result.Errors, x => x.Code == MessagingCatalogValidationCodes.DuplicateIdentifier);
    }

    [Theory]
    [InlineData("producer-transport")]
    [InlineData("producer-channel")]
    [InlineData("consumer-transport")]
    [InlineData("consumer-channel")]
    [InlineData("channel-transport")]
    [InlineData("security")]
    public void Unknown_references_are_rejected(string target)
    {
        var catalog = MessagingCatalogTestData.CreateValidCatalog();
        catalog = target switch
        {
            "producer-transport" => catalog with { Producers = [catalog.Producers[0] with { TransportId = "missing" }] },
            "producer-channel" => catalog with { Producers = [catalog.Producers[0] with { ChannelId = "missing" }] },
            "consumer-transport" => catalog with { Consumers = [catalog.Consumers[0] with { TransportId = "missing" }] },
            "consumer-channel" => catalog with { Consumers = [catalog.Consumers[0] with { ChannelId = "missing" }] },
            "channel-transport" => catalog with { Channels = [catalog.Channels[0] with { TransportId = "missing" }] },
            _ => catalog with { Transports = [catalog.Transports[0] with { SecuritySchemeId = "missing" }] }
        };

        Assert.Contains(MessagingCatalogValidator.Validate(catalog).Errors, x => x.Code == MessagingCatalogValidationCodes.UnknownReference);
    }

    [Fact]
    public void Invalid_relationship_endpoint_is_rejected()
    {
        var catalog = MessagingCatalogTestData.CreateValidCatalog() with
        {
            Relationships =
            [
                new MessagingRelationship
                {
                    Id = "invalid",
                    Kind = MessagingRelationshipKind.Consumes,
                    Source = new MessagingEntityReference { Kind = MessagingEntityKind.Consumer, Id = "missing" },
                    Target = new MessagingEntityReference { Kind = MessagingEntityKind.Channel, Id = "orders-created" }
                }
            ]
        };

        Assert.Contains(MessagingCatalogValidator.Validate(catalog).Errors, x => x.Code == MessagingCatalogValidationCodes.InvalidRelationship);
    }

    [Fact]
    public void Unsupported_schema_version_and_missing_identity_are_actionable()
    {
        var catalog = new MessagingCatalog
        {
            SchemaVersion = 2,
            Application = new MessagingApplication { Name = "", Version = "" },
            Document = new MessagingDocumentInfo { Title = "", Version = "" }
        };

        var result = MessagingCatalogValidator.Validate(catalog);

        Assert.Contains(result.Errors, x => x.Code == MessagingCatalogValidationCodes.UnsupportedSchemaVersion);
        Assert.Contains(result.Errors, x => x.Path == "$.application.name" && x.Code == MessagingCatalogValidationCodes.RequiredValue);
        Assert.Contains(result.Errors, x => x.Path == "$.document.title" && x.Code == MessagingCatalogValidationCodes.RequiredValue);
    }

    [Fact]
    public void Invalid_dynamic_destination_is_rejected()
    {
        var catalog = MessagingCatalogTestData.CreateValidCatalog() with
        {
            Channels = [MessagingCatalogTestData.CreateValidCatalog().Channels[0] with { DynamicDestination = new MessagingDynamicDestination { NamingStrategyId = "", Pattern = "" } }]
        };

        Assert.Contains(MessagingCatalogValidator.Validate(catalog).Errors, x => x.Code == MessagingCatalogValidationCodes.InvalidDynamicDestination);
    }

    [Theory]
    [InlineData("Server=sql;Password=super-secret;")]
    [InlineData("client_secret=super-secret")]
    [InlineData("SharedAccessSignature sr=x&sig=super-secret")]
    [InlineData("-----BEGIN PRIVATE KEY----- super-secret")]
    [InlineData("AccountKey=super-secret")]
    public void Secret_bearing_metadata_is_rejected_without_echoing_value(string secret)
    {
        var catalog = MessagingCatalogTestData.CreateValidCatalog() with
        {
            Application = MessagingCatalogTestData.CreateValidCatalog().Application with { Description = secret }
        };

        var error = Assert.Single(MessagingCatalogValidator.Validate(catalog).Errors, x => x.Code == MessagingCatalogValidationCodes.ProhibitedSecretMetadata);
        Assert.DoesNotContain("super-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Collection_and_string_bounds_are_enforced()
    {
        var producer = MessagingCatalogTestData.CreateValidCatalog().Producers[0];
        var catalog = MessagingCatalogTestData.CreateValidCatalog() with
        {
            Application = MessagingCatalogTestData.CreateValidCatalog().Application with { Description = new string('x', MessagingCatalogValidator.MaxStringLength + 1) },
            Producers = Enumerable.Range(0, MessagingCatalogValidator.MaxEntityCount + 1).Select(i => producer with { Id = $"producer-{i}" }).ToArray()
        };

        var errors = MessagingCatalogValidator.Validate(catalog).Errors;
        Assert.True(errors.Count(x => x.Code == MessagingCatalogValidationCodes.BoundExceeded) >= 2);
    }

    [Fact]
    public void Identifier_normalization_is_deterministic_for_bounded_fuzz_inputs()
    {
        var random = new Random(5302);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._- ";
        for (var i = 0; i < 256; i++)
        {
            var chars = Enumerable.Range(0, random.Next(1, 64)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();
            var value = new string(chars).Trim();
            if (string.IsNullOrWhiteSpace(value)) continue;
            var first = MessagingCatalogValidator.NormalizeIdentifier(value);
            var second = MessagingCatalogValidator.NormalizeIdentifier(value);
            Assert.Equal(first, second);
        }
    }
}
