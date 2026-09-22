using System.Globalization;
using System.Text;

namespace TCJ.Messaging.AsyncApi.Tests;

public sealed class MarkdownCatalogGenerationTests
{
    [Fact]
    public void Generates_index_and_stable_message_pages_from_normalized_catalog()
    {
        EventCatalog catalog = CreateCatalog();

        MarkdownCatalogGenerationResult result = MarkdownCatalogGenerator.Generate(catalog);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(result.Files, static x => x.RelativePath == "catalog/index.md");
        MarkdownCatalogFile message = Assert.Single(result.Files.Where(static x => x.RelativePath == "catalog/messages/orders-created-v1.md"));
        string index = Text(result.Files.Single(static x => x.RelativePath == "catalog/index.md"));
        string detail = Text(message);
        Assert.Contains("## Overview", index, StringComparison.Ordinal);
        Assert.Contains("declared messaging topology", index, StringComparison.Ordinal);
        Assert.Contains("## Messages", index, StringComparison.Ordinal);
        Assert.Contains("## Producers", index, StringComparison.Ordinal);
        Assert.Contains("## Consumers", index, StringComparison.Ordinal);
        Assert.Contains("## Channels", index, StringComparison.Ordinal);
        Assert.Contains("## Transports", index, StringComparison.Ordinal);
        Assert.Contains("orders.created", detail, StringComparison.Ordinal);
        Assert.Contains("sha256:abc123", detail, StringComparison.Ordinal);
        Assert.Contains("Owner A", detail, StringComparison.Ordinal);
        Assert.Contains("Internal", detail, StringComparison.Ordinal);
        Assert.EndsWith("\n", index, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', index);
    }

    [Fact]
    public void Minimal_synthetic_catalog_matches_intentional_golden_markdown()
    {
        EventCatalog catalog = new()
        {
            SchemaVersion = EventCatalog.CurrentSchemaVersion,
            Identity = new EventCatalogIdentity { ApplicationName = "sample-app", ApplicationVersion = "1.0.0" },
            Scope = new EventCatalogScope { Completeness = EventCatalogCompleteness.DeclaredMetadataOnly, Synthetic = true },
            Nodes = [new EventCatalogNode { Id = "application-sample", Type = EventCatalogNodeType.Application, LogicalId = "sample-app", Version = "1.0.0" }]
        };

        MarkdownCatalogGenerationResult result = MarkdownCatalogGenerator.Generate(catalog);

        Assert.True(result.IsValid);
        byte[] expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "minimal-markdown-index.md"));
        Assert.Equal(expected, result.Files.Single(static x => x.RelativePath == "catalog/index.md").Utf8Content.ToArray());
    }

    [Fact]
    public void Rendering_uses_declared_inbox_outbox_dynamic_destination_and_saga_semantics_only()
    {
        EventCatalog baseline = CreateCatalog();
        EventCatalogNode producer = baseline.Nodes.Single(static x => x.Type == EventCatalogNodeType.Producer) with
        {
            OutboxEnabled = true,
            DynamicDestination = true,
            DynamicNamingStrategyId = "tenant-topic",
            DynamicPattern = "orders.{tenant}.created"
        };
        EventCatalogNode consumer = baseline.Nodes.Single(static x => x.Type == EventCatalogNodeType.Consumer) with { InboxEnabled = true };
        EventCatalogNode message = baseline.Nodes.Single(static x => x.Type == EventCatalogNodeType.MessageContract);
        var saga = new EventCatalogNode { Id = "saga-order", Type = EventCatalogNodeType.Saga, LogicalId = "order-saga", SagaDefinitionVersion = 1 };
        EventCatalog catalog = baseline with
        {
            Scope = baseline.Scope with { Synthetic = true },
            Nodes = baseline.Nodes.Select(n => n.Type == EventCatalogNodeType.Producer ? producer : n.Type == EventCatalogNodeType.Consumer ? consumer : n).Append(saga).ToArray(),
            Relationships = baseline.Relationships.Append(new EventCatalogEdge { Id = "edge-saga", Type = EventCatalogRelationshipType.StartsSaga, SourceNodeId = message.Id, TargetNodeId = saga.Id }).ToArray()
        };

        MarkdownCatalogGenerationResult result = MarkdownCatalogGenerator.Generate(catalog);

        Assert.True(result.IsValid);
        string markdown = Text(result.Files.Single(static x => x.RelativePath.EndsWith("orders-created-v1.md", StringComparison.Ordinal)));
        Assert.Contains("Outbox: Enabled (durable at-least-once publication; not global exactly-once)", markdown, StringComparison.Ordinal);
        Assert.Contains("Inbox: Enabled", markdown, StringComparison.Ordinal);
        Assert.Contains("Dynamic destination: Yes", markdown, StringComparison.Ordinal);
        Assert.Contains("tenant-topic", markdown, StringComparison.Ordinal);
        Assert.Contains("order-saga", markdown, StringComparison.Ordinal);
        Assert.Contains("Synthetic example only", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Saga instance", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("correlation", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exactly-once", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reordered_input_and_changed_culture_produce_byte_identical_markdown()
    {
        EventCatalog baseline = CreateCatalog();
        EventCatalog reordered = baseline with { Nodes = baseline.Nodes.Reverse().ToArray(), Relationships = baseline.Relationships.Reverse().ToArray() };
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            MarkdownCatalogGenerationResult first = MarkdownCatalogGenerator.Generate(baseline);
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
            MarkdownCatalogGenerationResult second = MarkdownCatalogGenerator.Generate(reordered);

            Assert.True(first.IsValid);
            Assert.True(second.IsValid);
            Assert.Equal(first.Files.Select(static x => x.RelativePath), second.Files.Select(static x => x.RelativePath));
            Assert.Equal(first.Files.Select(static x => x.Utf8Content.ToArray()), second.Files.Select(static x => x.Utf8Content.ToArray()), ByteArrayComparer.Instance);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData("orders/created")]
    [InlineData("orders\\created")]
    [InlineData("../orders.created")]
    [InlineData("ORDERS.CREATED")]
    public void Message_file_names_are_safe_and_derived_from_logical_identity(string messageType)
    {
        EventCatalog baseline = CreateCatalog();
        EventCatalogNode message = baseline.Nodes.Single(static x => x.Type == EventCatalogNodeType.MessageContract) with { MessageType = messageType, LogicalId = messageType + ":v1" };
        EventCatalog catalog = ReplaceMessage(baseline, message);

        MarkdownCatalogGenerationResult result = MarkdownCatalogGenerator.Generate(catalog);

        Assert.True(result.IsValid);
        MarkdownCatalogFile page = Assert.Single(result.Files.Where(static x => x.RelativePath.StartsWith("catalog/messages/", StringComparison.Ordinal)));
        Assert.DoesNotContain("..", page.RelativePath, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', page.RelativePath);
        Assert.DoesNotContain("TCJ.", page.RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public void File_name_collision_fails_closed()
    {
        EventCatalog baseline = CreateCatalog();
        EventCatalogNode first = baseline.Nodes.Single(static x => x.Type == EventCatalogNodeType.MessageContract);
        EventCatalogNode second = first with { Id = "message-2", LogicalId = "orders-created:v1", MessageType = "orders-created" };
        EventCatalog catalog = baseline with { Nodes = baseline.Nodes.Append(second).ToArray() };

        MarkdownCatalogGenerationResult result = MarkdownCatalogGenerator.Generate(catalog);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static x => x.Code == MarkdownCatalogGenerationCodes.FileNameCollision);
    }

    [Fact]
    public void Markdown_control_characters_are_escaped_and_secret_values_are_not_invented()
    {
        EventCatalog baseline = CreateCatalog();
        EventCatalogNode message = baseline.Nodes.Single(static x => x.Type == EventCatalogNodeType.MessageContract) with { Owner = "A|B [team] <ops> `x`" };
        EventCatalog catalog = ReplaceMessage(baseline, message);

        MarkdownCatalogGenerationResult result = MarkdownCatalogGenerator.Generate(catalog);

        Assert.True(result.IsValid);
        string markdown = Text(result.Files.Single(static x => x.RelativePath.EndsWith("orders-created-v1.md", StringComparison.Ordinal)));
        Assert.Contains("A\\|B \\[team\\] &lt;ops&gt; \\`x\\`", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("password=", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connectionString=", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Governed_schema_and_example_paths_are_rendered_as_relative_links()
    {
        EventCatalog baseline = CreateCatalog();
        EventCatalogNode message = baseline.Nodes.Single(static x => x.Type == EventCatalogNodeType.MessageContract) with
        {
            SchemaRelativePath = "contracts/orders.created/v1/schema.json",
            ExampleRelativePaths = ["contracts/orders.created/v1/examples/minimal.json"]
        };
        EventCatalog catalog = ReplaceMessage(baseline, message);

        MarkdownCatalogGenerationResult result = MarkdownCatalogGenerator.Generate(catalog);

        Assert.True(result.IsValid);
        string markdown = Text(result.Files.Single(static x => x.RelativePath.EndsWith("orders-created-v1.md", StringComparison.Ordinal)));
        Assert.Contains("../../contracts/orders.created/v1/schema.json", markdown, StringComparison.Ordinal);
        Assert.Contains("../../contracts/orders.created/v1/examples/minimal.json", markdown, StringComparison.Ordinal);
    }

    private static EventCatalog ReplaceMessage(EventCatalog catalog, EventCatalogNode replacement) => catalog with
    {
        Nodes = catalog.Nodes.Select(n => n.Type == EventCatalogNodeType.MessageContract ? replacement : n).ToArray()
    };

    private static EventCatalog CreateCatalog()
    {
        var app = new EventCatalogNode { Id = "application-app", Type = EventCatalogNodeType.Application, LogicalId = "app", Version = "1.0.0" };
        var producer = new EventCatalogNode { Id = "producer-orders", Type = EventCatalogNodeType.Producer, LogicalId = "orders-producer", Component = "api", Owner = "Owner A", Lifecycle = MessagingLifecycle.Published, RetryOwner = "Outbox" };
        var consumer = new EventCatalogNode { Id = "consumer-orders", Type = EventCatalogNodeType.Consumer, LogicalId = "orders-consumer", Component = "worker", Owner = "Owner B", Lifecycle = MessagingLifecycle.Published, RetryOwner = "Inbox", OrderingSemantics = MessagingOrderingSemantics.PerPartition, DeadLetterSemantics = MessagingDeadLetterSemantics.Native };
        var message = new EventCatalogNode { Id = "message-orders-v1", Type = EventCatalogNodeType.MessageContract, LogicalId = "orders.created:v1", MessageType = "orders.created", MessageVersion = 1, Owner = "Owner A", Lifecycle = MessagingLifecycle.Published, SchemaFingerprintAlgorithm = "sha256", SchemaFingerprint = "abc123", CompatibilityMode = "Backward", DataClassifications = ["Internal"] };
        var channel = new EventCatalogNode { Id = "channel-orders", Type = EventCatalogNodeType.Channel, LogicalId = "orders", Address = "orders", Lifecycle = MessagingLifecycle.Published };
        var transport = new EventCatalogNode { Id = "transport-kafka", Type = EventCatalogNodeType.Transport, LogicalId = "kafka", TransportKind = "Kafka", Protocol = "kafka", DeliverySemantics = MessagingDeliverySemantics.AtLeastOnce, OrderingSemantics = MessagingOrderingSemantics.PerPartition, DeadLetterSemantics = MessagingDeadLetterSemantics.Native };
        return new EventCatalog
        {
            SchemaVersion = EventCatalog.CurrentSchemaVersion,
            Identity = new EventCatalogIdentity { ApplicationName = "sample-app", ApplicationVersion = "1.0.0" },
            Scope = new EventCatalogScope { Completeness = EventCatalogCompleteness.DeclaredMetadataOnly },
            Nodes = [app, producer, consumer, message, channel, transport],
            Relationships =
            [
                new EventCatalogEdge { Id = "e1", Type = EventCatalogRelationshipType.Publishes, SourceNodeId = producer.Id, TargetNodeId = message.Id },
                new EventCatalogEdge { Id = "e2", Type = EventCatalogRelationshipType.Consumes, SourceNodeId = consumer.Id, TargetNodeId = message.Id },
                new EventCatalogEdge { Id = "e3", Type = EventCatalogRelationshipType.UsesChannel, SourceNodeId = producer.Id, TargetNodeId = channel.Id },
                new EventCatalogEdge { Id = "e4", Type = EventCatalogRelationshipType.UsesChannel, SourceNodeId = consumer.Id, TargetNodeId = channel.Id },
                new EventCatalogEdge { Id = "e5", Type = EventCatalogRelationshipType.UsesTransport, SourceNodeId = producer.Id, TargetNodeId = transport.Id },
                new EventCatalogEdge { Id = "e6", Type = EventCatalogRelationshipType.UsesTransport, SourceNodeId = consumer.Id, TargetNodeId = transport.Id },
                new EventCatalogEdge { Id = "e7", Type = EventCatalogRelationshipType.UsesTransport, SourceNodeId = channel.Id, TargetNodeId = transport.Id }
            ]
        };
    }

    private static string Text(MarkdownCatalogFile file) => Encoding.UTF8.GetString(file.Utf8Content.Span);

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => 0;
    }
}
