namespace TCJ.Messaging.AsyncApi;

public sealed record EventCatalogValidationError(string Code, string Path, string Message);

public sealed class EventCatalogValidationResult
{
    internal EventCatalogValidationResult(IReadOnlyList<EventCatalogValidationError> errors) => Errors = errors;
    public IReadOnlyList<EventCatalogValidationError> Errors { get; }
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Structural fail-closed validation for canonical event-catalog and graph models.</summary>
public static class EventCatalogValidator
{
    public static EventCatalogValidationResult Validate(EventCatalog? catalog, RelationshipGraph? graph, int maximumNodes = 2048, int maximumEdges = 8192, int maximumMetadataStringLength = 2048)
    {
        var errors = new List<EventCatalogValidationError>();
        if (catalog is null) { errors.Add(new(EventCatalogGenerationCodes.InvalidCatalog, "$", "Event catalog is required.")); return new(errors); }
        if (graph is null) { errors.Add(new(EventCatalogGenerationCodes.InvalidCatalog, "$.graph", "Relationship graph is required.")); return new(errors); }
        if (!string.Equals(catalog.SchemaVersion, EventCatalog.CurrentSchemaVersion, StringComparison.Ordinal))
            errors.Add(new(EventCatalogGenerationCodes.InvalidCatalog, "$.schemaVersion", "Unsupported event-catalog schema version."));
        if (!string.Equals(graph.SchemaVersion, RelationshipGraph.CurrentSchemaVersion, StringComparison.Ordinal))
            errors.Add(new(EventCatalogGenerationCodes.InvalidCatalog, "$.graph.schemaVersion", "Unsupported relationship-graph schema version."));
        if (catalog.Nodes.Count > maximumNodes || graph.Nodes.Count > maximumNodes)
            errors.Add(new(EventCatalogGenerationCodes.BoundExceeded, "$.nodes", "Node count exceeds the configured bound."));
        if (catalog.Relationships.Count > maximumEdges || graph.Edges.Count > maximumEdges)
            errors.Add(new(EventCatalogGenerationCodes.BoundExceeded, "$.relationships", "Edge count exceeds the configured bound."));
        if (catalog.Scope.Completeness != EventCatalogCompleteness.DeclaredMetadataOnly || catalog.Scope.RuntimeDiscovery || catalog.Scope.OrganizationWide || catalog.Scope.RuntimeAvailability)
            errors.Add(new(EventCatalogGenerationCodes.InvalidCatalog, "$.scope", "Catalog scope must describe declared metadata only and must not claim runtime/global completeness or availability."));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (EventCatalogNode node in catalog.Nodes)
        {
            if (!Enum.IsDefined(node.Type)) errors.Add(new(EventCatalogGenerationCodes.InvalidCatalog, "$.nodes", "Unsupported event-catalog node type."));
            if (!ids.Add(node.Id)) errors.Add(new(EventCatalogGenerationCodes.DuplicateNodeId, "$.nodes", "Duplicate node ID."));
            if (node.Id.Length > maximumMetadataStringLength || node.LogicalId.Length > maximumMetadataStringLength)
                errors.Add(new(EventCatalogGenerationCodes.BoundExceeded, "$.nodes", "Node identity exceeds the configured metadata bound."));
        }

        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (EventCatalogEdge edge in catalog.Relationships)
        {
            if (!Enum.IsDefined(edge.Type)) errors.Add(new(EventCatalogGenerationCodes.UnsupportedRelationship, "$.relationships", "Unsupported relationship type."));
            if (!edgeIds.Add(edge.Id)) errors.Add(new(EventCatalogGenerationCodes.DuplicateEdgeId, "$.relationships", "Duplicate relationship ID."));
            bool endpointsExist = ids.Contains(edge.SourceNodeId) && ids.Contains(edge.TargetNodeId);
            if (!endpointsExist)
                errors.Add(new(EventCatalogGenerationCodes.UnknownRelationshipEndpoint, "$.relationships", "Relationship endpoint does not resolve to an existing node."));
            else
                ValidateDirection(edge, catalog.Nodes, errors);
        }

        if (!catalog.Nodes.Select(static x => (x.Id, x.Type)).SequenceEqual(graph.Nodes.Select(static x => (x.Id, x.Type))))
            errors.Add(new(EventCatalogGenerationCodes.InvalidCatalog, "$.graph.nodes", "Relationship graph nodes must be derived from the canonical catalog node sequence."));
        if (!catalog.Relationships.SequenceEqual(graph.Edges))
            errors.Add(new(EventCatalogGenerationCodes.InvalidCatalog, "$.graph.edges", "Relationship graph edges must be derived from the canonical catalog relationship sequence."));
        if (catalog.Identity != graph.CatalogIdentity)
            errors.Add(new(EventCatalogGenerationCodes.InvalidCatalog, "$.graph.catalogIdentity", "Relationship graph catalog identity must match the event catalog."));

        return new(errors.OrderBy(static x => x.Path, StringComparer.Ordinal).ThenBy(static x => x.Code, StringComparer.Ordinal).ToArray());
    }

    private static void ValidateDirection(EventCatalogEdge edge, IReadOnlyList<EventCatalogNode> nodes, List<EventCatalogValidationError> errors)
    {
        EventCatalogNodeType source = nodes.First(x => x.Id == edge.SourceNodeId).Type;
        EventCatalogNodeType target = nodes.First(x => x.Id == edge.TargetNodeId).Type;
        bool valid = edge.Type switch
        {
            EventCatalogRelationshipType.Publishes => source == EventCatalogNodeType.Producer && target == EventCatalogNodeType.MessageContract,
            EventCatalogRelationshipType.Consumes => source == EventCatalogNodeType.Consumer && target == EventCatalogNodeType.MessageContract,
            EventCatalogRelationshipType.UsesChannel => source is EventCatalogNodeType.Producer or EventCatalogNodeType.Consumer && target == EventCatalogNodeType.Channel,
            EventCatalogRelationshipType.UsesTransport => source is EventCatalogNodeType.Producer or EventCatalogNodeType.Consumer or EventCatalogNodeType.Channel && target == EventCatalogNodeType.Transport,
            EventCatalogRelationshipType.StartsSaga or EventCatalogRelationshipType.ContinuesSaga or EventCatalogRelationshipType.TimesOutSaga or EventCatalogRelationshipType.CompensatesSaga or EventCatalogRelationshipType.CompletesSaga or EventCatalogRelationshipType.FailsSaga => source == EventCatalogNodeType.MessageContract && target == EventCatalogNodeType.Saga,
            EventCatalogRelationshipType.Replaces or EventCatalogRelationshipType.UpcastsTo => source == EventCatalogNodeType.MessageContract && target == EventCatalogNodeType.MessageContract,
            _ => false
        };
        if (!valid) errors.Add(new(EventCatalogGenerationCodes.InvalidRelationshipDirection, "$.relationships", "Relationship source/target node types are invalid for the governed relationship type."));
    }
}
