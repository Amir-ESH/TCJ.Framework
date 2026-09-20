using System.Text.Json.Serialization;

namespace TCJ.Messaging.AsyncApi;

/// <summary>Versioned application-owned messaging topology catalog.</summary>
public sealed record MessagingCatalog
{
    /// <summary>Initial messaging catalog input schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required MessagingApplication Application { get; init; }
    public required MessagingDocumentInfo Document { get; init; }
    public IReadOnlyList<MessagingSecurityScheme> SecuritySchemes { get; init; } = [];
    public IReadOnlyList<MessagingServer> Servers { get; init; } = [];
    public IReadOnlyList<MessagingTransport> Transports { get; init; } = [];
    public IReadOnlyList<MessagingProducer> Producers { get; init; } = [];
    public IReadOnlyList<MessagingConsumer> Consumers { get; init; } = [];
    public IReadOnlyList<MessagingChannel> Channels { get; init; } = [];
    public IReadOnlyList<MessagingRelationship> Relationships { get; init; } = [];
}

public sealed record MessagingApplication
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public string? Owner { get; init; }
}

public sealed record MessagingDocumentInfo
{
    public required string Title { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public MessagingContact? Contact { get; init; }
    public MessagingLicense? License { get; init; }
    public MessagingExternalDocumentation? ExternalDocumentation { get; init; }
}

public sealed record MessagingContact
{
    public string? Name { get; init; }
    public string? Url { get; init; }
}

public sealed record MessagingLicense
{
    public required string Name { get; init; }
    public string? Url { get; init; }
}

public sealed record MessagingExternalDocumentation
{
    public required string Url { get; init; }
    public string? Description { get; init; }
}

public sealed record MessagingSecurityScheme
{
    public required string Id { get; init; }
    public required MessagingSecurityMechanism Mechanism { get; init; }
    public string? Description { get; init; }
}


public sealed record MessagingServer
{
    public required string Id { get; init; }
    public required string Host { get; init; }
    public required string Protocol { get; init; }
    public string? Description { get; init; }
    public string? SecuritySchemeId { get; init; }
}

public sealed record MessagingTransport
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Protocol { get; init; }
    public string? Description { get; init; }
    public MessagingDeliverySemantics DeliverySemantics { get; init; } = MessagingDeliverySemantics.TransportSpecific;
    public MessagingOrderingSemantics OrderingSemantics { get; init; } = MessagingOrderingSemantics.None;
    public MessagingDeadLetterSemantics DeadLetterSemantics { get; init; } = MessagingDeadLetterSemantics.TransportSpecific;
    public MessagingPartitioningSemantics PartitioningSemantics { get; init; } = MessagingPartitioningSemantics.TransportSpecific;
    public IReadOnlyList<string> CapabilityIds { get; init; } = [];
    public string? SecuritySchemeId { get; init; }
    public string? Owner { get; init; }
}

public sealed record MessagingProducer
{
    public required string Id { get; init; }
    public required string Component { get; init; }
    public required MessagingMessageReference Message { get; init; }
    public required string ChannelId { get; init; }
    public required string TransportId { get; init; }
    public MessagingOutboxDeclaration? Outbox { get; init; }
    public string? PartitionKeyStrategy { get; init; }
    public string? OrderingKeyStrategy { get; init; }
    public MessagingRetryOwner RetryOwner { get; init; } = MessagingRetryOwner.Application;
    public MessagingLifecycle Lifecycle { get; init; } = MessagingLifecycle.Published;
    public string? Owner { get; init; }
    public MessagingDynamicDestination? DynamicDestination { get; init; }
}

public sealed record MessagingConsumer
{
    public required string Id { get; init; }
    public required string Component { get; init; }
    public required string MessageType { get; init; }
    public IReadOnlyList<int> AcceptedMessageVersions { get; init; } = [];
    public required string ChannelId { get; init; }
    public required string TransportId { get; init; }
    public string? SubscriptionOrGroup { get; init; }
    public MessagingInboxDeclaration? Inbox { get; init; }
    public MessagingOrderingSemantics OrderingScope { get; init; } = MessagingOrderingSemantics.None;
    public MessagingRetryOwner RetryOwner { get; init; } = MessagingRetryOwner.Application;
    public MessagingDeadLetterSemantics DeadLetterSemantics { get; init; } = MessagingDeadLetterSemantics.TransportSpecific;
    public MessagingLifecycle Lifecycle { get; init; } = MessagingLifecycle.Published;
    public string? Owner { get; init; }
}

