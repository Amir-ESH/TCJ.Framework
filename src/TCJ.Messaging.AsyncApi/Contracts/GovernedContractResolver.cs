using System.Text.Json;
using TCJ.Messaging.Contracts;

namespace TCJ.Messaging.AsyncApi;

/// <summary>Resolves explicit application catalog message references against offline governed Step 52 artifacts.</summary>
public static class GovernedContractResolver
{
    private const string ManifestFileName = "manifest.json";

    /// <summary>
    /// Resolves all producer, consumer-version, and channel message references using exact logical <c>MessageType + MessageVersion</c> identity.
    /// </summary>
    /// <param name="catalog">Validated Step 53 application messaging catalog input.</param>
    /// <param name="options">Explicit offline artifact-root and bounded validation options.</param>
    /// <returns>A fail-closed result. Contracts are exposed only when the complete requested set validates.</returns>
    public static GovernedContractResolutionResult Resolve(MessagingCatalog catalog, GovernedContractResolutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ArtifactRoot);
        if (options.MaximumSchemaBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumSchemaBytes), "MaximumSchemaBytes must be positive.");
        if (options.MaximumExampleBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumExampleBytes), "MaximumExampleBytes must be positive.");
        if (options.MaximumReferenceDepth <= 0 || options.MaximumReferenceDepth > 128)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumReferenceDepth), "MaximumReferenceDepth must be between 1 and 128.");

        var errors = new List<GovernedContractValidationError>();
        MessagingCatalogValidationResult catalogValidation = MessagingCatalogValidator.Validate(catalog);
        if (!catalogValidation.IsValid)
        {
            errors.AddRange(catalogValidation.Errors.Select(static error =>
                new GovernedContractValidationError(
                    GovernedContractValidationCodes.CatalogInvalid,
                    error.Path,
                    $"Catalog validation failed with {error.Code}: {error.Message}")));
            return Invalid(errors);
        }

        IReadOnlyList<CatalogContractReference> requested = CollectRequestedContracts(catalog, errors);
        string root = Path.GetFullPath(options.ArtifactRoot);
        if (!GovernedArtifactPath.TryResolve(root, ManifestFileName, out string manifestPath, out string manifestRelativePath) ||
            !File.Exists(manifestPath))
        {
            errors.Add(new(GovernedContractValidationCodes.ManifestMissing, ManifestFileName, "Step 52 contract manifest was not found in the configured artifact root."));
            return Invalid(errors);
        }

        if (!TryReadBounded(manifestPath, MessageContractManifestSerializer.MaximumManifestBytes, manifestRelativePath,
                GovernedContractValidationCodes.ManifestMalformed, errors, out ReadOnlyMemory<byte> manifestUtf8))
            return Invalid(errors);

        MessageContractManifest manifest;
        try
        {
            manifest = MessageContractManifestSerializer.Deserialize(manifestUtf8);
        }
        catch (JsonException exception)
        {
            errors.Add(ClassifyManifestError(exception));
            return Invalid(errors);
        }

        var byIdentity = new Dictionary<(string Type, int Version), MessageContractArtifact>();
        var versionsByType = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (MessageContractArtifact artifact in manifest.Contracts)
        {
            if (!byIdentity.TryAdd((artifact.MessageType, artifact.MessageVersion), artifact))
            {
                errors.Add(new(
                    GovernedContractValidationCodes.DuplicateContractIdentity,
                    ManifestFileName,
                    FormattableString.Invariant($"Governed contract identity '{artifact.MessageType}' v{artifact.MessageVersion} is duplicated.")));
                continue;
            }
            if (!versionsByType.TryGetValue(artifact.MessageType, out HashSet<int>? versions))
            {
                versions = [];
                versionsByType.Add(artifact.MessageType, versions);
            }
            versions.Add(artifact.MessageVersion);
        }

        var requestedIdentities = new HashSet<(string Type, int Version)>();
        foreach (CatalogContractReference reference in requested
            .OrderBy(static item => item.Path, StringComparer.Ordinal)
            .ThenBy(static item => item.MessageType, StringComparer.Ordinal)
            .ThenBy(static item => item.MessageVersion))
        {
            if (!versionsByType.TryGetValue(reference.MessageType, out HashSet<int>? versions))
            {
                errors.Add(new(
                    GovernedContractValidationCodes.ContractNotFound,
                    reference.Path,
                    $"Governed contract '{reference.MessageType}' was not found in the Step 52 manifest."));
                continue;
            }
            if (!versions.Contains(reference.MessageVersion))
            {
                errors.Add(new(
                    GovernedContractValidationCodes.MessageVersionNotFound,
                    reference.Path,
                    FormattableString.Invariant($"Governed contract '{reference.MessageType}' does not declare message version {reference.MessageVersion}.")));
                continue;
            }
            requestedIdentities.Add((reference.MessageType, reference.MessageVersion));
        }

        var resolved = new List<ResolvedGovernedMessageContract>(requestedIdentities.Count);
        foreach ((string messageType, int messageVersion) in requestedIdentities
            .OrderBy(static identity => identity.Type, StringComparer.Ordinal)
            .ThenBy(static identity => identity.Version))
        {
            MessageContractArtifact artifact = byIdentity[(messageType, messageVersion)];
            ResolvedGovernedMessageContract? contract = ResolveArtifact(root, manifest, artifact, options, errors);
            if (contract is not null)
                resolved.Add(contract);
        }

        if (errors.Count != 0)
            return Invalid(errors);
        return new(resolved, []);
    }

    private static ResolvedGovernedMessageContract? ResolveArtifact(
        string root,
        MessageContractManifest manifest,
        MessageContractArtifact artifact,
        GovernedContractResolutionOptions options,
        List<GovernedContractValidationError> errors)
    {
        if (!string.Equals(artifact.SchemaDraft, manifest.SchemaDraft, StringComparison.Ordinal))
        {
            errors.Add(new(
                GovernedContractValidationCodes.UnsupportedSchemaMetadata,
                artifact.SchemaPath,
                "Contract schema dialect does not agree with the governed Step 52 manifest."));
            return null;
        }

        if (!GovernedArtifactPath.TryResolve(root, artifact.SchemaPath, out string schemaPath, out string schemaRelativePath))
        {
            errors.Add(new(GovernedContractValidationCodes.SchemaPathTraversal, artifact.SchemaPath, "Governed schema path escapes or traverses the configured artifact root."));
            return null;
        }
        if (!File.Exists(schemaPath))
        {
            errors.Add(new(GovernedContractValidationCodes.SchemaMissing, schemaRelativePath, "Governed schema artifact does not exist."));
            return null;
        }
        if (!TryReadBounded(schemaPath, options.MaximumSchemaBytes, schemaRelativePath,
                GovernedContractValidationCodes.SchemaMalformed, errors, out ReadOnlyMemory<byte> schemaUtf8))
            return null;

        byte[] canonical;
        try
        {
            canonical = MessageContractSchemaGenerator.Canonicalize(schemaUtf8.Span);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            errors.Add(new(GovernedContractValidationCodes.SchemaMalformed, schemaRelativePath, "Governed schema artifact is not valid bounded JSON Schema input."));
            return null;
        }

        MessageContractFingerprint actual = MessageContractSchemaGenerator.ComputeFingerprint(canonical);
        if (!string.Equals(actual.Algorithm, artifact.WireSchemaFingerprint.Algorithm, StringComparison.Ordinal) ||
            !string.Equals(actual.Value, artifact.WireSchemaFingerprint.Value, StringComparison.Ordinal))
        {
            errors.Add(new(GovernedContractValidationCodes.SchemaFingerprintMismatch, schemaRelativePath, "Governed schema fingerprint does not match the Step 52 manifest."));
            return null;
        }

        if (!ValidateSchemaDialect(schemaUtf8, artifact, schemaRelativePath, errors))
            return null;

        int errorCountBeforeReferences = errors.Count;
        new GovernedSchemaReferenceValidator(root, options, errors).Validate(schemaRelativePath, schemaUtf8);
        if (errors.Count != errorCountBeforeReferences)
            return null;

        IReadOnlyList<ResolvedGovernedExample> examples = ResolveExamples(root, artifact, options, errors);
        if (errors.Count != errorCountBeforeReferences)
            return null;

        return new ResolvedGovernedMessageContract
        {
            MessageType = artifact.MessageType,
            MessageVersion = artifact.MessageVersion,
            ContentType = null,
            SchemaDialect = artifact.SchemaDraft,
            SchemaRelativePath = schemaRelativePath,
            SchemaFingerprint = artifact.WireSchemaFingerprint,
            SchemaUtf8 = schemaUtf8,
            Owner = artifact.Owner,
            CompatibilityMode = artifact.CompatibilityMode,
            Deprecated = artifact.Deprecated,
            DeprecatedSince = artifact.DeprecatedSince,
            ReplacementVersion = artifact.ReplacementVersion,
            ChangeSummary = artifact.ChangeSummary,
            MigrationNotes = artifact.MigrationNotes,
            SemanticCompatibilityNotes = artifact.SemanticCompatibilityNotes,
            DataClassifications = artifact.DataClassifications.ToArray(),
            ReviewedCompatibilityExceptions = artifact.ReviewedExceptions.ToArray(),
            Examples = examples
        };
    }

    private static IReadOnlyList<ResolvedGovernedExample> ResolveExamples(
        string root,
        MessageContractArtifact artifact,
        GovernedContractResolutionOptions options,
        List<GovernedContractValidationError> errors)
    {
        var examples = new List<ResolvedGovernedExample>(artifact.Examples.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string contractDirectory = artifact.SchemaPath[..artifact.SchemaPath.LastIndexOf('/')];
        string expectedPrefix = contractDirectory + "/examples/";
        foreach (string example in artifact.Examples.Order(StringComparer.Ordinal))
        {
            if (!seen.Add(example))
            {
                errors.Add(new(GovernedContractValidationCodes.DuplicateExampleReference, example, "Governed manifest contains a duplicate example reference."));
                continue;
            }
            if (!example.StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                errors.Add(new(GovernedContractValidationCodes.ExamplePathMismatch, example, "Governed example path does not match the Step 52 contract artifact layout."));
                continue;
            }
            if (!GovernedArtifactPath.TryResolve(root, example, out string examplePath, out string exampleRelativePath))
            {
                errors.Add(new(GovernedContractValidationCodes.ExamplePathTraversal, example, "Governed example path escapes or traverses the configured artifact root."));
                continue;
            }
            if (!File.Exists(examplePath))
            {
                errors.Add(new(GovernedContractValidationCodes.ExampleMissing, exampleRelativePath, "Governed example artifact does not exist."));
                continue;
            }
            if (!TryReadBounded(examplePath, options.MaximumExampleBytes, exampleRelativePath,
                    GovernedContractValidationCodes.ExampleMalformed, errors, out ReadOnlyMemory<byte> exampleUtf8))
                continue;
            try
            {
                using JsonDocument _ = JsonDocument.Parse(exampleUtf8, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 128
                });
            }
            catch (JsonException)
            {
                errors.Add(new(GovernedContractValidationCodes.ExampleMalformed, exampleRelativePath, "Governed example artifact is not valid JSON."));
                continue;
            }
            examples.Add(new ResolvedGovernedExample { RelativePath = exampleRelativePath, Utf8Json = exampleUtf8 });
        }
        return examples;
    }

    private static bool ValidateSchemaDialect(
        ReadOnlyMemory<byte> schemaUtf8,
        MessageContractArtifact artifact,
        string schemaRelativePath,
        List<GovernedContractValidationError> errors)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(schemaUtf8, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("$schema", out JsonElement schemaDialect))
            {
                if (schemaDialect.ValueKind != JsonValueKind.String ||
                    !string.Equals(schemaDialect.GetString(), artifact.SchemaDraft, StringComparison.Ordinal))
                {
                    errors.Add(new(GovernedContractValidationCodes.UnsupportedSchemaMetadata, schemaRelativePath, "Schema $schema metadata does not match the Step 52 governed dialect."));
                    return false;
                }
            }
            return true;
        }
        catch (JsonException)
        {
            errors.Add(new(GovernedContractValidationCodes.SchemaMalformed, schemaRelativePath, "Governed schema artifact is not structurally valid JSON."));
            return false;
        }
    }

    private static IReadOnlyList<CatalogContractReference> CollectRequestedContracts(
        MessagingCatalog catalog,
        List<GovernedContractValidationError> errors)
    {
        var result = new List<CatalogContractReference>();
        for (var index = 0; index < catalog.Producers.Count; index++)
        {
            MessagingProducer producer = catalog.Producers[index];
            result.Add(new(producer.Message.Type, producer.Message.Version, FormattableString.Invariant($"$.producers[{index}].message")));
        }
        for (var index = 0; index < catalog.Consumers.Count; index++)
        {
            MessagingConsumer consumer = catalog.Consumers[index];
            var seenVersions = new HashSet<int>();
            for (var versionIndex = 0; versionIndex < consumer.AcceptedMessageVersions.Count; versionIndex++)
            {
                int version = consumer.AcceptedMessageVersions[versionIndex];
                string path = FormattableString.Invariant($"$.consumers[{index}].acceptedMessageVersions[{versionIndex}]");
                if (!seenVersions.Add(version))
                {
                    errors.Add(new(GovernedContractValidationCodes.DuplicateAcceptedVersion, path, "Consumer accepted-message versions must not contain duplicates."));
                    continue;
                }
                result.Add(new(consumer.MessageType, version, path));
            }
        }
        for (var channelIndex = 0; channelIndex < catalog.Channels.Count; channelIndex++)
        {
            MessagingChannel channel = catalog.Channels[channelIndex];
            for (var messageIndex = 0; messageIndex < channel.Messages.Count; messageIndex++)
            {
                MessagingMessageReference message = channel.Messages[messageIndex];
                result.Add(new(message.Type, message.Version, FormattableString.Invariant($"$.channels[{channelIndex}].messages[{messageIndex}]")));
            }
        }
        return result;
    }

    private static bool TryReadBounded(
        string path,
        int maximumBytes,
        string relativePath,
        string invalidCode,
        List<GovernedContractValidationError> errors,
        out ReadOnlyMemory<byte> bytes)
    {
        bytes = default;
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > maximumBytes || info.Length > int.MaxValue)
            {
                errors.Add(new(invalidCode, relativePath, "Governed artifact is empty or exceeds the configured size bound."));
                return false;
            }
            bytes = File.ReadAllBytes(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add(new(GovernedContractValidationCodes.ArtifactReadFailure, relativePath, "Governed artifact could not be read."));
            return false;
        }
    }

    private static GovernedContractValidationError ClassifyManifestError(JsonException exception)
    {
        string message = exception.Message;
        if (message.Contains("Unsupported message-contract manifest schemaVersion", StringComparison.Ordinal) ||
            message.Contains("Unsupported message-contract canonicalizationVersion", StringComparison.Ordinal) ||
            message.Contains("Unsupported message-contract schemaDraft", StringComparison.Ordinal) ||
            message.Contains("Unsupported message-contract fingerprintAlgorithm", StringComparison.Ordinal))
            return new(GovernedContractValidationCodes.UnsupportedManifestVersion, ManifestFileName, "Step 52 manifest version or governed schema metadata is not supported.");
        if (message.Contains("Duplicate contract identity", StringComparison.Ordinal))
            return new(GovernedContractValidationCodes.DuplicateContractIdentity, ManifestFileName, "Step 52 manifest contains a duplicate governed contract identity.");
        if (message.Contains("schemaPath", StringComparison.Ordinal) &&
            message.Contains("safe relative path", StringComparison.Ordinal))
            return new(GovernedContractValidationCodes.SchemaPathTraversal, ManifestFileName, "Step 52 manifest contains an unsafe schema path.");
        if (message.Contains("schemaPath", StringComparison.Ordinal) &&
            message.Contains("artifact layout", StringComparison.Ordinal))
            return new(GovernedContractValidationCodes.SchemaIdentityMismatch, ManifestFileName, "Step 52 manifest schema path does not agree with its governed contract identity.");
        if (message.Contains("Example path", StringComparison.Ordinal))
            return new(GovernedContractValidationCodes.ExamplePathTraversal, ManifestFileName, "Step 52 manifest contains an invalid governed example path.");
        return new(GovernedContractValidationCodes.ManifestMalformed, ManifestFileName, "Step 52 manifest is malformed or structurally inconsistent.");
    }

    private static GovernedContractResolutionResult Invalid(List<GovernedContractValidationError> errors) =>
        new([], errors
            .OrderBy(static error => error.Path, StringComparer.Ordinal)
            .ThenBy(static error => error.Code, StringComparer.Ordinal)
            .ThenBy(static error => error.Message, StringComparer.Ordinal)
            .ToArray());

    private sealed record CatalogContractReference(string MessageType, int MessageVersion, string Path);
}
