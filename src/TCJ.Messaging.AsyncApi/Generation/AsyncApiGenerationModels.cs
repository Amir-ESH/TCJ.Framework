namespace TCJ.Messaging.AsyncApi;

/// <summary>Governed AsyncAPI payload schema emission mode.</summary>
public enum AsyncApiSchemaMode
{
    /// <summary>References the validated Step 52 schema artifact by deterministic relative path.</summary>
    Referenced = 0,
    /// <summary>Embeds the validated Step 52 schema bytes without regeneration.</summary>
    Bundled = 1
}

/// <summary>Bounded deterministic options for core AsyncAPI document generation.</summary>
public sealed record AsyncApiGenerationOptions
{
    /// <summary>Gets the only governed AsyncAPI specification version supported by this generator.</summary>
    public string AsyncApiVersion { get; init; } = AsyncApiDocumentGenerator.SupportedAsyncApiVersion;

    /// <summary>Gets the explicit governed schema emission mode.</summary>
    public AsyncApiSchemaMode SchemaMode { get; init; } = AsyncApiSchemaMode.Referenced;

    /// <summary>Gets the maximum number of generated channels.</summary>
    public int MaximumChannels { get; init; } = 512;

    /// <summary>Gets the maximum number of generated message components.</summary>
    public int MaximumMessages { get; init; } = 512;

    /// <summary>Gets the maximum number of generated operations.</summary>
    public int MaximumOperations { get; init; } = 1024;

    /// <summary>Gets the maximum number of generated servers.</summary>
    public int MaximumServers { get; init; } = 512;

    /// <summary>Gets the maximum number of generated standard security schemes.</summary>
    public int MaximumSecuritySchemes { get; init; } = 512;
}

/// <summary>Stable machine-readable AsyncAPI generation diagnostic codes.</summary>
public static class AsyncApiGenerationCodes
{
    public const string UnsupportedAsyncApiVersion = "AAG001";
    public const string InvalidCatalog = "AAG002";
    public const string InvalidGovernedContracts = "AAG003";
    public const string UnknownContract = "AAG004";
    public const string IdentifierCollision = "AAG005";
    public const string UnsupportedSchemaMode = "AAG006";
    public const string DocumentBoundExceeded = "AAG007";
    public const string InvalidSchemaArtifact = "AAG008";
    public const string InvalidServerReference = "AAG009";
}

/// <summary>One deterministic, sensitive-safe AsyncAPI generation error.</summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="Path">Logical input path associated with the error.</param>
/// <param name="Message">Bounded sensitive-safe diagnostic text.</param>
public sealed record AsyncApiGenerationError(string Code, string Path, string Message);

/// <summary>Immutable deterministic AsyncAPI generation result.</summary>
public sealed class AsyncApiGenerationResult
{
    internal AsyncApiGenerationResult(ReadOnlyMemory<byte> utf8Json, IReadOnlyList<AsyncApiGenerationError> errors)
    {
        Utf8Json = utf8Json;
        Errors = errors;
    }

    /// <summary>Gets canonical UTF-8 JSON bytes without a BOM. Empty when generation fails.</summary>
    public ReadOnlyMemory<byte> Utf8Json { get; }

    /// <summary>Gets deterministic generation errors.</summary>
    public IReadOnlyList<AsyncApiGenerationError> Errors { get; }

    /// <summary>Gets whether generation completed successfully.</summary>
    public bool IsValid => Errors.Count == 0;
}
