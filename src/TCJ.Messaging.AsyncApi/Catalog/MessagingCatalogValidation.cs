using System.Text.RegularExpressions;

namespace TCJ.Messaging.AsyncApi;

public static class MessagingCatalogValidationCodes
{
    public const string UnsupportedSchemaVersion = "CAT001";
    public const string RequiredValue = "CAT002";
    public const string InvalidIdentifier = "CAT003";
    public const string DuplicateIdentifier = "CAT004";
    public const string UnknownReference = "CAT005";
    public const string InvalidRelationship = "CAT006";
    public const string ProhibitedSecretMetadata = "CAT007";
    public const string InvalidDynamicDestination = "CAT008";
    public const string BoundExceeded = "CAT009";
    public const string InvalidMessageVersion = "CAT010";
}

public sealed record MessagingCatalogValidationError(string Code, string Path, string Message);

public sealed class MessagingCatalogValidationResult
{
    internal MessagingCatalogValidationResult(IReadOnlyList<MessagingCatalogValidationError> errors) => Errors = errors;
    public IReadOnlyList<MessagingCatalogValidationError> Errors { get; }
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Deterministic bounded validation for application-supplied messaging catalogs.</summary>
public static partial class MessagingCatalogValidator
{
    public const int MaxIdentifierLength = 128;
    public const int MaxStringLength = 2048;
    public const int MaxEntityCount = 512;
    public const int MaxRelationshipCount = 2048;
    public const int MaxMetadataCount = 64;

    private static readonly string[] ForbiddenSecretMarkers =
    [
        "password=", "password:", "client_secret", "clientsecret", "access_token", "refreshtoken",
        "refresh_token", "sas token", "sharedaccesssignature", "access key", "accesskey=", "private key",
        "begin private key", "connectionstring=", "connection string=", "accountkey=", "sharedaccesskey=",
        "server=", "data source=", "initial catalog=", "endpoint=sb://", "bootstrap.servers=", "defaultendpointsprotocol="
    ];

