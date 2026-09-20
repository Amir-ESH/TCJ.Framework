using System.Text.Json;

namespace TCJ.Messaging.AsyncApi;

/// <summary>Governed TCJ-specific AsyncAPI extension contract.</summary>
public static class TcjAsyncApiExtensions
{
    /// <summary>Initial compatibility-sensitive TCJ extension schema version.</summary>
    public const string Version = "1.0";

    public const string VersionName = "x-tcj-extension-version";
    public const string ContractId = "x-tcj-contract-id";
    public const string ContractVersion = "x-tcj-contract-version";
    public const string SchemaFingerprint = "x-tcj-schema-fingerprint";
    public const string Owner = "x-tcj-owner";
    public const string Lifecycle = "x-tcj-lifecycle";
    public const string Compatibility = "x-tcj-compatibility";
    public const string Outbox = "x-tcj-outbox";
    public const string Inbox = "x-tcj-inbox";
    public const string Saga = "x-tcj-saga";
    public const string DeliverySemantics = "x-tcj-delivery-semantics";
    public const string OrderingScope = "x-tcj-ordering-scope";
    public const string RetryOwner = "x-tcj-retry-owner";
    public const string DeadLetter = "x-tcj-dead-letter";
    public const string UpcasterPath = "x-tcj-upcaster-path";
    public const string DataClassification = "x-tcj-data-classification";
    public const string DynamicDestination = "x-tcj-dynamic-destination";

    internal static readonly HashSet<string> ApprovedNames = new(StringComparer.Ordinal)
    {
        VersionName, ContractId, ContractVersion, SchemaFingerprint, Owner, Lifecycle, Compatibility,
        Outbox, Inbox, Saga, DeliverySemantics, OrderingScope, RetryOwner, DeadLetter, UpcasterPath,
        DataClassification, DynamicDestination
    };
}

/// <summary>Stable extension validation diagnostics.</summary>
public static class TcjAsyncApiExtensionValidationCodes
{
    public const string MissingVersion = "TCJEXT001";
    public const string UnsupportedVersion = "TCJEXT002";
    public const string UnknownExtension = "TCJEXT003";
    public const string InvalidStructure = "TCJEXT004";
    public const string BoundExceeded = "TCJEXT005";
    public const string ProhibitedSecretMetadata = "TCJEXT006";
}

/// <summary>One bounded extension validation error.</summary>
public sealed record TcjAsyncApiExtensionValidationError(string Code, string Path, string Message);

/// <summary>Result of validating one TCJ extension set.</summary>
public sealed class TcjAsyncApiExtensionValidationResult
{
    internal TcjAsyncApiExtensionValidationResult(IReadOnlyList<TcjAsyncApiExtensionValidationError> errors) => Errors = errors;
    public IReadOnlyList<TcjAsyncApiExtensionValidationError> Errors { get; }
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Fail-closed validator for governed TCJ AsyncAPI extension sets.</summary>
public static class TcjAsyncApiExtensionValidator
{
    private const int MaxStringLength = 2048;
    private const int MaxIdentifierLength = 128;
    private const int MaxCollectionCount = 64;

    private static readonly HashSet<string> DeliveryValues = new(StringComparer.Ordinal) { "AtLeastOnce", "AtMostOnce", "BestEffort", "TransportSpecific" };
    private static readonly HashSet<string> OrderingValues = new(StringComparer.Ordinal) { "None", "BestEffort", "PerPartition", "PerSession" };
    private static readonly HashSet<string> DeadLetterValues = new(StringComparer.Ordinal) { "Native", "ConventionBased", "Unsupported", "TransportSpecific" };
    private static readonly HashSet<string> RetryValues = new(StringComparer.Ordinal) { "TransportClient", "Outbox", "Broker", "Inbox", "HandlerResilience", "Saga", "Application", "TransportSpecific" };
    private static readonly HashSet<string> LifecycleValues = new(StringComparer.Ordinal) { "Draft", "Published", "Deprecated", "Retired" };
    private static readonly HashSet<string> ClassificationValues = new(StringComparer.Ordinal) { "Public", "Internal", "Personal", "Confidential", "SecretProhibited" };
    private static readonly HashSet<string> SagaValues = new(StringComparer.Ordinal) { "Start", "Continue", "Timeout", "Compensation", "Completion", "Failure" };
    private static readonly string[] ForbiddenSecretMarkers =
    [
        "password", "clientsecret", "client_secret", "accesstoken", "access_token", "refreshtoken", "refresh_token",
        "sastoken", "sharedaccesssignature", "accesskey", "privatekey", "private key", "connectionstring", "connection string",
        "sharedaccesskey", "accountkey", "begin private key"
    ];

