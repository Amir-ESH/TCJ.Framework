using TCJ.Messaging.Contracts;

namespace TCJ.Messaging.AsyncApi;

/// <summary>Stable diagnostics emitted while resolving governed Step 52 contract artifacts.</summary>
public static class GovernedContractValidationCodes
{
    /// <summary>Manifest artifact is missing.</summary>
    public const string ManifestMissing = "GCR001";
    /// <summary>Manifest JSON or structure is malformed.</summary>
    public const string ManifestMalformed = "GCR002";
    /// <summary>Manifest governance version or governed metadata version is unsupported.</summary>
    public const string UnsupportedManifestVersion = "GCR003";
    /// <summary>Manifest contains a duplicate logical message type/version identity.</summary>
    public const string DuplicateContractIdentity = "GCR004";
    /// <summary>Requested logical message type is absent from the manifest.</summary>
    public const string ContractNotFound = "GCR005";
    /// <summary>Requested logical message version is absent from the manifest.</summary>
    public const string MessageVersionNotFound = "GCR006";
    /// <summary>Declared governed schema artifact is missing.</summary>
    public const string SchemaMissing = "GCR007";
    /// <summary>Governed schema artifact is malformed.</summary>
    public const string SchemaMalformed = "GCR008";
    /// <summary>Schema path is unsafe or escapes the configured artifact root.</summary>
    public const string SchemaPathTraversal = "GCR009";
    /// <summary>Governed schema fingerprint does not match the Step 52 manifest.</summary>
    public const string SchemaFingerprintMismatch = "GCR010";
    /// <summary>Local JSON Schema reference is malformed or cannot be resolved.</summary>
    public const string InvalidLocalReference = "GCR011";
    /// <summary>Remote or absolute JSON Schema reference is prohibited.</summary>
    public const string RemoteReferenceNotAllowed = "GCR012";
    /// <summary>Governed example artifact is missing.</summary>
    public const string ExampleMissing = "GCR013";
    /// <summary>Governed example path is unsafe or escapes the configured artifact root.</summary>
    public const string ExamplePathTraversal = "GCR014";
    /// <summary>Application messaging catalog is invalid.</summary>
    public const string CatalogInvalid = "GCR015";
    /// <summary>Consumer declares a duplicate accepted message version.</summary>
    public const string DuplicateAcceptedVersion = "GCR016";
    /// <summary>Governed schema metadata is unsupported or inconsistent.</summary>
    public const string UnsupportedSchemaMetadata = "GCR017";
    /// <summary>Governed artifact could not be read safely.</summary>
    public const string ArtifactReadFailure = "GCR018";
    /// <summary>Manifest contains a duplicate governed example reference.</summary>
    public const string DuplicateExampleReference = "GCR019";
    /// <summary>Governed example artifact is malformed.</summary>
    public const string ExampleMalformed = "GCR020";
    /// <summary>Governed example path does not match the contract artifact layout.</summary>
    public const string ExamplePathMismatch = "GCR021";
    /// <summary>Local JSON Schema reference nesting exceeds the configured bound.</summary>
    public const string ReferenceDepthExceeded = "GCR022";
    /// <summary>Schema path does not agree with the governed message identity.</summary>
    public const string SchemaIdentityMismatch = "GCR023";
}

/// <summary>One deterministic, sensitive-safe governed-contract validation diagnostic.</summary>
/// <param name="Code">Stable machine-consumable diagnostic code.</param>
/// <param name="Path">Logical catalog or artifact-relative path associated with the error.</param>
/// <param name="Message">Bounded diagnostic message that does not contain payload data.</param>
public sealed record GovernedContractValidationError(string Code, string Path, string Message);

/// <summary>Bounded offline options for Step 52 governed-contract resolution.</summary>
public sealed record GovernedContractResolutionOptions
{
    /// <summary>Gets the explicit Step 52 artifact root containing <c>manifest.json</c>.</summary>
    public required string ArtifactRoot { get; init; }

    /// <summary>Gets the maximum accepted schema artifact size.</summary>
    public int MaximumSchemaBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Gets the maximum accepted governed example size.</summary>
    public int MaximumExampleBytes { get; init; } = 1024 * 1024;

