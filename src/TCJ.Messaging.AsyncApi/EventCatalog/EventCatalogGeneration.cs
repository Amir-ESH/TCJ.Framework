using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TCJ.Messaging.Contracts;

namespace TCJ.Messaging.AsyncApi;

public static class EventCatalogGenerationCodes
{
    public const string InvalidCatalog = "ECG001";
    public const string InvalidGovernedContracts = "ECG002";
    public const string UnknownContract = "ECG003";
    public const string DuplicateNodeId = "ECG004";
    public const string NodeIdCollision = "ECG005";
    public const string UnknownRelationshipEndpoint = "ECG006";
    public const string UnsupportedRelationship = "ECG007";
    public const string InvalidRelationshipDirection = "ECG008";
    public const string DuplicateEdgeId = "ECG009";
    public const string EdgeIdCollision = "ECG010";
    public const string BoundExceeded = "ECG011";
    public const string ProhibitedSecretMetadata = "ECG012";
    public const string InvalidReplacementReference = "ECG013";
}

public sealed record EventCatalogGenerationError(string Code, string Path, string Message);

public sealed record EventCatalogGenerationOptions
{
    public bool Synthetic { get; init; }
    public int MaximumNodes { get; init; } = 2048;
    public int MaximumEdges { get; init; } = 8192;
    public int MaximumMetadataStringLength { get; init; } = 2048;
}

public sealed class EventCatalogGenerationResult
{
    internal EventCatalogGenerationResult(EventCatalog? catalog, RelationshipGraph? graph, byte[] catalogJson, byte[] graphJson, IReadOnlyList<EventCatalogGenerationError> errors)
    {
        Catalog = catalog;
        Graph = graph;
        EventCatalogUtf8Json = catalogJson;
        RelationshipGraphUtf8Json = graphJson;
        Errors = errors;
    }

    public EventCatalog? Catalog { get; }
    public RelationshipGraph? Graph { get; }
    public ReadOnlyMemory<byte> EventCatalogUtf8Json { get; }
    public ReadOnlyMemory<byte> RelationshipGraphUtf8Json { get; }
    public IReadOnlyList<EventCatalogGenerationError> Errors { get; }
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Offline deterministic event-catalog and relationship-graph generator.</summary>
public static class EventCatalogGenerator
{
    private static readonly string[] SecretMarkers =
    [
        "password=", "password:", "client_secret", "clientsecret", "access_token", "refresh_token",
        "sharedaccesssignature", "accesskey=", "private key", "begin private key", "connectionstring=",
        "connection string=", "accountkey=", "sharedaccesskey=", "endpoint=sb://", "bootstrap.servers="
    ];

