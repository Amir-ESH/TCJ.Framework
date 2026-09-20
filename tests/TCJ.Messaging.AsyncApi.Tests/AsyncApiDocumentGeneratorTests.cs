using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TCJ.Messaging.AsyncApi.Tests;

public sealed class AsyncApiDocumentGeneratorTests
{
    [Fact]
    public void Generated_document_uses_exact_AsyncAPI_3_1_identity_and_operation_semantics()
    {
        using var fixture = CreateFixture(MessagingCatalogTestData.CreateValidCatalog(), includeExample: false, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(MessagingCatalogTestData.CreateValidCatalog(), resolved);

        Assert.True(result.IsValid);
        using JsonDocument document = JsonDocument.Parse(result.Utf8Json);
        JsonElement root = document.RootElement;
        Assert.Equal("3.1.0", root.GetProperty("asyncapi").GetString());
        Assert.Equal("Orders Messaging API", root.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("1.4.0", root.GetProperty("info").GetProperty("version").GetString());
        JsonElement operations = root.GetProperty("operations");
        Assert.Contains(operations.EnumerateObject(), static x => x.Value.GetProperty("action").GetString() == "send");
        Assert.Contains(operations.EnumerateObject(), static x => x.Value.GetProperty("action").GetString() == "receive");
        Assert.DoesNotContain("\"publish\"", Encoding.UTF8.GetString(result.Utf8Json.Span), StringComparison.Ordinal);
        Assert.DoesNotContain("\"subscribe\"", Encoding.UTF8.GetString(result.Utf8Json.Span), StringComparison.Ordinal);
        Assert.Equal((byte)'\n', result.Utf8Json.Span[^1]);
        Assert.Equal("asyncapi.json", AsyncApiDocumentGenerator.CanonicalFileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("3.0.0")]
    [InlineData("3.2.0")]
    public void Unsupported_AsyncAPI_versions_fail_closed(string version)
    {
        MessagingCatalog catalog = MessagingCatalogTestData.CreateValidCatalog();
        using var fixture = CreateFixture(catalog, includeExample: false, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved, new AsyncApiGenerationOptions { AsyncApiVersion = version });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static x => x.Code == AsyncApiGenerationCodes.UnsupportedAsyncApiVersion);
    }

    [Fact]
    public void Missing_document_identity_fails_through_catalog_validation()
    {
        MessagingCatalog catalog = MessagingCatalogTestData.CreateValidCatalog() with
        {
            Document = new MessagingDocumentInfo { Title = "", Version = "" }
        };
        using var fixture = new GovernedContractTestFixture();
        fixture.AddContract("orders.created", 1, includeExample: false);
        fixture.WriteManifest();
        GovernedContractResolutionResult resolved = GovernedContractResolver.Resolve(catalog, new GovernedContractResolutionOptions { ArtifactRoot = fixture.Root });

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static x => x.Code == AsyncApiGenerationCodes.InvalidCatalog && x.Path == "$.document.title");
        Assert.Contains(result.Errors, static x => x.Code == AsyncApiGenerationCodes.InvalidCatalog && x.Path == "$.document.version");
    }

    [Fact]
    public void Message_component_identity_is_logical_and_multiple_versions_are_stable()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with
        {
            Channels = [baseline.Channels[0] with { Messages = [new MessagingMessageReference { Type = "orders.created", Version = 1 }, new MessagingMessageReference { Type = "orders.created", Version = 2 }] }],
            Consumers = [baseline.Consumers[0] with { AcceptedMessageVersions = [2, 1] }]
        };
        using var fixture = new GovernedContractTestFixture();
        fixture.AddContract("orders.created", 1, includeExample: false);
        fixture.AddContract("orders.created", 2, includeExample: false);
        fixture.WriteManifest();
        GovernedContractResolutionResult resolved = Resolve(fixture, catalog);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved);

