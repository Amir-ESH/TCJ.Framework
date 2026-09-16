using TCJ.Messaging.Serialization;

namespace TCJ.Messaging.Contracts.Tests;

public sealed class UpcasterAndParserTests
{
    [Fact]
    public void Duplicate_upcaster_source_fails_graph_validation()
    {
        MessageUpcasterValidationResult result = new MessageUpcasterGraphValidator().Validate([new V1ToV2Upcaster(), new DuplicateV1Upcaster()]);
        Assert.False(result.IsValid);
    }


    [Fact]
    public void Cyclic_or_non_advancing_upcaster_path_fails_graph_validation()
    {
        MessageUpcasterValidationResult result = new MessageUpcasterGraphValidator().Validate(
            [new V1ToV2Upcaster(), new V2ToV1Upcaster()]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("strictly advance", StringComparison.Ordinal));
    }

    [Fact]
    public void Representative_example_upcasts_and_validates_as_target_contract()
    {
        MessagingMessageContract target = ContractTestFixture.Contract("test.contract", 2, ContractJsonContext.Default.ContractV2);
        GeneratedMessageContractSchema schema = new MessageContractSchemaGenerator().Generate(target);
        var validator = new MessageUpcasterGraphValidator();
        MessageUpcasterValidationResult result = validator.ValidateRepresentativePath(
            [new V1ToV2Upcaster()], "test.contract", 1, target,
            ContractTestFixture.Utf8("{\"order_id\":\"A-1\",\"Quantity\":2}"),
            schema, 1024 * 1024);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Oversized_source_payload_is_rejected_before_upcaster_execution()
    {
        MessagingMessageContract target = ContractTestFixture.Contract("test.contract", 2, ContractJsonContext.Default.ContractV2);
        GeneratedMessageContractSchema schema = new MessageContractSchemaGenerator().Generate(target);
        var upcaster = new CountingUpcaster();

        MessageUpcasterValidationResult result = new MessageUpcasterGraphValidator().ValidateRepresentativePath(
            [upcaster], "test.contract", 1, target, new byte[32], schema, maximumPayloadBytes: 16);

        Assert.False(result.IsValid);
        Assert.Equal(0, upcaster.InvocationCount);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":1}")]
    public void Malformed_manifest_is_rejected_safely(string json)
    {
        Assert.ThrowsAny<Exception>(() => MessageContractManifestSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(json)));
    }


    private sealed class V2ToV1Upcaster : IMessageUpcaster
    {
        public string MessageType => "test.contract";
        public int SourceVersion => 2;
        public int TargetVersion => 1;
        public ReadOnlyMemory<byte> Upcast(ReadOnlyMemory<byte> payload) => payload;
    }

    private sealed class CountingUpcaster : IMessageUpcaster
    {
        public int InvocationCount { get; private set; }
        public string MessageType => "test.contract";
        public int SourceVersion => 1;
        public int TargetVersion => 2;
        public ReadOnlyMemory<byte> Upcast(ReadOnlyMemory<byte> payload)
        {
            InvocationCount++;
            return payload;
        }
    }
}