    public static EventCatalogGenerationResult Generate(
        MessagingCatalog catalog,
        GovernedContractResolutionResult governedContracts,
        EventCatalogGenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(governedContracts);
        options ??= new EventCatalogGenerationOptions();

        var errors = new List<EventCatalogGenerationError>();
        MessagingCatalogValidationResult catalogValidation = MessagingCatalogValidator.Validate(catalog);
        foreach (MessagingCatalogValidationError error in catalogValidation.Errors)
            errors.Add(new(EventCatalogGenerationCodes.InvalidCatalog, error.Path, error.Code + ": " + error.Message));

        if (!governedContracts.IsValid)
        {
            foreach (GovernedContractValidationError error in governedContracts.Errors)
                errors.Add(new(EventCatalogGenerationCodes.InvalidGovernedContracts, error.Path, error.Code + ": " + error.Message));
        }

        if (options.MaximumNodes <= 0 || options.MaximumEdges <= 0 || options.MaximumMetadataStringLength <= 0)
            errors.Add(new(EventCatalogGenerationCodes.BoundExceeded, "$", "Generation bounds must be positive."));

        if (errors.Count != 0) return Invalid(errors);

        Dictionary<string, ResolvedGovernedMessageContract> contracts = governedContracts.Contracts.ToDictionary(
            static x => ContractKey(x.MessageType, x.MessageVersion), StringComparer.Ordinal);
        var requiredContractKeys = RequiredContracts(catalog).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        foreach (string key in requiredContractKeys)
        {
            if (!contracts.ContainsKey(key))
                errors.Add(new(EventCatalogGenerationCodes.UnknownContract, "$.contracts", "A catalog message contract is not present in governed Step 52 evidence."));
        }
        if (errors.Count != 0) return Invalid(errors);

        var nodes = new List<EventCatalogNode>();
        var nodeBySemanticKey = new Dictionary<string, EventCatalogNode>(StringComparer.Ordinal);
        var idToSemanticKey = new Dictionary<string, string>(StringComparer.Ordinal);

        void AddNode(string semanticKey, EventCatalogNode node)
        {
            if (nodeBySemanticKey.ContainsKey(semanticKey)) return;
            if (idToSemanticKey.TryGetValue(node.Id, out string? existing) && !string.Equals(existing, semanticKey, StringComparison.Ordinal))
            {
                errors.Add(new(EventCatalogGenerationCodes.NodeIdCollision, "$.nodes", "Two distinct node identities collide on the same stable node ID."));
                return;
            }
            idToSemanticKey[node.Id] = semanticKey;
            nodeBySemanticKey[semanticKey] = node;
            nodes.Add(node);
        }

        string applicationKey = EntityKey("application", catalog.Application.Name);
        AddNode(applicationKey, new EventCatalogNode
        {
            Id = NodeId(EventCatalogNodeType.Application, catalog.Application.Name),
            Type = EventCatalogNodeType.Application,
            LogicalId = catalog.Application.Name,
            Version = catalog.Application.Version,
            Description = catalog.Application.Description,
            Owner = catalog.Application.Owner
        });

        foreach (MessagingTransport transport in catalog.Transports.OrderBy(static x => x.Id, StringComparer.Ordinal))
        {
            AddNode(EntityKey("transport", transport.Id), new EventCatalogNode
            {
                Id = NodeId(EventCatalogNodeType.Transport, transport.Id),
                Type = EventCatalogNodeType.Transport,
                LogicalId = transport.Id,
                Description = transport.Description,
                Owner = transport.Owner,
                TransportKind = transport.Kind,
                Protocol = transport.Protocol,
                DeliverySemantics = transport.DeliverySemantics,
                OrderingSemantics = transport.OrderingSemantics,
                PartitioningSemantics = transport.PartitioningSemantics,
                DeadLetterSemantics = transport.DeadLetterSemantics
            });
        }

        foreach (MessagingChannel channel in catalog.Channels.OrderBy(static x => x.Id, StringComparer.Ordinal))
        {
            bool dynamic = channel.DynamicDestination is not null;
            AddNode(EntityKey("channel", channel.Id), new EventCatalogNode
            {
                Id = NodeId(EventCatalogNodeType.Channel, channel.Id),
                Type = EventCatalogNodeType.Channel,
                LogicalId = channel.Id,
                Description = channel.Description,
                Owner = channel.Owner,
                Lifecycle = channel.Lifecycle,
                Address = dynamic ? null : channel.Address,
                DynamicDestination = dynamic,
                DynamicNamingStrategyId = channel.DynamicDestination?.NamingStrategyId,
                DynamicPattern = channel.DynamicDestination?.Pattern,
                DataClassifications = channel.DataClassifications.Select(static x => x.ToString()).OrderBy(static x => x, StringComparer.Ordinal).ToArray()
            });
        }

        foreach (ResolvedGovernedMessageContract contract in governedContracts.Contracts
                     .Where(x => requiredContractKeys.Contains(ContractKey(x.MessageType, x.MessageVersion), StringComparer.Ordinal))
                     .OrderBy(static x => x.MessageType, StringComparer.Ordinal).ThenBy(static x => x.MessageVersion))
        {
            string identity = contract.MessageType + ":v" + contract.MessageVersion.ToString(CultureInfo.InvariantCulture);
            AddNode("message\u001f" + ContractKey(contract.MessageType, contract.MessageVersion), new EventCatalogNode
            {
                Id = NodeId(EventCatalogNodeType.MessageContract, identity),
                Type = EventCatalogNodeType.MessageContract,
                LogicalId = identity,
                MessageType = contract.MessageType,
                MessageVersion = contract.MessageVersion,
                ContentType = contract.ContentType,
                SchemaFingerprintAlgorithm = contract.SchemaFingerprint.Algorithm,
                SchemaFingerprint = contract.SchemaFingerprint.Value,
                Owner = contract.Owner,
                Lifecycle = contract.Deprecated ? MessagingLifecycle.Deprecated : MessagingLifecycle.Published,
                CompatibilityMode = contract.CompatibilityMode.ToString(),
                DataClassifications = contract.DataClassifications.Select(static x => x.Classification.ToString()).Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray()
            });
        }

        foreach (MessagingProducer producer in catalog.Producers.OrderBy(static x => x.Id, StringComparer.Ordinal))
        {
            AddNode(EntityKey("producer", producer.Id), new EventCatalogNode
            {
                Id = NodeId(EventCatalogNodeType.Producer, producer.Id),
                Type = EventCatalogNodeType.Producer,
                LogicalId = producer.Id,
                Component = producer.Component,
                Owner = producer.Owner,
                Lifecycle = producer.Lifecycle,
                OutboxEnabled = producer.Outbox?.Enabled,
                RetryOwner = producer.RetryOwner?.ToString(),
                DynamicDestination = producer.DynamicDestination is not null,
                DynamicNamingStrategyId = producer.DynamicDestination?.NamingStrategyId,
                DynamicPattern = producer.DynamicDestination?.Pattern
            });
        }

        foreach (MessagingConsumer consumer in catalog.Consumers.OrderBy(static x => x.Id, StringComparer.Ordinal))
        {
            AddNode(EntityKey("consumer", consumer.Id), new EventCatalogNode
            {
                Id = NodeId(EventCatalogNodeType.Consumer, consumer.Id),
                Type = EventCatalogNodeType.Consumer,
                LogicalId = consumer.Id,
                Component = consumer.Component,
                Owner = consumer.Owner,
                Lifecycle = consumer.Lifecycle,
                InboxEnabled = consumer.Inbox?.Enabled,
                OrderingSemantics = consumer.OrderingScope,
                DeadLetterSemantics = consumer.DeadLetterSemantics,
                RetryOwner = consumer.RetryOwner?.ToString(),
                SubscriptionOrGroup = consumer.SubscriptionOrGroup
            });
        }

        foreach (MessagingRelationship relationship in catalog.Relationships
                     .Where(static x => IsSagaRelationship(x.Kind))
                     .OrderBy(static x => x.Id, StringComparer.Ordinal))
        {
            if (relationship.Saga is null)
            {
                errors.Add(new(EventCatalogGenerationCodes.InvalidRelationshipDirection, "$.relationships", "Saga relationship requires stable Saga definition metadata."));
                continue;
            }
            string sagaLogicalId = relationship.Saga.DefinitionId + ":v" + relationship.Saga.DefinitionVersion.ToString(CultureInfo.InvariantCulture);
            AddNode("saga\u001f" + sagaLogicalId, new EventCatalogNode
            {
                Id = NodeId(EventCatalogNodeType.Saga, sagaLogicalId),
                Type = EventCatalogNodeType.Saga,
                LogicalId = relationship.Saga.DefinitionId,
                SagaDefinitionVersion = relationship.Saga.DefinitionVersion
            });
        }

        if (nodes.Count > options.MaximumNodes)
            errors.Add(new(EventCatalogGenerationCodes.BoundExceeded, "$.nodes", "Catalog node count exceeds the configured generation bound."));
        ValidateMetadata(nodes, options.MaximumMetadataStringLength, errors);
        if (errors.Count != 0) return Invalid(errors);

        var edges = new List<EventCatalogEdge>();
        var edgeSemanticKeys = new HashSet<string>(StringComparer.Ordinal);
        var edgeIds = new Dictionary<string, string>(StringComparer.Ordinal);

        void AddEdge(EventCatalogRelationshipType type, EventCatalogNode source, EventCatalogNode target)
        {
            string semanticKey = type + "\u001f" + source.Id + "\u001f" + target.Id;
            if (!edgeSemanticKeys.Add(semanticKey)) return;
            string id = EdgeId(type, source.Id, target.Id);
            if (edgeIds.TryGetValue(id, out string? existing) && !string.Equals(existing, semanticKey, StringComparison.Ordinal))
            {
                errors.Add(new(EventCatalogGenerationCodes.EdgeIdCollision, "$.relationships", "Two distinct relationships collide on the same stable edge ID."));
                return;
            }
            edgeIds[id] = semanticKey;
            edges.Add(new EventCatalogEdge { Id = id, Type = type, SourceNodeId = source.Id, TargetNodeId = target.Id });
        }

        foreach (MessagingProducer producer in catalog.Producers)
        {
            EventCatalogNode producerNode = Node(nodeBySemanticKey, EntityKey("producer", producer.Id));
            EventCatalogNode messageNode = Node(nodeBySemanticKey, "message\u001f" + ContractKey(producer.Message.Type, producer.Message.Version));
            EventCatalogNode transportNode = Node(nodeBySemanticKey, EntityKey("transport", producer.TransportId));
            AddEdge(EventCatalogRelationshipType.Publishes, producerNode, messageNode);
            AddEdge(EventCatalogRelationshipType.UsesTransport, producerNode, transportNode);
            MessagingChannel channel = catalog.Channels.First(x => SameId(x.Id, producer.ChannelId));
            if (producer.DynamicDestination is null && channel.DynamicDestination is null)
                AddEdge(EventCatalogRelationshipType.UsesChannel, producerNode, Node(nodeBySemanticKey, EntityKey("channel", channel.Id)));
        }

        foreach (MessagingConsumer consumer in catalog.Consumers)
        {
            EventCatalogNode consumerNode = Node(nodeBySemanticKey, EntityKey("consumer", consumer.Id));
            foreach (int version in consumer.AcceptedMessageVersions.OrderBy(static x => x))
                AddEdge(EventCatalogRelationshipType.Consumes, consumerNode, Node(nodeBySemanticKey, "message\u001f" + ContractKey(consumer.MessageType, version)));
            AddEdge(EventCatalogRelationshipType.UsesTransport, consumerNode, Node(nodeBySemanticKey, EntityKey("transport", consumer.TransportId)));
            MessagingChannel channel = catalog.Channels.First(x => SameId(x.Id, consumer.ChannelId));
            if (channel.DynamicDestination is null)
                AddEdge(EventCatalogRelationshipType.UsesChannel, consumerNode, Node(nodeBySemanticKey, EntityKey("channel", channel.Id)));
        }

        foreach (MessagingChannel channel in catalog.Channels)
            AddEdge(EventCatalogRelationshipType.UsesTransport, Node(nodeBySemanticKey, EntityKey("channel", channel.Id)), Node(nodeBySemanticKey, EntityKey("transport", channel.TransportId)));

        foreach (ResolvedGovernedMessageContract contract in governedContracts.Contracts.Where(x => requiredContractKeys.Contains(ContractKey(x.MessageType, x.MessageVersion), StringComparer.Ordinal)))
        {
            if (contract.ReplacementVersion is not int replacementVersion) continue;
            string replacementKey = ContractKey(contract.MessageType, replacementVersion);
            if (!requiredContractKeys.Contains(replacementKey, StringComparer.Ordinal) || !contracts.ContainsKey(replacementKey))
            {
                errors.Add(new(EventCatalogGenerationCodes.InvalidReplacementReference, "$.contracts", "Governed replacement evidence references a contract version outside the supplied catalog."));
                continue;
            }
            EventCatalogNode replaced = Node(nodeBySemanticKey, "message\u001f" + ContractKey(contract.MessageType, contract.MessageVersion));
            EventCatalogNode replacement = Node(nodeBySemanticKey, "message\u001f" + replacementKey);
            AddEdge(EventCatalogRelationshipType.Replaces, replacement, replaced);
        }

        foreach (MessagingRelationship relationship in catalog.Relationships.Where(static x => IsSagaRelationship(x.Kind)))
        {
            if (relationship.Saga is null) continue;
            string sagaKey = "saga\u001f" + relationship.Saga.DefinitionId + ":v" + relationship.Saga.DefinitionVersion.ToString(CultureInfo.InvariantCulture);
            EventCatalogNode saga = Node(nodeBySemanticKey, sagaKey);
            IReadOnlyList<EventCatalogNode> messageNodes = ResolveRelationshipMessages(relationship, catalog, nodeBySemanticKey, errors);
            foreach (EventCatalogNode message in messageNodes)
                AddEdge(MapSagaRelationship(relationship.Kind), message, saga);
        }

        if (edges.Count > options.MaximumEdges)
            errors.Add(new(EventCatalogGenerationCodes.BoundExceeded, "$.relationships", "Catalog relationship count exceeds the configured generation bound."));
        if (errors.Count != 0) return Invalid(errors);

        EventCatalogNode[] orderedNodes = nodes.OrderBy(static x => x.Type.ToString(), StringComparer.Ordinal).ThenBy(static x => x.Id, StringComparer.Ordinal).ToArray();
        EventCatalogEdge[] orderedEdges = edges.OrderBy(static x => x.Type.ToString(), StringComparer.Ordinal)
            .ThenBy(static x => x.SourceNodeId, StringComparer.Ordinal)
            .ThenBy(static x => x.TargetNodeId, StringComparer.Ordinal)
            .ThenBy(static x => x.Id, StringComparer.Ordinal).ToArray();

        var resultCatalog = new EventCatalog
        {
            SchemaVersion = EventCatalog.CurrentSchemaVersion,
            Identity = new EventCatalogIdentity { ApplicationName = catalog.Application.Name, ApplicationVersion = catalog.Application.Version },
            Scope = new EventCatalogScope
            {
                Completeness = EventCatalogCompleteness.DeclaredMetadataOnly,
                RuntimeDiscovery = false,
                OrganizationWide = false,
                RuntimeAvailability = false,
                Synthetic = options.Synthetic
            },
            Nodes = orderedNodes,
            Relationships = orderedEdges
        };
        var graph = new RelationshipGraph
        {
            SchemaVersion = RelationshipGraph.CurrentSchemaVersion,
            CatalogIdentity = resultCatalog.Identity,
            Nodes = orderedNodes.Select(static x => new RelationshipGraphNode { Id = x.Id, Type = x.Type }).ToArray(),
            Edges = orderedEdges
        };

        EventCatalogValidationResult validation = EventCatalogValidator.Validate(resultCatalog, graph, options.MaximumNodes, options.MaximumEdges, options.MaximumMetadataStringLength);
        foreach (EventCatalogValidationError error in validation.Errors)
            errors.Add(new(error.Code, error.Path, error.Message));
        if (errors.Count != 0) return Invalid(errors);

        byte[] catalogJson = EventCatalogJson.Serialize(resultCatalog);
        byte[] graphJson = EventCatalogJson.Serialize(graph);
        EventCatalogSchemaValidationResult catalogSchema = EventCatalogSchemaValidator.ValidateEventCatalog(catalogJson);
        EventCatalogSchemaValidationResult graphSchema = EventCatalogSchemaValidator.ValidateRelationshipGraph(graphJson);
        foreach (EventCatalogSchemaValidationError error in catalogSchema.Errors.Concat(graphSchema.Errors))
            errors.Add(new(error.Code, error.Path, error.Message));
        if (errors.Count != 0) return Invalid(errors);

        return new EventCatalogGenerationResult(resultCatalog, graph, catalogJson, graphJson, []);
    }

