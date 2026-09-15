using System.Text;
using TCJ.Messaging.Serialization;

namespace TCJ.Messaging.Contracts;

/// <summary>One runtime contract plus governance metadata and deterministic representative examples.</summary>
public sealed class MessageContractDefinition
{
    /// <summary>Gets the existing TCJ runtime contract used as schema source of truth.</summary>
    public required MessagingMessageContract RuntimeContract { get; init; }

    /// <summary>Gets governance metadata that does not participate in the wire-schema fingerprint.</summary>
    public required MessageContractMetadata Metadata { get; init; }

    /// <summary>Gets named representative JSON examples. Keys are safe file stems without extension.</summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Examples { get; init; } = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
}

/// <summary>Generates deterministic consumer-selected contract artifact trees.</summary>
public sealed class MessageContractArtifactGenerator
{
    /// <summary>Manifest schema version emitted by this package.</summary>
    public const int ManifestSchemaVersion = 1;

    private readonly MessageContractSchemaGenerator _schemaGenerator = new();
    private readonly MessageContractExampleValidator _exampleValidator = new();

    /// <summary>Generates schema, contract metadata, fingerprint, examples, and root manifest artifacts.</summary>
    /// <param name="outputRoot">Consumer-selected output root.</param>
    /// <param name="definitions">Runtime contracts and governance metadata.</param>
    /// <param name="baselineProvenance">Optional immutable release-baseline provenance.</param>
    /// <returns>Generated deterministic manifest.</returns>
    public MessageContractManifest Generate(
        string outputRoot,
        IEnumerable<MessageContractDefinition> definitions,
        MessageContractBaselineProvenance? baselineProvenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(definitions);

        string root = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(root);
        MessageContractDefinition[] ordered = definitions
            .OrderBy(static definition => definition.RuntimeContract.MessageType, StringComparer.Ordinal)
            .ThenBy(static definition => definition.RuntimeContract.MessageVersion)
            .ToArray();
        if (ordered.Length > 10000)
            throw new InvalidOperationException("Message-contract generation is limited to 10,000 contracts per manifest.");

        var identities = new HashSet<(string Type, int Version)>();
        var artifacts = new List<MessageContractArtifact>(ordered.Length);
        foreach (MessageContractDefinition definition in ordered)
        {
            ValidateDefinition(definition);
            MessagingMessageContract contract = definition.RuntimeContract;
            if (!identities.Add((contract.MessageType, contract.MessageVersion)))
                throw new InvalidOperationException($"Duplicate governed contract '{contract.MessageType}' v{contract.MessageVersion}.");

            GeneratedMessageContractSchema generated = _schemaGenerator.Generate(contract);
            string relativeDirectory = MessageContractArtifactPath.GetContractDirectory(contract.MessageType, contract.MessageVersion);
            string directory = ResolveInsideRoot(root, relativeDirectory);
            Directory.CreateDirectory(directory);

            string schemaPath = MessageContractArtifactPath.GetSchemaPath(contract.MessageType, contract.MessageVersion);
            WriteAllBytesDeterministically(ResolveInsideRoot(root, schemaPath), generated.CanonicalUtf8.Span);
            File.WriteAllText(
                ResolveInsideRoot(root, relativeDirectory + "/fingerprint.sha256"),
                generated.Fingerprint.Value + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var examplePaths = new List<string>();
            foreach ((string exampleName, ReadOnlyMemory<byte> example) in definition.Examples.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                ValidateFileStem(exampleName, nameof(definition.Examples));
                MessageContractExampleValidationResult validation = _exampleValidator.Validate(contract, generated, example, definition.Metadata);
                if (!validation.IsValid)
                    throw new InvalidOperationException($"Example '{exampleName}' for '{contract.MessageType}' v{contract.MessageVersion} is invalid: {string.Join("; ", validation.Errors)}");
                byte[] canonicalExample = _exampleValidator.CanonicalizeExample(example.Span);
                string relativeExample = relativeDirectory + "/examples/" + exampleName + ".json";
                string examplePath = ResolveInsideRoot(root, relativeExample);
                Directory.CreateDirectory(Path.GetDirectoryName(examplePath)!);
                WriteAllBytesDeterministically(examplePath, canonicalExample);
                examplePaths.Add(relativeExample);
            }

            var artifact = new MessageContractArtifact
            {
                MessageType = contract.MessageType,
                MessageVersion = contract.MessageVersion,
                SchemaDraft = MessageContractSchemaGenerator.SchemaDraft,
                SchemaPath = schemaPath,
                WireSchemaFingerprint = generated.Fingerprint,
                Owner = definition.Metadata.Owner,
                CompatibilityMode = definition.Metadata.CompatibilityMode,
                Deprecated = definition.Metadata.Deprecated,
                DeprecatedSince = definition.Metadata.DeprecatedSince,
                ReplacementVersion = definition.Metadata.ReplacementVersion,
                ChangeSummary = definition.Metadata.ChangeSummary,
                MigrationNotes = definition.Metadata.MigrationNotes,
                SemanticCompatibilityNotes = definition.Metadata.SemanticCompatibilityNotes,
                DataClassifications = definition.Metadata.DataClassifications,
                ReviewedExceptions = definition.Metadata.ReviewedExceptions,
                Examples = examplePaths.Order(StringComparer.Ordinal).ToArray()
            };
            artifacts.Add(artifact);

            var oneContractManifest = new MessageContractManifest
            {
                SchemaVersion = ManifestSchemaVersion,
                CanonicalizationVersion = MessageContractSchemaGenerator.CanonicalizationVersion,
                SchemaDraft = MessageContractSchemaGenerator.SchemaDraft,
                FingerprintAlgorithm = MessageContractSchemaGenerator.FingerprintAlgorithm,
                Contracts = [artifact]
            };
            WriteAllBytesDeterministically(
                ResolveInsideRoot(root, relativeDirectory + "/contract.json"),
                SerializeArtifact(oneContractManifest));
        }

        var manifest = new MessageContractManifest
        {
            SchemaVersion = ManifestSchemaVersion,
            CanonicalizationVersion = MessageContractSchemaGenerator.CanonicalizationVersion,
            SchemaDraft = MessageContractSchemaGenerator.SchemaDraft,
            FingerprintAlgorithm = MessageContractSchemaGenerator.FingerprintAlgorithm,
            BaselineProvenance = baselineProvenance,
            Contracts = artifacts
        };
        WriteAllBytesDeterministically(ResolveInsideRoot(root, "manifest.json"), MessageContractManifestSerializer.Serialize(manifest));
        return manifest;
    }

    private static byte[] SerializeArtifact(MessageContractManifest singleArtifactManifest)
    {
        byte[] manifestBytes = MessageContractManifestSerializer.Serialize(singleArtifactManifest);
        using var document = System.Text.Json.JsonDocument.Parse(manifestBytes);
        System.Text.Json.JsonElement contract = document.RootElement.GetProperty("contracts")[0];
        return Encoding.UTF8.GetBytes(contract.GetRawText());
    }

    private static void ValidateDefinition(MessageContractDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.RuntimeContract);
        ArgumentNullException.ThrowIfNull(definition.Metadata);
        ValidateMessageType(definition.RuntimeContract.MessageType);
        if (definition.RuntimeContract.MessageVersion <= 0)
            throw new InvalidOperationException("Message version must be positive.");
        ValidateBoundedText(definition.Metadata.Owner, nameof(definition.Metadata.Owner), 128, required: true);
        ValidateBoundedText(definition.Metadata.ChangeSummary, nameof(definition.Metadata.ChangeSummary), 2048, required: true);
        ValidateBoundedText(definition.Metadata.MigrationNotes, nameof(definition.Metadata.MigrationNotes), 4096, required: false);
        ValidateBoundedText(definition.Metadata.SemanticCompatibilityNotes, nameof(definition.Metadata.SemanticCompatibilityNotes), 4096, required: false);
        if (definition.Metadata.DeprecatedSince is <= 0 || definition.Metadata.ReplacementVersion is <= 0)
            throw new InvalidOperationException("Deprecation/replacement versions must be positive when present.");
        if (definition.Metadata.DataClassifications.Count > 256 || definition.Metadata.ReviewedExceptions.Count > 256 || definition.Examples.Count > 256)
            throw new InvalidOperationException("Governance metadata exceeds bounded collection limits.");
        foreach (MessageContractDataClassification classification in definition.Metadata.DataClassifications)
        {
            ValidateBoundedText(classification.Path, nameof(classification.Path), 256, required: true);
            if (classification.Classification == MessageDataClassification.SecretProhibited)
                throw new InvalidOperationException($"SecretProhibited field '{classification.Path}' is forbidden by message-contract governance.");
        }
        foreach (MessageContractReviewException exception in definition.Metadata.ReviewedExceptions)
        {
            ValidateBoundedText(exception.Code, nameof(exception.Code), 256, required: true);
            ValidateBoundedText(exception.Justification, nameof(exception.Justification), 1024, required: true);
        }
    }

