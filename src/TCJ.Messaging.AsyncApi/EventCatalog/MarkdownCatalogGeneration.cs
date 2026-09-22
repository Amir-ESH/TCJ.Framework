using System.Globalization;
using System.Text;

namespace TCJ.Messaging.AsyncApi;

/// <summary>Stable diagnostics emitted while rendering deterministic Markdown catalog output.</summary>
public static class MarkdownCatalogGenerationCodes
{
    public const string InvalidCatalog = "MCG001";
    public const string FileNameCollision = "MCG002";
    public const string UnsafePath = "MCG003";
    public const string BrokenLink = "MCG004";
    public const string BoundExceeded = "MCG005";
}

/// <summary>One deterministic Markdown generation diagnostic.</summary>
public sealed record MarkdownCatalogGenerationError(string Code, string Path, string Message);

/// <summary>One generated UTF-8 Markdown file using a normalized forward-slash relative path.</summary>
public sealed record MarkdownCatalogFile
{
    public required string RelativePath { get; init; }
    public required ReadOnlyMemory<byte> Utf8Content { get; init; }
}

/// <summary>Bounded options for deterministic Markdown event-catalog rendering.</summary>
public sealed record MarkdownCatalogGenerationOptions
{
    public string CatalogRoot { get; init; } = "catalog";
    public string IndexFileName { get; init; } = "index.md";
    public string MessageDirectoryName { get; init; } = "messages";
    public int MaximumFiles { get; init; } = 4096;
    public int MaximumOutputBytes { get; init; } = 16 * 1024 * 1024;
    public bool LinkEventCatalog { get; init; } = true;
    public bool LinkRelationshipGraph { get; init; } = true;
    public bool LinkAsyncApi { get; init; }
    public string AsyncApiFileName { get; init; } = "asyncapi.json";
}

/// <summary>Result of deterministic in-memory Markdown event-catalog rendering.</summary>
public sealed class MarkdownCatalogGenerationResult
{
    internal MarkdownCatalogGenerationResult(IReadOnlyList<MarkdownCatalogFile> files, IReadOnlyList<MarkdownCatalogGenerationError> errors)
    {
        Files = files;
        Errors = errors;
    }

    public IReadOnlyList<MarkdownCatalogFile> Files { get; }
    public IReadOnlyList<MarkdownCatalogGenerationError> Errors { get; }
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Presentation-only deterministic Markdown renderer over an already-normalized event catalog.</summary>
public static class MarkdownCatalogGenerator
{
    private const string ScopeDisclaimer = "This catalog describes explicitly declared messaging topology and governed contract metadata. It does not represent live runtime discovery or prove broker/application availability.";