    public static MessagingCatalogValidationResult Validate(MessagingCatalog? catalog)
    {
        var errors = new List<MessagingCatalogValidationError>();
        if (catalog is null)
        {
            errors.Add(new(MessagingCatalogValidationCodes.RequiredValue, "$", "Catalog is required."));
            return new(errors);
        }

        if (catalog.SchemaVersion != MessagingCatalog.CurrentSchemaVersion)
            errors.Add(new(MessagingCatalogValidationCodes.UnsupportedSchemaVersion, "$.schemaVersion", $"Supported schema version is {MessagingCatalog.CurrentSchemaVersion}."));

        ValidateApplication(catalog.Application, errors);
        ValidateDocument(catalog.Document, errors);
        ValidateCount(catalog.SecuritySchemes, "$.securitySchemes", MaxEntityCount, errors);
        ValidateCount(catalog.Transports, "$.transports", MaxEntityCount, errors);
        ValidateCount(catalog.Producers, "$.producers", MaxEntityCount, errors);
        ValidateCount(catalog.Consumers, "$.consumers", MaxEntityCount, errors);
        ValidateCount(catalog.Channels, "$.channels", MaxEntityCount, errors);
        ValidateCount(catalog.Relationships, "$.relationships", MaxRelationshipCount, errors);

        var securities = BuildIdSet(catalog.SecuritySchemes, x => x.Id, "$.securitySchemes", errors);
        var transports = BuildIdSet(catalog.Transports, x => x.Id, "$.transports", errors);
        var producers = BuildIdSet(catalog.Producers, x => x.Id, "$.producers", errors);
        var consumers = BuildIdSet(catalog.Consumers, x => x.Id, "$.consumers", errors);
        var channels = BuildIdSet(catalog.Channels, x => x.Id, "$.channels", errors);
        var relationships = BuildIdSet(catalog.Relationships, x => x.Id, "$.relationships", errors);
        _ = relationships;

        for (var i = 0; i < catalog.SecuritySchemes.Count; i++)
        {
            var item = catalog.SecuritySchemes[i];
            ValidateText(item.Description, $"$.securitySchemes[{i}].description", errors);
        }

        for (var i = 0; i < catalog.Transports.Count; i++)
        {
            var item = catalog.Transports[i];
            var path = $"$.transports[{i}]";
            ValidateRequiredText(item.Kind, path + ".kind", errors);
            ValidateRequiredText(item.Protocol, path + ".protocol", errors);
            ValidateText(item.Description, path + ".description", errors);
            ValidateText(item.Owner, path + ".owner", errors);
            ValidateCount(item.CapabilityIds, path + ".capabilityIds", MaxMetadataCount, errors);
            foreach (var capability in item.CapabilityIds)
                ValidateRequiredText(capability, path + ".capabilityIds", errors, MaxIdentifierLength);
            ValidateReference(item.SecuritySchemeId, securities, path + ".securitySchemeId", "security scheme", errors, optional: true);
        }

        for (var i = 0; i < catalog.Channels.Count; i++)
        {
            var item = catalog.Channels[i];
            var path = $"$.channels[{i}]";
            ValidateRequiredText(item.Address, path + ".address", errors);
            ValidateReference(item.TransportId, transports, path + ".transportId", "transport", errors);
            ValidateText(item.Description, path + ".description", errors);
            ValidateText(item.Owner, path + ".owner", errors);
            ValidateCount(item.Messages, path + ".messages", MaxMetadataCount, errors);
            ValidateCount(item.DataClassifications, path + ".dataClassifications", MaxMetadataCount, errors);
            foreach (var message in item.Messages) ValidateMessage(message, path + ".messages", errors);
            ValidateDynamic(item.DynamicDestination, path + ".dynamicDestination", errors);
        }

        for (var i = 0; i < catalog.Producers.Count; i++)
        {
            var item = catalog.Producers[i];
            var path = $"$.producers[{i}]";
            ValidateRequiredText(item.Component, path + ".component", errors);
            ValidateMessage(item.Message, path + ".message", errors);
            ValidateReference(item.TransportId, transports, path + ".transportId", "transport", errors);
            ValidateReference(item.ChannelId, channels, path + ".channelId", "channel", errors);
            ValidateText(item.PartitionKeyStrategy, path + ".partitionKeyStrategy", errors, MaxIdentifierLength);
            ValidateText(item.OrderingKeyStrategy, path + ".orderingKeyStrategy", errors, MaxIdentifierLength);
            ValidateText(item.Owner, path + ".owner", errors);
            ValidateText(item.Outbox?.DeduplicationModel, path + ".outbox.deduplicationModel", errors);
            ValidateDynamic(item.DynamicDestination, path + ".dynamicDestination", errors);
        }

        for (var i = 0; i < catalog.Consumers.Count; i++)
        {
            var item = catalog.Consumers[i];
            var path = $"$.consumers[{i}]";
            ValidateRequiredText(item.Component, path + ".component", errors);
            ValidateRequiredText(item.MessageType, path + ".messageType", errors);
            ValidateCount(item.AcceptedMessageVersions, path + ".acceptedMessageVersions", MaxMetadataCount, errors);
            if (item.AcceptedMessageVersions.Count == 0 || item.AcceptedMessageVersions.Any(x => x <= 0))
                errors.Add(new(MessagingCatalogValidationCodes.InvalidMessageVersion, path + ".acceptedMessageVersions", "At least one positive accepted message version is required."));
            ValidateReference(item.TransportId, transports, path + ".transportId", "transport", errors);
            ValidateReference(item.ChannelId, channels, path + ".channelId", "channel", errors);
            ValidateText(item.SubscriptionOrGroup, path + ".subscriptionOrGroup", errors, MaxIdentifierLength);
            ValidateText(item.Inbox?.DeduplicationModel, path + ".inbox.deduplicationModel", errors);
            ValidateText(item.Owner, path + ".owner", errors);
        }

        for (var i = 0; i < catalog.Relationships.Count; i++)
        {
            var item = catalog.Relationships[i];
            var path = $"$.relationships[{i}]";
            ValidateEntityReference(item.Source, path + ".source", catalog, securities, transports, producers, consumers, channels, errors);
            ValidateEntityReference(item.Target, path + ".target", catalog, securities, transports, producers, consumers, channels, errors);
            if (item.Saga is not null)
            {
                ValidateRequiredText(item.Saga.DefinitionId, path + ".saga.definitionId", errors, MaxIdentifierLength);
                if (item.Saga.DefinitionVersion <= 0)
                    errors.Add(new(MessagingCatalogValidationCodes.InvalidRelationship, path + ".saga.definitionVersion", "Saga definition version must be positive."));
                if (item.Kind is not (MessagingRelationshipKind.StartsSaga or MessagingRelationshipKind.ContinuesSaga or MessagingRelationshipKind.CompensatesSaga))
                    errors.Add(new(MessagingCatalogValidationCodes.InvalidRelationship, path + ".saga", "Saga metadata is allowed only for Saga relationship kinds."));
            }
        }

        return new(errors);
    }

