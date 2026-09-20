using System.Globalization;
using System.Text.Json;
using TCJ.Messaging.Contracts;

namespace TCJ.Messaging.AsyncApi;

internal static class TcjAsyncApiExtensionWriter
{
    public static void WriteDocument(Utf8JsonWriter writer, MessagingCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog.Application.Owner)) return;
        WriteVersion(writer);
        writer.WriteString(TcjAsyncApiExtensions.Owner, catalog.Application.Owner);
    }

    public static void WriteMessage(Utf8JsonWriter writer, ResolvedGovernedMessageContract contract)
    {
        WriteVersion(writer);
        writer.WriteString(TcjAsyncApiExtensions.ContractId, contract.MessageType);
        writer.WriteNumber(TcjAsyncApiExtensions.ContractVersion, contract.MessageVersion);

        writer.WritePropertyName(TcjAsyncApiExtensions.SchemaFingerprint);
        writer.WriteStartObject();
        writer.WriteString("algorithm", contract.SchemaFingerprint.Algorithm);
        writer.WriteString("value", contract.SchemaFingerprint.Value);
        writer.WriteEndObject();

        writer.WriteString(TcjAsyncApiExtensions.Owner, contract.Owner);
        if (contract.Deprecated)
            writer.WriteString(TcjAsyncApiExtensions.Lifecycle, MessagingLifecycle.Deprecated.ToString());

        writer.WritePropertyName(TcjAsyncApiExtensions.Compatibility);
        writer.WriteStartObject();
        writer.WriteString("mode", contract.CompatibilityMode.ToString());
        writer.WriteBoolean("deprecated", contract.Deprecated);
        if (contract.DeprecatedSince is int deprecatedSince) writer.WriteNumber("deprecatedSince", deprecatedSince);
        if (contract.ReplacementVersion is int replacementVersion) writer.WriteNumber("replacementVersion", replacementVersion);
        if (!string.IsNullOrWhiteSpace(contract.SemanticCompatibilityNotes)) writer.WriteString("semanticNotes", contract.SemanticCompatibilityNotes);
        writer.WriteEndObject();

        MessageDataClassification[] classifications = contract.DataClassifications
            .Select(static item => item.Classification)
            .Distinct()
            .OrderBy(static item => item.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (classifications.Length != 0)
        {
            writer.WritePropertyName(TcjAsyncApiExtensions.DataClassification);
            writer.WriteStartArray();
            foreach (MessageDataClassification classification in classifications) writer.WriteStringValue(classification.ToString());
            writer.WriteEndArray();
        }
    }

    public static void WriteChannel(Utf8JsonWriter writer, MessagingChannel channel)
    {
        bool hasExtensions = !string.IsNullOrWhiteSpace(channel.Owner) || channel.DynamicDestination is not null ||
            channel.DataClassifications.Count != 0 || channel.Lifecycle.HasValue;
        if (!hasExtensions) return;

        WriteVersion(writer);
        WriteOptional(writer, TcjAsyncApiExtensions.Owner, channel.Owner);
        if (channel.Lifecycle is MessagingLifecycle lifecycle) writer.WriteString(TcjAsyncApiExtensions.Lifecycle, lifecycle.ToString());
        WriteClassifications(writer, channel.DataClassifications);
        WriteDynamicDestination(writer, channel.DynamicDestination);
    }

    public static void WriteProducerOperation(Utf8JsonWriter writer, MessagingProducer producer, MessagingTransport transport, IReadOnlyList<MessagingRelationship> relationships)
    {
        bool hasExtensions = !string.IsNullOrWhiteSpace(producer.Owner) || producer.Lifecycle.HasValue || transport.DeliverySemantics.HasValue ||
            transport.OrderingSemantics.HasValue || transport.PartitioningSemantics.HasValue || !string.IsNullOrWhiteSpace(producer.PartitionKeyStrategy) ||
            !string.IsNullOrWhiteSpace(producer.OrderingKeyStrategy) || producer.RetryOwner.HasValue || producer.Outbox is not null ||
            producer.DynamicDestination is not null || HasSaga(relationships, MessagingEntityKind.Producer, producer.Id);
        if (!hasExtensions) return;
        WriteVersion(writer);
        WriteOptional(writer, TcjAsyncApiExtensions.Owner, producer.Owner);
        if (producer.Lifecycle is MessagingLifecycle lifecycle) writer.WriteString(TcjAsyncApiExtensions.Lifecycle, lifecycle.ToString());
        if (transport.DeliverySemantics is MessagingDeliverySemantics delivery) writer.WriteString(TcjAsyncApiExtensions.DeliverySemantics, delivery.ToString());
        WriteOrdering(writer, transport.OrderingSemantics, transport.PartitioningSemantics, producer.PartitionKeyStrategy, producer.OrderingKeyStrategy);
        WriteRetryOwner(writer, producer.RetryOwner);
        WriteOutbox(writer, producer.Outbox, producer.RetryOwner);
        WriteDynamicDestination(writer, producer.DynamicDestination);
        WriteSaga(writer, relationships, MessagingEntityKind.Producer, producer.Id);
    }

    public static void WriteConsumerOperation(Utf8JsonWriter writer, MessagingConsumer consumer, MessagingTransport transport, IReadOnlyList<MessagingRelationship> relationships)
    {
        bool hasExtensions = !string.IsNullOrWhiteSpace(consumer.Owner) || consumer.Lifecycle.HasValue || transport.DeliverySemantics.HasValue ||
            consumer.OrderingScope.HasValue || transport.OrderingSemantics.HasValue || transport.PartitioningSemantics.HasValue || consumer.RetryOwner.HasValue ||
            consumer.DeadLetterSemantics.HasValue || transport.DeadLetterSemantics.HasValue || consumer.Inbox is not null ||
            HasSaga(relationships, MessagingEntityKind.Consumer, consumer.Id);
        if (!hasExtensions) return;
        WriteVersion(writer);
        WriteOptional(writer, TcjAsyncApiExtensions.Owner, consumer.Owner);
        if (consumer.Lifecycle is MessagingLifecycle lifecycle) writer.WriteString(TcjAsyncApiExtensions.Lifecycle, lifecycle.ToString());
        if (transport.DeliverySemantics is MessagingDeliverySemantics delivery) writer.WriteString(TcjAsyncApiExtensions.DeliverySemantics, delivery.ToString());
        WriteOrdering(writer, consumer.OrderingScope ?? transport.OrderingSemantics, transport.PartitioningSemantics, null, null);
        WriteRetryOwner(writer, consumer.RetryOwner);
        if ((consumer.DeadLetterSemantics ?? transport.DeadLetterSemantics) is MessagingDeadLetterSemantics deadLetter) writer.WriteString(TcjAsyncApiExtensions.DeadLetter, deadLetter.ToString());
        WriteInbox(writer, consumer.Inbox, consumer.RetryOwner);
        WriteSaga(writer, relationships, MessagingEntityKind.Consumer, consumer.Id);
    }

    private static void WriteVersion(Utf8JsonWriter writer) => writer.WriteString(TcjAsyncApiExtensions.VersionName, TcjAsyncApiExtensions.Version);

    private static void WriteOrdering(Utf8JsonWriter writer, MessagingOrderingSemantics? ordering, MessagingPartitioningSemantics? partitioning, string? partitionKeyStrategy, string? orderingKeyStrategy)
    {
        if (ordering is null && partitioning is null && string.IsNullOrWhiteSpace(partitionKeyStrategy) && string.IsNullOrWhiteSpace(orderingKeyStrategy)) return;
        writer.WritePropertyName(TcjAsyncApiExtensions.OrderingScope);
        writer.WriteStartObject();
        if (ordering is MessagingOrderingSemantics scope) writer.WriteString("scope", scope.ToString());
        if (partitioning is MessagingPartitioningSemantics partitioningValue) writer.WriteString("partitioning", partitioningValue.ToString());
        WriteOptional(writer, "partitionKeyStrategy", partitionKeyStrategy);
        WriteOptional(writer, "orderingKeyStrategy", orderingKeyStrategy);
        writer.WriteEndObject();
    }

    private static void WriteRetryOwner(Utf8JsonWriter writer, MessagingRetryOwner? owner)
    {
        if (owner is null) return;
        string? value = NormalizeRetryOwner(owner.Value);
        if (value is not null) writer.WriteString(TcjAsyncApiExtensions.RetryOwner, value);
    }

    private static string? NormalizeRetryOwner(MessagingRetryOwner owner) => owner switch
    {
        MessagingRetryOwner.None => null,
        MessagingRetryOwner.Transport => MessagingRetryOwner.TransportSpecific.ToString(),
        _ => owner.ToString()
    };

    private static void WriteInbox(Utf8JsonWriter writer, MessagingInboxDeclaration? inbox, MessagingRetryOwner? retryOwner)
    {
        if (inbox is null) return;
        writer.WritePropertyName(TcjAsyncApiExtensions.Inbox);
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", inbox.Enabled);
        if (!string.IsNullOrWhiteSpace(inbox.DeduplicationModel)) writer.WriteString("deduplicationIdentity", inbox.DeduplicationModel);
        writer.WriteString("transactionalBoundary", inbox.TransactionalBoundary.ToString());
        string? normalizedRetry = retryOwner is null ? null : NormalizeRetryOwner(retryOwner.Value);
        if (normalizedRetry is not null) writer.WriteString("retryOwner", normalizedRetry);
        writer.WriteEndObject();
    }

    private static void WriteOutbox(Utf8JsonWriter writer, MessagingOutboxDeclaration? outbox, MessagingRetryOwner? retryOwner)
    {
        if (outbox is null) return;
        writer.WritePropertyName(TcjAsyncApiExtensions.Outbox);
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", outbox.Enabled);
        if (!string.IsNullOrWhiteSpace(outbox.DeduplicationModel)) writer.WriteString("deduplicationIdentity", outbox.DeduplicationModel);
        writer.WriteString("transactionalBoundary", outbox.TransactionalBoundary.ToString());
        writer.WriteString("publicationModel", outbox.Enabled ? "DurableAtLeastOnce" : "Disabled");
        string? normalizedRetry = retryOwner is null ? null : NormalizeRetryOwner(retryOwner.Value);
        if (normalizedRetry is not null) writer.WriteString("retryOwner", normalizedRetry);
        writer.WriteEndObject();
    }

    private static void WriteClassifications(Utf8JsonWriter writer, IReadOnlyList<MessagingDataClassification> classifications)
    {
        if (classifications.Count == 0) return;
        writer.WritePropertyName(TcjAsyncApiExtensions.DataClassification);
        writer.WriteStartArray();
        foreach (MessagingDataClassification classification in classifications.Distinct().OrderBy(static item => item.ToString(), StringComparer.Ordinal))
            writer.WriteStringValue(classification.ToString());
        writer.WriteEndArray();
    }

    private static void WriteDynamicDestination(Utf8JsonWriter writer, MessagingDynamicDestination? dynamicDestination)
    {
        if (dynamicDestination is null) return;
        writer.WritePropertyName(TcjAsyncApiExtensions.DynamicDestination);
        writer.WriteStartObject();
        writer.WriteBoolean("dynamic", true);
        writer.WriteString("namingStrategyId", dynamicDestination.NamingStrategyId);
        writer.WriteString("pattern", dynamicDestination.Pattern);
        WriteOptional(writer, "description", dynamicDestination.Description);
        writer.WriteEndObject();
    }

    private static void WriteSaga(Utf8JsonWriter writer, IReadOnlyList<MessagingRelationship> relationships, MessagingEntityKind entityKind, string entityId)
    {
        MessagingRelationship[] sagaRelationships = relationships
            .Where(item => item.Saga is not null && (IsEntity(item.Source, entityKind, entityId) || IsEntity(item.Target, entityKind, entityId)))
            .Where(static item => TryMapSagaRelationship(item.Kind, out _))
            .OrderBy(static item => item.Saga!.DefinitionId, StringComparer.Ordinal)
            .ThenBy(static item => item.Saga!.DefinitionVersion)
            .ThenBy(static item => item.Kind.ToString(), StringComparer.Ordinal)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (sagaRelationships.Length == 0) return;

        writer.WritePropertyName(TcjAsyncApiExtensions.Saga);
        writer.WriteStartArray();
        foreach (MessagingRelationship relationship in sagaRelationships)
        {
            TryMapSagaRelationship(relationship.Kind, out string? relation);
            writer.WriteStartObject();
            writer.WriteString("definitionId", relationship.Saga!.DefinitionId);
            writer.WriteNumber("definitionVersion", relationship.Saga.DefinitionVersion);
            writer.WriteString("relationship", relation);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }


    private static bool HasSaga(IReadOnlyList<MessagingRelationship> relationships, MessagingEntityKind entityKind, string entityId) =>
        relationships.Any(item => item.Saga is not null && TryMapSagaRelationship(item.Kind, out _) &&
            (IsEntity(item.Source, entityKind, entityId) || IsEntity(item.Target, entityKind, entityId)));

    private static bool IsEntity(MessagingEntityReference reference, MessagingEntityKind kind, string id) =>
        reference.Kind == kind && string.Equals(MessagingCatalogValidator.NormalizeIdentifier(reference.Id), MessagingCatalogValidator.NormalizeIdentifier(id), StringComparison.Ordinal);

    private static bool TryMapSagaRelationship(MessagingRelationshipKind kind, out string? value)
    {
        value = kind switch
        {
            MessagingRelationshipKind.StartsSaga => "Start",
            MessagingRelationshipKind.ContinuesSaga => "Continue",
            MessagingRelationshipKind.TimesOutSaga => "Timeout",
            MessagingRelationshipKind.CompensatesSaga => "Compensation",
            MessagingRelationshipKind.CompletesSaga => "Completion",
            MessagingRelationshipKind.FailsSaga => "Failure",
            _ => null
        };
        return value is not null;
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) writer.WriteString(name, value);
    }
}
