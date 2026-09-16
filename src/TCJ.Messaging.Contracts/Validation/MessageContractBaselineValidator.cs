namespace TCJ.Messaging.Contracts;

/// <summary>Result of published-version immutability and contract-family history validation.</summary>
public sealed class MessageContractBaselineValidationResult
{
    /// <summary>Gets whether the current manifest preserves every released wire identity and fingerprint.</summary>
    public required bool IsValid { get; init; }

    /// <summary>Gets deterministic validation errors.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>Validates immutable release-derived baselines without allowing historical wire identities to be rewritten or deleted.</summary>
public sealed class MessageContractBaselineValidator
{
    /// <summary>Compares the current candidate manifest against an immutable published baseline.</summary>
    /// <param name="publishedBaseline">Release-derived published baseline with provenance.</param>
    /// <param name="current">Current generated manifest.</param>
    /// <returns>Validation result.</returns>
    public MessageContractBaselineValidationResult Validate(MessageContractManifest publishedBaseline, MessageContractManifest current)
    {
        ArgumentNullException.ThrowIfNull(publishedBaseline);
        ArgumentNullException.ThrowIfNull(current);
        var errors = new List<string>();

        if (publishedBaseline.BaselineProvenance is null)
            errors.Add("Published baseline must record releaseVersion, sourceTag, and sourceCommit provenance.");
        else
            ValidateProvenance(publishedBaseline.BaselineProvenance, errors);

        if (publishedBaseline.SchemaVersion != current.SchemaVersion ||
            publishedBaseline.CanonicalizationVersion != current.CanonicalizationVersion ||
            !string.Equals(publishedBaseline.SchemaDraft, current.SchemaDraft, StringComparison.Ordinal) ||
            !string.Equals(publishedBaseline.FingerprintAlgorithm, current.FingerprintAlgorithm, StringComparison.Ordinal))
            errors.Add("Current manifest governance format does not match the published baseline format.");

        int publishedIdentityCount = publishedBaseline.Contracts
            .Select(static item => (item.MessageType, item.MessageVersion))
            .Distinct()
            .Count();
        if (publishedIdentityCount != publishedBaseline.Contracts.Count)
            errors.Add("Published baseline contains duplicate wire identities and is malformed.");

        Dictionary<(string Type, int Version), MessageContractArtifact> currentByIdentity = current.Contracts
            .GroupBy(static item => (item.MessageType, item.MessageVersion))
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single());
        if (currentByIdentity.Count != current.Contracts.Count)
            errors.Add("Current manifest contains duplicate wire identities.");

        foreach (MessageContractArtifact published in publishedBaseline.Contracts)
        {
            var identity = (published.MessageType, published.MessageVersion);
            if (!currentByIdentity.TryGetValue(identity, out MessageContractArtifact? candidate))
            {
                errors.Add($"Released contract '{published.MessageType}' v{published.MessageVersion} was deleted.");
                continue;
            }
            if (!string.Equals(published.WireSchemaFingerprint.Algorithm, candidate.WireSchemaFingerprint.Algorithm, StringComparison.Ordinal) ||
                !string.Equals(published.WireSchemaFingerprint.Value, candidate.WireSchemaFingerprint.Value, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Released contract '{published.MessageType}' v{published.MessageVersion} changed its immutable wire-schema fingerprint; increment MessageVersion instead.");
        }

        foreach (IGrouping<string, MessageContractArtifact> family in current.Contracts.GroupBy(static item => item.MessageType, StringComparer.Ordinal))
        {
            int[] versions = family.Select(static item => item.MessageVersion).Order().ToArray();
            if (versions.Any(static version => version <= 0) || versions.Distinct().Count() != versions.Length)
                errors.Add($"Contract family '{family.Key}' contains invalid or duplicate versions.");
            foreach (MessageContractArtifact artifact in family)
            {
                if (artifact.DeprecatedSince is int deprecatedSince && deprecatedSince < artifact.MessageVersion)
                    errors.Add($"Contract '{artifact.MessageType}' v{artifact.MessageVersion} has deprecatedSince below its own version.");
                if (artifact.ReplacementVersion is int replacement && replacement <= artifact.MessageVersion)
                    errors.Add($"Contract '{artifact.MessageType}' v{artifact.MessageVersion} replacementVersion must be newer.");
            }
        }

        return new MessageContractBaselineValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        };
    }

    private static void ValidateProvenance(MessageContractBaselineProvenance provenance, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(provenance.ReleaseVersion) || provenance.ReleaseVersion.Length > 128 ||
            string.IsNullOrWhiteSpace(provenance.SourceTag) || provenance.SourceTag.Length > 128 ||
            provenance.SourceCommit.Length != 40 || provenance.SourceCommit.Any(static c => !Uri.IsHexDigit(c)))
            errors.Add("Published baseline provenance is malformed or not commit-addressable.");
    }
}
