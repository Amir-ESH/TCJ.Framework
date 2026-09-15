using System.Text;
using TCJ.Messaging.Serialization;

namespace TCJ.Messaging.Contracts.Tests;

public sealed class SchemaAndArtifactTests
{
    [Fact]
    public void Schema_uses_runtime_json_metadata_and_stable_fingerprint()
    {
        MessagingMessageContract contract = ContractTestFixture.Contract("test.contract", 1, ContractJsonContext.Default.ContractV1);
        var generator = new MessageContractSchemaGenerator();
        GeneratedMessageContractSchema first = generator.Generate(contract);
        GeneratedMessageContractSchema second = generator.Generate(contract);
        string schema = Encoding.UTF8.GetString(first.CanonicalUtf8.Span);

        Assert.Contains("\"order_id\"", schema, StringComparison.Ordinal);
        Assert.Equal(first.CanonicalUtf8.ToArray(), second.CanonicalUtf8.ToArray());
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal("SHA-256", first.Fingerprint.Algorithm);
        Assert.Equal(64, first.Fingerprint.Value.Length);
    }

    [Fact]
    public void Documentation_metadata_does_not_change_wire_fingerprint()
    {
        MessagingMessageContract contract = ContractTestFixture.Contract("test.contract", 1, ContractJsonContext.Default.ContractV1);
        var generator = new MessageContractArtifactGenerator();
        string one = TempDirectory();
        string two = TempDirectory();
        try
        {
            MessageContractManifest a = generator.Generate(one, [Definition(contract, "first documentation")]);
            MessageContractManifest b = generator.Generate(two, [Definition(contract, "changed documentation")]);
            Assert.Equal(a.Contracts[0].WireSchemaFingerprint, b.Contracts[0].WireSchemaFingerprint);
            Assert.Equal(File.ReadAllBytes(Path.Combine(one, "test.contract", "v1", "schema.json")),
                         File.ReadAllBytes(Path.Combine(two, "test.contract", "v1", "schema.json")));
        }
        finally { Directory.Delete(one, true); Directory.Delete(two, true); }
    }

