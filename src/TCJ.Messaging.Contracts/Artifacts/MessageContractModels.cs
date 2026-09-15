using System.Collections.ObjectModel;

namespace TCJ.Messaging.Contracts;

/// <summary>Supported schema compatibility policies using standard schema-registry direction semantics.</summary>
public enum MessageContractCompatibilityMode
{
    /// <summary>No structural compatibility requirement is enforced.</summary>
    None,
    /// <summary>The new reader must accept data written using the previous schema.</summary>
    Backward,
    /// <summary>The previous reader must accept data written using the new schema.</summary>
    Forward,
    /// <summary>Both backward and forward compatibility are required.</summary>
    Full,
    /// <summary>Backward compatibility is required against every retained published version.</summary>
    BackwardTransitive,
    /// <summary>Forward compatibility is required against every retained published version.</summary>
    ForwardTransitive,
    /// <summary>Full compatibility is required against every retained published version.</summary>
    FullTransitive
}

/// <summary>Conservative result of a structural schema compatibility analysis.</summary>
public enum MessageContractCompatibilityStatus
{
    /// <summary>The analyzer proved the supported structural rules are compatible.</summary>
    Compatible,
    /// <summary>The analyzer found a structural change that violates the requested direction.</summary>
    Breaking,
    /// <summary>The analyzer cannot safely prove compatibility and human review is required.</summary>
    ReviewRequired
}

/// <summary>Data sensitivity classification carried as governance metadata.</summary>
public enum MessageDataClassification
{
    /// <summary>Data intended for public disclosure.</summary>
    Public,
    /// <summary>Non-public operational data.</summary>
    Internal,
    /// <summary>Personal data requiring application-specific handling.</summary>
    Personal,
    /// <summary>Confidential data requiring stronger operational controls.</summary>
    Confidential,
    /// <summary>Secret-bearing content that governance policy prohibits in integration contracts.</summary>
    SecretProhibited
}

/// <summary>SHA-256 fingerprint of a canonical wire schema.</summary>
/// <param name="Algorithm">Fingerprint algorithm identifier.</param>
/// <param name="Value">Lowercase hexadecimal digest.</param>
public sealed record MessageContractFingerprint(string Algorithm, string Value);

/// <summary>Classification metadata for one logical JSON path.</summary>
/// <param name="Path">Bounded logical JSON path, such as <c>$.customerId</c>.</param>
/// <param name="Classification">Declared data classification.</param>
public sealed record MessageContractDataClassification(string Path, MessageDataClassification Classification);

/// <summary>Human-reviewed exception for a conservative compatibility finding.</summary>
/// <param name="Code">Stable finding code returned by the analyzer.</param>
/// <param name="Justification">Bounded human justification for accepting the review-required finding.</param>
public sealed record MessageContractReviewException(string Code, string Justification);

/// <summary>Governance metadata associated with one runtime message contract.</summary>
public sealed class MessageContractMetadata
{
    /// <summary>Gets or sets the bounded owning team or component identifier.</summary>
    public required string Owner { get; init; }

    /// <summary>Gets or sets the compatibility mode for the contract family.</summary>
    public MessageContractCompatibilityMode CompatibilityMode { get; init; } = MessageContractCompatibilityMode.Backward;

    /// <summary>Gets or sets whether this version is deprecated.</summary>
    public bool Deprecated { get; init; }

    /// <summary>Gets or sets the version at which deprecation became effective.</summary>
    public int? DeprecatedSince { get; init; }

    /// <summary>Gets or sets an optional replacement message version.</summary>
    public int? ReplacementVersion { get; init; }

    /// <summary>Gets or sets a bounded human-reviewed summary of the wire/semantic change.</summary>
    public required string ChangeSummary { get; init; }

    /// <summary>Gets or sets optional migration guidance.</summary>
    public string? MigrationNotes { get; init; }

    /// <summary>Gets or sets optional explicit semantic-compatibility review notes.</summary>
    public string? SemanticCompatibilityNotes { get; init; }

    /// <summary>Gets declared data classifications.</summary>
    public IReadOnlyList<MessageContractDataClassification> DataClassifications { get; init; } = [];

    /// <summary>Gets explicitly reviewed conservative compatibility exceptions.</summary>
    public IReadOnlyList<MessageContractReviewException> ReviewedExceptions { get; init; } = [];
}

/// <summary>Deterministic governance artifact for one immutable wire identity.</summary>
public sealed class MessageContractArtifact
{
    /// <summary>Gets the stable logical runtime message type.</summary>
    public required string MessageType { get; init; }

    /// <summary>Gets the positive runtime message version.</summary>
    public required int MessageVersion { get; init; }

    /// <summary>Gets the pinned JSON Schema draft URI.</summary>
    public required string SchemaDraft { get; init; }

    /// <summary>Gets the repository/output-root-relative canonical schema path.</summary>
    public required string SchemaPath { get; init; }

    /// <summary>Gets the canonical wire schema fingerprint.</summary>
    public required MessageContractFingerprint WireSchemaFingerprint { get; init; }

    /// <summary>Gets the bounded owning team/component.</summary>
    public required string Owner { get; init; }

    /// <summary>Gets the configured structural compatibility mode.</summary>
    public required MessageContractCompatibilityMode CompatibilityMode { get; init; }

    /// <summary>Gets whether the version is deprecated.</summary>
    public bool Deprecated { get; init; }

    /// <summary>Gets the version at which deprecation became effective.</summary>
    public int? DeprecatedSince { get; init; }

    /// <summary>Gets an optional replacement version.</summary>
    public int? ReplacementVersion { get; init; }

    /// <summary>Gets the reviewed change summary.</summary>
    public required string ChangeSummary { get; init; }

    /// <summary>Gets optional migration guidance.</summary>
    public string? MigrationNotes { get; init; }

    /// <summary>Gets optional semantic-compatibility review notes.</summary>
    public string? SemanticCompatibilityNotes { get; init; }

    /// <summary>Gets data classifications.</summary>
    public IReadOnlyList<MessageContractDataClassification> DataClassifications { get; init; } = [];

    /// <summary>Gets reviewed exceptions for conservative findings.</summary>
    public IReadOnlyList<MessageContractReviewException> ReviewedExceptions { get; init; } = [];

    /// <summary>Gets relative example payload paths.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];
}

/// <summary>Deterministic manifest describing a set of governed message contracts.</summary>
public sealed class MessageContractManifest
{
    /// <summary>Gets the manifest schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Gets the canonicalization algorithm version.</summary>
    public required int CanonicalizationVersion { get; init; }

    /// <summary>Gets the pinned JSON Schema draft URI.</summary>
    public required string SchemaDraft { get; init; }

    /// <summary>Gets the fingerprint algorithm.</summary>
    public required string FingerprintAlgorithm { get; init; }

    /// <summary>Gets optional immutable release-baseline provenance.</summary>
    public MessageContractBaselineProvenance? BaselineProvenance { get; init; }

    /// <summary>Gets deterministic contract entries.</summary>
    public IReadOnlyList<MessageContractArtifact> Contracts { get; init; } = [];
}

/// <summary>Immutable provenance for a published contract baseline.</summary>
public sealed class MessageContractBaselineProvenance
{
    /// <summary>Gets the release/package version that produced the baseline.</summary>
    public required string ReleaseVersion { get; init; }

    /// <summary>Gets the immutable source tag.</summary>
    public required string SourceTag { get; init; }

    /// <summary>Gets the full source commit SHA.</summary>
    public required string SourceCommit { get; init; }
}
