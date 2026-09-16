using System.Text;
using FsCheck.Xunit;

namespace TCJ.Messaging.Contracts.Tests;

public sealed class MessageContractPropertyTests
{
    [Property(MaxTest = 100, Replay = "5201,6201")]
    [Trait("Category", "Property")]
    [Trait("Category", "MessageContracts")]
    public bool Canonicalization_is_idempotent(int value)
    {
        byte[] source = Encoding.UTF8.GetBytes($"{{\"minimum\":{value}.0,\"required\":[\"z\",\"a\"]}}");
        byte[] once = MessageContractSchemaGenerator.Canonicalize(source);
        byte[] twice = MessageContractSchemaGenerator.Canonicalize(once);
        return once.AsSpan().SequenceEqual(twice);
    }

    [Property(MaxTest = 100, Replay = "5202,6203")]
    [Trait("Category", "Property")]
    [Trait("Category", "MessageContracts")]
    public bool Compatibility_analyzer_fails_closed_without_throwing(byte[] candidate)
    {
        ReadOnlyMemory<byte> published = ContractTestFixture.Utf8("{\"type\":\"string\"}");
        MessageContractCompatibilityResult result = new MessageContractCompatibilityAnalyzer().Analyze(
            published, candidate, MessageContractCompatibilityMode.Backward);

        return result.Status is MessageContractCompatibilityStatus.Compatible
            or MessageContractCompatibilityStatus.Breaking
            or MessageContractCompatibilityStatus.ReviewRequired;
    }
}
