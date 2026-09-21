using System.Text;
using System.Text.Json;

namespace TCJ.Messaging.AsyncApi;

/// <summary>Canonical deterministic JSON serialization for event catalog and relationship graph artifacts.</summary>
public static class EventCatalogJson
{
    public static byte[] Serialize(EventCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        using var stream = new MemoryStream();
        using (var writer = Writer(stream)) WriteCatalog(writer, catalog);
        return WithLf(stream.ToArray());
    }

    public static byte[] Serialize(RelationshipGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        using var stream = new MemoryStream();
        using (var writer = Writer(stream)) WriteGraph(writer, graph);
        return WithLf(stream.ToArray());
    }

    private static Utf8JsonWriter Writer(Stream stream) => new(stream, new JsonWriterOptions { Indented = true, SkipValidation = false });

    private static void WriteCatalog(Utf8JsonWriter writer, EventCatalog catalog)
    {
        writer.WriteStartObject();
        writer.WriteString("schemaVersion", catalog.SchemaVersion);
        writer.WritePropertyName("identity"); WriteIdentity(writer, catalog.Identity);
        writer.WritePropertyName("scope"); writer.WriteStartObject();
        writer.WriteString("completeness", catalog.Scope.Completeness.ToString());
        writer.WriteBoolean("runtimeDiscovery", catalog.Scope.RuntimeDiscovery);
        writer.WriteBoolean("organizationWide", catalog.Scope.OrganizationWide);
        writer.WriteBoolean("runtimeAvailability", catalog.Scope.RuntimeAvailability);
        writer.WriteBoolean("synthetic", catalog.Scope.Synthetic);
        writer.WriteEndObject();
        writer.WritePropertyName("nodes"); writer.WriteStartArray(); foreach (EventCatalogNode node in catalog.Nodes) WriteNode(writer, node); writer.WriteEndArray();
        writer.WritePropertyName("relationships"); writer.WriteStartArray(); foreach (EventCatalogEdge edge in catalog.Relationships) WriteEdge(writer, edge); writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteGraph(Utf8JsonWriter writer, RelationshipGraph graph)
    {
        writer.WriteStartObject();
        writer.WriteString("schemaVersion", graph.SchemaVersion);
        writer.WritePropertyName("catalogIdentity"); WriteIdentity(writer, graph.CatalogIdentity);
        writer.WritePropertyName("nodes"); writer.WriteStartArray();
        foreach (RelationshipGraphNode node in graph.Nodes)
        {
            writer.WriteStartObject(); writer.WriteString("id", node.Id); writer.WriteString("type", node.Type.ToString()); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("edges"); writer.WriteStartArray(); foreach (EventCatalogEdge edge in graph.Edges) WriteEdge(writer, edge); writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteIdentity(Utf8JsonWriter writer, EventCatalogIdentity identity)
    {
        writer.WriteStartObject(); writer.WriteString("applicationName", identity.ApplicationName); writer.WriteString("applicationVersion", identity.ApplicationVersion); writer.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter writer, EventCatalogNode node)
    {
        writer.WriteStartObject();
        writer.WriteString("id", node.Id); writer.WriteString("type", node.Type.ToString()); writer.WriteString("logicalId", node.LogicalId);
        Optional(writer, "version", node.Version); Optional(writer, "description", node.Description); Optional(writer, "owner", node.Owner); OptionalEnum(writer, "lifecycle", node.Lifecycle);
        Optional(writer, "component", node.Component); Optional(writer, "messageType", node.MessageType); Optional(writer, "messageVersion", node.MessageVersion); Optional(writer, "contentType", node.ContentType);
        Optional(writer, "schemaFingerprintAlgorithm", node.SchemaFingerprintAlgorithm); Optional(writer, "schemaFingerprint", node.SchemaFingerprint); Optional(writer, "compatibilityMode", node.CompatibilityMode);
        if (node.DataClassifications.Count != 0) { writer.WritePropertyName("dataClassifications"); writer.WriteStartArray(); foreach (string value in node.DataClassifications) writer.WriteStringValue(value); writer.WriteEndArray(); }
        Optional(writer, "address", node.Address); Optional(writer, "transportKind", node.TransportKind); Optional(writer, "protocol", node.Protocol);
        OptionalEnum(writer, "deliverySemantics", node.DeliverySemantics); OptionalEnum(writer, "orderingSemantics", node.OrderingSemantics); OptionalEnum(writer, "partitioningSemantics", node.PartitioningSemantics); OptionalEnum(writer, "deadLetterSemantics", node.DeadLetterSemantics);
        Optional(writer, "inboxEnabled", node.InboxEnabled); Optional(writer, "outboxEnabled", node.OutboxEnabled); Optional(writer, "retryOwner", node.RetryOwner); Optional(writer, "subscriptionOrGroup", node.SubscriptionOrGroup);
        if (node.DynamicDestination) writer.WriteBoolean("dynamicDestination", true);
        Optional(writer, "dynamicNamingStrategyId", node.DynamicNamingStrategyId); Optional(writer, "dynamicPattern", node.DynamicPattern); Optional(writer, "sagaDefinitionVersion", node.SagaDefinitionVersion);
        writer.WriteEndObject();
    }

    private static void WriteEdge(Utf8JsonWriter writer, EventCatalogEdge edge)
    {
        writer.WriteStartObject(); writer.WriteString("id", edge.Id); writer.WriteString("type", edge.Type.ToString()); writer.WriteString("sourceNodeId", edge.SourceNodeId); writer.WriteString("targetNodeId", edge.TargetNodeId); writer.WriteEndObject();
    }

    private static void Optional(Utf8JsonWriter writer, string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) writer.WriteString(name, value); }
    private static void Optional(Utf8JsonWriter writer, string name, int? value) { if (value.HasValue) writer.WriteNumber(name, value.Value); }
    private static void Optional(Utf8JsonWriter writer, string name, bool? value) { if (value.HasValue) writer.WriteBoolean(name, value.Value); }
    private static void OptionalEnum<T>(Utf8JsonWriter writer, string name, T? value) where T : struct, Enum { if (value.HasValue) writer.WriteString(name, value.Value.ToString()); }
    private static byte[] WithLf(byte[] bytes) { byte[] result = new byte[bytes.Length + 1]; bytes.CopyTo(result, 0); result[^1] = (byte)'\n'; return result; }
}