    public static MarkdownCatalogGenerationResult Generate(EventCatalog catalog, MarkdownCatalogGenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        options ??= new MarkdownCatalogGenerationOptions();
        var errors = new List<MarkdownCatalogGenerationError>();

        if (options.MaximumFiles <= 0 || options.MaximumOutputBytes <= 0)
            errors.Add(new(MarkdownCatalogGenerationCodes.BoundExceeded, "$", "Markdown generation bounds must be positive."));
        if (!TryNormalizeRoot(options.CatalogRoot, out string root) || !SafeSegment(options.IndexFileName) || !SafeSegment(options.MessageDirectoryName))
            errors.Add(new(MarkdownCatalogGenerationCodes.UnsafePath, "$", "Markdown output paths must be safe relative paths."));
        if (options.LinkAsyncApi && !TryNormalizeArtifactPath(options.AsyncApiFileName, out _))
            errors.Add(new(MarkdownCatalogGenerationCodes.UnsafePath, "$.asyncApiFileName", "AsyncAPI artifact path must be a safe relative path."));
        if (catalog.Scope.RuntimeDiscovery || catalog.Scope.OrganizationWide || catalog.Scope.RuntimeAvailability)
            errors.Add(new(MarkdownCatalogGenerationCodes.InvalidCatalog, "$.scope", "Markdown rendering requires the governed declared-metadata-only event-catalog scope."));
        if (errors.Count != 0) return Invalid(errors);

        EventCatalogNode[] nodes = catalog.Nodes.OrderBy(static x => x.Type.ToString(), StringComparer.Ordinal).ThenBy(static x => x.Id, StringComparer.Ordinal).ToArray();
        EventCatalogEdge[] edges = catalog.Relationships.OrderBy(static x => x.Type.ToString(), StringComparer.Ordinal).ThenBy(static x => x.SourceNodeId, StringComparer.Ordinal).ThenBy(static x => x.TargetNodeId, StringComparer.Ordinal).ThenBy(static x => x.Id, StringComparer.Ordinal).ToArray();
        Dictionary<string, EventCatalogNode> byId = nodes.ToDictionary(static x => x.Id, StringComparer.Ordinal);
        foreach (EventCatalogNode node in nodes)
        {
            if (ContainsSecret(node))
                errors.Add(new(MarkdownCatalogGenerationCodes.InvalidCatalog, "$.nodes", "Secret-bearing metadata cannot be rendered to Markdown."));
            if (!string.IsNullOrWhiteSpace(node.SchemaRelativePath) && !TryNormalizeArtifactPath(node.SchemaRelativePath, out _))
                errors.Add(new(MarkdownCatalogGenerationCodes.UnsafePath, "$.nodes", "Governed schema path is not a safe relative artifact path."));
            if (node.ExampleRelativePaths.Any(static path => !TryNormalizeArtifactPath(path, out _)))
                errors.Add(new(MarkdownCatalogGenerationCodes.UnsafePath, "$.nodes", "Governed example path is not a safe relative artifact path."));
        }
        foreach (EventCatalogEdge edge in edges)
        {
            if (!byId.ContainsKey(edge.SourceNodeId) || !byId.ContainsKey(edge.TargetNodeId))
                errors.Add(new(MarkdownCatalogGenerationCodes.InvalidCatalog, "$.relationships", "A catalog relationship references an unknown node."));
        }
        if (errors.Count != 0) return Invalid(errors);

        EventCatalogNode[] messages = nodes.Where(static x => x.Type == EventCatalogNodeType.MessageContract).OrderBy(static x => x.MessageType, StringComparer.Ordinal).ThenBy(static x => x.MessageVersion).ThenBy(static x => x.Id, StringComparer.Ordinal).ToArray();
        var messagePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (EventCatalogNode message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.MessageType) || message.MessageVersion is null)
            {
                errors.Add(new(MarkdownCatalogGenerationCodes.InvalidCatalog, "$.nodes", "Message nodes require governed logical message type and version."));
                continue;
            }
            string name;
            try { name = NormalizeFileStem(message.MessageType) + "-v" + message.MessageVersion.Value.ToString(CultureInfo.InvariantCulture) + ".md"; }
            catch (ArgumentException)
            {
                errors.Add(new(MarkdownCatalogGenerationCodes.UnsafePath, "$.nodes", "A logical message identity cannot be normalized to a safe Markdown file name."));
                continue;
            }
            string path = root + "/" + options.MessageDirectoryName + "/" + name;
            if (used.TryGetValue(path, out string? existing) && !string.Equals(existing, message.Id, StringComparison.Ordinal))
                errors.Add(new(MarkdownCatalogGenerationCodes.FileNameCollision, path, "Distinct message identities collide on the same deterministic Markdown file name."));
            else
            {
                used[path] = message.Id;
                messagePaths[message.Id] = path;
            }
        }
        if (errors.Count != 0) return Invalid(errors);

        var files = new List<MarkdownCatalogFile>();
        string indexPath = root + "/" + options.IndexFileName;
        files.Add(File(indexPath, RenderIndex(catalog, nodes, edges, messagePaths, options, root)));
        foreach (EventCatalogNode message in messages)
            files.Add(File(messagePaths[message.Id], RenderMessage(catalog, message, nodes, edges, byId, messagePaths, options, root)));

        files = files.OrderBy(static x => x.RelativePath, StringComparer.Ordinal).ToList();
        if (files.Count > options.MaximumFiles || files.Sum(static x => x.Utf8Content.Length) > options.MaximumOutputBytes)
            return Invalid([new(MarkdownCatalogGenerationCodes.BoundExceeded, "$", "Generated Markdown exceeds the configured output bounds.")]);

