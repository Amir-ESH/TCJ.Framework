using System.Text;
using System.Text.Json;

namespace TCJ.Messaging.AsyncApi.Tests;

public sealed class TcjAsyncApiExtensionTests
{
    [Fact]
    public void Extension_schema_is_versioned_closed_and_governs_exact_supported_names()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "asyncapi-tcj-extensions.schema.json")));
        JsonElement root = schema.RootElement;
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(TcjAsyncApiExtensions.Version, root.GetProperty("properties").GetProperty(TcjAsyncApiExtensions.VersionName).GetProperty("const").GetString());
        string[] names = root.GetProperty("properties").EnumerateObject().Select(static property => property.Name).ToArray();
        Assert.Equal(16, names.Length);
        Assert.Contains(TcjAsyncApiExtensions.UpcasterPath, names);
        Assert.Contains(TcjAsyncApiExtensions.DynamicDestination, names);
    }

    [Fact]
    public void Generated_extensions_preserve_governed_contract_identity_and_semantics()
    {
        MessagingCatalog catalog = MessagingCatalogTestData.CreateValidCatalog();
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved);

        Assert.True(result.IsValid);
        using JsonDocument document = JsonDocument.Parse(result.Utf8Json);
        JsonElement message = document.RootElement.GetProperty("components").GetProperty("messages").GetProperty("orders-created-v1");
        Assert.Equal(TcjAsyncApiExtensions.Version, message.GetProperty(TcjAsyncApiExtensions.VersionName).GetString());
        Assert.Equal("orders.created", message.GetProperty(TcjAsyncApiExtensions.ContractId).GetString());
        Assert.Equal(1, message.GetProperty(TcjAsyncApiExtensions.ContractVersion).GetInt32());
        Assert.Equal(resolved.Contracts[0].SchemaFingerprint.Value, message.GetProperty(TcjAsyncApiExtensions.SchemaFingerprint).GetProperty("value").GetString());
        Assert.Equal("synthetic-test-owner", message.GetProperty(TcjAsyncApiExtensions.Owner).GetString());
        Assert.Equal("Backward", message.GetProperty(TcjAsyncApiExtensions.Compatibility).GetProperty("mode").GetString());
        Assert.Equal("Internal", message.GetProperty(TcjAsyncApiExtensions.DataClassification)[0].GetString());
        Assert.DoesNotContain("System.", message.GetProperty(TcjAsyncApiExtensions.ContractId).GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void Inbox_outbox_delivery_retry_dead_letter_and_ordering_remain_distinct()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with
        {
            Producers = [baseline.Producers[0] with { RetryOwner = MessagingRetryOwner.Outbox }],
            Consumers = [baseline.Consumers[0] with { RetryOwner = MessagingRetryOwner.Inbox, DeadLetterSemantics = MessagingDeadLetterSemantics.ConventionBased }]
        };
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved);

        Assert.True(result.IsValid);
        using JsonDocument document = JsonDocument.Parse(result.Utf8Json);
        JsonElement operations = document.RootElement.GetProperty("operations");
        JsonElement producer = operations.EnumerateObject().Single(static item => item.Value.GetProperty("action").GetString() == "send").Value;
        JsonElement consumer = operations.EnumerateObject().Single(static item => item.Value.GetProperty("action").GetString() == "receive").Value;

        Assert.Equal("AtLeastOnce", producer.GetProperty(TcjAsyncApiExtensions.DeliverySemantics).GetString());
        Assert.Equal("Outbox", producer.GetProperty(TcjAsyncApiExtensions.RetryOwner).GetString());
        Assert.Equal("DurableAtLeastOnce", producer.GetProperty(TcjAsyncApiExtensions.Outbox).GetProperty("publicationModel").GetString());
        Assert.Equal("PerSession", producer.GetProperty(TcjAsyncApiExtensions.OrderingScope).GetProperty("scope").GetString());
        Assert.Equal("Inbox", consumer.GetProperty(TcjAsyncApiExtensions.RetryOwner).GetString());
        Assert.Equal("ConventionBased", consumer.GetProperty(TcjAsyncApiExtensions.DeadLetter).GetString());
        Assert.True(consumer.GetProperty(TcjAsyncApiExtensions.Inbox).GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain("ExactlyOnce", Encoding.UTF8.GetString(result.Utf8Json.Span), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MessagingDeliverySemantics.AtLeastOnce, "AtLeastOnce")]
    [InlineData(MessagingDeliverySemantics.AtMostOnce, "AtMostOnce")]
    [InlineData(MessagingDeliverySemantics.BestEffort, "BestEffort")]
    [InlineData(MessagingDeliverySemantics.TransportSpecific, "TransportSpecific")]
    public void Governed_delivery_values_are_emitted(MessagingDeliverySemantics semantics, string expected)
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with { Transports = [baseline.Transports[0] with { DeliverySemantics = semantics }] };
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved);

        using JsonDocument document = JsonDocument.Parse(result.Utf8Json);
        foreach (JsonProperty operation in document.RootElement.GetProperty("operations").EnumerateObject())
            Assert.Equal(expected, operation.Value.GetProperty(TcjAsyncApiExtensions.DeliverySemantics).GetString());
    }

    [Fact]
    public void Dynamic_destination_emits_strategy_metadata_without_concrete_runtime_destination()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        var dynamicDestination = new MessagingDynamicDestination { NamingStrategyId = "tenant-topic-strategy", Pattern = "orders.{tenant}.created", Description = "Logical tenant-scoped pattern." };
        MessagingCatalog catalog = baseline with
        {
            Channels = [baseline.Channels[0] with { DynamicDestination = dynamicDestination, Address = "logical-orders-created" }],
            Producers = [baseline.Producers[0] with { DynamicDestination = dynamicDestination }]
        };
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved);

        Assert.True(result.IsValid);
        using JsonDocument document = JsonDocument.Parse(result.Utf8Json);
        JsonElement channel = document.RootElement.GetProperty("channels").GetProperty("orders-created");
        Assert.Equal(JsonValueKind.Null, channel.GetProperty("address").ValueKind);
        JsonElement extension = channel.GetProperty(TcjAsyncApiExtensions.DynamicDestination);
        Assert.True(extension.GetProperty("dynamic").GetBoolean());
        Assert.Equal("tenant-topic-strategy", extension.GetProperty("namingStrategyId").GetString());
        Assert.Equal("orders.{tenant}.created", extension.GetProperty("pattern").GetString());
    }

    [Theory]
    [InlineData(MessagingRelationshipKind.StartsSaga, "Start")]
    [InlineData(MessagingRelationshipKind.ContinuesSaga, "Continue")]
    [InlineData(MessagingRelationshipKind.TimesOutSaga, "Timeout")]
    [InlineData(MessagingRelationshipKind.CompensatesSaga, "Compensation")]
    [InlineData(MessagingRelationshipKind.CompletesSaga, "Completion")]
    [InlineData(MessagingRelationshipKind.FailsSaga, "Failure")]
    public void Saga_relationships_emit_definition_metadata_only(MessagingRelationshipKind kind, string expectedRelationship)
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with
        {
            Relationships =
            [
                new MessagingRelationship
                {
                    Id = "saga-relation",
                    Kind = kind,
                    Source = new MessagingEntityReference { Kind = MessagingEntityKind.Producer, Id = baseline.Producers[0].Id },
                    Target = new MessagingEntityReference { Kind = MessagingEntityKind.Channel, Id = baseline.Channels[0].Id },
                    Saga = new MessagingSagaReference { DefinitionId = "order-saga", DefinitionVersion = 3 }
                }
            ]
        };
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved);

        Assert.True(result.IsValid);
        using JsonDocument document = JsonDocument.Parse(result.Utf8Json);
        JsonElement producer = document.RootElement.GetProperty("operations").EnumerateObject().Single(static item => item.Value.GetProperty("action").GetString() == "send").Value;
        JsonElement saga = producer.GetProperty(TcjAsyncApiExtensions.Saga)[0];
        Assert.Equal("order-saga", saga.GetProperty("definitionId").GetString());
        Assert.Equal(3, saga.GetProperty("definitionVersion").GetInt32());
        Assert.Equal(expectedRelationship, saga.GetProperty("relationship").GetString());
        Assert.False(saga.TryGetProperty("instanceId", out _));
        Assert.False(saga.TryGetProperty("correlationValue", out _));
        Assert.False(saga.TryGetProperty("state", out _));
    }

    [Fact]
    public void Extension_validator_rejects_missing_version_unknown_names_invalid_enums_and_secret_metadata()
    {
        using JsonDocument missingVersion = JsonDocument.Parse("""{"x-tcj-owner":"team"}""");
        using JsonDocument unknown = JsonDocument.Parse("""{"x-tcj-extension-version":"1.0","x-tcj-unknown":true}""");
        using JsonDocument invalidEnum = JsonDocument.Parse("""{"x-tcj-extension-version":"1.0","x-tcj-delivery-semantics":"ExactlyOnce"}""");
        using JsonDocument secret = JsonDocument.Parse("""{"x-tcj-extension-version":"1.0","x-tcj-owner":"connectionString=secret"}""");

        Assert.Contains(TcjAsyncApiExtensionValidator.Validate(missingVersion.RootElement).Errors, static error => error.Code == TcjAsyncApiExtensionValidationCodes.MissingVersion);
        Assert.Contains(TcjAsyncApiExtensionValidator.Validate(unknown.RootElement).Errors, static error => error.Code == TcjAsyncApiExtensionValidationCodes.UnknownExtension);
        Assert.Contains(TcjAsyncApiExtensionValidator.Validate(invalidEnum.RootElement).Errors, static error => error.Code == TcjAsyncApiExtensionValidationCodes.InvalidStructure);
        TcjAsyncApiExtensionValidationResult secretResult = TcjAsyncApiExtensionValidator.Validate(secret.RootElement);
        Assert.Contains(secretResult.Errors, static error => error.Code == TcjAsyncApiExtensionValidationCodes.ProhibitedSecretMetadata);
        Assert.DoesNotContain("connectionString=secret", string.Join("|", secretResult.Errors.Select(static error => error.Message)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Absent_optional_semantics_are_omitted_and_upcaster_paths_are_not_inferred()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with
        {
            Application = baseline.Application with { Owner = null },
            Transports = [baseline.Transports[0] with { DeliverySemantics = null, OrderingSemantics = null, DeadLetterSemantics = null, PartitioningSemantics = null, Owner = null }],
            Channels = [baseline.Channels[0] with { Owner = null, Lifecycle = null, DataClassifications = [] }],
            Producers = [baseline.Producers[0] with { Owner = null, Lifecycle = null, RetryOwner = null, Outbox = null, PartitionKeyStrategy = null, OrderingKeyStrategy = null }],
            Consumers = [baseline.Consumers[0] with { Owner = null, Lifecycle = null, RetryOwner = null, Inbox = null, OrderingScope = null, DeadLetterSemantics = null }]
        };
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved);

        Assert.True(result.IsValid);
        using JsonDocument document = JsonDocument.Parse(result.Utf8Json);
        Assert.False(document.RootElement.TryGetProperty(TcjAsyncApiExtensions.VersionName, out _));
        JsonElement channel = document.RootElement.GetProperty("channels").GetProperty("orders-created");
        Assert.False(channel.EnumerateObject().Any(static property => property.Name.StartsWith("x-tcj-", StringComparison.Ordinal)));
        foreach (JsonProperty operation in document.RootElement.GetProperty("operations").EnumerateObject())
            Assert.False(operation.Value.EnumerateObject().Any(static property => property.Name.StartsWith("x-tcj-", StringComparison.Ordinal)));
        JsonElement message = document.RootElement.GetProperty("components").GetProperty("messages").GetProperty("orders-created-v1");
        Assert.False(message.TryGetProperty(TcjAsyncApiExtensions.UpcasterPath, out _));
    }

    [Fact]
    public void Referenced_and_bundled_modes_preserve_identical_semantic_extensions()
    {
        MessagingCatalog catalog = MessagingCatalogTestData.CreateValidCatalog();
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult referenced = AsyncApiDocumentGenerator.Generate(catalog, resolved, new AsyncApiGenerationOptions { SchemaMode = AsyncApiSchemaMode.Referenced });
        AsyncApiGenerationResult bundled = AsyncApiDocumentGenerator.Generate(catalog, resolved, new AsyncApiGenerationOptions { SchemaMode = AsyncApiSchemaMode.Bundled });

        Assert.True(referenced.IsValid);
        Assert.True(bundled.IsValid);
        using JsonDocument first = JsonDocument.Parse(referenced.Utf8Json);
        using JsonDocument second = JsonDocument.Parse(bundled.Utf8Json);
        Assert.Equal(CollectExtensions(first.RootElement), CollectExtensions(second.RootElement));
    }

    private static string CollectExtensions(JsonElement root)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Visit(root, "$", values);
        return JsonSerializer.Serialize(values);

        static void Visit(JsonElement value, string path, SortedDictionary<string, string> values)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    string childPath = path + "." + property.Name;
                    if (property.Name.StartsWith("x-tcj-", StringComparison.Ordinal)) values[childPath] = property.Value.GetRawText();
                    Visit(property.Value, childPath, values);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement item in value.EnumerateArray()) Visit(item, path + "[" + index++ + "]", values);
            }
        }
    }

    private static GovernedContractTestFixture Resolve(MessagingCatalog catalog, out GovernedContractResolutionResult resolved)
    {
        var fixture = new GovernedContractTestFixture();
        foreach ((string type, int version) in catalog.Producers.Select(static x => (x.Message.Type, x.Message.Version))
                     .Concat(catalog.Consumers.SelectMany(static x => x.AcceptedMessageVersions.Select(version => (x.MessageType, version))))
                     .Concat(catalog.Channels.SelectMany(static x => x.Messages.Select(message => (message.Type, message.Version))))
                     .Distinct())
            fixture.AddContract(type, version, includeExample: false);
        fixture.WriteManifest();
        resolved = GovernedContractResolver.Resolve(catalog, new GovernedContractResolutionOptions { ArtifactRoot = fixture.Root });
        return fixture;
    }
}
