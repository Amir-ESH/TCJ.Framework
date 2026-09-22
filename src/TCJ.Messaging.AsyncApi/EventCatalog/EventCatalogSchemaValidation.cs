using System.Text.Json;

namespace TCJ.Messaging.AsyncApi;

public static class EventCatalogSchemaValidationCodes
{
    public const string MalformedJson = "ECS001";
    public const string InvalidRoot = "ECS002";
    public const string UnsupportedSchemaVersion = "ECS003";
    public const string MissingIdentity = "ECS004";
    public const string InvalidScope = "ECS005";
    public const string InvalidNode = "ECS006";
    public const string InvalidRelationship = "ECS007";
    public const string BoundExceeded = "ECS008";
    public const string UnknownProperty = "ECS009";
    public const string ProhibitedSecretMetadata = "ECS010";
}

public sealed record EventCatalogSchemaValidationError(string Code, string Path, string Message);

public sealed class EventCatalogSchemaValidationResult
{
    internal EventCatalogSchemaValidationResult(IReadOnlyList<EventCatalogSchemaValidationError> errors) => Errors = errors;
    public IReadOnlyList<EventCatalogSchemaValidationError> Errors { get; }
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Bounded validation of canonical JSON against the governed event-catalog schema family.</summary>
public static class EventCatalogSchemaValidator
{
    private const int MaximumNodes = 2048;
    private const int MaximumEdges = 8192;
    private const int MaximumTextLength = 2048;
    private static readonly HashSet<string> NodeTypes = Enum.GetNames<EventCatalogNodeType>().ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> RelationshipTypes = Enum.GetNames<EventCatalogRelationshipType>().ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> GraphNodeProperties = new(["id", "type"], StringComparer.Ordinal);
    private static readonly HashSet<string> NodeProperties = new(StringComparer.Ordinal)
    {
        "id", "type", "logicalId", "version", "description", "owner", "lifecycle", "component", "messageType", "messageVersion",
        "contentType", "schemaFingerprintAlgorithm", "schemaFingerprint", "schemaRelativePath", "exampleRelativePaths", "compatibilityMode", "dataClassifications", "address", "transportKind",
        "protocol", "deliverySemantics", "orderingSemantics", "partitioningSemantics", "deadLetterSemantics", "inboxEnabled", "outboxEnabled",
        "retryOwner", "subscriptionOrGroup", "dynamicDestination", "dynamicNamingStrategyId", "dynamicPattern", "sagaDefinitionVersion"
    };
    private static readonly string[] SecretMarkers =
    [
        "password=", "password:", "client_secret", "clientsecret", "access_token", "refresh_token", "sharedaccesssignature",
        "accesskey=", "private key", "begin private key", "connectionstring=", "connection string=", "accountkey=", "sharedaccesskey=",
        "endpoint=sb://", "bootstrap.servers="
    ];

    public static EventCatalogSchemaValidationResult ValidateEventCatalog(ReadOnlySpan<byte> utf8Json) => Validate(utf8Json, graph: false);
    public static EventCatalogSchemaValidationResult ValidateRelationshipGraph(ReadOnlySpan<byte> utf8Json) => Validate(utf8Json, graph: true);

    private static EventCatalogSchemaValidationResult Validate(ReadOnlySpan<byte> utf8Json, bool graph)
    {
        var errors = new List<EventCatalogSchemaValidationError>();
        JsonDocument document;
        try { document = JsonDocument.Parse(utf8Json.ToArray()); }
        catch (JsonException)
        {
            errors.Add(new(EventCatalogSchemaValidationCodes.MalformedJson, "$", "JSON is malformed."));
            return new(errors);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new(EventCatalogSchemaValidationCodes.InvalidRoot, "$", "Root must be a JSON object."));
                return new(errors);
            }