    public static string NormalizeIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return HyphenRuns().Replace(NonIdentifierCharacters().Replace(identifier.Trim().ToLowerInvariant(), "-"), "-").Trim('-');
    }

    private static void ValidateApplication(MessagingApplication? application, List<MessagingCatalogValidationError> errors)
    {
        if (application is null) { errors.Add(new(MessagingCatalogValidationCodes.RequiredValue, "$.application", "Application identity is required.")); return; }
        ValidateIdentifier(application.Name, "$.application.name", errors);
        ValidateRequiredText(application.Version, "$.application.version", errors, MaxIdentifierLength);
        ValidateText(application.Description, "$.application.description", errors);
        ValidateText(application.Owner, "$.application.owner", errors);
    }

    private static void ValidateDocument(MessagingDocumentInfo? document, List<MessagingCatalogValidationError> errors)
    {
        if (document is null) { errors.Add(new(MessagingCatalogValidationCodes.RequiredValue, "$.document", "Document identity is required.")); return; }
        ValidateRequiredText(document.Title, "$.document.title", errors);
        ValidateRequiredText(document.Version, "$.document.version", errors, MaxIdentifierLength);
        ValidateText(document.Description, "$.document.description", errors);
        ValidateText(document.Contact?.Name, "$.document.contact.name", errors);
        ValidateText(document.Contact?.Url, "$.document.contact.url", errors);
        ValidateText(document.License?.Name, "$.document.license.name", errors);
        ValidateText(document.License?.Url, "$.document.license.url", errors);
        ValidateText(document.ExternalDocumentation?.Url, "$.document.externalDocumentation.url", errors);
        ValidateText(document.ExternalDocumentation?.Description, "$.document.externalDocumentation.description", errors);
    }

