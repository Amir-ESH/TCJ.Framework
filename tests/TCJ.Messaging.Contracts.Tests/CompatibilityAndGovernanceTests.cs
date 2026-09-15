namespace TCJ.Messaging.Contracts.Tests;

public sealed class CompatibilityAndGovernanceTests
{
    private static readonly ReadOnlyMemory<byte> V1 = ContractTestFixture.Utf8("""
        {"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":true}
        """);
    private static readonly ReadOnlyMemory<byte> V2Optional = ContractTestFixture.Utf8("""
        {"type":"object","properties":{"id":{"type":"string"},"note":{"type":["string","null"]}},"required":["id"],"additionalProperties":true}
        """);
    private static readonly ReadOnlyMemory<byte> V2Required = ContractTestFixture.Utf8("""
        {"type":"object","properties":{"id":{"type":"string"},"note":{"type":"string"}},"required":["id","note"],"additionalProperties":true}
        """);

    [Fact]
    public void Backward_allows_optional_property_but_required_property_breaks()
    {
        var analyzer = new MessageContractCompatibilityAnalyzer();
        Assert.Equal(MessageContractCompatibilityStatus.Compatible, analyzer.Analyze(V1, V2Optional, MessageContractCompatibilityMode.Backward).Status);
        Assert.Equal(MessageContractCompatibilityStatus.Breaking, analyzer.Analyze(V1, V2Required, MessageContractCompatibilityMode.Backward).Status);
    }

    [Fact]
    public void Forward_uses_opposite_reader_writer_direction()
    {
        var analyzer = new MessageContractCompatibilityAnalyzer();
        Assert.Equal(MessageContractCompatibilityStatus.Compatible, analyzer.Analyze(V1, V2Required, MessageContractCompatibilityMode.Forward).Status);
        Assert.Equal(MessageContractCompatibilityStatus.Breaking, analyzer.Analyze(V2Required, V1, MessageContractCompatibilityMode.Forward).Status);
    }

    [Fact]
    public void Changed_pattern_requires_review_and_unreviewed_result_is_not_accepted()
    {
        ReadOnlyMemory<byte> oldSchema = ContractTestFixture.Utf8("{\"type\":\"string\",\"pattern\":\"^[A-Z]+$\"}");
        ReadOnlyMemory<byte> newSchema = ContractTestFixture.Utf8("{\"type\":\"string\",\"pattern\":\"^[A-Z0-9]+$\"}");
        MessageContractCompatibilityResult result = new MessageContractCompatibilityAnalyzer().Analyze(oldSchema, newSchema, MessageContractCompatibilityMode.Backward);
        Assert.Equal(MessageContractCompatibilityStatus.ReviewRequired, result.Status);
        Assert.False(result.IsAccepted());
        Assert.True(result.IsAccepted([new(result.Findings[0].Code, "Reviewed against the producer language and approved for this synthetic fixture.")]));
    }

    [Fact]
    public void Transitive_mode_checks_every_retained_version()
    {
        ReadOnlyMemory<byte> permissive = ContractTestFixture.Utf8("{\"type\":\"object\",\"additionalProperties\":true}");
        MessageContractCompatibilityResult direct = new MessageContractCompatibilityAnalyzer().Analyze([permissive, V1], V2Optional, MessageContractCompatibilityMode.Backward);
        MessageContractCompatibilityResult transitive = new MessageContractCompatibilityAnalyzer().Analyze([permissive, V1], V2Optional, MessageContractCompatibilityMode.BackwardTransitive);
        Assert.Equal(MessageContractCompatibilityStatus.Compatible, direct.Status);
        Assert.NotEqual(MessageContractCompatibilityStatus.Compatible, transitive.Status);
    }