    /// <summary>Gets the maximum recursive local-schema reference depth.</summary>
    public int MaximumReferenceDepth { get; init; } = 32;
}

/// <summary>One governed Step 52 example made available to later offline generation stages.</summary>
public sealed record ResolvedGovernedExample
{
    /// <summary>Gets the normalized artifact-root-relative path using forward slashes.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Gets the validated governed JSON bytes without mutation.</summary>
    public required ReadOnlyMemory<byte> Utf8Json { get; init; }
}

/// <summary>Validated immutable view of one governed Step 52 message contract.</summary>
public sealed record ResolvedGovernedMessageContract
{
    /// <summary>Gets the stable logical wire message type.</summary>
    public required string MessageType { get; init; }

    /// <summary>Gets the explicit positive wire message version.</summary>
    public required int MessageVersion { get; init; }

    /// <summary>
    /// Gets the governed content type when Step 52 supplies one. The current Step 52 manifest does not define a content-type field,
    /// so absence remains <see langword="null"/> rather than being inferred by Step 53.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>Gets the pinned Step 52 JSON Schema dialect URI.</summary>
    public required string SchemaDialect { get; init; }

    /// <summary>Gets the normalized artifact-root-relative governed schema path.</summary>
    public required string SchemaRelativePath { get; init; }

    /// <summary>Gets the Step 52 governed canonical wire-schema fingerprint.</summary>
    public required MessageContractFingerprint SchemaFingerprint { get; init; }

    /// <summary>Gets the validated governed schema bytes without regeneration or rewrite.</summary>
    public required ReadOnlyMemory<byte> SchemaUtf8 { get; init; }

    /// <summary>Gets the Step 52 governed owner.</summary>
    public required string Owner { get; init; }

    /// <summary>Gets the Step 52 governed compatibility mode without recomputation.</summary>
    public required MessageContractCompatibilityMode CompatibilityMode { get; init; }

    /// <summary>Gets whether Step 52 marks this contract version deprecated.</summary>
    public bool Deprecated { get; init; }

    /// <summary>Gets the governed deprecation version when present.</summary>
    public int? DeprecatedSince { get; init; }

    /// <summary>Gets the governed replacement version when present.</summary>
    public int? ReplacementVersion { get; init; }

    /// <summary>Gets the governed change summary.</summary>
    public required string ChangeSummary { get; init; }

    /// <summary>Gets governed migration guidance when present.</summary>
    public string? MigrationNotes { get; init; }

    /// <summary>Gets governed semantic compatibility notes when present.</summary>
    public string? SemanticCompatibilityNotes { get; init; }

    /// <summary>Gets governed data classifications without inference.</summary>
    public IReadOnlyList<MessageContractDataClassification> DataClassifications { get; init; } = [];

    /// <summary>Gets existing reviewed compatibility exceptions without running a compatibility engine.</summary>
    public IReadOnlyList<MessageContractReviewException> ReviewedCompatibilityExceptions { get; init; } = [];

    /// <summary>Gets only examples declared by the Step 52 governed manifest.</summary>
    public IReadOnlyList<ResolvedGovernedExample> Examples { get; init; } = [];
}

/// <summary>Fail-closed result of resolving a catalog against governed Step 52 artifacts.</summary>
public sealed class GovernedContractResolutionResult
{
    internal GovernedContractResolutionResult(
        IReadOnlyList<ResolvedGovernedMessageContract> contracts,
        IReadOnlyList<GovernedContractValidationError> errors)
    {
        Contracts = contracts;
        Errors = errors;
    }

    /// <summary>Gets fully validated contracts. This collection is empty when any validation error exists.</summary>
    public IReadOnlyList<ResolvedGovernedMessageContract> Contracts { get; }

    /// <summary>Gets deterministic validation diagnostics.</summary>
    public IReadOnlyList<GovernedContractValidationError> Errors { get; }

    /// <summary>Gets whether all requested catalog contracts resolved and validated successfully.</summary>
    public bool IsValid => Errors.Count == 0;
}
