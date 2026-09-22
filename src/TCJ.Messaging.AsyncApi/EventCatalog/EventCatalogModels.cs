using System.Text.Json.Serialization;

namespace TCJ.Messaging.AsyncApi;

/// <summary>Governed canonical artifact names for Step 53 event-catalog generation.</summary>
public static class EventCatalogArtifacts
{
    public const string EventCatalogFileName = "event-catalog.json";
    public const string RelationshipGraphFileName = "catalog-graph.json";
}

/// <summary>Canonical versioned machine-readable event catalog generated from declared messaging metadata.</summary>
public sealed record EventCatalog
{
    public const string CurrentSchemaVersion = "1.0";
    public required string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required EventCatalogIdentity Identity { get; init; }
    public required EventCatalogScope Scope { get; init; }
    public IReadOnlyList<EventCatalogNode> Nodes { get; init; } = [];
    public IReadOnlyList<EventCatalogEdge> Relationships { get; init; } = [];
}

public sealed record EventCatalogIdentity
{
    public required string ApplicationName { get; init; }
    public required string ApplicationVersion { get; init; }
}

public sealed record EventCatalogScope
{
    public required EventCatalogCompleteness Completeness { get; init; } = EventCatalogCompleteness.DeclaredMetadataOnly;
    public bool RuntimeDiscovery { get; init; }
    public bool OrganizationWide { get; init; }
    public bool RuntimeAvailability { get; init; }
    public bool Synthetic { get; init; }
}

public sealed record EventCatalogNode
{
    public required string Id { get; init; }
    public required EventCatalogNodeType Type { get; init; }
    public required string LogicalId { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? Owner { get; init; }
    public MessagingLifecycle? Lifecycle { get; init; }
    public string? Component { get; init; }
    public string? MessageType { get; init; }
    public int? MessageVersion { get; init; }
    public string? ContentType { get; init; }
    public string? SchemaFingerprintAlgorithm { get; init; }
    public string? SchemaFingerprint { get; init; }
    /// <summary>Gets the validated governed schema artifact path relative to the Step 52 artifact root.</summary>
    public string? SchemaRelativePath { get; init; }
    /// <summary>Gets validated governed example artifact paths relative to the Step 52 artifact root.</summary>
    public IReadOnlyList<string> ExampleRelativePaths { get; init; } = [];
    public string? CompatibilityMode { get; init; }
    public IReadOnlyList<string> DataClassifications { get; init; } = [];
    public string? Address { get; init; }
    public string? TransportKind { get; init; }
    public string? Protocol { get; init; }
    public MessagingDeliverySemantics? DeliverySemantics { get; init; }
    public MessagingOrderingSemantics? OrderingSemantics { get; init; }
    public MessagingPartitioningSemantics? PartitioningSemantics { get; init; }
    public MessagingDeadLetterSemantics? DeadLetterSemantics { get; init; }
    public bool? InboxEnabled { get; init; }
    public bool? OutboxEnabled { get; init; }
    public string? RetryOwner { get; init; }
    public string? SubscriptionOrGroup { get; init; }
    public bool DynamicDestination { get; init; }
    public string? DynamicNamingStrategyId { get; init; }
    public string? DynamicPattern { get; init; }
    public int? SagaDefinitionVersion { get; init; }
}

public sealed record EventCatalogEdge
{
    public required string Id { get; init; }
    public required EventCatalogRelationshipType Type { get; init; }
    public required string SourceNodeId { get; init; }
    public required string TargetNodeId { get; init; }
}

public sealed record RelationshipGraph
{
    public const string CurrentSchemaVersion = "1.0";
    public required string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required EventCatalogIdentity CatalogIdentity { get; init; }
    public IReadOnlyList<RelationshipGraphNode> Nodes { get; init; } = [];
    public IReadOnlyList<EventCatalogEdge> Edges { get; init; } = [];
}

public sealed record RelationshipGraphNode
{
    public required string Id { get; init; }
    public required EventCatalogNodeType Type { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<EventCatalogNodeType>))]
public enum EventCatalogNodeType
{
    Application,
    Producer,
    Consumer,
    MessageContract,
    Channel,
    Transport,
    Saga
}

[JsonConverter(typeof(JsonStringEnumConverter<EventCatalogRelationshipType>))]
public enum EventCatalogRelationshipType
{
    Publishes,
    Consumes,
    UsesChannel,
    UsesTransport,
    StartsSaga,
    ContinuesSaga,
    TimesOutSaga,
    CompensatesSaga,
    CompletesSaga,
    FailsSaga,
    Replaces,
    UpcastsTo
}

[JsonConverter(typeof(JsonStringEnumConverter<EventCatalogCompleteness>))]
public enum EventCatalogCompleteness
{
    DeclaredMetadataOnly
}
