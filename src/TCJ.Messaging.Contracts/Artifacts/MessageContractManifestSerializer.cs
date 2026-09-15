using System.Buffers;
using System.Text.Json;

namespace TCJ.Messaging.Contracts;

/// <summary>Deterministic, bounded serializer for message-contract governance manifests.</summary>
public static class MessageContractManifestSerializer
{
    /// <summary>Maximum accepted manifest size.</summary>
    public const int MaximumManifestBytes = 16 * 1024 * 1024;

    /// <summary>Serializes a manifest with deterministic property and entry ordering.</summary>
    /// <param name="manifest">Manifest to serialize.</param>
    /// <returns>Canonical UTF-8 JSON bytes.</returns>
    public static byte[] Serialize(MessageContractManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            writer.WriteNumber("canonicalizationVersion", manifest.CanonicalizationVersion);
            writer.WriteString("schemaDraft", manifest.SchemaDraft);
            writer.WriteString("fingerprintAlgorithm", manifest.FingerprintAlgorithm);
            if (manifest.BaselineProvenance is not null)
            {
                writer.WritePropertyName("baselineProvenance");
                writer.WriteStartObject();
                writer.WriteString("releaseVersion", manifest.BaselineProvenance.ReleaseVersion);
                writer.WriteString("sourceTag", manifest.BaselineProvenance.SourceTag);
                writer.WriteString("sourceCommit", manifest.BaselineProvenance.SourceCommit);
                writer.WriteEndObject();
            }
            writer.WritePropertyName("contracts");
            writer.WriteStartArray();
            foreach (MessageContractArtifact artifact in manifest.Contracts
                .OrderBy(static item => item.MessageType, StringComparer.Ordinal)
                .ThenBy(static item => item.MessageVersion))
            {
                WriteArtifact(writer, artifact);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Parses a bounded manifest and rejects malformed or structurally invalid input.</summary>
    /// <param name="utf8Json">UTF-8 manifest data.</param>
    /// <returns>Parsed manifest.</returns>
    public static MessageContractManifest Deserialize(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length == 0 || utf8Json.Length > MaximumManifestBytes)
            throw new JsonException($"Manifest must be between 1 and {MaximumManifestBytes} bytes.");

        var options = new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        };
        using JsonDocument document = JsonDocument.Parse(utf8Json, options);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Manifest root must be an object.");

        int schemaVersion = RequiredPositiveInt(root, "schemaVersion");
        int canonicalizationVersion = RequiredPositiveInt(root, "canonicalizationVersion");
        string schemaDraft = RequiredString(root, "schemaDraft", 256);
        string fingerprintAlgorithm = RequiredString(root, "fingerprintAlgorithm", 32);
        if (schemaVersion != MessageContractArtifactGenerator.ManifestSchemaVersion)
            throw new JsonException($"Unsupported message-contract manifest schemaVersion '{schemaVersion}'.");
        if (canonicalizationVersion != MessageContractSchemaGenerator.CanonicalizationVersion)
            throw new JsonException($"Unsupported message-contract canonicalizationVersion '{canonicalizationVersion}'.");
        if (!string.Equals(schemaDraft, MessageContractSchemaGenerator.SchemaDraft, StringComparison.Ordinal))
            throw new JsonException($"Unsupported message-contract schemaDraft '{schemaDraft}'.");
        if (!string.Equals(fingerprintAlgorithm, MessageContractSchemaGenerator.FingerprintAlgorithm, StringComparison.Ordinal))
            throw new JsonException($"Unsupported message-contract fingerprintAlgorithm '{fingerprintAlgorithm}'.");
        MessageContractBaselineProvenance? provenance = null;
        if (root.TryGetProperty("baselineProvenance", out JsonElement provenanceElement))
        {
            RequireObject(provenanceElement, "baselineProvenance");
            string sourceCommit = RequiredString(provenanceElement, "sourceCommit", 40);
            if (sourceCommit.Length != 40 || sourceCommit.Any(static c => !Uri.IsHexDigit(c)))
                throw new JsonException("'baselineProvenance.sourceCommit' must be a full 40-character hexadecimal commit SHA.");
            provenance = new MessageContractBaselineProvenance
            {
                ReleaseVersion = RequiredString(provenanceElement, "releaseVersion", 128),
                SourceTag = RequiredString(provenanceElement, "sourceTag", 128),
                SourceCommit = sourceCommit.ToLowerInvariant()
            };
        }

        if (!root.TryGetProperty("contracts", out JsonElement contractsElement) || contractsElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("Manifest contracts must be an array.");
        if (contractsElement.GetArrayLength() > 10000)
            throw new JsonException("Manifest contains too many contracts.");

        var contracts = new List<MessageContractArtifact>(contractsElement.GetArrayLength());
        var identities = new HashSet<(string Type, int Version)>();
        foreach (JsonElement element in contractsElement.EnumerateArray())
        {
            MessageContractArtifact artifact = ReadArtifact(element);
            if (!string.Equals(artifact.SchemaDraft, schemaDraft, StringComparison.Ordinal))
                throw new JsonException($"Contract '{artifact.MessageType}' v{artifact.MessageVersion} schemaDraft does not match the manifest schemaDraft.");
            if (!string.Equals(artifact.WireSchemaFingerprint.Algorithm, fingerprintAlgorithm, StringComparison.Ordinal))
                throw new JsonException($"Contract '{artifact.MessageType}' v{artifact.MessageVersion} fingerprint algorithm does not match the manifest fingerprintAlgorithm.");
            if (!identities.Add((artifact.MessageType, artifact.MessageVersion)))
                throw new JsonException($"Duplicate contract identity '{artifact.MessageType}' v{artifact.MessageVersion}.");
            contracts.Add(artifact);
        }

        return new MessageContractManifest
        {
            SchemaVersion = schemaVersion,
            CanonicalizationVersion = canonicalizationVersion,
            SchemaDraft = schemaDraft,
            FingerprintAlgorithm = fingerprintAlgorithm,
            BaselineProvenance = provenance,
            Contracts = contracts.OrderBy(static item => item.MessageType, StringComparer.Ordinal).ThenBy(static item => item.MessageVersion).ToArray()
        };
    }

    private static void WriteArtifact(Utf8JsonWriter writer, MessageContractArtifact artifact)
    {
        writer.WriteStartObject();
        writer.WriteString("messageType", artifact.MessageType);
        writer.WriteNumber("messageVersion", artifact.MessageVersion);
        writer.WriteString("schemaDraft", artifact.SchemaDraft);
        writer.WriteString("schemaPath", artifact.SchemaPath);
        writer.WritePropertyName("wireSchemaFingerprint");
        writer.WriteStartObject();
        writer.WriteString("algorithm", artifact.WireSchemaFingerprint.Algorithm);
        writer.WriteString("value", artifact.WireSchemaFingerprint.Value);
        writer.WriteEndObject();
        writer.WriteString("owner", artifact.Owner);
        writer.WriteString("compatibilityMode", artifact.CompatibilityMode.ToString());
        writer.WriteBoolean("deprecated", artifact.Deprecated);
        if (artifact.DeprecatedSince is int deprecatedSince)
            writer.WriteNumber("deprecatedSince", deprecatedSince);
        if (artifact.ReplacementVersion is int replacementVersion)
            writer.WriteNumber("replacementVersion", replacementVersion);
        writer.WriteString("changeSummary", artifact.ChangeSummary);
        if (artifact.MigrationNotes is not null)
            writer.WriteString("migrationNotes", artifact.MigrationNotes);
        if (artifact.SemanticCompatibilityNotes is not null)
            writer.WriteString("semanticCompatibilityNotes", artifact.SemanticCompatibilityNotes);

        writer.WritePropertyName("dataClassifications");
        writer.WriteStartArray();
        foreach (MessageContractDataClassification classification in artifact.DataClassifications
            .OrderBy(static item => item.Path, StringComparer.Ordinal)
            .ThenBy(static item => item.Classification))
        {
            writer.WriteStartObject();
            writer.WriteString("path", classification.Path);
            writer.WriteString("classification", classification.Classification.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("reviewedExceptions");
        writer.WriteStartArray();
        foreach (MessageContractReviewException exception in artifact.ReviewedExceptions.OrderBy(static item => item.Code, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("code", exception.Code);
            writer.WriteString("justification", exception.Justification);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("examples");
        writer.WriteStartArray();
        foreach (string example in artifact.Examples.Order(StringComparer.Ordinal))
            writer.WriteStringValue(example);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static MessageContractArtifact ReadArtifact(JsonElement element)
    {
        RequireObject(element, "contract artifact");
        string messageType = RequiredString(element, "messageType", 128);
        ValidateMessageType(messageType);
        int messageVersion = RequiredPositiveInt(element, "messageVersion");
        string schemaDraft = RequiredString(element, "schemaDraft", 256);
        string schemaPath = RequiredRelativePath(element, "schemaPath", 512);
        string expectedSchemaPath = MessageContractArtifactPath.GetSchemaPath(messageType, messageVersion);
        if (!string.Equals(schemaPath, expectedSchemaPath, StringComparison.Ordinal))
            throw new JsonException($"'schemaPath' must match the contract identity and deterministic artifact layout '{expectedSchemaPath}'.");
        if (!element.TryGetProperty("wireSchemaFingerprint", out JsonElement fingerprint))
            throw new JsonException("wireSchemaFingerprint is required.");
        RequireObject(fingerprint, "wireSchemaFingerprint");
        var wireFingerprint = new MessageContractFingerprint(
            RequiredString(fingerprint, "algorithm", 32),
            RequiredHex(fingerprint, "value", 64));
        string owner = RequiredString(element, "owner", 128);
        string modeText = RequiredString(element, "compatibilityMode", 64);
        if (!Enum.TryParse(modeText, ignoreCase: false, out MessageContractCompatibilityMode mode))
            throw new JsonException($"Unsupported compatibility mode '{modeText}'.");

        bool deprecated = OptionalBoolean(element, "deprecated");
        int? deprecatedSince = OptionalPositiveInt(element, "deprecatedSince");
        int? replacementVersion = OptionalPositiveInt(element, "replacementVersion");
        string changeSummary = RequiredString(element, "changeSummary", 2048);
        string? migrationNotes = OptionalString(element, "migrationNotes", 4096);
        string? semanticNotes = OptionalString(element, "semanticCompatibilityNotes", 4096);

        IReadOnlyList<MessageContractDataClassification> classifications = ReadClassifications(element);
        IReadOnlyList<MessageContractReviewException> exceptions = ReadExceptions(element);
        IReadOnlyList<string> examples = ReadExamples(element);

        return new MessageContractArtifact
        {
            MessageType = messageType,
            MessageVersion = messageVersion,
            SchemaDraft = schemaDraft,
            SchemaPath = schemaPath,
            WireSchemaFingerprint = wireFingerprint,
            Owner = owner,
            CompatibilityMode = mode,
            Deprecated = deprecated,
            DeprecatedSince = deprecatedSince,
            ReplacementVersion = replacementVersion,
            ChangeSummary = changeSummary,
            MigrationNotes = migrationNotes,
            SemanticCompatibilityNotes = semanticNotes,
            DataClassifications = classifications,
            ReviewedExceptions = exceptions,
            Examples = examples
        };
    }

    private static IReadOnlyList<MessageContractDataClassification> ReadClassifications(JsonElement element)
    {
        if (!element.TryGetProperty("dataClassifications", out JsonElement array))
            return [];
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 256)
            throw new JsonException("dataClassifications must be a bounded array.");
        var result = new List<MessageContractDataClassification>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            RequireObject(item, "data classification");
            string path = RequiredString(item, "path", 256);
            string classificationText = RequiredString(item, "classification", 64);
            if (!Enum.TryParse(classificationText, ignoreCase: false, out MessageDataClassification classification))
                throw new JsonException($"Unsupported data classification '{classificationText}'.");
            result.Add(new MessageContractDataClassification(path, classification));
        }
        return result;
    }

    private static IReadOnlyList<MessageContractReviewException> ReadExceptions(JsonElement element)
    {
        if (!element.TryGetProperty("reviewedExceptions", out JsonElement array))
            return [];
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 256)
            throw new JsonException("reviewedExceptions must be a bounded array.");
        var result = new List<MessageContractReviewException>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            RequireObject(item, "review exception");
            result.Add(new MessageContractReviewException(
                RequiredString(item, "code", 256),
                RequiredString(item, "justification", 1024)));
        }
        return result;
    }

    private static IReadOnlyList<string> ReadExamples(JsonElement element)
    {
        if (!element.TryGetProperty("examples", out JsonElement array))
            return [];
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 256)
            throw new JsonException("examples must be a bounded array.");
        var result = new List<string>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new JsonException("Example paths must be strings.");
            string value = item.GetString()!;
            if (!IsSafeRelativePath(value, 512))
                throw new JsonException("Example path is invalid or escapes the artifact root.");
            result.Add(value);
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static string RequiredRelativePath(JsonElement element, string propertyName, int maximumLength)
    {
        string value = RequiredString(element, propertyName, maximumLength);
        if (!IsSafeRelativePath(value, maximumLength))
            throw new JsonException($"'{propertyName}' must be a safe relative path.");
        return value;
    }

    private static bool IsSafeRelativePath(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || Path.IsPathRooted(value) ||
            value.Contains('\\') || value.Contains(':'))
            return false;
        string[] parts = value.Split('/', StringSplitOptions.None);
        return parts.Length > 0 && parts.All(static part =>
            !string.IsNullOrEmpty(part) && part is not "." and not ".." && part.All(static c => !char.IsControl(c)));
    }

    private static void ValidateMessageType(string value)
    {
        if (!char.IsAsciiLetterOrDigit(value[0]) || value.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new JsonException("'messageType' is not a valid TCJ logical wire identity.");
    }

    private static string RequiredString(JsonElement element, string propertyName, int maximumLength)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
            throw new JsonException($"'{propertyName}' is required and must be a string.");
        string value = property.GetString()!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new JsonException($"'{propertyName}' is empty, too long, or contains control characters.");
        return value;
    }

    private static string RequiredHex(JsonElement element, string propertyName, int length)
    {
        string value = RequiredString(element, propertyName, length);
        if (value.Length != length || value.Any(static c => !Uri.IsHexDigit(c)))
            throw new JsonException($"'{propertyName}' must be a {length}-character hexadecimal value.");
        return value.ToLowerInvariant();
    }

    private static int RequiredPositiveInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || !property.TryGetInt32(out int value) || value <= 0)
            throw new JsonException($"'{propertyName}' must be a positive integer.");
        return value;
    }

    private static int? OptionalPositiveInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
            return null;
        if (!property.TryGetInt32(out int value) || value <= 0)
            throw new JsonException($"'{propertyName}' must be a positive integer when present.");
        return value;
    }

    private static bool OptionalBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
            return false;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException($"'{propertyName}' must be boolean.")
        };
    }

    private static string? OptionalString(JsonElement element, string propertyName, int maximumLength)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new JsonException($"'{propertyName}' must be a string when present.");
        string value = property.GetString()!;
        if (value.Length > maximumLength || value.Any(char.IsControl))
            throw new JsonException($"'{propertyName}' is too long or contains control characters.");
        return value;
    }

    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException($"{name} must be an object.");
    }
}