    private static IReadOnlyList<EventCatalogNode> ResolveRelationshipMessages(MessagingRelationship relationship, MessagingCatalog catalog, IReadOnlyDictionary<string, EventCatalogNode> nodes, List<EventCatalogGenerationError> errors)
    {
        var result = new Dictionary<string, EventCatalogNode>(StringComparer.Ordinal);
        AddFromReference(relationship.Source);
        AddFromReference(relationship.Target);
        if (result.Count == 0)
            errors.Add(new(EventCatalogGenerationCodes.InvalidRelationshipDirection, "$.relationships", "Saga relationship does not identify any governed message through its explicit endpoints."));
        return result.Values.OrderBy(static x => x.Id, StringComparer.Ordinal).ToArray();

        void AddFromReference(MessagingEntityReference reference)
        {
            if (reference.Kind == MessagingEntityKind.Producer)
            {
                MessagingProducer? producer = catalog.Producers.FirstOrDefault(x => SameId(x.Id, reference.Id));
                if (producer is not null)
                {
                    EventCatalogNode node = Node(nodes, "message\u001f" + ContractKey(producer.Message.Type, producer.Message.Version));
                    result[node.Id] = node;
                }
            }
            else if (reference.Kind == MessagingEntityKind.Consumer)
            {
                MessagingConsumer? consumer = catalog.Consumers.FirstOrDefault(x => SameId(x.Id, reference.Id));
                if (consumer is not null)
                {
                    foreach (int version in consumer.AcceptedMessageVersions)
                    {
                        EventCatalogNode node = Node(nodes, "message\u001f" + ContractKey(consumer.MessageType, version));
                        result[node.Id] = node;
                    }
                }
            }
            else if (reference.Kind == MessagingEntityKind.Channel)
            {
                MessagingChannel? channel = catalog.Channels.FirstOrDefault(x => SameId(x.Id, reference.Id));
                if (channel is not null)
                {
                    foreach (MessagingMessageReference message in channel.Messages)
                    {
                        EventCatalogNode node = Node(nodes, "message\u001f" + ContractKey(message.Type, message.Version));
                        result[node.Id] = node;
                    }
                }
            }
        }
    }

