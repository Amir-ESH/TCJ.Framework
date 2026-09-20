using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TCJ.Messaging.AsyncApi;

/// <summary>Generates deterministic core AsyncAPI 3.1.0 documents from explicit offline inputs.</summary>
public static class AsyncApiDocumentGenerator
{
    public const string SupportedAsyncApiVersion = "3.1.0";
    public const string CanonicalFileName = "asyncapi.json";

    /// <summary>Generates canonical AsyncAPI JSON from an explicit catalog and validated Step 52 contracts.</summary>
    public static AsyncApiGenerationResult Generate(
        MessagingCatalog? catalog,
        GovernedContractResolutionResult? governedContracts,
        AsyncApiGenerationOptions? options = null)
    {
        options ??= new AsyncApiGenerationOptions();
        var errors = ValidateInputs(catalog, governedContracts, options);
        if (errors.Count != 0)
            return new(ReadOnlyMemory<byte>.Empty, errors);

        MessagingCatalog validCatalog = catalog!;
        IReadOnlyList<ResolvedGovernedMessageContract> contracts = governedContracts!.Contracts;

        var contractMap = contracts.ToDictionary(static c => ContractKey(c.MessageType, c.MessageVersion), StringComparer.Ordinal);
        var messageIds = BuildMessageIds(contracts, errors);
        var channelIds = BuildIds(validCatalog.Channels.Select(static x => (x.Id, "$.channels")), errors);
        var serverIds = BuildIds(validCatalog.Servers.Select(static x => (x.Id, "$.servers")), errors);
        var securityIds = BuildIds(validCatalog.SecuritySchemes.Select(static x => (x.Id, "$.securitySchemes")), errors);
        var operations = BuildOperations(validCatalog, contractMap, messageIds, channelIds, errors);

        CheckBounds(validCatalog, messageIds.Count, operations.Count, options, errors);
        ValidateServers(validCatalog, serverIds, securityIds, errors);
        if (errors.Count != 0)
            return new(ReadOnlyMemory<byte>.Empty, SortErrors(errors));

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, SkipValidation = false }))
        {
            WriteDocument(writer, validCatalog, contracts, messageIds, channelIds, serverIds, securityIds, operations, options.SchemaMode);
        }

        byte[] canonical = EnsureTrailingLf(buffer.WrittenSpan);
        return new(canonical, Array.Empty<AsyncApiGenerationError>());
    }

    /// <summary>Writes generated canonical JSON to an explicitly supplied stream without closing it.</summary>
    public static AsyncApiGenerationResult GenerateTo(
        Stream destination,
        MessagingCatalog? catalog,
        GovernedContractResolutionResult? governedContracts,
        AsyncApiGenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("Destination stream must be writable.", nameof(destination));

        AsyncApiGenerationResult result = Generate(catalog, governedContracts, options);
        if (result.IsValid)
            destination.Write(result.Utf8Json.Span);
        return result;
    }

    private static List<AsyncApiGenerationError> ValidateInputs(
        MessagingCatalog? catalog,
        GovernedContractResolutionResult? governedContracts,
        AsyncApiGenerationOptions options)
    {
        var errors = new List<AsyncApiGenerationError>();
        if (!string.Equals(options.AsyncApiVersion, SupportedAsyncApiVersion, StringComparison.Ordinal))
            errors.Add(new(AsyncApiGenerationCodes.UnsupportedAsyncApiVersion, "$.asyncapi", $"Only AsyncAPI {SupportedAsyncApiVersion} is supported."));
        if (!Enum.IsDefined(options.SchemaMode))
            errors.Add(new(AsyncApiGenerationCodes.UnsupportedSchemaMode, "$.schemaMode", "Schema mode is not supported."));

        MessagingCatalogValidationResult catalogValidation = MessagingCatalogValidator.Validate(catalog);
        foreach (MessagingCatalogValidationError error in catalogValidation.Errors)
            errors.Add(new(AsyncApiGenerationCodes.InvalidCatalog, error.Path, $"{error.Code}: {error.Message}"));

        if (governedContracts is null)
        {
            errors.Add(new(AsyncApiGenerationCodes.InvalidGovernedContracts, "$.contracts", "Validated governed contract resolution is required."));
        }
        else if (!governedContracts.IsValid)
        {
            foreach (GovernedContractValidationError error in governedContracts.Errors)
                errors.Add(new(AsyncApiGenerationCodes.InvalidGovernedContracts, error.Path, $"{error.Code}: {error.Message}"));
        }

        if (options.MaximumChannels <= 0 || options.MaximumMessages <= 0 || options.MaximumOperations <= 0 ||
            options.MaximumServers <= 0 || options.MaximumSecuritySchemes <= 0)
            errors.Add(new(AsyncApiGenerationCodes.DocumentBoundExceeded, "$.options", "Generation bounds must be positive."));

        return SortErrors(errors);
    }

    private static Dictionary<string, string> BuildMessageIds(
        IReadOnlyList<ResolvedGovernedMessageContract> contracts,
        List<AsyncApiGenerationError> errors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ResolvedGovernedMessageContract contract in contracts.OrderBy(static x => x.MessageType, StringComparer.Ordinal).ThenBy(static x => x.MessageVersion))
        {
            string key = ContractKey(contract.MessageType, contract.MessageVersion);
            string id = Normalize($"{contract.MessageType}.v{contract.MessageVersion.ToString(CultureInfo.InvariantCulture)}");
            if (!reverse.TryAdd(id, key))
                errors.Add(new(AsyncApiGenerationCodes.IdentifierCollision, "$.components.messages", $"Distinct governed contracts normalize to the same message component id '{id}'."));
            result[key] = id;
        }
        return result;
    }

    private static Dictionary<string, string> BuildIds(IEnumerable<(string Raw, string Path)> values, List<AsyncApiGenerationError> errors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string raw, string path) in values.OrderBy(static x => x.Raw, StringComparer.Ordinal))
        {
            string id = Normalize(raw);
            if (!reverse.TryAdd(id, raw) && !string.Equals(reverse[id], raw, StringComparison.Ordinal))
                errors.Add(new(AsyncApiGenerationCodes.IdentifierCollision, path, $"Distinct identifiers normalize to the same id '{id}'."));
            result[Normalize(raw)] = id;
        }
        return result;
    }

    private static List<GeneratedOperation> BuildOperations(
        MessagingCatalog catalog,
        IReadOnlyDictionary<string, ResolvedGovernedMessageContract> contracts,
        IReadOnlyDictionary<string, string> messageIds,
        IReadOnlyDictionary<string, string> channelIds,
        List<AsyncApiGenerationError> errors)
    {
        var result = new List<GeneratedOperation>();
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        string application = Normalize(catalog.Application.Name);

        foreach (MessagingProducer producer in catalog.Producers.OrderBy(static x => x.Id, StringComparer.Ordinal))
        {
            AddOperation("send", producer.Id, producer.ChannelId, producer.Message.Type, [producer.Message.Version]);
        }

        foreach (MessagingConsumer consumer in catalog.Consumers.OrderBy(static x => x.Id, StringComparer.Ordinal))
        {
            AddOperation("receive", consumer.Id, consumer.ChannelId, consumer.MessageType, consumer.AcceptedMessageVersions.Order().ToArray());
        }

        return result.OrderBy(static x => x.Id, StringComparer.Ordinal).ToList();

        void AddOperation(string action, string logicalId, string channelRawId, string messageType, IReadOnlyList<int> versions)
        {
            if (!channelIds.TryGetValue(Normalize(channelRawId), out string? channelId))
            {
                errors.Add(new(AsyncApiGenerationCodes.InvalidCatalog, "$.operations", "Operation references an unknown channel."));
                return;
            }

            MessagingChannel channel = catalog.Channels.First(x => string.Equals(Normalize(x.Id), Normalize(channelRawId), StringComparison.Ordinal));
            var channelContracts = channel.Messages
                .Select(static x => ContractKey(x.Type, x.Version))
                .ToHashSet(StringComparer.Ordinal);
            var refs = new List<(string MessageId, string ChannelMessageId)>();
            foreach (int version in versions)
            {
                string key = ContractKey(messageType, version);
                if (!channelContracts.Contains(key))
                {
                    errors.Add(new(AsyncApiGenerationCodes.UnknownContract, "$.operations", $"Operation contract '{messageType}' version {version.ToString(CultureInfo.InvariantCulture)} is not declared on channel '{channelRawId}'."));
                    continue;
                }
                if (!contracts.ContainsKey(key) || !messageIds.TryGetValue(key, out string? messageId))
                {
                    errors.Add(new(AsyncApiGenerationCodes.UnknownContract, "$.operations", $"Operation references unresolved governed contract '{messageType}' version {version.ToString(CultureInfo.InvariantCulture)}."));
                    continue;
                }
                refs.Add((messageId, messageId));
            }

            string firstVersion = versions.Count == 1 ? $"v{versions[0].ToString(CultureInfo.InvariantCulture)}" : "multi-version";
            string operationId = Normalize($"{application}.{action}.{messageType}.{firstVersion}.{logicalId}");
            if (!operationIds.Add(operationId))
            {
                errors.Add(new(AsyncApiGenerationCodes.IdentifierCollision, "$.operations", $"Operation id collision for '{operationId}'."));
                return;
            }
            result.Add(new(operationId, action, channelId, refs.OrderBy(static x => x.MessageId, StringComparer.Ordinal).ToArray()));
        }
    }

    private static void ValidateServers(
        MessagingCatalog catalog,
        IReadOnlyDictionary<string, string> serverIds,
        IReadOnlyDictionary<string, string> securityIds,
        List<AsyncApiGenerationError> errors)
    {
        foreach (MessagingServer server in catalog.Servers)
        {
            if (!serverIds.ContainsKey(Normalize(server.Id)))
                continue;
            if (!string.IsNullOrWhiteSpace(server.SecuritySchemeId) && !securityIds.ContainsKey(Normalize(server.SecuritySchemeId)))
                errors.Add(new(AsyncApiGenerationCodes.InvalidServerReference, "$.servers", "Server references an unknown security scheme."));
        }
    }

    private static void CheckBounds(MessagingCatalog catalog, int messageCount, int operationCount, AsyncApiGenerationOptions options, List<AsyncApiGenerationError> errors)
    {
        if (catalog.Channels.Count > options.MaximumChannels) Add("channels", options.MaximumChannels);
        if (messageCount > options.MaximumMessages) Add("messages", options.MaximumMessages);
        if (operationCount > options.MaximumOperations) Add("operations", options.MaximumOperations);
        if (catalog.Servers.Count > options.MaximumServers) Add("servers", options.MaximumServers);
        if (catalog.SecuritySchemes.Count > options.MaximumSecuritySchemes) Add("securitySchemes", options.MaximumSecuritySchemes);

        void Add(string name, int maximum) => errors.Add(new(AsyncApiGenerationCodes.DocumentBoundExceeded, "$.'" + name + "'", $"Generated {name} exceed configured maximum {maximum.ToString(CultureInfo.InvariantCulture)}."));
    }

    private static void WriteDocument(
        Utf8JsonWriter writer,
        MessagingCatalog catalog,
        IReadOnlyList<ResolvedGovernedMessageContract> contracts,
        IReadOnlyDictionary<string, string> messageIds,
        IReadOnlyDictionary<string, string> channelIds,
        IReadOnlyDictionary<string, string> serverIds,
        IReadOnlyDictionary<string, string> securityIds,
        IReadOnlyList<GeneratedOperation> operations,
        AsyncApiSchemaMode schemaMode)
    {
        writer.WriteStartObject();
        writer.WriteString("asyncapi", SupportedAsyncApiVersion);
        WriteInfo(writer, catalog.Document);
        if (catalog.Servers.Count != 0) WriteServers(writer, catalog, serverIds, securityIds);
        WriteChannels(writer, catalog, messageIds, channelIds);
        WriteOperations(writer, operations);
        WriteComponents(writer, catalog, contracts, messageIds, securityIds, schemaMode);
        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteInfo(Utf8JsonWriter writer, MessagingDocumentInfo info)
    {
        writer.WritePropertyName("info");
        writer.WriteStartObject();
        writer.WriteString("title", info.Title);
        writer.WriteString("version", info.Version);
        WriteOptional(writer, "description", info.Description);
        if (info.Contact is not null)
        {
            writer.WritePropertyName("contact"); writer.WriteStartObject();
            WriteOptional(writer, "name", info.Contact.Name); WriteOptional(writer, "url", info.Contact.Url);
            writer.WriteEndObject();
        }
        if (info.License is not null)
        {
            writer.WritePropertyName("license"); writer.WriteStartObject();
            writer.WriteString("name", info.License.Name); WriteOptional(writer, "url", info.License.Url);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        if (info.ExternalDocumentation is not null)
        {
            writer.WritePropertyName("externalDocs"); writer.WriteStartObject();
            WriteOptional(writer, "description", info.ExternalDocumentation.Description);
            writer.WriteString("url", info.ExternalDocumentation.Url);
            writer.WriteEndObject();
        }
    }

    private static void WriteServers(Utf8JsonWriter writer, MessagingCatalog catalog, IReadOnlyDictionary<string, string> serverIds, IReadOnlyDictionary<string, string> securityIds)
    {
        writer.WritePropertyName("servers"); writer.WriteStartObject();
        foreach (MessagingServer server in catalog.Servers.OrderBy(x => serverIds[Normalize(x.Id)], StringComparer.Ordinal))
        {
            writer.WritePropertyName(serverIds[Normalize(server.Id)]); writer.WriteStartObject();
            writer.WriteString("host", server.Host);
            writer.WriteString("protocol", server.Protocol);
            WriteOptional(writer, "description", server.Description);
            if (!string.IsNullOrWhiteSpace(server.SecuritySchemeId) &&
                TryMapSecurity(catalog.SecuritySchemes.First(x => string.Equals(Normalize(x.Id), Normalize(server.SecuritySchemeId), StringComparison.Ordinal)).Mechanism, out _))
            {
                writer.WritePropertyName("security"); writer.WriteStartArray();
                writer.WriteStartObject(); writer.WriteString("$ref", $"#/components/securitySchemes/{securityIds[Normalize(server.SecuritySchemeId)]}"); writer.WriteEndObject();
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteChannels(Utf8JsonWriter writer, MessagingCatalog catalog, IReadOnlyDictionary<string, string> messageIds, IReadOnlyDictionary<string, string> channelIds)
    {
        writer.WritePropertyName("channels"); writer.WriteStartObject();
        foreach (MessagingChannel channel in catalog.Channels.OrderBy(x => channelIds[Normalize(x.Id)], StringComparer.Ordinal))
        {
            string channelId = channelIds[Normalize(channel.Id)];
            writer.WritePropertyName(channelId); writer.WriteStartObject();
            if (channel.DynamicDestination is null) writer.WriteString("address", channel.Address); else writer.WriteNull("address");
            WriteOptional(writer, "description", channel.Description);
            writer.WritePropertyName("messages"); writer.WriteStartObject();
            foreach (MessagingMessageReference message in channel.Messages.OrderBy(static x => x.Type, StringComparer.Ordinal).ThenBy(static x => x.Version))
            {
                string messageId = messageIds[ContractKey(message.Type, message.Version)];
                writer.WritePropertyName(messageId); writer.WriteStartObject();
                writer.WriteString("$ref", $"#/components/messages/{messageId}");
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteOperations(Utf8JsonWriter writer, IReadOnlyList<GeneratedOperation> operations)
    {
        writer.WritePropertyName("operations"); writer.WriteStartObject();
        foreach (GeneratedOperation operation in operations)
        {
            writer.WritePropertyName(operation.Id); writer.WriteStartObject();
            writer.WriteString("action", operation.Action);
            writer.WritePropertyName("channel"); writer.WriteStartObject(); writer.WriteString("$ref", $"#/channels/{operation.ChannelId}"); writer.WriteEndObject();
            writer.WritePropertyName("messages"); writer.WriteStartArray();
            foreach ((_, string channelMessageId) in operation.Messages)
            {
                writer.WriteStartObject(); writer.WriteString("$ref", $"#/channels/{operation.ChannelId}/messages/{channelMessageId}"); writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteComponents(
        Utf8JsonWriter writer,
        MessagingCatalog catalog,
        IReadOnlyList<ResolvedGovernedMessageContract> contracts,
        IReadOnlyDictionary<string, string> messageIds,
        IReadOnlyDictionary<string, string> securityIds,
        AsyncApiSchemaMode schemaMode)
    {
        writer.WritePropertyName("components"); writer.WriteStartObject();
        writer.WritePropertyName("messages"); writer.WriteStartObject();
        foreach (ResolvedGovernedMessageContract contract in contracts.OrderBy(x => messageIds[ContractKey(x.MessageType, x.MessageVersion)], StringComparer.Ordinal))
        {
            string id = messageIds[ContractKey(contract.MessageType, contract.MessageVersion)];
            writer.WritePropertyName(id); writer.WriteStartObject();
            writer.WriteString("name", $"{contract.MessageType}.v{contract.MessageVersion.ToString(CultureInfo.InvariantCulture)}");
            WriteOptional(writer, "contentType", contract.ContentType);
            writer.WritePropertyName("payload"); writer.WriteStartObject();
            writer.WriteString("schemaFormat", SchemaFormat(contract.SchemaDialect));
            writer.WritePropertyName("schema");
            if (schemaMode == AsyncApiSchemaMode.Referenced)
            {
                writer.WriteStartObject();
                writer.WriteString("$ref", "./" + contract.SchemaRelativePath.Replace('\\', '/'));
                writer.WriteEndObject();
            }
            else
            {
                using JsonDocument schema = JsonDocument.Parse(contract.SchemaUtf8);
                schema.RootElement.WriteTo(writer);
            }
            writer.WriteEndObject();
            if (contract.Examples.Count != 0)
            {
                writer.WritePropertyName("examples"); writer.WriteStartArray();
                int index = 1;
                foreach (ResolvedGovernedExample example in contract.Examples.OrderBy(static x => x.RelativePath, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", $"example-{index.ToString(CultureInfo.InvariantCulture)}");
                    writer.WritePropertyName("payload");
                    using JsonDocument exampleDocument = JsonDocument.Parse(example.Utf8Json);
                    exampleDocument.RootElement.WriteTo(writer);
                    writer.WriteEndObject();
                    index++;
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();

        var representable = catalog.SecuritySchemes.Where(static x => TryMapSecurity(x.Mechanism, out _)).OrderBy(x => securityIds[Normalize(x.Id)], StringComparer.Ordinal).ToArray();
        if (representable.Length != 0)
        {
            writer.WritePropertyName("securitySchemes"); writer.WriteStartObject();
            foreach (MessagingSecurityScheme scheme in representable)
            {
                writer.WritePropertyName(securityIds[Normalize(scheme.Id)]); writer.WriteStartObject();
                TryMapSecurity(scheme.Mechanism, out string? type);
                writer.WriteString("type", type);
                WriteOptional(writer, "description", scheme.Description);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static string SchemaFormat(string schemaDialect) => schemaDialect switch
    {
        "https://json-schema.org/draft/2020-12/schema" => "application/schema+json;version=draft-2020-12",
        _ => throw new InvalidOperationException("Resolved governed contract contains an unsupported schema dialect.")
    };

    private static bool TryMapSecurity(MessagingSecurityMechanism mechanism, out string? type)
    {
        type = mechanism switch
        {
            MessagingSecurityMechanism.X509 => "X509",
            MessagingSecurityMechanism.SaslPlain => "plain",
            MessagingSecurityMechanism.UserPassword => "userPassword",
            _ => null
        };
        return type is not null;
    }

    private static string Normalize(string value) => MessagingCatalogValidator.NormalizeIdentifier(value);
    private static string ContractKey(string type, int version) => type + "\u001f" + version.ToString(CultureInfo.InvariantCulture);

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) writer.WriteString(name, value);
    }

    private static byte[] EnsureTrailingLf(ReadOnlySpan<byte> bytes)
    {
        byte[] result = new byte[bytes.Length + 1];
        bytes.CopyTo(result);
        result[^1] = (byte)'\n';
        return result;
    }

    private static List<AsyncApiGenerationError> SortErrors(IEnumerable<AsyncApiGenerationError> errors) =>
        errors.OrderBy(static x => x.Path, StringComparer.Ordinal)
            .ThenBy(static x => x.Code, StringComparer.Ordinal)
            .ThenBy(static x => x.Message, StringComparer.Ordinal)
            .ToList();

    private sealed record GeneratedOperation(string Id, string Action, string ChannelId, IReadOnlyList<(string MessageId, string ChannelMessageId)> Messages);
}