        ValidateGeneratedLinks(files, errors);
        return errors.Count == 0 ? new(files, []) : Invalid(errors);
    }

    public static void WriteToDirectory(MarkdownCatalogGenerationResult result, string outputRoot)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        if (!result.IsValid) throw new InvalidOperationException("Cannot write an invalid Markdown catalog generation result.");
        string root = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(root);
        foreach (MarkdownCatalogFile file in result.Files)
        {
            string path = Path.GetFullPath(Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !string.Equals(path, root, StringComparison.Ordinal))
                throw new InvalidOperationException("Generated Markdown path escapes the explicit output root.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, file.Utf8Content.ToArray());
        }
    }

    private static string RenderIndex(EventCatalog catalog, EventCatalogNode[] nodes, EventCatalogEdge[] edges, Dictionary<string, string> messagePaths, MarkdownCatalogGenerationOptions options, string root)
    {
        var b = new StringBuilder();
        Line(b, "# Messaging Event Catalog"); Blank(b);
        Line(b, "## Overview");
        Item(b, "Application", catalog.Identity.ApplicationName);
        Item(b, "Application version", catalog.Identity.ApplicationVersion);
        Item(b, "Catalog schema version", catalog.SchemaVersion);
        Item(b, "Scope", catalog.Scope.Completeness.ToString());
        Item(b, "Synthetic/sample", catalog.Scope.Synthetic ? "Yes — synthetic example; not a TCJ production business event catalog." : "No");
        Blank(b); Line(b, ScopeDisclaimer); Blank(b);
        if (options.LinkEventCatalog || options.LinkRelationshipGraph || options.LinkAsyncApi)
        {
            Line(b, "### Machine-readable artifacts");
            if (options.LinkEventCatalog) Line(b, "- [event-catalog.json](../" + EventCatalogArtifacts.EventCatalogFileName + ")");
            if (options.LinkRelationshipGraph) Line(b, "- [catalog-graph.json](../" + EventCatalogArtifacts.RelationshipGraphFileName + ")");
            if (options.LinkAsyncApi) Line(b, "- [AsyncAPI](../" + EscapeLinkTarget(options.AsyncApiFileName) + ")");
            Blank(b);
        }
        RenderSummaryTable(b, "Messages", nodes.Where(static x => x.Type == EventCatalogNodeType.MessageContract), n =>
        {
            string text = Escape(n.MessageType ?? n.LogicalId) + " v" + (n.MessageVersion?.ToString(CultureInfo.InvariantCulture) ?? "?");
            string link = Relative(root + "/" + options.IndexFileName, messagePaths[n.Id]);
            return ["[" + text + "](" + link + ")", Escape(n.Lifecycle?.ToString()), Escape(n.Owner), Escape(n.ContentType), Escape(n.SchemaFingerprint), Escape(Join(n.DataClassifications)), Escape(n.CompatibilityMode)];
        }, ["Message", "Lifecycle", "Owner", "Content type", "Schema fingerprint", "Classification", "Compatibility"]);
        RenderSummaryTable(b, "Producers", nodes.Where(static x => x.Type == EventCatalogNodeType.Producer), n => ProducerConsumerRow(n, edges, nodes, EventCatalogRelationshipType.Publishes), ["Producer", "Component", "Message", "Channel", "Transport", "Owner", "Lifecycle"]);
        RenderSummaryTable(b, "Consumers", nodes.Where(static x => x.Type == EventCatalogNodeType.Consumer), n => ProducerConsumerRow(n, edges, nodes, EventCatalogRelationshipType.Consumes), ["Consumer", "Component", "Message", "Channel", "Transport", "Owner", "Lifecycle"]);
        RenderSummaryTable(b, "Channels", nodes.Where(static x => x.Type == EventCatalogNodeType.Channel), n => [Escape(n.LogicalId), Escape(n.DynamicDestination ? "Dynamic" : n.Address), Escape(n.TransportKind), Escape(n.Owner), Escape(n.Lifecycle?.ToString())], ["Channel", "Address/classification", "Transport", "Owner", "Lifecycle"]);
        RenderSummaryTable(b, "Transports", nodes.Where(static x => x.Type == EventCatalogNodeType.Transport), n => [Escape(n.LogicalId), Escape(n.TransportKind), Escape(n.Protocol), Escape(n.DeliverySemantics?.ToString()), Escape(n.OrderingSemantics?.ToString()), Escape(n.DeadLetterSemantics?.ToString())], ["Transport", "Kind", "Protocol", "Delivery", "Ordering", "Dead-letter"]);
        RenderSagaSummary(b, nodes, edges);
        RenderDeprecatedSummary(b, nodes, messagePaths, root + "/" + options.IndexFileName);
        return b.ToString();
    }

    private static string RenderMessage(EventCatalog catalog, EventCatalogNode message, EventCatalogNode[] nodes, EventCatalogEdge[] edges, Dictionary<string, EventCatalogNode> byId, Dictionary<string, string> messagePaths, MarkdownCatalogGenerationOptions options, string root)
    {
        var b = new StringBuilder();
        string path = messagePaths[message.Id];
        Line(b, "# " + Escape(message.MessageType ?? message.LogicalId) + " v" + message.MessageVersion?.ToString(CultureInfo.InvariantCulture)); Blank(b);
        Line(b, ScopeDisclaimer); Blank(b);
        if (catalog.Scope.Synthetic) { Line(b, "> Synthetic example only; this is not a TCJ production business event."); Blank(b); }
        Line(b, "## Message identity");
        Item(b, "Message type", message.MessageType);
        Item(b, "Message version", message.MessageVersion?.ToString(CultureInfo.InvariantCulture));
        Item(b, "Lifecycle", message.Lifecycle?.ToString());
        Item(b, "Owner", message.Owner);
        Item(b, "Content type", message.ContentType);
        Blank(b);
        Line(b, "## Schema");
        Item(b, "Fingerprint", JoinNonEmpty(message.SchemaFingerprintAlgorithm, message.SchemaFingerprint, ":"));
        if (!string.IsNullOrWhiteSpace(message.SchemaRelativePath)) Line(b, "- Schema: [" + Escape(message.SchemaRelativePath) + "](" + Relative(path, NormalizeRelativeArtifactPath(message.SchemaRelativePath)) + ")");
        Blank(b);
        RenderRelatedNodes(b, "Producers", message, EventCatalogRelationshipType.Publishes, nodes, edges, incoming: true);
        RenderRelatedNodes(b, "Consumers", message, EventCatalogRelationshipType.Consumes, nodes, edges, incoming: true);
        RenderMessageChannelsAndTransports(b, message, nodes, edges);
        RenderCompatibility(b, message, nodes, edges, messagePaths, path);
        if (message.DataClassifications.Count != 0) { Line(b, "## Security and data classification"); Item(b, "Classification", Join(message.DataClassifications)); Blank(b); }
        RenderSagaForMessage(b, message, nodes, edges);
        if (message.ExampleRelativePaths.Count != 0)
        {
            Line(b, "## Examples");
            foreach (string example in message.ExampleRelativePaths.Order(StringComparer.Ordinal)) Line(b, "- [" + Escape(example) + "](" + Relative(path, NormalizeRelativeArtifactPath(example)) + ")");
            Blank(b);
        }
        Line(b, "## Canonical artifacts");
        if (options.LinkEventCatalog) Line(b, "- [event-catalog.json](" + Relative(path, EventCatalogArtifacts.EventCatalogFileName) + ")");
        if (options.LinkRelationshipGraph) Line(b, "- [catalog-graph.json](" + Relative(path, EventCatalogArtifacts.RelationshipGraphFileName) + ")");
        if (options.LinkAsyncApi) Line(b, "- [AsyncAPI](" + Relative(path, EscapeLinkTarget(options.AsyncApiFileName)) + ")");
        return b.ToString();
    }

    private static void RenderRelatedNodes(StringBuilder b, string heading, EventCatalogNode message, EventCatalogRelationshipType type, EventCatalogNode[] nodes, EventCatalogEdge[] edges, bool incoming)
    {
        EventCatalogNode[] related = edges.Where(e => e.Type == type && (incoming ? e.TargetNodeId == message.Id : e.SourceNodeId == message.Id)).Select(e => nodes.Single(n => n.Id == (incoming ? e.SourceNodeId : e.TargetNodeId))).OrderBy(static n => n.LogicalId, StringComparer.Ordinal).ToArray();
        if (related.Length == 0) return;
        Line(b, "## " + heading);
        foreach (EventCatalogNode n in related)
        {
            Line(b, "- **" + Escape(n.LogicalId) + "**");
            if (!string.IsNullOrWhiteSpace(n.Component)) Line(b, "  - Component: " + Escape(n.Component));
            if (n.OutboxEnabled == true) Line(b, "  - Outbox: Enabled (durable at-least-once publication; not global exactly-once)");
            if (n.InboxEnabled == true) Line(b, "  - Inbox: Enabled (logical deduplication participation)");
            if (!string.IsNullOrWhiteSpace(n.RetryOwner)) Line(b, "  - Retry owner: " + Escape(n.RetryOwner));
            if (n.OrderingSemantics is not null) Line(b, "  - Ordering: " + Escape(n.OrderingSemantics.Value.ToString()));
            if (n.DeadLetterSemantics is not null) Line(b, "  - Dead-letter: " + Escape(n.DeadLetterSemantics.Value.ToString()));
            if (n.DynamicDestination) { Line(b, "  - Dynamic destination: Yes"); ItemIndented(b, "Naming strategy", n.DynamicNamingStrategyId); ItemIndented(b, "Pattern", n.DynamicPattern); }
        }
        Blank(b);
    }

    private static void RenderMessageChannelsAndTransports(StringBuilder b, EventCatalogNode message, EventCatalogNode[] nodes, EventCatalogEdge[] edges)
    {
        var actors = edges.Where(e => (e.Type == EventCatalogRelationshipType.Publishes || e.Type == EventCatalogRelationshipType.Consumes) && e.TargetNodeId == message.Id).Select(e => e.SourceNodeId).ToHashSet(StringComparer.Ordinal);
        EventCatalogNode[] channels = edges.Where(e => e.Type == EventCatalogRelationshipType.UsesChannel && actors.Contains(e.SourceNodeId)).Select(e => nodes.Single(n => n.Id == e.TargetNodeId)).DistinctBy(static n => n.Id).OrderBy(static n => n.LogicalId, StringComparer.Ordinal).ToArray();
        EventCatalogNode[] transports = edges.Where(e => e.Type == EventCatalogRelationshipType.UsesTransport && actors.Contains(e.SourceNodeId)).Select(e => nodes.Single(n => n.Id == e.TargetNodeId)).DistinctBy(static n => n.Id).OrderBy(static n => n.LogicalId, StringComparer.Ordinal).ToArray();
        if (channels.Length != 0) { Line(b, "## Channels"); foreach (EventCatalogNode n in channels) Line(b, "- " + Escape(n.LogicalId) + (n.DynamicDestination ? " — Dynamic" : string.IsNullOrWhiteSpace(n.Address) ? "" : " — " + Escape(n.Address))); Blank(b); }
        if (transports.Length != 0) { Line(b, "## Transports"); foreach (EventCatalogNode n in transports) Line(b, "- " + Escape(n.LogicalId) + Delivery(n)); Blank(b); }
    }

    private static void RenderCompatibility(StringBuilder b, EventCatalogNode message, EventCatalogNode[] nodes, EventCatalogEdge[] edges, Dictionary<string, string> messagePaths, string currentPath)
    {
        var replacement = edges.Where(e => e.Type == EventCatalogRelationshipType.Replaces && e.TargetNodeId == message.Id).Select(e => nodes.Single(n => n.Id == e.SourceNodeId)).OrderBy(static n => n.Id, StringComparer.Ordinal).FirstOrDefault();
        var upcasts = edges.Where(e => e.Type == EventCatalogRelationshipType.UpcastsTo && e.SourceNodeId == message.Id).Select(e => nodes.Single(n => n.Id == e.TargetNodeId)).OrderBy(static n => n.MessageVersion).ToArray();
        if (message.CompatibilityMode is null && replacement is null && upcasts.Length == 0 && message.Lifecycle != MessagingLifecycle.Deprecated) return;
        Line(b, "## Compatibility"); Item(b, "Mode", message.CompatibilityMode); Item(b, "Lifecycle", message.Lifecycle?.ToString());
        if (replacement is not null) Line(b, "- Replacement: [" + Escape(replacement.MessageType) + " v" + replacement.MessageVersion + "](" + Relative(currentPath, messagePaths[replacement.Id]) + ")");
        if (upcasts.Length != 0) Line(b, "- Upcaster path: " + Escape(string.Join(" -> ", new[] { message }.Concat(upcasts).Select(n => "v" + n.MessageVersion?.ToString(CultureInfo.InvariantCulture)))));
        Blank(b);
    }

    private static void RenderSagaForMessage(StringBuilder b, EventCatalogNode message, EventCatalogNode[] nodes, EventCatalogEdge[] edges)
    {
        var sagaEdges = edges.Where(e => IsSaga(e.Type) && e.SourceNodeId == message.Id).OrderBy(static e => e.Type.ToString(), StringComparer.Ordinal).ThenBy(static e => e.TargetNodeId, StringComparer.Ordinal).ToArray();
        if (sagaEdges.Length == 0) return;
        Line(b, "## Saga relationships");
        foreach (EventCatalogEdge edge in sagaEdges)
        {
            EventCatalogNode saga = nodes.Single(n => n.Id == edge.TargetNodeId);
            Line(b, "- " + Escape(edge.Type.ToString()) + ": " + Escape(saga.LogicalId) + " v" + saga.SagaDefinitionVersion?.ToString(CultureInfo.InvariantCulture));
        }
        Blank(b);
    }

    private static void RenderSagaSummary(StringBuilder b, EventCatalogNode[] nodes, EventCatalogEdge[] edges)
    {
        var sagaEdges = edges.Where(e => IsSaga(e.Type)).ToArray(); if (sagaEdges.Length == 0) return;
        Line(b, "## Saga relationships"); Line(b, "| Relationship | Message | Saga |"); Line(b, "| --- | --- | --- |");
        foreach (EventCatalogEdge edge in sagaEdges)
        {
            EventCatalogNode m = nodes.Single(n => n.Id == edge.SourceNodeId); EventCatalogNode s = nodes.Single(n => n.Id == edge.TargetNodeId);
            Line(b, "| " + Escape(edge.Type.ToString()) + " | " + Escape(m.MessageType) + " v" + m.MessageVersion + " | " + Escape(s.LogicalId) + " v" + s.SagaDefinitionVersion + " |");
        }
        Blank(b);
    }

    private static void RenderDeprecatedSummary(StringBuilder b, EventCatalogNode[] nodes, Dictionary<string, string> messagePaths, string indexPath)
    {
        EventCatalogNode[] deprecated = nodes.Where(static n => n.Type == EventCatalogNodeType.MessageContract && n.Lifecycle == MessagingLifecycle.Deprecated).OrderBy(static n => n.MessageType, StringComparer.Ordinal).ThenBy(static n => n.MessageVersion).ToArray();
        if (deprecated.Length == 0) return; Line(b, "## Deprecated contracts");
        foreach (EventCatalogNode n in deprecated) Line(b, "- [" + Escape(n.MessageType) + " v" + n.MessageVersion + "](" + Relative(indexPath, messagePaths[n.Id]) + ")");
        Blank(b);
    }

    private static string[] ProducerConsumerRow(EventCatalogNode n, EventCatalogEdge[] edges, EventCatalogNode[] nodes, EventCatalogRelationshipType messageEdge)
    {
        string messages = string.Join(", ", edges.Where(e => e.Type == messageEdge && e.SourceNodeId == n.Id).Select(e => nodes.Single(x => x.Id == e.TargetNodeId)).OrderBy(static x => x.MessageType, StringComparer.Ordinal).ThenBy(static x => x.MessageVersion).Select(static x => (x.MessageType ?? x.LogicalId) + " v" + x.MessageVersion));
        string channels = string.Join(", ", edges.Where(e => e.Type == EventCatalogRelationshipType.UsesChannel && e.SourceNodeId == n.Id).Select(e => nodes.Single(x => x.Id == e.TargetNodeId).LogicalId).Order(StringComparer.Ordinal));
        string transports = string.Join(", ", edges.Where(e => e.Type == EventCatalogRelationshipType.UsesTransport && e.SourceNodeId == n.Id).Select(e => nodes.Single(x => x.Id == e.TargetNodeId).LogicalId).Order(StringComparer.Ordinal));
        if (n.DynamicDestination) channels = "Dynamic: " + (n.DynamicNamingStrategyId ?? "declared");
        return [Escape(n.LogicalId), Escape(n.Component), Escape(messages), Escape(channels), Escape(transports), Escape(n.Owner), Escape(n.Lifecycle?.ToString())];
    }

    private static void RenderSummaryTable(StringBuilder b, string heading, IEnumerable<EventCatalogNode> source, Func<EventCatalogNode, string[]> row, string[] headers)
    {
        EventCatalogNode[] values = source.OrderBy(static x => x.LogicalId, StringComparer.Ordinal).ThenBy(static x => x.Version, StringComparer.Ordinal).ThenBy(static x => x.Id, StringComparer.Ordinal).ToArray();
        if (values.Length == 0) return; Line(b, "## " + heading); Line(b, "| " + string.Join(" | ", headers) + " |"); Line(b, "| " + string.Join(" | ", headers.Select(static _ => "---")) + " |"); foreach (EventCatalogNode n in values) Line(b, "| " + string.Join(" | ", row(n)) + " |"); Blank(b);
    }

    private static void ValidateGeneratedLinks(IReadOnlyList<MarkdownCatalogFile> files, List<MarkdownCatalogGenerationError> errors)
    {
        var paths = files.Select(static x => x.RelativePath).ToHashSet(StringComparer.Ordinal);
        foreach (MarkdownCatalogFile file in files)
        {
            string text = Encoding.UTF8.GetString(file.Utf8Content.Span);
            int i = 0;
            while ((i = text.IndexOf("](", i, StringComparison.Ordinal)) >= 0)
            {
                int start = i + 2; int end = text.IndexOf(')', start); if (end < 0) break;
                string target = text[start..end]; i = end + 1;
                if (target.Contains("://", StringComparison.Ordinal) || target.StartsWith('#')) continue;
                string clean = target.Split('#')[0]; if (string.IsNullOrWhiteSpace(clean)) continue;
                string resolved = Resolve(file.RelativePath, clean);
                if (resolved.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !paths.Contains(resolved)) errors.Add(new(MarkdownCatalogGenerationCodes.BrokenLink, file.RelativePath, "Generated Markdown contains a broken local page link."));
                if (resolved.Contains("../", StringComparison.Ordinal) || resolved.StartsWith('/')) errors.Add(new(MarkdownCatalogGenerationCodes.UnsafePath, file.RelativePath, "Generated Markdown contains an unsafe local link."));
            }
        }
    }

    private static MarkdownCatalogFile File(string path, string text) => new() { RelativePath = path, Utf8Content = Encoding.UTF8.GetBytes(text.EndsWith('\n') ? text : text + "\n") };
    private static MarkdownCatalogGenerationResult Invalid(IReadOnlyList<MarkdownCatalogGenerationError> errors) => new([], errors.OrderBy(static e => e.Code, StringComparer.Ordinal).ThenBy(static e => e.Path, StringComparer.Ordinal).ToArray());
    private static void Line(StringBuilder b, string value) => b.Append(value).Append('\n');
    private static void Blank(StringBuilder b) => b.Append('\n');
    private static void Item(StringBuilder b, string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) Line(b, "- **" + label + ":** " + Escape(value)); }
    private static void ItemIndented(StringBuilder b, string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) Line(b, "    - " + label + ": " + Escape(value)); }
    private static string Escape(string? value) => string.IsNullOrEmpty(value) ? "" : value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    private static string EscapeLinkTarget(string value) => value.Replace("\\", "/", StringComparison.Ordinal).Replace(" ", "%20", StringComparison.Ordinal);
    private static string Join(IReadOnlyList<string> values) => string.Join(", ", values.Order(StringComparer.Ordinal));
    private static string JoinNonEmpty(string? a, string? b, string sep) => string.Join(sep, new[] { a, b }.Where(static x => !string.IsNullOrWhiteSpace(x)));
    private static string Delivery(EventCatalogNode n) => n.DeliverySemantics is null ? "" : " — " + n.DeliverySemantics + (n.OrderingSemantics is null ? "" : ", " + n.OrderingSemantics);
    private static bool IsSaga(EventCatalogRelationshipType t) => t is EventCatalogRelationshipType.StartsSaga or EventCatalogRelationshipType.ContinuesSaga or EventCatalogRelationshipType.TimesOutSaga or EventCatalogRelationshipType.CompensatesSaga or EventCatalogRelationshipType.CompletesSaga or EventCatalogRelationshipType.FailsSaga;
    private static string NormalizeFileStem(string value)
    {
        var b = new StringBuilder(); bool dash = false;
        foreach (char c in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsAsciiLetterOrDigit(c)) { if (dash && b.Length != 0) b.Append('-'); b.Append(char.ToLowerInvariant(c)); dash = false; }
            else dash = true;
        }
        string result = b.ToString().Trim('-');
        if (string.IsNullOrEmpty(result) || result is "." or ".." || ReservedDeviceNames.Contains(result)) throw new ArgumentException("Message identity cannot be normalized to a safe file name.", nameof(value));
        return result;
    }
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase) { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9" };
    private static bool TryNormalizeRoot(string value, out string root) { root = value.Replace('\\', '/').Trim('/'); return root.Length != 0 && root.Split('/').All(SafeSegment); }
    private static bool SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.EndsWith(' ') || value.EndsWith('.')) return false;
        if (value.Any(static c => char.IsControl(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')) return false;
        return !ReservedDeviceNames.Contains(Path.GetFileNameWithoutExtension(value));
    }
    private static string Relative(string fromFile, string target)
    {
        string[] from = fromFile.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] to = target.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        int fromDirectoryLength = Math.Max(0, from.Length - 1);
        int common = 0;
        while (common < fromDirectoryLength && common < to.Length && string.Equals(from[common], to[common], StringComparison.Ordinal)) common++;
        return string.Join('/', Enumerable.Repeat("..", fromDirectoryLength - common).Concat(to.Skip(common)));
    }
    private static string Resolve(string fromFile, string target)
    {
        var segments = fromFile.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1).ToList();
        foreach (string part in target.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (segments.Count == 0) return "../"; segments.RemoveAt(segments.Count - 1); }
            else segments.Add(part);
        }
        return string.Join('/', segments);
    }
    private static string NormalizeRelativeArtifactPath(string value) => TryNormalizeArtifactPath(value, out string path) ? path : throw new ArgumentException("Artifact path must be a safe relative path.", nameof(value));
    private static bool TryNormalizeArtifactPath(string value, out string path)
    {
        path = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':', StringComparison.Ordinal)) return false;
        string[] parts = path.Split('/');
        return parts.All(static x => !string.IsNullOrWhiteSpace(x) && x is not "." and not "..");
    }
    private static bool ContainsSecret(EventCatalogNode node)
    {
        IEnumerable<string?> values = new string?[]
        {
            node.LogicalId, node.Version, node.Description, node.Owner, node.Component, node.MessageType, node.ContentType,
            node.SchemaFingerprintAlgorithm, node.SchemaFingerprint, node.SchemaRelativePath, node.CompatibilityMode, node.Address,
            node.TransportKind, node.Protocol, node.RetryOwner, node.SubscriptionOrGroup, node.DynamicNamingStrategyId, node.DynamicPattern
        }.Concat(node.DataClassifications).Concat(node.ExampleRelativePaths);
        return values.Where(static x => !string.IsNullOrEmpty(x)).Select(static x => x!.ToLowerInvariant()).Any(static text => SecretMarkers.Any(text.Contains));
    }
    private static readonly string[] SecretMarkers =
    [
        "password=", "password:", "client_secret", "clientsecret", "access_token", "refresh_token", "sharedaccesssignature",
        "accesskey=", "private key", "begin private key", "connectionstring=", "connection string=", "accountkey=", "sharedaccesskey=",
        "endpoint=sb://", "bootstrap.servers="
    ];
}