    private static void ValidateMetadata(IEnumerable<EventCatalogNode> nodes, int maximumLength, List<EventCatalogGenerationError> errors)
    {
        foreach (EventCatalogNode node in nodes)
        {
            foreach (string? value in NodeStrings(node))
            {
                if (value is null) continue;
                if (value.Length > maximumLength)
                    errors.Add(new(EventCatalogGenerationCodes.BoundExceeded, "$.nodes", "Generated catalog metadata exceeds the configured string bound."));
                string lowered = value.ToLowerInvariant();
                if (SecretMarkers.Any(lowered.Contains))
                    errors.Add(new(EventCatalogGenerationCodes.ProhibitedSecretMetadata, "$.nodes", "Generated catalog metadata contains prohibited credential material."));
            }
        }
    }

    private static IEnumerable<string?> NodeStrings(EventCatalogNode node)
    {
        yield return node.LogicalId; yield return node.Version; yield return node.Description; yield return node.Owner; yield return node.Component;
        yield return node.MessageType; yield return node.ContentType; yield return node.SchemaFingerprintAlgorithm; yield return node.SchemaFingerprint;
        yield return node.CompatibilityMode; yield return node.Address; yield return node.TransportKind; yield return node.Protocol; yield return node.RetryOwner;
        yield return node.SubscriptionOrGroup; yield return node.DynamicNamingStrategyId; yield return node.DynamicPattern;
        foreach (string classification in node.DataClassifications) yield return classification;
    }

