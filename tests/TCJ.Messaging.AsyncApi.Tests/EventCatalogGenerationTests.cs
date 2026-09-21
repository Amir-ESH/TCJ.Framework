using System.Globalization;
using System.Text;
using System.Text.Json;
using TCJ.Messaging.Contracts;

namespace TCJ.Messaging.AsyncApi.Tests;

public sealed class EventCatalogGenerationTests
{
    [Fact]
    public void Valid_catalog_generates_versioned_catalog_and_graph_from_same_semantics()
    {
        MessagingCatalog catalog = MessagingCatalogTestData.CreateValidCatalog();
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        EventCatalogGenerationResult result = EventCatalogGenerator.Generate(catalog, resolved);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Catalog);
        Assert.NotNull(result.Graph);
        Assert.Equal("1.0", result.Catalog!.SchemaVersion);
        Assert.Equal("1.0", result.Graph!.SchemaVersion);
        Assert.Equal(EventCatalogCompleteness.DeclaredMetadataOnly, result.Catalog.Scope.Completeness);
        Assert.False(result.Catalog.Scope.RuntimeDiscovery);
        Assert.False(result.Catalog.Scope.OrganizationWide);
        Assert.False(result.Catalog.Scope.RuntimeAvailability);
        Assert.Equal(result.Catalog.Nodes.Select(static x => (x.Id, x.Type)), result.Graph.Nodes.Select(static x => (x.Id, x.Type)));
        Assert.Equal(result.Catalog.Relationships, result.Graph.Edges);
    }

    [Fact]
    public void Required_node_types_and_governed_message_identity_are_emitted()
    {
        MessagingCatalog catalog = MessagingCatalogTestData.CreateValidCatalog();
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        EventCatalogGenerationResult result = EventCatalogGenerator.Generate(catalog, resolved);

        Assert.True(result.IsValid);
        EventCatalogNode[] nodes = result.Catalog!.Nodes.ToArray();
        Assert.Contains(nodes, static x => x.Type == EventCatalogNodeType.Application);
        Assert.Contains(nodes, static x => x.Type == EventCatalogNodeType.Producer);
        Assert.Contains(nodes, static x => x.Type == EventCatalogNodeType.Consumer);
        Assert.Contains(nodes, static x => x.Type == EventCatalogNodeType.MessageContract);
        Assert.Contains(nodes, static x => x.Type == EventCatalogNodeType.Channel);
        Assert.Contains(nodes, static x => x.Type == EventCatalogNodeType.Transport);
        EventCatalogNode message = Assert.Single(nodes.Where(static x => x.Type == EventCatalogNodeType.MessageContract));
        Assert.Equal("orders.created", message.MessageType);
        Assert.Equal(1, message.MessageVersion);
        Assert.Equal(resolved.Contracts[0].SchemaFingerprint.Value, message.SchemaFingerprint);
        Assert.DoesNotContain("TCJ.Messaging.AsyncApi.Tests", message.LogicalId, StringComparison.Ordinal);
    }

    [Fact]
    public void Publishing_consuming_channel_and_transport_relationships_are_canonical()
    {
        MessagingCatalog catalog = MessagingCatalogTestData.CreateValidCatalog();
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        EventCatalogGenerationResult result = EventCatalogGenerator.Generate(catalog, resolved);

        Assert.True(result.IsValid);
        Assert.Contains(result.Catalog!.Relationships, static x => x.Type == EventCatalogRelationshipType.Publishes);
        Assert.Contains(result.Catalog.Relationships, static x => x.Type == EventCatalogRelationshipType.Consumes);
        Assert.Equal(2, result.Catalog.Relationships.Count(static x => x.Type == EventCatalogRelationshipType.UsesChannel));
        Assert.Equal(3, result.Catalog.Relationships.Count(static x => x.Type == EventCatalogRelationshipType.UsesTransport));
        Assert.All(result.Catalog.Relationships, edge =>
        {
            Assert.Contains(result.Catalog.Nodes, node => node.Id == edge.SourceNodeId);
            Assert.Contains(result.Catalog.Nodes, node => node.Id == edge.TargetNodeId);
        });
    }

    [Fact]
    public void Saga_definition_is_explicit_and_runtime_saga_data_is_absent()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with
        {
            Relationships =
            [
                new MessagingRelationship
                {
                    Id = "starts-order-saga",
                    Kind = MessagingRelationshipKind.StartsSaga,
                    Source = new MessagingEntityReference { Kind = MessagingEntityKind.Producer, Id = baseline.Producers[0].Id },
                    Target = new MessagingEntityReference { Kind = MessagingEntityKind.Channel, Id = baseline.Channels[0].Id },
                    Saga = new MessagingSagaReference { DefinitionId = "order-saga", DefinitionVersion = 2 }
                }
            ]
        };
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        EventCatalogGenerationResult result = EventCatalogGenerator.Generate(catalog, resolved);

        Assert.True(result.IsValid);
        EventCatalogNode saga = Assert.Single(result.Catalog!.Nodes.Where(static x => x.Type == EventCatalogNodeType.Saga));
        Assert.Equal("order-saga", saga.LogicalId);
        Assert.Equal(2, saga.SagaDefinitionVersion);
        Assert.Contains(result.Catalog.Relationships, static x => x.Type == EventCatalogRelationshipType.StartsSaga);
        string json = Encoding.UTF8.GetString(result.EventCatalogUtf8Json.Span);
        Assert.DoesNotContain("instanceId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("correlation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sagaState", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("database", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Replacement_edge_is_emitted_only_from_governed_replacement_evidence()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with
        {
            Producers =
            [
                baseline.Producers[0],
                baseline.Producers[0] with
                {
                    Id = "orders-publisher-v2",
                    Message = new MessagingMessageReference { Type = "orders.created", Version = 2 }
                }
            ],
            Channels =
            [
                baseline.Channels[0] with
                {
                    Messages =
                    [
                        new MessagingMessageReference { Type = "orders.created", Version = 1 },
                        new MessagingMessageReference { Type = "orders.created", Version = 2 }
                    ]
                }
            ]
        };
        using var fixture = new GovernedContractTestFixture();
        fixture.AddContract("orders.created", 1, includeExample: false, deprecated: true);
        fixture.AddContract("orders.created", 2, includeExample: false);
        fixture.WriteManifest();
        GovernedContractResolutionResult resolved = GovernedContractResolver.Resolve(catalog, new GovernedContractResolutionOptions { ArtifactRoot = fixture.Root });

        EventCatalogGenerationResult result = EventCatalogGenerator.Generate(catalog, resolved);

        Assert.True(result.IsValid);
        EventCatalogEdge replacement = Assert.Single(result.Catalog!.Relationships.Where(static x => x.Type == EventCatalogRelationshipType.Replaces));
        EventCatalogNode source = result.Catalog.Nodes.Single(x => x.Id == replacement.SourceNodeId);
        EventCatalogNode target = result.Catalog.Nodes.Single(x => x.Id == replacement.TargetNodeId);
        Assert.Equal(2, source.MessageVersion);
        Assert.Equal(1, target.MessageVersion);
        Assert.DoesNotContain(result.Catalog.Relationships, static x => x.Type == EventCatalogRelationshipType.UpcastsTo);
    }

    [Fact]
    public void Higher_version_without_replacement_evidence_does_not_create_compatibility_edges()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with
        {
            Producers =
            [
                baseline.Producers[0],
                baseline.Producers[0] with { Id = "orders-publisher-v2", Message = new MessagingMessageReference { Type = "orders.created", Version = 2 } }
            ],
            Channels =
            [
                baseline.Channels[0] with { Messages = [new MessagingMessageReference { Type = "orders.created", Version = 1 }, new MessagingMessageReference { Type = "orders.created", Version = 2 }] }
            ]
        };
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        EventCatalogGenerationResult result = EventCatalogGenerator.Generate(catalog, resolved);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Catalog!.Relationships, static x => x.Type is EventCatalogRelationshipType.Replaces or EventCatalogRelationshipType.UpcastsTo);
    }

    [Fact]
    public void Dynamic_destination_remains_explicit_without_concrete_channel_edge()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingDynamicDestination dynamic = new() { NamingStrategyId = "tenant-topic", Pattern = "orders.{tenant}.created", Description = "Synthetic logical pattern." };
        MessagingCatalog catalog = baseline with
        {
            Channels = [baseline.Channels[0] with { DynamicDestination = dynamic }],
            Producers = [baseline.Producers[0] with { DynamicDestination = dynamic }]
        };
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        EventCatalogGenerationResult result = EventCatalogGenerator.Generate(catalog, resolved);

        Assert.True(result.IsValid);
        EventCatalogNode producer = result.Catalog!.Nodes.Single(static x => x.Type == EventCatalogNodeType.Producer);
        EventCatalogNode channel = result.Catalog.Nodes.Single(static x => x.Type == EventCatalogNodeType.Channel);
        Assert.True(producer.DynamicDestination);
        Assert.True(channel.DynamicDestination);
        Assert.Null(channel.Address);
        Assert.Equal("tenant-topic", producer.DynamicNamingStrategyId);
        Assert.Equal("orders.{tenant}.created", producer.DynamicPattern);
        Assert.DoesNotContain(result.Catalog.Relationships, static x => x.Type == EventCatalogRelationshipType.UsesChannel);
    }

    [Fact]
    public void Synthetic_fixture_marker_is_explicit()
    {
        MessagingCatalog catalog = MessagingCatalogTestData.CreateValidCatalog();
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        EventCatalogGenerationResult result = EventCatalogGenerator.Generate(catalog, resolved, new EventCatalogGenerationOptions { Synthetic = true });

        Assert.True(result.IsValid);
        Assert.True(result.Catalog!.Scope.Synthetic);
        using JsonDocument json = JsonDocument.Parse(result.EventCatalogUtf8Json);
        Assert.True(json.RootElement.GetProperty("scope").GetProperty("synthetic").GetBoolean());
    }

    [Fact]
    public void Secret_bearing_metadata_fails_without_echoing_secret_value()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog catalog = baseline with { Application = baseline.Application with { Description = "connectionString=super-secret" } };
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        EventCatalogGenerationResult result = EventCatalogGenerator.Generate(catalog, resolved);

        Assert.False(result.IsValid);
        Assert.Equal(0, result.EventCatalogUtf8Json.Length);
        Assert.Contains(result.Errors, static x => x.Code == EventCatalogGenerationCodes.InvalidCatalog);
        Assert.DoesNotContain("super-secret", string.Join('|', result.Errors.Select(static x => x.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public void Node_and_edge_bounds_fail_closed()
    {
        MessagingCatalog catalog = MessagingCatalogTestData.CreateValidCatalog();
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        EventCatalogGenerationResult nodeBound = EventCatalogGenerator.Generate(catalog, resolved, new EventCatalogGenerationOptions { MaximumNodes = 1 });
        EventCatalogGenerationResult edgeBound = EventCatalogGenerator.Generate(catalog, resolved, new EventCatalogGenerationOptions { MaximumEdges = 1 });

        Assert.False(nodeBound.IsValid);
        Assert.Contains(nodeBound.Errors, static x => x.Code == EventCatalogGenerationCodes.BoundExceeded);
        Assert.False(edgeBound.IsValid);
        Assert.Contains(edgeBound.Errors, static x => x.Code == EventCatalogGenerationCodes.BoundExceeded);
    }

    [Fact]
    public void Reordered_input_and_repeated_generation_are_byte_identical_and_culture_independent()
    {
        MessagingCatalog baseline = MessagingCatalogTestData.CreateValidCatalog();
        MessagingCatalog reordered = baseline with
        {
            Transports = baseline.Transports.Reverse().ToArray(),
            Producers = baseline.Producers.Reverse().ToArray(),
            Consumers = baseline.Consumers.Reverse().ToArray(),
            Channels = baseline.Channels.Reverse().ToArray(),
            Relationships = baseline.Relationships.Reverse().ToArray()
        };
        using GovernedContractTestFixture fixture = Resolve(baseline, out GovernedContractResolutionResult resolved);
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
            EventCatalogGenerationResult first = EventCatalogGenerator.Generate(baseline, resolved);
            EventCatalogGenerationResult second = EventCatalogGenerator.Generate(reordered, resolved);
            EventCatalogGenerationResult third = EventCatalogGenerator.Generate(baseline, resolved);

            Assert.True(first.IsValid);
            Assert.True(second.IsValid);
            Assert.True(third.IsValid);
            Assert.Equal(first.EventCatalogUtf8Json.ToArray(), second.EventCatalogUtf8Json.ToArray());
            Assert.Equal(first.RelationshipGraphUtf8Json.ToArray(), second.RelationshipGraphUtf8Json.ToArray());
            Assert.Equal(first.EventCatalogUtf8Json.ToArray(), third.EventCatalogUtf8Json.ToArray());
            Assert.Equal(first.RelationshipGraphUtf8Json.ToArray(), third.RelationshipGraphUtf8Json.ToArray());
            Assert.Equal((byte)'\n', first.EventCatalogUtf8Json.Span[^1]);
            Assert.Equal((byte)'\n', first.RelationshipGraphUtf8Json.Span[^1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Referenced_and_bundled_asyncapi_modes_do_not_change_catalog_semantics()
    {
        MessagingCatalog catalog = MessagingCatalogTestData.CreateValidCatalog();
        using GovernedContractTestFixture fixture = Resolve(catalog, out GovernedContractResolutionResult resolved);

        AsyncApiGenerationResult referenced = AsyncApiDocumentGenerator.Generate(catalog, resolved, new AsyncApiGenerationOptions { SchemaMode = AsyncApiSchemaMode.Referenced });
        AsyncApiGenerationResult bundled = AsyncApiDocumentGenerator.Generate(catalog, resolved, new AsyncApiGenerationOptions { SchemaMode = AsyncApiSchemaMode.Bundled });
        EventCatalogGenerationResult first = EventCatalogGenerator.Generate(catalog, resolved);
        EventCatalogGenerationResult second = EventCatalogGenerator.Generate(catalog, resolved);

        Assert.True(referenced.IsValid);
        Assert.True(bundled.IsValid);
        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Equal(first.EventCatalogUtf8Json.ToArray(), second.EventCatalogUtf8Json.ToArray());
        Assert.Equal(first.RelationshipGraphUtf8Json.ToArray(), second.RelationshipGraphUtf8Json.ToArray());
    }

    [Fact]
    public void Schema_validator_rejects_unsupported_version_missing_identity_invalid_node_invalid_relationship_and_malformed_json()
    {
        byte[] unsupportedVersion = Encoding.UTF8.GetBytes("""{"schemaVersion":"2.0","identity":{"applicationName":"a","applicationVersion":"1"},"scope":{"completeness":"DeclaredMetadataOnly","runtimeDiscovery":false,"organizationWide":false,"runtimeAvailability":false,"synthetic":false},"nodes":[],"relationships":[]}""");
        byte[] missingIdentity = Encoding.UTF8.GetBytes("""{"schemaVersion":"1.0","scope":{"completeness":"DeclaredMetadataOnly","runtimeDiscovery":false,"organizationWide":false,"runtimeAvailability":false,"synthetic":false},"nodes":[],"relationships":[]}""");
        byte[] invalidNode = Encoding.UTF8.GetBytes("""{"schemaVersion":"1.0","identity":{"applicationName":"a","applicationVersion":"1"},"scope":{"completeness":"DeclaredMetadataOnly","runtimeDiscovery":false,"organizationWide":false,"runtimeAvailability":false,"synthetic":false},"nodes":[{"id":"x","type":"Unknown","logicalId":"x"}],"relationships":[]}""");
        byte[] invalidRelationship = Encoding.UTF8.GetBytes("""{"schemaVersion":"1.0","identity":{"applicationName":"a","applicationVersion":"1"},"scope":{"completeness":"DeclaredMetadataOnly","runtimeDiscovery":false,"organizationWide":false,"runtimeAvailability":false,"synthetic":false},"nodes":[],"relationships":[{"id":"x","type":"Unknown","sourceNodeId":"a","targetNodeId":"b"}]}""");

        Assert.Contains(EventCatalogSchemaValidator.ValidateEventCatalog(unsupportedVersion).Errors, static x => x.Code == EventCatalogSchemaValidationCodes.UnsupportedSchemaVersion);
        Assert.Contains(EventCatalogSchemaValidator.ValidateEventCatalog(missingIdentity).Errors, static x => x.Code == EventCatalogSchemaValidationCodes.MissingIdentity);
        Assert.Contains(EventCatalogSchemaValidator.ValidateEventCatalog(invalidNode).Errors, static x => x.Code == EventCatalogSchemaValidationCodes.InvalidNode);
        Assert.Contains(EventCatalogSchemaValidator.ValidateEventCatalog(invalidRelationship).Errors, static x => x.Code == EventCatalogSchemaValidationCodes.InvalidRelationship);
        Assert.Contains(EventCatalogSchemaValidator.ValidateEventCatalog("{"u8).Errors, static x => x.Code == EventCatalogSchemaValidationCodes.MalformedJson);
    }

    [Fact]
    public void Schema_validator_enforces_catalog_bounds_without_unbounded_graph_traversal()
    {
        string nodes = string.Join(',', Enumerable.Repeat("{\"id\":\"a\",\"type\":\"Application\",\"logicalId\":\"a\"}", 2049));
        byte[] json = Encoding.UTF8.GetBytes("{\"schemaVersion\":\"1.0\",\"identity\":{\"applicationName\":\"a\",\"applicationVersion\":\"1\"},\"scope\":{\"completeness\":\"DeclaredMetadataOnly\",\"runtimeDiscovery\":false,\"organizationWide\":false,\"runtimeAvailability\":false,\"synthetic\":true},\"nodes\":[" + nodes + "],\"relationships\":[]}");

        EventCatalogSchemaValidationResult result = EventCatalogSchemaValidator.ValidateEventCatalog(json);

        Assert.Contains(result.Errors, static x => x.Code == EventCatalogSchemaValidationCodes.BoundExceeded);
    }

    [Fact]
    public void Event_catalog_and_graph_validator_reject_dangling_and_invalid_direction_edges()
    {
        EventCatalog catalog = new()
        {
            SchemaVersion = EventCatalog.CurrentSchemaVersion,
            Identity = new EventCatalogIdentity { ApplicationName = "sample", ApplicationVersion = "1.0.0" },
            Scope = new EventCatalogScope { Completeness = EventCatalogCompleteness.DeclaredMetadataOnly },
            Nodes =
            [
                new EventCatalogNode { Id = "producer-a", Type = EventCatalogNodeType.Producer, LogicalId = "a" },
                new EventCatalogNode { Id = "transport-b", Type = EventCatalogNodeType.Transport, LogicalId = "b" }
            ],
            Relationships =
            [
                new EventCatalogEdge { Id = "edge-a", Type = EventCatalogRelationshipType.Publishes, SourceNodeId = "producer-a", TargetNodeId = "missing" }
            ]
        };
        RelationshipGraph graph = new()
        {
            SchemaVersion = RelationshipGraph.CurrentSchemaVersion,
            CatalogIdentity = catalog.Identity,
            Nodes = catalog.Nodes.Select(static x => new RelationshipGraphNode { Id = x.Id, Type = x.Type }).ToArray(),
            Edges = catalog.Relationships
        };

        EventCatalogValidationResult result = EventCatalogValidator.Validate(catalog, graph);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static x => x.Code == EventCatalogGenerationCodes.UnknownRelationshipEndpoint);
    }

    [Fact]
    public void Graph_cycles_do_not_break_bounded_structural_validation()
    {
        EventCatalogNode first = new() { Id = "message-a", Type = EventCatalogNodeType.MessageContract, LogicalId = "sample:v1", MessageType = "sample", MessageVersion = 1 };
        EventCatalogNode second = new() { Id = "message-b", Type = EventCatalogNodeType.MessageContract, LogicalId = "sample:v2", MessageType = "sample", MessageVersion = 2 };
        EventCatalogEdge a = new() { Id = "edge-a", Type = EventCatalogRelationshipType.Replaces, SourceNodeId = first.Id, TargetNodeId = second.Id };
        EventCatalogEdge b = new() { Id = "edge-b", Type = EventCatalogRelationshipType.Replaces, SourceNodeId = second.Id, TargetNodeId = first.Id };
        EventCatalog catalog = new()
        {
            SchemaVersion = EventCatalog.CurrentSchemaVersion,
            Identity = new EventCatalogIdentity { ApplicationName = "sample", ApplicationVersion = "1" },
            Scope = new EventCatalogScope { Completeness = EventCatalogCompleteness.DeclaredMetadataOnly },
            Nodes = [first, second],
            Relationships = [a, b]
        };
        RelationshipGraph graph = new()
        {
            SchemaVersion = RelationshipGraph.CurrentSchemaVersion,
            CatalogIdentity = catalog.Identity,
            Nodes = catalog.Nodes.Select(static x => new RelationshipGraphNode { Id = x.Id, Type = x.Type }).ToArray(),
            Edges = catalog.Relationships
        };

        EventCatalogValidationResult result = EventCatalogValidator.Validate(catalog, graph);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Canonical_serializer_matches_intentional_golden_fixtures()
    {
        EventCatalog catalog = new()
        {
            SchemaVersion = EventCatalog.CurrentSchemaVersion,
            Identity = new EventCatalogIdentity { ApplicationName = "sample-app", ApplicationVersion = "1.0.0" },
            Scope = new EventCatalogScope { Completeness = EventCatalogCompleteness.DeclaredMetadataOnly, Synthetic = true },
            Nodes = [new EventCatalogNode { Id = "application-sample", Type = EventCatalogNodeType.Application, LogicalId = "sample-app", Version = "1.0.0" }]
        };
        RelationshipGraph graph = new()
        {
            SchemaVersion = RelationshipGraph.CurrentSchemaVersion,
            CatalogIdentity = catalog.Identity,
            Nodes = [new RelationshipGraphNode { Id = "application-sample", Type = EventCatalogNodeType.Application }]
        };

        byte[] actualCatalog = EventCatalogJson.Serialize(catalog);
        byte[] actualGraph = EventCatalogJson.Serialize(graph);
        byte[] expectedCatalog = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "minimal-event-catalog.json"));
        byte[] expectedGraph = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "minimal-catalog-graph.json"));

        Assert.Equal(expectedCatalog, actualCatalog);
        Assert.Equal(expectedGraph, actualGraph);
    }

    private static GovernedContractTestFixture Resolve(MessagingCatalog catalog, out GovernedContractResolutionResult resolved)
    {
        var fixture = new GovernedContractTestFixture();
        foreach ((string type, int version) in catalog.Producers.Select(static x => (x.Message.Type, x.Message.Version))
                     .Concat(catalog.Consumers.SelectMany(static x => x.AcceptedMessageVersions.Select(version => (x.MessageType, version))))
                     .Concat(catalog.Channels.SelectMany(static x => x.Messages.Select(message => (message.Type, message.Version))))
                     .Distinct())
            fixture.AddContract(type, version, includeExample: false);
        fixture.WriteManifest();
        resolved = GovernedContractResolver.Resolve(catalog, new GovernedContractResolutionOptions { ArtifactRoot = fixture.Root });
        return fixture;
    }
}