    public static TcjAsyncApiExtensionValidationResult Validate(JsonElement extensionSet)
    {
        var errors = new List<TcjAsyncApiExtensionValidationError>();
        if (extensionSet.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new(TcjAsyncApiExtensionValidationCodes.InvalidStructure, "$", "TCJ extension set must be a JSON object."));
            return new(errors);
        }

        var properties = extensionSet.EnumerateObject().Where(static property => property.Name.StartsWith("x-tcj-", StringComparison.Ordinal)).ToArray();
        if (properties.Length == 0)
            return new(errors);

        foreach (JsonProperty property in properties)
        {
            if (!TcjAsyncApiExtensions.ApprovedNames.Contains(property.Name))
                errors.Add(new(TcjAsyncApiExtensionValidationCodes.UnknownExtension, "$.'" + property.Name + "'", "TCJ extension name is not governed."));
        }

        if (!extensionSet.TryGetProperty(TcjAsyncApiExtensions.VersionName, out JsonElement version))
            errors.Add(new(TcjAsyncApiExtensionValidationCodes.MissingVersion, "$.'" + TcjAsyncApiExtensions.VersionName + "'", "TCJ extension version is required whenever TCJ extensions are present."));
        else if (version.ValueKind != JsonValueKind.String || !string.Equals(version.GetString(), TcjAsyncApiExtensions.Version, StringComparison.Ordinal))
            errors.Add(new(TcjAsyncApiExtensionValidationCodes.UnsupportedVersion, "$.'" + TcjAsyncApiExtensions.VersionName + "'", "TCJ extension version is unsupported."));

        ValidateString(extensionSet, TcjAsyncApiExtensions.ContractId, MaxIdentifierLength, errors);
        ValidatePositiveInteger(extensionSet, TcjAsyncApiExtensions.ContractVersion, errors);
        ValidateString(extensionSet, TcjAsyncApiExtensions.Owner, MaxIdentifierLength, errors);
        ValidateEnum(extensionSet, TcjAsyncApiExtensions.Lifecycle, LifecycleValues, errors);
        ValidateEnum(extensionSet, TcjAsyncApiExtensions.DeliverySemantics, DeliveryValues, errors);
        ValidateEnum(extensionSet, TcjAsyncApiExtensions.RetryOwner, RetryValues, errors);
        ValidateEnum(extensionSet, TcjAsyncApiExtensions.DeadLetter, DeadLetterValues, errors);
        ValidateArrayOfEnums(extensionSet, TcjAsyncApiExtensions.DataClassification, ClassificationValues, errors);

        if (extensionSet.TryGetProperty(TcjAsyncApiExtensions.SchemaFingerprint, out JsonElement fingerprint))
            ValidateFingerprint(fingerprint, errors);
        if (extensionSet.TryGetProperty(TcjAsyncApiExtensions.OrderingScope, out JsonElement ordering))
            ValidateOrdering(ordering, errors);
        if (extensionSet.TryGetProperty(TcjAsyncApiExtensions.Outbox, out JsonElement outbox))
            ValidateInboxOutbox(outbox, TcjAsyncApiExtensions.Outbox, isOutbox: true, errors);
        if (extensionSet.TryGetProperty(TcjAsyncApiExtensions.Inbox, out JsonElement inbox))
            ValidateInboxOutbox(inbox, TcjAsyncApiExtensions.Inbox, isOutbox: false, errors);
        if (extensionSet.TryGetProperty(TcjAsyncApiExtensions.DynamicDestination, out JsonElement dynamicDestination))
            ValidateDynamicDestination(dynamicDestination, errors);
        if (extensionSet.TryGetProperty(TcjAsyncApiExtensions.Saga, out JsonElement saga))
            ValidateSaga(saga, errors);
        if (extensionSet.TryGetProperty(TcjAsyncApiExtensions.Compatibility, out JsonElement compatibility) && compatibility.ValueKind != JsonValueKind.Object)
            AddInvalid(TcjAsyncApiExtensions.Compatibility, errors);
        if (extensionSet.TryGetProperty(TcjAsyncApiExtensions.UpcasterPath, out JsonElement upcasterPath))
            ValidateUpcasterPath(upcasterPath, errors);