    private static HashSet<string> RequiredContracts(MessagingCatalog catalog)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (MessagingProducer producer in catalog.Producers) result.Add(ContractKey(producer.Message.Type, producer.Message.Version));
        foreach (MessagingConsumer consumer in catalog.Consumers)
            foreach (int version in consumer.AcceptedMessageVersions) result.Add(ContractKey(consumer.MessageType, version));
        foreach (MessagingChannel channel in catalog.Channels)
            foreach (MessagingMessageReference message in channel.Messages) result.Add(ContractKey(message.Type, message.Version));
        return result;
    }

    private static bool IsSagaRelationship(MessagingRelationshipKind kind) => kind is MessagingRelationshipKind.StartsSaga or MessagingRelationshipKind.ContinuesSaga or MessagingRelationshipKind.TimesOutSaga or MessagingRelationshipKind.CompensatesSaga or MessagingRelationshipKind.CompletesSaga or MessagingRelationshipKind.FailsSaga;

    private static EventCatalogRelationshipType MapSagaRelationship(MessagingRelationshipKind kind) => kind switch
    {
        MessagingRelationshipKind.StartsSaga => EventCatalogRelationshipType.StartsSaga,
        MessagingRelationshipKind.ContinuesSaga => EventCatalogRelationshipType.ContinuesSaga,
        MessagingRelationshipKind.TimesOutSaga => EventCatalogRelationshipType.TimesOutSaga,
        MessagingRelationshipKind.CompensatesSaga => EventCatalogRelationshipType.CompensatesSaga,
        MessagingRelationshipKind.CompletesSaga => EventCatalogRelationshipType.CompletesSaga,
        MessagingRelationshipKind.FailsSaga => EventCatalogRelationshipType.FailsSaga,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static EventCatalogNode Node(IReadOnlyDictionary<string, EventCatalogNode> nodes, string key) => nodes[key];
    private static bool SameId(string left, string right) => string.Equals(MessagingCatalogValidator.NormalizeIdentifier(left), MessagingCatalogValidator.NormalizeIdentifier(right), StringComparison.Ordinal);
    private static string EntityKey(string kind, string id) => kind + "\u001f" + MessagingCatalogValidator.NormalizeIdentifier(id);
    private static string ContractKey(string type, int version) => type + "\u001f" + version.ToString(CultureInfo.InvariantCulture);

    private static string NodeId(EventCatalogNodeType type, string logicalIdentity) => StableId("node", type.ToString(), logicalIdentity);
    private static string EdgeId(EventCatalogRelationshipType type, string sourceNodeId, string targetNodeId) => StableId("edge", type.ToString(), sourceNodeId, targetNodeId);

    private static string StableId(params string[] parts)
    {
        string canonical = string.Join("\u001f", parts.Select(static x => x.Normalize(NormalizationForm.FormC).ToLowerInvariant()));
        string prefix = MessagingCatalogValidator.NormalizeIdentifier(parts[1]);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..24];
        return prefix + "-" + hash;
    }

    private static EventCatalogGenerationResult Invalid(IEnumerable<EventCatalogGenerationError> errors) =>
        new(null, null, [], [], errors.OrderBy(static x => x.Path, StringComparer.Ordinal).ThenBy(static x => x.Code, StringComparer.Ordinal).ThenBy(static x => x.Message, StringComparer.Ordinal).ToArray());
}
