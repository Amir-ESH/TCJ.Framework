using System.Text;
using System.Text.Json;
using TCJ.Messaging.Contracts.Validation;
using TCJ.Messaging.Serialization;

namespace TCJ.Messaging.Contracts;

/// <summary>Result of representative example validation.</summary>
public sealed class MessageContractExampleValidationResult
{
    /// <summary>Gets whether schema, runtime deserialization, bounds, and security checks all succeeded.</summary>
    public required bool IsValid { get; init; }

    /// <summary>Gets deterministic validation errors without payload contents.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>Validates bounded representative JSON payloads against schema and the runtime <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/> contract.</summary>
public sealed class MessageContractExampleValidator
{
    private static readonly HashSet<string> SensitivePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "secret", "clientsecret", "accesstoken", "refreshtoken", "authorization", "apikey", "api_key", "connectionstring"
    };

    /// <summary>Validates one representative example without using reflection-based type discovery.</summary>
    /// <param name="contract">Existing runtime message contract.</param>
    /// <param name="schema">Generated schema for the same runtime contract.</param>
    /// <param name="exampleUtf8">Representative UTF-8 JSON payload.</param>
    /// <param name="metadata">Optional governance metadata used for classification enforcement.</param>
    /// <param name="maximumPayloadBytes">Maximum allowed example/upcast payload size.</param>
    /// <returns>Validation result containing no payload data.</returns>
    public MessageContractExampleValidationResult Validate(
        MessagingMessageContract contract,
        GeneratedMessageContractSchema schema,
        ReadOnlyMemory<byte> exampleUtf8,
        MessageContractMetadata? metadata = null,
        int maximumPayloadBytes = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(schema);
        if (maximumPayloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

        var errors = new List<string>();
        if (exampleUtf8.Length == 0 || exampleUtf8.Length > maximumPayloadBytes)
        {
            errors.Add("Example payload is empty or exceeds the configured payload bound.");
            return Result(errors);
        }
        if (metadata?.DataClassifications.Any(static item => item.Classification == MessageDataClassification.SecretProhibited) == true)
            errors.Add("SecretProhibited classification is not permitted for governed integration-message fields.");

        try
        {
            var documentOptions = new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            };
            using JsonDocument instanceDocument = JsonDocument.Parse(exampleUtf8, documentOptions);
            using JsonDocument schemaDocument = JsonDocument.Parse(schema.CanonicalUtf8, documentOptions);
            errors.AddRange(JsonSchemaInstanceValidator.Validate(instanceDocument.RootElement, schemaDocument.RootElement));
            InspectSensitiveExample(instanceDocument.RootElement, "$", errors, 0);

            try
            {
                object? value = JsonSerializer.Deserialize(exampleUtf8.Span, contract.JsonTypeInfo);
                if (value is null)
                    errors.Add("Runtime JsonTypeInfo deserialization returned null.");
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                errors.Add("Runtime JsonTypeInfo deserialization rejected the example.");
            }
        }
        catch (JsonException)
        {
            errors.Add("Example payload is malformed JSON or exceeds the maximum JSON depth.");
        }

        return Result(errors);
    }

    /// <summary>Produces the deterministic canonical JSON representation used for committed example artifacts.</summary>
    /// <param name="exampleUtf8">Representative example payload.</param>
    /// <returns>Canonical UTF-8 JSON bytes.</returns>
    public byte[] CanonicalizeExample(ReadOnlySpan<byte> exampleUtf8) => MessageContractSchemaGenerator.Canonicalize(exampleUtf8);

    private static void InspectSensitiveExample(JsonElement element, string path, List<string> errors, int depth)
    {
        if (depth > 128)
        {
            errors.Add("Example security scan exceeded maximum JSON depth.");
            return;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string compactName = new(property.Name.Where(static c => c != '-' && c != '.').ToArray());
                if (SensitivePropertyNames.Contains(compactName))
                    errors.Add($"{path}: example contains a prohibited secret-like property name.");
                InspectSensitiveExample(property.Value, path + "." + property.Name, errors, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                InspectSensitiveExample(item, path + "[]", errors, depth + 1);
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            string value = element.GetString()!;
            if (value.Contains("BEGIN PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("AccountKey=", StringComparison.OrdinalIgnoreCase))
                errors.Add($"{path}: example contains a prohibited credential marker.");
        }
    }

    private static MessageContractExampleValidationResult Result(List<string> errors) => new()
    {
        IsValid = errors.Count == 0,
        Errors = errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
    };
}