public sealed record MessagingChannel
{
    public required string Id { get; init; }
    public required string Address { get; init; }
    public required string TransportId { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<MessagingMessageReference> Messages { get; init; } = [];
    public MessagingDynamicDestination? DynamicDestination { get; init; }
    public MessagingLifecycle Lifecycle { get; init; } = MessagingLifecycle.Published;
    public string? Owner { get; init; }
    public IReadOnlyList<MessagingDataClassification> DataClassifications { get; init; } = [];
}

public sealed record MessagingDynamicDestination
{
    public required string NamingStrategyId { get; init; }
    public required string Pattern { get; init; }
    public string? Description { get; init; }
}

public sealed record MessagingMessageReference
{
    public required string Type { get; init; }
    public required int Version { get; init; }
}

public sealed record MessagingOutboxDeclaration
{
    public bool Enabled { get; init; }
    public string? DeduplicationModel { get; init; }
    public MessagingTransactionalBoundary TransactionalBoundary { get; init; } = MessagingTransactionalBoundary.Application;
    public MessagingDurablePublicationModel DurablePublicationModel { get; init; } = MessagingDurablePublicationModel.ApplicationManaged;
}

public sealed record MessagingInboxDeclaration
{
    public bool Enabled { get; init; }
    public string? DeduplicationModel { get; init; }
    public MessagingTransactionalBoundary TransactionalBoundary { get; init; } = MessagingTransactionalBoundary.Application;
}

public sealed record MessagingRelationship
{
    public required string Id { get; init; }
    public required MessagingRelationshipKind Kind { get; init; }
    public required MessagingEntityReference Source { get; init; }
    public required MessagingEntityReference Target { get; init; }
    public MessagingSagaReference? Saga { get; init; }
}

public sealed record MessagingEntityReference
{
    public required MessagingEntityKind Kind { get; init; }
    public required string Id { get; init; }
}

public sealed record MessagingSagaReference
{
    public required string DefinitionId { get; init; }
    public required int DefinitionVersion { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<MessagingDeliverySemantics>))]
public enum MessagingDeliverySemantics { AtLeastOnce, AtMostOnce, BestEffort, TransportSpecific }

[JsonConverter(typeof(JsonStringEnumConverter<MessagingOrderingSemantics>))]
public enum MessagingOrderingSemantics { None, BestEffort, PerPartition, PerSession }

[JsonConverter(typeof(JsonStringEnumConverter<MessagingDeadLetterSemantics>))]
public enum MessagingDeadLetterSemantics { Native, ConventionBased, Unsupported, TransportSpecific }

[JsonConverter(typeof(JsonStringEnumConverter<MessagingPartitioningSemantics>))]
public enum MessagingPartitioningSemantics { None, Supported, Required, TransportSpecific }

[JsonConverter(typeof(JsonStringEnumConverter<MessagingLifecycle>))]
public enum MessagingLifecycle { Draft, Published, Deprecated, Retired }

[JsonConverter(typeof(JsonStringEnumConverter<MessagingSecurityMechanism>))]
public enum MessagingSecurityMechanism
{
    TLS,
    X509,
    [JsonStringEnumMemberName("SASL/PLAIN")] SaslPlain,
    [JsonStringEnumMemberName("SASL/SCRAM")] SaslScram,
    OAuth2,
    ManagedIdentity,
    UserPassword,
    ConnectionStringCredential
}

[JsonConverter(typeof(JsonStringEnumConverter<MessagingDataClassification>))]
public enum MessagingDataClassification { Public, Internal, Personal, Confidential, SecretProhibited }

[JsonConverter(typeof(JsonStringEnumConverter<MessagingRelationshipKind>))]
public enum MessagingRelationshipKind { Publishes, Consumes, UsesChannel, UsesTransport, StartsSaga, ContinuesSaga, CompensatesSaga, Replaces, UpcastsTo }

[JsonConverter(typeof(JsonStringEnumConverter<MessagingEntityKind>))]
public enum MessagingEntityKind { Application, Transport, Producer, Consumer, Channel, SecurityScheme }

[JsonConverter(typeof(JsonStringEnumConverter<MessagingRetryOwner>))]
public enum MessagingRetryOwner { Application, Transport, None }

[JsonConverter(typeof(JsonStringEnumConverter<MessagingTransactionalBoundary>))]
public enum MessagingTransactionalBoundary { Application, MessageHandler, Separate }

[JsonConverter(typeof(JsonStringEnumConverter<MessagingDurablePublicationModel>))]
public enum MessagingDurablePublicationModel { ApplicationManaged, TransportManaged }