    [Fact]
    public void Published_wire_fingerprint_is_immutable_but_documentation_may_change()
    {
        MessageContractArtifact published = Artifact("abc", "old docs");
        MessageContractArtifact changedDocs = Artifact("abc", "new docs");
        MessageContractArtifact changedWire = Artifact("def", "old docs");
        MessageContractManifest baseline = Manifest([published], provenance: true);
        var validator = new MessageContractBaselineValidator();
        Assert.True(validator.Validate(baseline, Manifest([changedDocs], provenance: false)).IsValid);
        Assert.False(validator.Validate(baseline, Manifest([changedWire], provenance: false)).IsValid);
    }


    [Fact]
    public void Full_requires_both_backward_and_forward_compatibility()
    {
        var analyzer = new MessageContractCompatibilityAnalyzer();
        Assert.Equal(MessageContractCompatibilityStatus.Compatible, analyzer.Analyze(V1, V2Optional, MessageContractCompatibilityMode.Full).Status);
        Assert.Equal(MessageContractCompatibilityStatus.Breaking, analyzer.Analyze(V1, V2Required, MessageContractCompatibilityMode.Full).Status);
    }

    [Fact]
    public void Nullability_type_enum_and_open_object_changes_follow_directional_containment()
    {
        var analyzer = new MessageContractCompatibilityAnalyzer();
        ReadOnlyMemory<byte> nullable = ContractTestFixture.Utf8("{\"type\":[\"string\",\"null\"]}");
        ReadOnlyMemory<byte> text = ContractTestFixture.Utf8("{\"type\":\"string\"}");
        ReadOnlyMemory<byte> integer = ContractTestFixture.Utf8("{\"type\":\"integer\"}");
        ReadOnlyMemory<byte> enumOne = ContractTestFixture.Utf8("{\"type\":\"string\",\"enum\":[\"a\"]}");
        ReadOnlyMemory<byte> enumTwo = ContractTestFixture.Utf8("{\"type\":\"string\",\"enum\":[\"a\",\"b\"]}");
        ReadOnlyMemory<byte> open = ContractTestFixture.Utf8("{\"type\":\"object\",\"additionalProperties\":true}");
        ReadOnlyMemory<byte> closed = ContractTestFixture.Utf8("{\"type\":\"object\",\"additionalProperties\":false}");

        Assert.Equal(MessageContractCompatibilityStatus.Compatible, analyzer.Analyze(text, nullable, MessageContractCompatibilityMode.Backward).Status);
        Assert.Equal(MessageContractCompatibilityStatus.Breaking, analyzer.Analyze(text, nullable, MessageContractCompatibilityMode.Forward).Status);
        Assert.Equal(MessageContractCompatibilityStatus.Breaking, analyzer.Analyze(text, integer, MessageContractCompatibilityMode.Backward).Status);
        Assert.Equal(MessageContractCompatibilityStatus.Compatible, analyzer.Analyze(enumOne, enumTwo, MessageContractCompatibilityMode.Backward).Status);
        Assert.Equal(MessageContractCompatibilityStatus.Breaking, analyzer.Analyze(enumOne, enumTwo, MessageContractCompatibilityMode.Forward).Status);
        Assert.Equal(MessageContractCompatibilityStatus.Breaking, analyzer.Analyze(open, closed, MessageContractCompatibilityMode.Backward).Status);
    }

    [Fact]
    public void Mixed_inclusive_and_exclusive_numeric_bounds_are_compared_semantically()
    {
        var analyzer = new MessageContractCompatibilityAnalyzer();
        ReadOnlyMemory<byte> writerStrict = ContractTestFixture.Utf8("{\"type\":\"number\",\"exclusiveMinimum\":5}");
        ReadOnlyMemory<byte> readerPermissive = ContractTestFixture.Utf8("{\"type\":\"number\",\"minimum\":5}");
        ReadOnlyMemory<byte> writerPermissive = ContractTestFixture.Utf8("{\"type\":\"number\",\"minimum\":5}");
        ReadOnlyMemory<byte> readerStrict = ContractTestFixture.Utf8("{\"type\":\"number\",\"exclusiveMinimum\":5}");

        Assert.Equal(MessageContractCompatibilityStatus.Compatible, analyzer.Analyze(writerStrict, readerPermissive, MessageContractCompatibilityMode.Backward).Status);
        Assert.Equal(MessageContractCompatibilityStatus.Breaking, analyzer.Analyze(writerPermissive, readerStrict, MessageContractCompatibilityMode.Backward).Status);
    }