    [Fact]
    public void Repeated_generation_is_byte_identical()
    {
        MessagingMessageContract contract = ContractTestFixture.Contract("test.contract", 1, ContractJsonContext.Default.ContractV1);
        var generator = new MessageContractArtifactGenerator();
        string one = TempDirectory();
        string two = TempDirectory();
        try
        {
            MessageContractDefinition definition = Definition(contract, "synthetic fixture");
            generator.Generate(one, [definition]);
            generator.Generate(two, [definition]);
            string[] firstFiles = Directory.GetFiles(one, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(one, p)).Order().ToArray();
            string[] secondFiles = Directory.GetFiles(two, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(two, p)).Order().ToArray();
            Assert.Equal(firstFiles, secondFiles);
            foreach (string relative in firstFiles)
                Assert.Equal(File.ReadAllBytes(Path.Combine(one, relative)), File.ReadAllBytes(Path.Combine(two, relative)));
        }
        finally { Directory.Delete(one, true); Directory.Delete(two, true); }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void Unsafe_example_name_is_rejected(string exampleName)
    {
        MessagingMessageContract contract = ContractTestFixture.Contract("test.contract", 1, ContractJsonContext.Default.ContractV1);
        var definition = Definition(contract, "synthetic fixture");
        definition = new MessageContractDefinition
        {
            RuntimeContract = definition.RuntimeContract,
            Metadata = definition.Metadata,
            Examples = new Dictionary<string, ReadOnlyMemory<byte>> { [exampleName] = ContractTestFixture.Utf8("{\"order_id\":\"A\",\"Quantity\":1}") }
        };
        string output = TempDirectory();
        try { Assert.ThrowsAny<ArgumentException>(() => new MessageContractArtifactGenerator().Generate(output, [definition])); }
        finally { Directory.Delete(output, true); }
    }

    [Fact]
    public void Valid_example_is_schema_valid_and_deserializes_through_runtime_metadata()
    {
        MessagingMessageContract contract = ContractTestFixture.Contract("test.contract", 1, ContractJsonContext.Default.ContractV1);
        GeneratedMessageContractSchema schema = new MessageContractSchemaGenerator().Generate(contract);
        var result = new MessageContractExampleValidator().Validate(
            contract, schema, ContractTestFixture.Utf8("{\"order_id\":\"A-1\",\"Quantity\":2}"), ContractTestFixture.Metadata());
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Secret_prohibited_classification_fails_generation()
    {
        MessagingMessageContract contract = ContractTestFixture.Contract("test.contract", 1, ContractJsonContext.Default.ContractV1);
        MessageContractMetadata metadata = new()
        {
            Owner = "TCJ.Tests",
            ChangeSummary = "synthetic",
            DataClassifications = [new("$.token", MessageDataClassification.SecretProhibited)]
        };
        string output = TempDirectory();
        try
        {
            Assert.Throws<InvalidOperationException>(() => new MessageContractArtifactGenerator().Generate(output,
                [new MessageContractDefinition { RuntimeContract = contract, Metadata = metadata }]));
        }
        finally { Directory.Delete(output, true); }
    }


    [Fact]
    public void Canonicalization_normalizes_required_order_and_equivalent_numbers()
    {
        byte[] first = MessageContractSchemaGenerator.Canonicalize(Encoding.UTF8.GetBytes("{\"minimum\":1.0,\"required\":[\"b\",\"a\"]}"));
        byte[] second = MessageContractSchemaGenerator.Canonicalize(Encoding.UTF8.GetBytes("{\"required\":[\"a\",\"b\"],\"minimum\":1e0}"));

        Assert.Equal(first, second);
        Assert.Equal("{\"minimum\":1,\"required\":[\"a\",\"b\"]}", Encoding.UTF8.GetString(first));
    }

    [Fact]
    public void Runtime_valid_message_type_with_consecutive_dots_is_not_rejected_by_artifact_paths()
    {
        MessagingMessageContract contract = ContractTestFixture.Contract("test..contract", 1, ContractJsonContext.Default.ContractV1);
        string output = TempDirectory();
        try
        {
            new MessageContractArtifactGenerator().Generate(output, [Definition(contract, "synthetic fixture")]);
            Assert.True(File.Exists(Path.Combine(output, "test..contract", "v1", "schema.json")));
        }
        finally { Directory.Delete(output, true); }
    }


    [Theory]
    [InlineData("CON")]
    [InlineData("event.")]
    [InlineData("LPT1.contract")]
    public void Runtime_valid_but_filesystem_unsafe_message_type_uses_portable_artifact_path(string messageType)
    {
        MessagingMessageContract contract = ContractTestFixture.Contract(messageType, 1, ContractJsonContext.Default.ContractV1);
        var definition = new MessageContractDefinition
        {
            RuntimeContract = contract,
            Metadata = ContractTestFixture.Metadata(),
            Examples = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
            {
                ["valid-minimal"] = ContractTestFixture.Utf8("{\"order_id\":\"A-1\",\"Quantity\":1}")
            }
        };
        string output = Path.Combine(Path.GetTempPath(), "tcj-message-contract-path-" + Guid.NewGuid().ToString("N"));
        try
        {
            MessageContractManifest manifest = new MessageContractArtifactGenerator().Generate(output, [definition]);
            MessageContractArtifact artifact = Assert.Single(manifest.Contracts);

            Assert.True(artifact.SchemaPath.StartsWith("~", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(output, artifact.SchemaPath.Replace('/', Path.DirectorySeparatorChar))));
            MessageContractManifest parsed = MessageContractManifestSerializer.Deserialize(File.ReadAllBytes(Path.Combine(output, "manifest.json")));
            Assert.Equal(messageType, Assert.Single(parsed.Contracts).MessageType);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void Manifest_parser_rejects_rooted_schema_paths()
    {
        MessageContractArtifact artifact = new()
        {
            MessageType = "test.contract",
            MessageVersion = 1,
            SchemaDraft = MessageContractSchemaGenerator.SchemaDraft,
            SchemaPath = "test.contract/v1/schema.json",
            WireSchemaFingerprint = new("SHA-256", new string('a', 64)),
            Owner = "TCJ.Tests",
            CompatibilityMode = MessageContractCompatibilityMode.Backward,
            ChangeSummary = "synthetic"
        };
        MessageContractManifest manifest = new()
        {
            SchemaVersion = 1,
            CanonicalizationVersion = 1,
            SchemaDraft = MessageContractSchemaGenerator.SchemaDraft,
            FingerprintAlgorithm = "SHA-256",
            Contracts = [artifact]
        };
        string json = Encoding.UTF8.GetString(MessageContractManifestSerializer.Serialize(manifest))
            .Replace("test.contract/v1/schema.json", "/tmp/schema.json", StringComparison.Ordinal);

        Assert.Throws<System.Text.Json.JsonException>(() => MessageContractManifestSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));
    }


    [Fact]
    public void Manifest_parser_rejects_unsupported_governance_format()
    {
        MessageContractManifest manifest = new()
        {
            SchemaVersion = 2,
            CanonicalizationVersion = MessageContractSchemaGenerator.CanonicalizationVersion,
            SchemaDraft = MessageContractSchemaGenerator.SchemaDraft,
            FingerprintAlgorithm = MessageContractSchemaGenerator.FingerprintAlgorithm
        };

        Assert.Throws<System.Text.Json.JsonException>(() =>
            MessageContractManifestSerializer.Deserialize(MessageContractManifestSerializer.Serialize(manifest)));
    }

    [Fact]
    public void Manifest_parser_rejects_contract_fingerprint_algorithm_drift()
    {
        MessageContractArtifact artifact = new()
        {
            MessageType = "test.contract",
            MessageVersion = 1,
            SchemaDraft = MessageContractSchemaGenerator.SchemaDraft,
            SchemaPath = "test.contract/v1/schema.json",
            WireSchemaFingerprint = new("SHA-512", new string('a', 64)),
            Owner = "TCJ.Tests",
            CompatibilityMode = MessageContractCompatibilityMode.Backward,
            ChangeSummary = "synthetic"
        };
        MessageContractManifest manifest = new()
        {
            SchemaVersion = MessageContractArtifactGenerator.ManifestSchemaVersion,
            CanonicalizationVersion = MessageContractSchemaGenerator.CanonicalizationVersion,
            SchemaDraft = MessageContractSchemaGenerator.SchemaDraft,
            FingerprintAlgorithm = MessageContractSchemaGenerator.FingerprintAlgorithm,
            Contracts = [artifact]
        };

        Assert.Throws<System.Text.Json.JsonException>(() =>
            MessageContractManifestSerializer.Deserialize(MessageContractManifestSerializer.Serialize(manifest)));
    }

    [Fact]
    public void Schema_generation_tracks_nullability_enum_collection_items_ignored_members_and_custom_names()
    {
        MessagingMessageContract contract = ContractTestFixture.Contract("test.shape", 1, ContractJsonContext.Default.SchemaShapeFixture);
        string schema = Encoding.UTF8.GetString(new MessageContractSchemaGenerator().Generate(contract).CanonicalUtf8.Span);

        Assert.Contains("\"custom_id\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"null\"", schema, StringComparison.Ordinal);
        Assert.Contains("Pending", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("Ignored", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_generation_tracks_closed_objects_and_polymorphic_discriminator_metadata()
    {
        MessagingMessageContract closed = ContractTestFixture.Contract("test.closed", 1, ContractJsonContext.Default.ClosedObjectFixture);
        MessagingMessageContract polymorphic = ContractTestFixture.Contract("test.polymorphic", 1, ContractJsonContext.Default.PolymorphicFixture);
        string closedSchema = Encoding.UTF8.GetString(new MessageContractSchemaGenerator().Generate(closed).CanonicalUtf8.Span);
        string polymorphicSchema = Encoding.UTF8.GetString(new MessageContractSchemaGenerator().Generate(polymorphic).CanonicalUtf8.Span);

        Assert.Contains("\"additionalProperties\":false", closedSchema, StringComparison.Ordinal);
        Assert.Contains("$kind", polymorphicSchema, StringComparison.Ordinal);
        Assert.Contains("email", polymorphicSchema, StringComparison.Ordinal);
    }

    private static MessageContractDefinition Definition(MessagingMessageContract contract, string summary) => new()
    {
        RuntimeContract = contract,
        Metadata = ContractTestFixture.Metadata(summary),
        Examples = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["valid-minimal"] = ContractTestFixture.Utf8("{\"order_id\":\"A-1\",\"Quantity\":2}")
        }
    };

    private static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "tcj-message-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
