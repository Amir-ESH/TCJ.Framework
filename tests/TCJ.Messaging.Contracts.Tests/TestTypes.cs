using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TCJ.Messaging.Extensions;
using TCJ.Messaging.Serialization;

namespace TCJ.Messaging.Contracts.Tests;

internal sealed record ContractV1([property: JsonPropertyName("order_id")] string OrderId, int Quantity);
internal sealed record ContractV2([property: JsonPropertyName("order_id")] string OrderId, int Quantity, string? Note = null);

[JsonConverter(typeof(JsonStringEnumConverter<FixtureStatus>))]
internal enum FixtureStatus
{
    Pending,
    Completed
}

internal sealed record SchemaShapeFixture(
    [property: JsonPropertyName("custom_id")] string Id,
    string RequiredText,
    string? OptionalText,
    IReadOnlyList<string?> Tags,
    FixtureStatus Status,
    [property: JsonIgnore] string? Ignored = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ClosedObjectFixture(string Id);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(PolymorphicEmailFixture), typeDiscriminator: "email")]
internal abstract record PolymorphicFixture;

internal sealed record PolymorphicEmailFixture(string Address) : PolymorphicFixture;

[JsonSerializable(typeof(ContractV1))]
[JsonSerializable(typeof(ContractV2))]
[JsonSerializable(typeof(SchemaShapeFixture))]
[JsonSerializable(typeof(ClosedObjectFixture))]
[JsonSerializable(typeof(PolymorphicFixture))]
[JsonSerializable(typeof(PolymorphicEmailFixture))]
internal sealed partial class ContractJsonContext : JsonSerializerContext;

internal static class ContractTestFixture
{
    public static MessagingMessageContract Contract<T>(string type, int version, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var services = new ServiceCollection();
        services.AddTcjMessaging();
        services.AddTcjMessage(type, version, typeInfo);

        MessagingMessageContract[] contracts = services
            .Where(static descriptor => descriptor.ServiceType == typeof(MessagingMessageContract))
            .Select(static descriptor => descriptor.ImplementationInstance)
            .OfType<MessagingMessageContract>()
            .ToArray();

        return new MessageContractRegistry(contracts).Resolve(type, version);
    }

    public static MessageContractMetadata Metadata(string summary = "synthetic fixture") => new()
    {
        Owner = "TCJ.Tests",
        ChangeSummary = summary,
        SemanticCompatibilityNotes = "Synthetic test contract; semantics reviewed."
    };

    public static ReadOnlyMemory<byte> Utf8(string json) => JsonSerializer.SerializeToUtf8Bytes(JsonDocument.Parse(json).RootElement);
}

internal sealed class V1ToV2Upcaster : IMessageUpcaster
{
    public string MessageType => "test.contract";
    public int SourceVersion => 1;
    public int TargetVersion => 2;
    public ReadOnlyMemory<byte> Upcast(ReadOnlyMemory<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        string orderId = document.RootElement.GetProperty("order_id").GetString()!;
        int quantity = document.RootElement.GetProperty("Quantity").GetInt32();
        return JsonSerializer.SerializeToUtf8Bytes(new ContractV2(orderId, quantity, "upcast"), ContractJsonContext.Default.ContractV2);
    }
}

internal sealed class DuplicateV1Upcaster : IMessageUpcaster
{
    public string MessageType => "test.contract";
    public int SourceVersion => 1;
    public int TargetVersion => 3;
    public ReadOnlyMemory<byte> Upcast(ReadOnlyMemory<byte> payload) => payload;
}