            ValidateVersion(root, errors);
            if (graph) ValidateGraph(root, errors); else ValidateCatalog(root, errors);
            ScanStrings(root, "$", errors);
        }
        return new(errors.OrderBy(static x => x.Path, StringComparer.Ordinal).ThenBy(static x => x.Code, StringComparer.Ordinal).ToArray());
    }

    private static void ValidateVersion(JsonElement root, List<EventCatalogSchemaValidationError> errors)
    {
        if (!root.TryGetProperty("schemaVersion", out JsonElement version) || version.ValueKind != JsonValueKind.String || !string.Equals(version.GetString(), EventCatalog.CurrentSchemaVersion, StringComparison.Ordinal))
            errors.Add(new(EventCatalogSchemaValidationCodes.UnsupportedSchemaVersion, "$.schemaVersion", "Schema version must be exactly 1.0."));
    }

    private static void ValidateCatalog(JsonElement root, List<EventCatalogSchemaValidationError> errors)
    {
        ValidateKnownProperties(root, ["schemaVersion", "identity", "scope", "nodes", "relationships"], "$", errors);
        ValidateIdentity(root, "identity", "$.identity", errors);
        if (!root.TryGetProperty("scope", out JsonElement scope) || scope.ValueKind != JsonValueKind.Object)
            errors.Add(new(EventCatalogSchemaValidationCodes.InvalidScope, "$.scope", "Scope metadata is required."));
        else
        {
            ValidateKnownProperties(scope, ["completeness", "runtimeDiscovery", "organizationWide", "runtimeAvailability", "synthetic"], "$.scope", errors);
            if (!IsString(scope, "completeness", "DeclaredMetadataOnly") || !IsFalse(scope, "runtimeDiscovery") || !IsFalse(scope, "organizationWide") || !IsFalse(scope, "runtimeAvailability") || !scope.TryGetProperty("synthetic", out JsonElement synthetic) || synthetic.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                errors.Add(new(EventCatalogSchemaValidationCodes.InvalidScope, "$.scope", "Scope must explicitly describe declared metadata only without runtime/global completeness or availability claims."));
        }

        ValidateNodes(root, "nodes", "$.nodes", graph: false, errors);
        ValidateEdges(root, "relationships", "$.relationships", errors);
    }

    private static void ValidateGraph(JsonElement root, List<EventCatalogSchemaValidationError> errors)
    {
        ValidateKnownProperties(root, ["schemaVersion", "catalogIdentity", "nodes", "edges"], "$", errors);
        ValidateIdentity(root, "catalogIdentity", "$.catalogIdentity", errors);
        ValidateNodes(root, "nodes", "$.nodes", graph: true, errors);
        ValidateEdges(root, "edges", "$.edges", errors);
    }

    private static void ValidateIdentity(JsonElement root, string name, string path, List<EventCatalogSchemaValidationError> errors)
    {
        if (!root.TryGetProperty(name, out JsonElement identity) || identity.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new(EventCatalogSchemaValidationCodes.MissingIdentity, path, "Catalog identity is required."));
            return;
        }
        ValidateKnownProperties(identity, ["applicationName", "applicationVersion"], path, errors);
        if (!RequiredString(identity, "applicationName", 128) || !RequiredString(identity, "applicationVersion", 128))
            errors.Add(new(EventCatalogSchemaValidationCodes.MissingIdentity, path, "Application name and version are required bounded identity values."));
    }

    private static void ValidateNodes(JsonElement root, string name, string path, bool graph, List<EventCatalogSchemaValidationError> errors)
    {
        if (!root.TryGetProperty(name, out JsonElement nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new(EventCatalogSchemaValidationCodes.InvalidNode, path, "Nodes must be an array."));
            return;
        }
        if (nodes.GetArrayLength() > MaximumNodes) errors.Add(new(EventCatalogSchemaValidationCodes.BoundExceeded, path, "Node count exceeds 2048."));
        int index = 0;
        foreach (JsonElement node in nodes.EnumerateArray())
        {
            string nodePath = path + "[" + index++ + "]";
            if (node.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new(EventCatalogSchemaValidationCodes.InvalidNode, nodePath, "Node must be an object."));
                continue;
            }
            ValidateKnownProperties(node, graph ? GraphNodeProperties : NodeProperties, nodePath, errors);
            bool valid = RequiredString(node, "id", 128)
                && node.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String && NodeTypes.Contains(type.GetString()!)
                && (graph || RequiredString(node, "logicalId", MaximumTextLength));
            if (!valid) errors.Add(new(EventCatalogSchemaValidationCodes.InvalidNode, nodePath, "Node identity/type is invalid or unsupported."));
        }
    }

    private static void ValidateEdges(JsonElement root, string name, string path, List<EventCatalogSchemaValidationError> errors)
    {
        if (!root.TryGetProperty(name, out JsonElement edges) || edges.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new(EventCatalogSchemaValidationCodes.InvalidRelationship, path, "Relationships must be an array."));
            return;
        }
        if (edges.GetArrayLength() > MaximumEdges) errors.Add(new(EventCatalogSchemaValidationCodes.BoundExceeded, path, "Relationship count exceeds 8192."));
        int index = 0;
        foreach (JsonElement edge in edges.EnumerateArray())
        {
            string edgePath = path + "[" + index++ + "]";
            if (edge.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new(EventCatalogSchemaValidationCodes.InvalidRelationship, edgePath, "Relationship must be an object."));
                continue;
            }
            ValidateKnownProperties(edge, ["id", "type", "sourceNodeId", "targetNodeId"], edgePath, errors);
            bool valid = RequiredString(edge, "id", 128)
                && edge.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String && RelationshipTypes.Contains(type.GetString()!)
                && RequiredString(edge, "sourceNodeId", 128) && RequiredString(edge, "targetNodeId", 128);
            if (!valid) errors.Add(new(EventCatalogSchemaValidationCodes.InvalidRelationship, edgePath, "Relationship identity/type/endpoints are invalid or unsupported."));
        }
    }

    private static void ValidateKnownProperties(JsonElement value, IEnumerable<string> allowed, string path, List<EventCatalogSchemaValidationError> errors)
    {
        HashSet<string> names = allowed is HashSet<string> set ? set : allowed.ToHashSet(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!names.Contains(property.Name)) errors.Add(new(EventCatalogSchemaValidationCodes.UnknownProperty, path + "." + property.Name, "Property is not defined by the governed schema."));
    }

    private static bool RequiredString(JsonElement value, string name, int maxLength) =>
        value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 } text && text.Length <= maxLength;

    private static bool IsString(JsonElement value, string name, string expected) => value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String && string.Equals(property.GetString(), expected, StringComparison.Ordinal);
    private static bool IsFalse(JsonElement value, string name) => value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.False;

    private static void ScanStrings(JsonElement value, string path, List<EventCatalogSchemaValidationError> errors)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString() ?? string.Empty;
            if (text.Length > MaximumTextLength) errors.Add(new(EventCatalogSchemaValidationCodes.BoundExceeded, path, "String exceeds the governed metadata bound."));
            string lowered = text.ToLowerInvariant();
            if (SecretMarkers.Any(lowered.Contains)) errors.Add(new(EventCatalogSchemaValidationCodes.ProhibitedSecretMetadata, path, "Secret-bearing metadata is prohibited."));
            return;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject()) ScanStrings(property.Value, path + "." + property.Name, errors);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray()) ScanStrings(item, path + "[" + index++ + "]", errors);
        }
    }
}
