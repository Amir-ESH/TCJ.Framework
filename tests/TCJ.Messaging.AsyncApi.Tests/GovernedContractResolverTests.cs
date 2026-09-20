using System.Text;
using TCJ.Messaging.Contracts;

namespace TCJ.Messaging.AsyncApi.Tests;

public sealed class GovernedContractResolverTests
{
    [Fact]
    public void Valid_manifest_resolves_exact_governed_contract_and_metadata()
    {
        using var fixture = new GovernedContractTestFixture();
        fixture.AddContract("orders.created", 1);
        fixture.WriteManifest();

        GovernedContractResolutionResult result = Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog());

        Assert.True(result.IsValid);
        ResolvedGovernedMessageContract contract = Assert.Single(result.Contracts);
        Assert.Equal("orders.created", contract.MessageType);
        Assert.Equal(1, contract.MessageVersion);
        Assert.Null(contract.ContentType);
        Assert.Equal(MessageContractSchemaGenerator.SchemaDraft, contract.SchemaDialect);
        Assert.Equal("orders.created/v1/schema.json", contract.SchemaRelativePath);
        Assert.Equal("synthetic-test-owner", contract.Owner);
        Assert.Equal(MessageContractCompatibilityMode.Backward, contract.CompatibilityMode);
        Assert.Equal(MessageDataClassification.Internal, Assert.Single(contract.DataClassifications).Classification);
        Assert.Equal("SYNTHETIC-REVIEW", Assert.Single(contract.ReviewedCompatibilityExceptions).Code);
        Assert.Equal("orders.created/v1/examples/valid-minimal.json", Assert.Single(contract.Examples).RelativePath);
        Assert.NotEmpty(contract.SchemaUtf8.ToArray());
    }

    [Fact]
    public void Missing_and_malformed_manifests_fail_closed()
    {
        using var missing = new GovernedContractTestFixture();
        GovernedContractResolutionResult missingResult = Resolve(missing, MessagingCatalogTestData.CreateValidCatalog());
        AssertError(missingResult, GovernedContractValidationCodes.ManifestMissing);
        Assert.Empty(missingResult.Contracts);

        using var malformed = new GovernedContractTestFixture();
        malformed.WriteText("manifest.json", "{");
        GovernedContractResolutionResult malformedResult = Resolve(malformed, MessagingCatalogTestData.CreateValidCatalog());
        AssertError(malformedResult, GovernedContractValidationCodes.ManifestMalformed);
        Assert.Empty(malformedResult.Contracts);
    }

    [Fact]
    public void Unsupported_manifest_version_fails_closed()
    {
        using var fixture = new GovernedContractTestFixture();
        fixture.AddContract("orders.created", 1);
        fixture.WriteManifest();
        string json = Encoding.UTF8.GetString(fixture.Read("manifest.json"))
            .Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal);
        fixture.WriteText("manifest.json", json);

        AssertError(Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog()), GovernedContractValidationCodes.UnsupportedManifestVersion);
    }

    [Fact]
    public void Duplicate_contract_identity_fails_closed()
    {
        using var fixture = new GovernedContractTestFixture();
        MessageContractArtifact artifact = fixture.AddContract("orders.created", 1);
        fixture.WriteManifestContracts(artifact, artifact);

        AssertError(Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog()), GovernedContractValidationCodes.DuplicateContractIdentity);
    }

    [Fact]
    public void Unknown_message_type_and_version_are_distinguished()
    {
        using var fixture = new GovernedContractTestFixture();
        fixture.AddContract("orders.created", 1);
        fixture.WriteManifest();
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();

        MessagingCatalog unknownType = baseline with
        {
            Producers = [baseline.Producers[0] with { Message = new MessagingMessageReference { Type = "missing.message", Version = 1 } }]
        };
        AssertError(Resolve(fixture, unknownType), GovernedContractValidationCodes.ContractNotFound);

        MessagingCatalog unknownVersion = baseline with
        {
            Producers = [baseline.Producers[0] with { Message = new MessagingMessageReference { Type = "orders.created", Version = 99 } }]
        };
        AssertError(Resolve(fixture, unknownVersion), GovernedContractValidationCodes.MessageVersionNotFound);
    }

    [Fact]
    public void Consumer_versions_resolve_independently_and_duplicate_declarations_fail()
    {
        using var fixture = new GovernedContractTestFixture();
        fixture.AddContract("orders.created", 1);
        fixture.AddContract("orders.created", 2, deprecated: true);
        fixture.WriteManifest();
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog twoVersions = baseline with
        {
            Consumers = [baseline.Consumers[0] with { AcceptedMessageVersions = [2, 1] }]
        };

        GovernedContractResolutionResult valid = Resolve(fixture, twoVersions);
        Assert.True(valid.IsValid);
        Assert.Equal(new[] { 1, 2 }, valid.Contracts.Select(static item => item.MessageVersion).ToArray());

        MessagingCatalog duplicate = baseline with
        {
            Consumers = [baseline.Consumers[0] with { AcceptedMessageVersions = [1, 1] }]
        };
        AssertError(Resolve(fixture, duplicate), GovernedContractValidationCodes.DuplicateAcceptedVersion);
    }

    [Fact]
    public void Unresolved_consumer_and_channel_contracts_cannot_be_silently_ignored()
    {
        using var fixture = new GovernedContractTestFixture();
        fixture.AddContract("orders.created", 1);
        fixture.WriteManifest();
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();

        MessagingCatalog unknownConsumerVersion = baseline with
        {
            Consumers = [baseline.Consumers[0] with { AcceptedMessageVersions = [99] }]
        };
        AssertError(Resolve(fixture, unknownConsumerVersion), GovernedContractValidationCodes.MessageVersionNotFound);

        MessagingCatalog unknownChannelContract = baseline with
        {
            Channels = [baseline.Channels[0] with { Messages = [new MessagingMessageReference { Type = "missing.channel.message", Version = 1 }] }]
        };
        AssertError(Resolve(fixture, unknownChannelContract), GovernedContractValidationCodes.ContractNotFound);
    }

    [Fact]
    public void Missing_malformed_and_modified_schema_artifacts_fail_closed()
    {
        using var missing = CreateValidFixture(out MessageContractArtifact missingArtifact);
        File.Delete(Path.Combine(missing.Root, missingArtifact.SchemaPath.Replace('/', Path.DirectorySeparatorChar)));
        AssertError(Resolve(missing, MessagingCatalogTestData.CreateValidCatalog()), GovernedContractValidationCodes.SchemaMissing);

        using var malformed = CreateValidFixture(out MessageContractArtifact malformedArtifact);
        malformed.WriteText(malformedArtifact.SchemaPath, "{");
        AssertError(Resolve(malformed, MessagingCatalogTestData.CreateValidCatalog()), GovernedContractValidationCodes.SchemaMalformed);

        using var modified = CreateValidFixture(out MessageContractArtifact modifiedArtifact);
        modified.WriteText(modifiedArtifact.SchemaPath, "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"string\"}");
        AssertError(Resolve(modified, MessagingCatalogTestData.CreateValidCatalog()), GovernedContractValidationCodes.SchemaFingerprintMismatch);
    }

    [Fact]
    public void Incorrect_manifest_fingerprint_fails_closed()
    {
        using var fixture = CreateValidFixture(out MessageContractArtifact artifact);
        string incorrectFingerprint =
            (artifact.WireSchemaFingerprint.Value[0] == '0' ? "1" : "0") + artifact.WireSchemaFingerprint.Value[1..];
        string json = Encoding.UTF8.GetString(fixture.Read("manifest.json"))
            .Replace(artifact.WireSchemaFingerprint.Value, incorrectFingerprint, StringComparison.Ordinal);
        fixture.WriteText("manifest.json", json);

        AssertError(Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog()), GovernedContractValidationCodes.SchemaFingerprintMismatch);
    }

    [Fact]
    public void Schema_dialect_disagreement_fails_closed()
    {
        const string schema = "{\"$schema\":\"https://json-schema.org/draft/2019-09/schema\",\"type\":\"object\"}";
        using var fixture = new GovernedContractTestFixture();
        fixture.AddContract("orders.created", 1, schema, includeExample: false);
        fixture.WriteManifest();

        AssertError(Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog()), GovernedContractValidationCodes.UnsupportedSchemaMetadata);
    }

    [Fact]
    public void Manifest_schema_path_traversal_is_rejected()
    {
        using var fixture = CreateValidFixture(out _);
        string json = Encoding.UTF8.GetString(fixture.Read("manifest.json"))
            .Replace("orders.created/v1/schema.json", "../../escape.json", StringComparison.Ordinal);
        fixture.WriteText("manifest.json", json);

        AssertError(Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog()), GovernedContractValidationCodes.SchemaPathTraversal);
    }

    [Fact]
    public void Same_document_and_local_artifact_references_validate_without_network_access()
    {
        const string schema = """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$defs": { "id": { "type": "string" } },
              "type": "object",
              "properties": {
                "id": { "$ref": "#/$defs/id" },
                "external": { "$ref": "shared/common.json#/$defs/value" }
              }
            }
            """;
        using var fixture = new GovernedContractTestFixture();
        fixture.AddContract("orders.created", 1, schema);
        fixture.WriteText("orders.created/v1/shared/common.json", "{\"$defs\":{\"value\":{\"type\":\"integer\"}}}");
        fixture.WriteManifest();

        Assert.True(Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog()).IsValid);
    }

    [Theory]
    [InlineData("#/$defs/missing", GovernedContractValidationCodes.InvalidLocalReference)]
    [InlineData("../shared.json#/value", GovernedContractValidationCodes.SchemaPathTraversal)]
    [InlineData("http://example.invalid/schema.json", GovernedContractValidationCodes.RemoteReferenceNotAllowed)]
    [InlineData("https://example.invalid/schema.json", GovernedContractValidationCodes.RemoteReferenceNotAllowed)]
    [InlineData("file:///tmp/schema.json", GovernedContractValidationCodes.RemoteReferenceNotAllowed)]
    public void Unsafe_or_broken_references_fail_closed(string reference, string expectedCode)
    {
        string schema = $$"""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$defs": { "id": { "type": "string" } },
              "properties": { "id": { "$ref": "{{reference}}" } }
            }
            """;
        using var fixture = new GovernedContractTestFixture();
        fixture.AddContract("orders.created", 1, schema);
        fixture.WriteManifest();

        AssertError(Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog()), expectedCode);
    }


    [Fact]
    public void Governed_lifecycle_and_compatibility_metadata_are_preserved_without_recomputation()
    {
        using var fixture = new GovernedContractTestFixture();
        fixture.AddContract("orders.created", 1);
        fixture.AddContract("orders.created", 2, deprecated: true);
        fixture.WriteManifest();
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with
        {
            Consumers = [baseline.Consumers[0] with { AcceptedMessageVersions = [2] }],
            Producers = [baseline.Producers[0] with { Message = new MessagingMessageReference { Type = "orders.created", Version = 2 } }],
            Channels = [baseline.Channels[0] with { Messages = [new MessagingMessageReference { Type = "orders.created", Version = 2 }] }]
        };

        GovernedContractResolutionResult result = Resolve(fixture, catalog);

        Assert.True(result.IsValid);
        ResolvedGovernedMessageContract contract = Assert.Single(result.Contracts);
        Assert.True(contract.Deprecated);
        Assert.Equal(2, contract.DeprecatedSince);
        Assert.Equal(3, contract.ReplacementVersion);
        Assert.Equal(MessageContractCompatibilityMode.Backward, contract.CompatibilityMode);
        Assert.Equal("Synthetic reviewed compatibility evidence.", contract.SemanticCompatibilityNotes);
        Assert.Equal("Synthetic migration guidance.", contract.MigrationNotes);
        Assert.Equal("synthetic-test-owner", contract.Owner);
    }

    [Fact]
    public void Manifest_identity_and_schema_path_disagreement_fails_closed()
    {
        using var fixture = CreateValidFixture(out _);
        string json = Encoding.UTF8.GetString(fixture.Read("manifest.json"))
            .Replace("orders.created/v1/schema.json", "other.message/v1/schema.json", StringComparison.Ordinal);
        fixture.WriteText("manifest.json", json);

        AssertError(Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog()), GovernedContractValidationCodes.SchemaIdentityMismatch);
    }

    [Fact]
    public void Governed_fingerprint_is_stable_across_schema_line_endings_and_formatting()
    {
        using var fixture = CreateValidFixture(out MessageContractArtifact artifact);
        const string equivalentSchema = "{\r\n  \"$schema\": \"https://json-schema.org/draft/2020-12/schema\",\r\n  \"type\": \"object\",\r\n  \"properties\": { \"id\": { \"type\": \"string\" } },\r\n  \"required\": [\"id\"],\r\n  \"additionalProperties\": false\r\n}";
        fixture.WriteText(artifact.SchemaPath, equivalentSchema);

        GovernedContractResolutionResult result = Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog());

        Assert.True(result.IsValid);
        Assert.Equal(artifact.WireSchemaFingerprint, Assert.Single(result.Contracts).SchemaFingerprint);
    }

    [Fact]
    public void Missing_owner_is_rejected_instead_of_being_invented()
    {
        using var fixture = CreateValidFixture(out _);
        string json = Encoding.UTF8.GetString(fixture.Read("manifest.json"))
            .Replace("\"owner\":\"synthetic-test-owner\",", string.Empty, StringComparison.Ordinal);
        fixture.WriteText("manifest.json", json);

        AssertError(Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog()), GovernedContractValidationCodes.ManifestMalformed);
    }

    [Fact]
    public void Only_manifest_declared_governed_examples_are_exposed()
    {
        using var fixture = CreateValidFixture(out _);
        fixture.WriteText("orders.created/v1/examples/untrusted.json", "{\"id\":\"not-declared\"}");

        GovernedContractResolutionResult result = Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog());

        Assert.True(result.IsValid);
        ResolvedGovernedExample example = Assert.Single(Assert.Single(result.Contracts).Examples);
        Assert.Equal("orders.created/v1/examples/valid-minimal.json", example.RelativePath);
    }

    [Fact]
    public void Missing_governed_example_fails_closed()
    {
        using var fixture = CreateValidFixture(out _);
        File.Delete(Path.Combine(fixture.Root, "orders.created", "v1", "examples", "valid-minimal.json"));

        AssertError(Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog()), GovernedContractValidationCodes.ExampleMissing);
    }

    [Fact]
    public void Resolution_is_deterministic_and_checkout_root_does_not_leak_into_identity()
    {
        using var first = CreateValidFixture(out _);
        using var second = CreateValidFixture(out _);

        GovernedContractResolutionResult firstResult = Resolve(first, MessagingCatalogTestData.CreateValidCatalog());
        GovernedContractResolutionResult secondResult = Resolve(second, MessagingCatalogTestData.CreateValidCatalog());

        Assert.True(firstResult.IsValid);
        Assert.True(secondResult.IsValid);
        ResolvedGovernedMessageContract a = Assert.Single(firstResult.Contracts);
        ResolvedGovernedMessageContract b = Assert.Single(secondResult.Contracts);
        Assert.Equal(a.MessageType, b.MessageType);
        Assert.Equal(a.MessageVersion, b.MessageVersion);
        Assert.Equal(a.SchemaRelativePath, b.SchemaRelativePath);
        Assert.Equal(a.SchemaFingerprint, b.SchemaFingerprint);
        Assert.Equal(a.SchemaUtf8.ToArray(), b.SchemaUtf8.ToArray());
        Assert.DoesNotContain(first.Root, a.SchemaRelativePath, StringComparison.Ordinal);
        Assert.DoesNotContain(second.Root, b.SchemaRelativePath, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", a.SchemaRelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Bounded_reference_traversal_fuzz_inputs_are_rejected_deterministically()
    {
        var random = new Random(533);
        for (var iteration = 0; iteration < 64; iteration++)
        {
            int parents = random.Next(1, 6);
            string reference = string.Concat(Enumerable.Repeat("../", parents)) + "outside.json#/value";
            string schema = $$"""
                {
                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                  "properties": { "value": { "$ref": "{{reference}}" } }
                }
                """;
            using var fixture = new GovernedContractTestFixture();
            fixture.AddContract("orders.created", 1, schema, includeExample: false);
            fixture.WriteManifest();

            GovernedContractResolutionResult first = Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog());
            GovernedContractResolutionResult second = Resolve(fixture, MessagingCatalogTestData.CreateValidCatalog());
            AssertError(first, GovernedContractValidationCodes.SchemaPathTraversal);
            Assert.Equal(first.Errors, second.Errors);
        }
    }

    private static GovernedContractTestFixture CreateValidFixture(out MessageContractArtifact artifact)
    {
        var fixture = new GovernedContractTestFixture();
        artifact = fixture.AddContract("orders.created", 1);
        fixture.WriteManifest();
        return fixture;
    }

    private static GovernedContractResolutionResult Resolve(GovernedContractTestFixture fixture, MessagingCatalog catalog) =>
        GovernedContractResolver.Resolve(catalog, new GovernedContractResolutionOptions { ArtifactRoot = fixture.Root });

    private static void AssertError(GovernedContractResolutionResult result, string code)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == code);
    }
}