    private static void ValidateMessageType(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new InvalidOperationException("Message type is not a bounded TCJ logical wire identity.");
    }

    private static void ValidateFileStem(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) || value is "." or "..")
            throw new ArgumentException("Artifact file names must be bounded safe ASCII file stems.", parameterName);
    }

    private static void ValidateBoundedText(string? value, string name, int maximumLength, bool required)
    {
        if ((required && string.IsNullOrWhiteSpace(value)) || (value is not null && (value.Length > maximumLength || value.Any(char.IsControl))))
            throw new InvalidOperationException($"Governance metadata '{name}' is missing, too long, or contains control characters.");
    }

    private static string ResolveInsideRoot(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
            throw new InvalidOperationException("Artifact path is not a safe relative path.");

        string[] segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(static segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
            throw new InvalidOperationException("Artifact path contains an unsafe path segment.");

        string candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(prefix, comparison) && !string.Equals(candidate, root, comparison))
            throw new InvalidOperationException("Artifact path escapes the selected output root.");

        RejectExistingLinkTraversal(root, segments);
        return candidate;
    }

    private static void RejectExistingLinkTraversal(string root, IReadOnlyList<string> segments)
    {
        string current = root;
        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!Path.Exists(current))
                continue;

            FileSystemInfo entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (entry.LinkTarget is not null)
                throw new InvalidOperationException("Artifact path traverses an existing symbolic link or reparse point.");
        }
    }

    private static void WriteAllBytesDeterministically(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }
}
