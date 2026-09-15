using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using TCJ.Messaging.Contracts.Schema;
using TCJ.Messaging.Serialization;

namespace TCJ.Messaging.Contracts;

/// <summary>Generated schema plus deterministic canonical wire representation.</summary>
public sealed class GeneratedMessageContractSchema
{
    /// <summary>Gets the generated schema DOM.</summary>
    public required JsonNode Schema { get; init; }

    /// <summary>Gets the byte-for-byte deterministic canonical schema.</summary>
    public required ReadOnlyMemory<byte> CanonicalUtf8 { get; init; }

    /// <summary>Gets the canonical schema fingerprint.</summary>
    public required MessageContractFingerprint Fingerprint { get; init; }
}

/// <summary>Generates JSON Schema from the same explicit <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/> used by TCJ runtime messaging.</summary>
public sealed class MessageContractSchemaGenerator
{
    /// <summary>Pinned JSON Schema draft URI for Step 52 governance artifacts.</summary>
    public const string SchemaDraft = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>Canonicalization format version.</summary>
    public const int CanonicalizationVersion = 1;

    /// <summary>Wire-schema fingerprint algorithm.</summary>
    public const string FingerprintAlgorithm = "SHA-256";

    /// <summary>Generates deterministic schema bytes and fingerprint from an existing runtime registry contract.</summary>
    /// <param name="contract">Runtime message contract containing explicit serialization metadata.</param>
    /// <returns>Generated deterministic schema data.</returns>
    public GeneratedMessageContractSchema Generate(MessagingMessageContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        JsonNode schema;
        try
        {
            schema = contract.JsonTypeInfo.GetJsonSchemaAsNode();
        }
        catch (NotSupportedException exception)
        {
            throw new NotSupportedException(
                $"Message contract '{contract.MessageType}' v{contract.MessageVersion} uses System.Text.Json metadata that the BCL JSON Schema exporter cannot represent safely. The contract must be reviewed or use supported serializer metadata before governance artifacts can be generated.",
                exception);
        }
        if (schema is JsonObject schemaObject && !schemaObject.ContainsKey("$schema"))
            schemaObject.Insert(0, "$schema", SchemaDraft);

        byte[] canonical = JsonSchemaCanonicalizer.Canonicalize(schema);
        return new GeneratedMessageContractSchema
        {
            Schema = schema,
            CanonicalUtf8 = canonical,
            Fingerprint = ComputeFingerprint(canonical)
        };
    }

    /// <summary>Canonicalizes an existing JSON schema using the versioned TCJ canonical representation.</summary>
    /// <param name="schemaUtf8">UTF-8 JSON schema bytes.</param>
    /// <returns>Canonical UTF-8 bytes.</returns>
    public static byte[] Canonicalize(ReadOnlySpan<byte> schemaUtf8) =>
        JsonSchemaCanonicalizer.CanonicalizeUtf8(schemaUtf8);

    /// <summary>Computes the SHA-256 fingerprint of canonical schema bytes.</summary>
    /// <param name="canonicalSchemaUtf8">Canonical wire schema bytes.</param>
    /// <returns>Stable SHA-256 fingerprint.</returns>
    public static MessageContractFingerprint ComputeFingerprint(ReadOnlySpan<byte> canonicalSchemaUtf8)
    {
        byte[] hash = SHA256.HashData(canonicalSchemaUtf8);
        return new MessageContractFingerprint(FingerprintAlgorithm, Convert.ToHexString(hash).ToLowerInvariant());
    }
}