        Assert.True(result.IsValid);
        string json = Encoding.UTF8.GetString(result.Utf8Json.Span);
        Assert.Contains("\"orders-created-v1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"orders-created-v2\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TCJ.Messaging.AsyncApi", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Referenced_mode_uses_only_normalized_governed_relative_schema_path()
    {
        MessagingCatalog catalog = MessagingCatalogTestData.CreateValidCatalog();
        using var fixture = CreateFixture(catalog, includeExample: false, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved, new AsyncApiGenerationOptions { SchemaMode = AsyncApiSchemaMode.Referenced });

        string json = Encoding.UTF8.GetString(result.Utf8Json.Span);
        using JsonDocument document = JsonDocument.Parse(result.Utf8Json);
        string schemaReference = document.RootElement
            .GetProperty("components")
            .GetProperty("messages")
            .GetProperty("orders-created-v1")
            .GetProperty("payload")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString()!;

        Assert.Equal("./orders.created/v1/schema.json", schemaReference);
        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', schemaReference);
    }

    [Fact]
    public void Bundled_mode_embeds_the_validated_governed_schema_without_regeneration()
    {
        MessagingCatalog catalog = MessagingCatalogTestData.CreateValidCatalog();
        using var fixture = CreateFixture(catalog, includeExample: false, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult bundled = AsyncApiDocumentGenerator.Generate(catalog, resolved, new AsyncApiGenerationOptions { SchemaMode = AsyncApiSchemaMode.Bundled });
        AsyncApiGenerationResult referenced = AsyncApiDocumentGenerator.Generate(catalog, resolved, new AsyncApiGenerationOptions { SchemaMode = AsyncApiSchemaMode.Referenced });

        Assert.True(bundled.IsValid);
        using JsonDocument document = JsonDocument.Parse(bundled.Utf8Json);
        JsonElement schema = document.RootElement.GetProperty("components").GetProperty("messages").GetProperty("orders-created-v1").GetProperty("payload").GetProperty("schema");
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.GetProperty("$schema").GetString());
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.Contains("orders-created-v1", Encoding.UTF8.GetString(referenced.Utf8Json.Span), StringComparison.Ordinal);
        Assert.Contains("orders-created-v1", Encoding.UTF8.GetString(bundled.Utf8Json.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void Dynamic_destination_emits_unknown_standard_address_instead_of_fabricating_a_concrete_channel()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with
        {
            Channels = [baseline.Channels[0] with { DynamicDestination = new MessagingDynamicDestination { NamingStrategyId = "tenant", Pattern = "tenant-{tenantId}.orders" } }]
        };
        using var fixture = CreateFixture(catalog, includeExample: false, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved);

        using JsonDocument document = JsonDocument.Parse(result.Utf8Json);
        JsonElement channel = document.RootElement.GetProperty("channels").GetProperty("orders-created");
        Assert.Equal(JsonValueKind.Null, channel.GetProperty("address").ValueKind);
        Assert.DoesNotContain("tenant-{tenantId}.orders", Encoding.UTF8.GetString(result.Utf8Json.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void Servers_are_omitted_by_default_and_safe_explicit_server_metadata_can_be_emitted()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        using var baselineFixture = CreateFixture(baseline, includeExample: false, out GovernedContractResolutionResult baselineResolved);
        AsyncApiGenerationResult withoutServer = AsyncApiDocumentGenerator.Generate(baseline, baselineResolved);
        using JsonDocument baselineDocument = JsonDocument.Parse(withoutServer.Utf8Json);
        Assert.False(baselineDocument.RootElement.TryGetProperty("servers", out _));

        MessagingCatalog withServer = baseline with
        {
            SecuritySchemes = [new MessagingSecurityScheme { Id = "client-cert", Mechanism = MessagingSecurityMechanism.X509 }],
            Transports = [baseline.Transports[0] with { SecuritySchemeId = "client-cert" }],
            Servers = [new MessagingServer { Id = "primary", Host = "broker.example.invalid", Protocol = "amqp", Description = "Non-secret documentation endpoint", SecuritySchemeId = "client-cert" }]
        };
        using var fixture = CreateFixture(withServer, includeExample: false, out GovernedContractResolutionResult resolved);
        AsyncApiGenerationResult generated = AsyncApiDocumentGenerator.Generate(withServer, resolved);

        Assert.True(generated.IsValid);
        string json = Encoding.UTF8.GetString(generated.Utf8Json.Span);
        Assert.Contains("broker.example.invalid", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"X509\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("connectionString", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unsupported_standard_security_mapping_is_deferred_without_misleading_fields()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with
        {
            Servers = [new MessagingServer { Id = "primary", Host = "broker.example.invalid", Protocol = "amqp", SecuritySchemeId = "managed-identity" }]
        };
        using var fixture = CreateFixture(catalog, includeExample: false, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved);

        Assert.True(result.IsValid);
        string json = Encoding.UTF8.GetString(result.Utf8Json.Span);
        Assert.DoesNotContain("managedIdentity", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("oauth2", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reordered_inputs_culture_and_checkout_root_do_not_change_canonical_bytes()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog reordered = baseline with
        {
            Producers = baseline.Producers.Reverse().ToArray(),
            Consumers = baseline.Consumers.Reverse().ToArray(),
            Channels = baseline.Channels.Reverse().ToArray(),
            SecuritySchemes = baseline.SecuritySchemes.Reverse().ToArray(),
            Transports = baseline.Transports.Reverse().ToArray()
        };

        using var firstFixture = CreateFixture(baseline, includeExample: false, out GovernedContractResolutionResult firstResolved);
        using var secondFixture = CreateFixture(reordered, includeExample: false, out GovernedContractResolutionResult secondResolved);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            byte[] first = AsyncApiDocumentGenerator.Generate(baseline, firstResolved).Utf8Json.ToArray();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            byte[] second = AsyncApiDocumentGenerator.Generate(reordered, secondResolved).Utf8Json.ToArray();
            Assert.Equal(first, second);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Invalid_schema_mode_and_document_bounds_fail_closed()
    {
        MessagingCatalog catalog = MessagingCatalogTestData.CreateValidCatalog();
        using var fixture = CreateFixture(catalog, includeExample: false, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult mode = AsyncApiDocumentGenerator.Generate(catalog, resolved, new AsyncApiGenerationOptions { SchemaMode = (AsyncApiSchemaMode)999 });
        AsyncApiGenerationResult bounds = AsyncApiDocumentGenerator.Generate(catalog, resolved, new AsyncApiGenerationOptions { MaximumChannels = 1, MaximumOperations = 1 });

        Assert.Contains(mode.Errors, static x => x.Code == AsyncApiGenerationCodes.UnsupportedSchemaMode);
        Assert.Contains(bounds.Errors, static x => x.Code == AsyncApiGenerationCodes.DocumentBoundExceeded);
    }

    [Fact]
    public void Operation_contract_must_be_declared_on_its_channel()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with { Channels = [baseline.Channels[0] with { Messages = [] }] };
        using var fixture = new GovernedContractTestFixture();
        fixture.AddContract("orders.created", 1, includeExample: false);
        fixture.WriteManifest();
        GovernedContractResolutionResult resolved = Resolve(fixture, catalog);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static x => x.Code == AsyncApiGenerationCodes.UnknownContract);
    }

    [Fact]
    public void Component_normalization_collision_fails_deterministically()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with
        {
            Channels = [baseline.Channels[0] with { Messages = [new MessagingMessageReference { Type = "orders.created", Version = 1 }, new MessagingMessageReference { Type = "orders-created", Version = 1 }] }],
            Producers = [baseline.Producers[0]],
            Consumers = []
        };
        using var fixture = new GovernedContractTestFixture();
        fixture.AddContract("orders.created", 1, includeExample: false);
        fixture.AddContract("orders-created", 1, includeExample: false);
        fixture.WriteManifest();
        GovernedContractResolutionResult resolved = Resolve(fixture, catalog);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static x => x.Code == AsyncApiGenerationCodes.IdentifierCollision);
    }

    [Fact]
    public void Minimal_producer_golden_document_is_byte_identical()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with
        {
            SecuritySchemes = [],
            Transports = [baseline.Transports[0] with { SecuritySchemeId = null }],
            Consumers = []
        };
        using var fixture = CreateFixture(catalog, includeExample: false, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult result = AsyncApiDocumentGenerator.Generate(catalog, resolved);
        string expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", "minimal-producer.referenced.json"), Encoding.UTF8);

        Assert.True(result.IsValid);
        Assert.Equal(expected, Encoding.UTF8.GetString(result.Utf8Json.Span));
    }

    [Fact]
    public void Identifier_normalization_and_serialization_are_stable_for_bounded_property_inputs()
    {
        var random = new Random(5340);
        for (int iteration = 0; iteration < 128; iteration++)
        {
            string suffix = random.Next(1, 100000).ToString(CultureInfo.InvariantCulture);
            MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
            MessagingCatalog catalog = baseline with { Application = baseline.Application with { Name = "orders-api-" + suffix } };
            using var fixture = CreateFixture(catalog, includeExample: false, out GovernedContractResolutionResult resolved);

            byte[] first = AsyncApiDocumentGenerator.Generate(catalog, resolved).Utf8Json.ToArray();
            byte[] second = AsyncApiDocumentGenerator.Generate(catalog, resolved).Utf8Json.ToArray();
            Assert.Equal(first, second);
        }
    }

    private static GovernedContractTestFixture CreateFixture(MessagingCatalog catalog, bool includeExample, out GovernedContractResolutionResult resolved)
    {
        var fixture = new GovernedContractTestFixture();
        foreach ((string type, int version) in EnumerateContracts(catalog))
            fixture.AddContract(type, version, includeExample: includeExample);
        fixture.WriteManifest();
        resolved = Resolve(fixture, catalog);
        return fixture;
    }

    private static IEnumerable<(string Type, int Version)> EnumerateContracts(MessagingCatalog catalog) =>
        catalog.Producers.Select(static x => (x.Message.Type, x.Message.Version))
            .Concat(catalog.Consumers.SelectMany(static x => x.AcceptedMessageVersions.Select(version => (x.MessageType, version))))
            .Concat(catalog.Channels.SelectMany(static x => x.Messages.Select(message => (message.Type, message.Version))))
            .Distinct();

    private static GovernedContractResolutionResult Resolve(GovernedContractTestFixture fixture, MessagingCatalog catalog) =>
        GovernedContractResolver.Resolve(catalog, new GovernedContractResolutionOptions { ArtifactRoot = fixture.Root });
}