    [Fact]
    public void Named_reader_property_is_checked_against_writer_additional_properties_schema()
    {
        var analyzer = new MessageContractCompatibilityAnalyzer();
        ReadOnlyMemory<byte> writer = ContractTestFixture.Utf8("{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}}");
        ReadOnlyMemory<byte> compatibleReader = ContractTestFixture.Utf8("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}},\"additionalProperties\":true}");
        ReadOnlyMemory<byte> breakingReader = ContractTestFixture.Utf8("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"}},\"additionalProperties\":true}");

        Assert.Equal(MessageContractCompatibilityStatus.Compatible, analyzer.Analyze(writer, compatibleReader, MessageContractCompatibilityMode.Backward).Status);
        Assert.Equal(MessageContractCompatibilityStatus.Breaking, analyzer.Analyze(writer, breakingReader, MessageContractCompatibilityMode.Backward).Status);
    }

    [Fact]
    public void Malformed_or_unsupported_schema_constructs_require_review_instead_of_false_compatibility()
    {
        var analyzer = new MessageContractCompatibilityAnalyzer();
        ReadOnlyMemory<byte> valid = ContractTestFixture.Utf8("{\"type\":\"string\"}");
        ReadOnlyMemory<byte> malformed = ContractTestFixture.Utf8("{\"type\":[\"string\",7]}");
        ReadOnlyMemory<byte> unsupportedOld = ContractTestFixture.Utf8("{\"oneOf\":[{\"type\":\"string\"},{\"type\":\"integer\"}],\"description\":\"old\"}");
        ReadOnlyMemory<byte> unsupportedNew = ContractTestFixture.Utf8("{\"oneOf\":[{\"type\":\"string\"},{\"type\":\"integer\"}],\"description\":\"new\"}");

        Assert.Equal(MessageContractCompatibilityStatus.ReviewRequired, analyzer.Analyze(valid, malformed, MessageContractCompatibilityMode.Backward).Status);
        Assert.Equal(MessageContractCompatibilityStatus.ReviewRequired, analyzer.Analyze(unsupportedOld, unsupportedNew, MessageContractCompatibilityMode.Backward).Status);
    }

    [Fact]
    public void Published_baseline_with_duplicate_identity_is_rejected()
    {
        MessageContractArtifact published = Artifact("abc", "old docs");
        MessageContractManifest baseline = Manifest([published, published], provenance: true);
        MessageContractManifest current = Manifest([published], provenance: false);

        MessageContractBaselineValidationResult result = new MessageContractBaselineValidator().Validate(baseline, current);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Contains("duplicate wire identities", StringComparison.Ordinal));
    }

    private static MessageContractArtifact Artifact(string fingerprint, string summary) => new()
    {
        MessageType = "test.contract",
        MessageVersion = 1,
        SchemaDraft = MessageContractSchemaGenerator.SchemaDraft,
        SchemaPath = "test.contract/v1/schema.json",
        WireSchemaFingerprint = new("SHA-256", fingerprint.PadRight(64, '0')),
        Owner = "TCJ.Tests",
        CompatibilityMode = MessageContractCompatibilityMode.Backward,
        ChangeSummary = summary
    };

    private static MessageContractManifest Manifest(IReadOnlyList<MessageContractArtifact> contracts, bool provenance) => new()
    {
        SchemaVersion = 1,
        CanonicalizationVersion = 1,
        SchemaDraft = MessageContractSchemaGenerator.SchemaDraft,
        FingerprintAlgorithm = "SHA-256",
        BaselineProvenance = provenance ? new MessageContractBaselineProvenance
        {
            ReleaseVersion = "0.1.0-preview.4",
            SourceTag = "v0.1.0-preview.4",
            SourceCommit = new string('a', 40)
        } : null,
        Contracts = contracts
    };
}
