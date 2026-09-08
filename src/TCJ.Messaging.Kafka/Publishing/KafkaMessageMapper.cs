using System.Globalization;
using System.Text;
using Confluent.Kafka;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Envelopes;
using TCJ.Messaging.Kafka.Configuration;
using TCJ.Messaging.Publishing;

namespace TCJ.Messaging.Kafka.Publishing;

internal sealed class KafkaMessageMapper
{
    private readonly MessagingHeaderPolicy _headers;
    internal KafkaMessageMapper(MessagingHeaderPolicy headers) => _headers = headers;
    private static readonly string[] Reserved = ["tcj-message-id","tcj-message-type","tcj-message-version","tcj-content-type","tcj-created-at","tcj-correlation-id","tcj-causation-id","tcj-partition-key","tcj-ordering-key","tcj-attempt"];
    internal Message<string, byte[]> ToKafka(TransportMessageEnvelope message, PublishContext context, IReadOnlyDictionary<string,string>? extra = null)
    {
        string? key = ResolveKey(message, context);
        var headers = new Headers();
        Add(headers, "tcj-message-id", message.MessageId); Add(headers, "tcj-message-type", message.MessageType);
        Add(headers, "tcj-message-version", message.MessageVersion.ToString(CultureInfo.InvariantCulture)); Add(headers, "tcj-content-type", message.ContentType);
        Add(headers, "tcj-created-at", message.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)); AddOptional(headers, "tcj-correlation-id", message.CorrelationId);
        AddOptional(headers, "tcj-causation-id", message.CausationId); AddOptional(headers, "tcj-partition-key", message.PartitionKey); AddOptional(headers, "tcj-ordering-key", message.OrderingKey);
        foreach ((string name,string value) in _headers.Filter(message.Headers))
            if (!Reserved.Contains(name, StringComparer.OrdinalIgnoreCase)) Add(headers, name, value);
        if (extra is not null) foreach ((string name,string value) in extra) { headers.Remove(name); Add(headers,name,value); }
        return new Message<string, byte[]> { Key = key, Value = message.Body.ToArray(), Headers = headers, Timestamp = new Timestamp(message.CreatedAtUtc.UtcDateTime) };
    }
    internal TransportMessageEnvelope FromKafka(ConsumeResult<string, byte[]> record)
    {
        string id = Required(record.Message.Headers, "tcj-message-id");
        string type = Required(record.Message.Headers, "tcj-message-type");
        int version = int.Parse(Required(record.Message.Headers, "tcj-message-version"), CultureInfo.InvariantCulture);
        string contentType = Required(record.Message.Headers, "tcj-content-type");
        DateTimeOffset created = DateTimeOffset.Parse(Required(record.Message.Headers, "tcj-created-at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var custom = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (IHeader h in record.Message.Headers) if (!Reserved.Contains(h.Key, StringComparer.OrdinalIgnoreCase)) custom[h.Key] = Encoding.UTF8.GetString(h.GetValueBytes());
        return new TransportMessageEnvelope(id,type,version,record.Message.Value ?? Array.Empty<byte>(),contentType,created,Optional(record.Message.Headers,"tcj-correlation-id"),Optional(record.Message.Headers,"tcj-causation-id"),Optional(record.Message.Headers,"tcj-partition-key"),Optional(record.Message.Headers,"tcj-ordering-key"),custom);
    }
    internal static string ResolveTopic(TcjKafkaOptions options, PublishContext context) { string topic = string.IsNullOrWhiteSpace(context.Destination) ? options.DefaultTopic : context.Destination!; TcjKafkaOptions.ValidateTopic(topic, nameof(context.Destination)); return topic; }
    internal static string? ResolveKey(TransportMessageEnvelope message, PublishContext context)
    {
        string? partition = context.PartitionKey ?? message.PartitionKey; string? ordering = context.OrderingKey ?? message.OrderingKey;
        if (partition is not null && ordering is not null && !string.Equals(partition, ordering, StringComparison.Ordinal)) throw new ArgumentException("PartitionKey and OrderingKey must be equal when both are supplied.");
        return partition ?? ordering;
    }
    internal static int GetAttempt(Headers headers) => int.TryParse(Optional(headers,"tcj-attempt"), NumberStyles.None, CultureInfo.InvariantCulture, out int attempt) && attempt > 0 ? attempt : 1;
    private static void Add(Headers headers,string name,string value) => headers.Add(name, Encoding.UTF8.GetBytes(value));
    private static void AddOptional(Headers headers,string name,string? value) { if (value is not null) Add(headers,name,value); }
    private static string Required(Headers headers,string name) => Optional(headers,name) ?? throw new InvalidDataException($"Kafka record is missing required TCJ header '{name}'.");
    private static string? Optional(Headers headers,string name) { IHeader? h = headers.LastOrDefault(x => string.Equals(x.Key,name,StringComparison.OrdinalIgnoreCase)); return h is null ? null : Encoding.UTF8.GetString(h.GetValueBytes()); }
}