        ValidateSecretSafety(extensionSet, "$", errors);
        return new(errors.OrderBy(static e => e.Path, StringComparer.Ordinal).ThenBy(static e => e.Code, StringComparer.Ordinal).ToArray());
    }

    private static void ValidateFingerprint(JsonElement value, List<TcjAsyncApiExtensionValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object || !TryBoundedString(value, "algorithm", MaxIdentifierLength) || !TryBoundedString(value, "value", MaxIdentifierLength))
            AddInvalid(TcjAsyncApiExtensions.SchemaFingerprint, errors);
    }

    private static void ValidateOrdering(JsonElement value, List<TcjAsyncApiExtensionValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object || value.GetRawText() == "{}")
        {
            AddInvalid(TcjAsyncApiExtensions.OrderingScope, errors);
            return;
        }
        if (value.TryGetProperty("scope", out JsonElement scope) && (scope.ValueKind != JsonValueKind.String || !OrderingValues.Contains(scope.GetString()!)))
            AddInvalid(TcjAsyncApiExtensions.OrderingScope, errors);
        if (value.TryGetProperty("partitioning", out JsonElement partitioning) &&
            (partitioning.ValueKind != JsonValueKind.String || partitioning.GetString() is not ("None" or "Supported" or "Required" or "TransportSpecific")))
            AddInvalid(TcjAsyncApiExtensions.OrderingScope, errors);
        foreach (string name in new[] { "partitionKeyStrategy", "orderingKeyStrategy" })
            if (value.TryGetProperty(name, out JsonElement property) && (property.ValueKind != JsonValueKind.String || property.GetString()!.Length > MaxIdentifierLength))
                AddInvalid(TcjAsyncApiExtensions.OrderingScope, errors);
        string[] allowed = ["scope", "partitioning", "partitionKeyStrategy", "orderingKeyStrategy"];
        if (value.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)))
            AddInvalid(TcjAsyncApiExtensions.OrderingScope, errors);
    }

    private static void ValidateInboxOutbox(JsonElement value, string extensionName, bool isOutbox, List<TcjAsyncApiExtensionValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("enabled", out JsonElement enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            AddInvalid(extensionName, errors);
            return;
        }
        if (value.TryGetProperty("deduplicationIdentity", out JsonElement dedup) && (dedup.ValueKind != JsonValueKind.String || dedup.GetString()!.Length > MaxIdentifierLength))
            AddInvalid(extensionName, errors);
        if (value.TryGetProperty("transactionalBoundary", out JsonElement boundary) && (boundary.ValueKind != JsonValueKind.String || boundary.GetString()!.Length > MaxIdentifierLength))
            AddInvalid(extensionName, errors);
        if (value.TryGetProperty("retryOwner", out JsonElement retry) && (retry.ValueKind != JsonValueKind.String || !RetryValues.Contains(retry.GetString()!)))
            AddInvalid(extensionName, errors);
        if (isOutbox && value.TryGetProperty("publicationModel", out JsonElement publication) && (publication.ValueKind != JsonValueKind.String || publication.GetString()!.Length > MaxIdentifierLength))
            AddInvalid(extensionName, errors);
    }

    private static void ValidateDynamicDestination(JsonElement value, List<TcjAsyncApiExtensionValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("dynamic", out JsonElement dynamicValue) || dynamicValue.ValueKind != JsonValueKind.True ||
            !TryBoundedString(value, "namingStrategyId", MaxIdentifierLength) || !TryBoundedString(value, "pattern", MaxStringLength))
            AddInvalid(TcjAsyncApiExtensions.DynamicDestination, errors);
        if (value.TryGetProperty("description", out JsonElement description) && (description.ValueKind != JsonValueKind.String || description.GetString()!.Length > MaxStringLength))
            AddInvalid(TcjAsyncApiExtensions.DynamicDestination, errors);
    }

    private static void ValidateSaga(JsonElement value, List<TcjAsyncApiExtensionValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxCollectionCount)
        {
            AddInvalid(TcjAsyncApiExtensions.Saga, errors);
            return;
        }
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryBoundedString(item, "definitionId", MaxIdentifierLength) ||
                !item.TryGetProperty("definitionVersion", out JsonElement definitionVersion) || !definitionVersion.TryGetInt32(out int version) || version <= 0 ||
                !item.TryGetProperty("relationship", out JsonElement relationship) || relationship.ValueKind != JsonValueKind.String || !SagaValues.Contains(relationship.GetString()!))
                AddInvalid(TcjAsyncApiExtensions.Saga, errors);
        }
    }

    private static void ValidateUpcasterPath(JsonElement value, List<TcjAsyncApiExtensionValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxCollectionCount)
            AddInvalid(TcjAsyncApiExtensions.UpcasterPath, errors);
        else
            foreach (JsonElement item in value.EnumerateArray())
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int version) || version <= 0)
                    AddInvalid(TcjAsyncApiExtensions.UpcasterPath, errors);
    }

    private static void ValidateString(JsonElement set, string name, int maxLength, List<TcjAsyncApiExtensionValidationError> errors)
    {
        if (set.TryGetProperty(name, out JsonElement value) && (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maxLength))
            AddInvalid(name, errors);
    }

    private static void ValidatePositiveInteger(JsonElement set, string name, List<TcjAsyncApiExtensionValidationError> errors)
    {
        if (set.TryGetProperty(name, out JsonElement value) && (!value.TryGetInt32(out int integer) || integer <= 0))
            AddInvalid(name, errors);
    }

    private static void ValidateEnum(JsonElement set, string name, HashSet<string> values, List<TcjAsyncApiExtensionValidationError> errors)
    {
        if (set.TryGetProperty(name, out JsonElement value) && (value.ValueKind != JsonValueKind.String || !values.Contains(value.GetString()!)))
            AddInvalid(name, errors);
    }

    private static void ValidateArrayOfEnums(JsonElement set, string name, HashSet<string> values, List<TcjAsyncApiExtensionValidationError> errors)
    {
        if (!set.TryGetProperty(name, out JsonElement value)) return;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxCollectionCount || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || !values.Contains(item.GetString()!)))
            AddInvalid(name, errors);
    }

    private static bool TryBoundedString(JsonElement value, string propertyName, int maxLength) =>
        value.TryGetProperty(propertyName, out JsonElement property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString()) && property.GetString()!.Length <= maxLength;

    private static void ValidateSecretSafety(JsonElement value, string path, List<TcjAsyncApiExtensionValidationError> errors)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    string normalizedName = property.Name.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
                    if (ForbiddenSecretMarkers.Any(marker => normalizedName.Contains(marker.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal)))
                    {
                        errors.Add(new(TcjAsyncApiExtensionValidationCodes.ProhibitedSecretMetadata, path + "." + property.Name, "Extension metadata contains a prohibited secret-bearing field."));
                        continue;
                    }
                    ValidateSecretSafety(property.Value, path + "." + property.Name, errors);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (JsonElement item in value.EnumerateArray()) ValidateSecretSafety(item, path + "[" + index++ + "]", errors);
                break;
            case JsonValueKind.String:
                string normalizedValue = value.GetString()!.ToLowerInvariant().Replace("_", string.Empty, StringComparison.Ordinal);
                if (ForbiddenSecretMarkers.Any(marker => normalizedValue.Contains(marker.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal)))
                    errors.Add(new(TcjAsyncApiExtensionValidationCodes.ProhibitedSecretMetadata, path, "Extension metadata contains prohibited credential material."));
                break;
        }
    }

    private static void AddInvalid(string name, List<TcjAsyncApiExtensionValidationError> errors) =>
        errors.Add(new(TcjAsyncApiExtensionValidationCodes.InvalidStructure, "$.'" + name + "'", "TCJ extension structure is invalid."));
}