    private static HashSet<string> BuildIdSet<T>(IReadOnlyList<T> items, Func<T, string> selector, string path, List<MessagingCatalogValidationError> errors)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var id = selector(items[i]);
            var itemPath = $"{path}[{i}].id";
            if (!ValidateIdentifier(id, itemPath, errors)) continue;
            var normalized = NormalizeIdentifier(id);
            if (!result.Add(normalized))
                errors.Add(new(MessagingCatalogValidationCodes.DuplicateIdentifier, itemPath, "Identifier collides with another identifier after normalization."));
        }
        return result;
    }

    private static bool ValidateIdentifier(string? value, string path, List<MessagingCatalogValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdentifierLength || !StableIdentifier().IsMatch(value))
        {
            errors.Add(new(MessagingCatalogValidationCodes.InvalidIdentifier, path, "Identifier must use 1-128 ASCII letters, digits, '.', '_' or '-' and start/end with a letter or digit."));
            return false;
        }
        return true;
    }

    private static void ValidateReference(string? id, HashSet<string> ids, string path, string kind, List<MessagingCatalogValidationError> errors, bool optional = false)
    {
        if (optional && string.IsNullOrWhiteSpace(id)) return;
        if (string.IsNullOrWhiteSpace(id) || !ids.Contains(NormalizeIdentifier(id)))
            errors.Add(new(MessagingCatalogValidationCodes.UnknownReference, path, $"Referenced {kind} is not declared."));
    }

    private static void ValidateEntityReference(MessagingEntityReference? reference, string path, MessagingCatalog catalog, HashSet<string> securities, HashSet<string> transports, HashSet<string> producers, HashSet<string> consumers, HashSet<string> channels, List<MessagingCatalogValidationError> errors)
    {
        if (reference is null) { errors.Add(new(MessagingCatalogValidationCodes.InvalidRelationship, path, "Relationship endpoint is required.")); return; }
        var valid = reference.Kind switch
        {
            MessagingEntityKind.Application => !string.IsNullOrWhiteSpace(reference.Id) && !string.IsNullOrWhiteSpace(catalog.Application?.Name)
                && string.Equals(NormalizeIdentifier(reference.Id), NormalizeIdentifier(catalog.Application.Name), StringComparison.Ordinal),
            MessagingEntityKind.Transport => Contains(transports, reference.Id),
            MessagingEntityKind.Producer => Contains(producers, reference.Id),
            MessagingEntityKind.Consumer => Contains(consumers, reference.Id),
            MessagingEntityKind.Channel => Contains(channels, reference.Id),
            MessagingEntityKind.SecurityScheme => Contains(securities, reference.Id),
            _ => false
        };
        if (!valid) errors.Add(new(MessagingCatalogValidationCodes.InvalidRelationship, path, "Relationship endpoint does not reference a declared catalog entity."));
    }

    private static bool Contains(HashSet<string> ids, string? id) => !string.IsNullOrWhiteSpace(id) && ids.Contains(NormalizeIdentifier(id));

    private static void ValidateDynamic(MessagingDynamicDestination? dynamicDestination, string path, List<MessagingCatalogValidationError> errors)
    {
        if (dynamicDestination is null) return;
        if (string.IsNullOrWhiteSpace(dynamicDestination.NamingStrategyId) || string.IsNullOrWhiteSpace(dynamicDestination.Pattern))
            errors.Add(new(MessagingCatalogValidationCodes.InvalidDynamicDestination, path, "Dynamic destination requires namingStrategyId and pattern."));
        ValidateText(dynamicDestination.NamingStrategyId, path + ".namingStrategyId", errors, MaxIdentifierLength);
        ValidateText(dynamicDestination.Pattern, path + ".pattern", errors);
        ValidateText(dynamicDestination.Description, path + ".description", errors);
    }

    private static void ValidateMessage(MessagingMessageReference? message, string path, List<MessagingCatalogValidationError> errors)
    {
        if (message is null) { errors.Add(new(MessagingCatalogValidationCodes.RequiredValue, path, "Message identity is required.")); return; }
        ValidateRequiredText(message.Type, path + ".type", errors, MaxIdentifierLength);
        if (message.Version <= 0) errors.Add(new(MessagingCatalogValidationCodes.InvalidMessageVersion, path + ".version", "Message version must be positive."));
    }

    private static void ValidateRequiredText(string? value, string path, List<MessagingCatalogValidationError> errors, int maxLength = MaxStringLength)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add(new(MessagingCatalogValidationCodes.RequiredValue, path, "Value is required."));
        else ValidateText(value, path, errors, maxLength);
    }

    private static void ValidateText(string? value, string path, List<MessagingCatalogValidationError> errors, int maxLength = MaxStringLength)
    {
        if (value is null) return;
        if (value.Length > maxLength) errors.Add(new(MessagingCatalogValidationCodes.BoundExceeded, path, $"Value exceeds the maximum length of {maxLength}."));
        var lowered = value.ToLowerInvariant();
        if (ForbiddenSecretMarkers.Any(lowered.Contains))
            errors.Add(new(MessagingCatalogValidationCodes.ProhibitedSecretMetadata, path, "Supplied metadata contains prohibited credential material."));
    }

    private static void ValidateCount<T>(IReadOnlyList<T> items, string path, int max, List<MessagingCatalogValidationError> errors)
    {
        if (items.Count > max) errors.Add(new(MessagingCatalogValidationCodes.BoundExceeded, path, $"Collection exceeds the maximum count of {max}."));
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,126}[A-Za-z0-9])?$")]
    private static partial Regex StableIdentifier();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonIdentifierCharacters();

    [GeneratedRegex("-+")]
    private static partial Regex HyphenRuns();
}
