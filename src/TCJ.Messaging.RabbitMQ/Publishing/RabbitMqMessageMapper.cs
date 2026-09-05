using System.Globalization;
using System.Text;
using RabbitMQ.Client;
using TCJ.Messaging.Configuration;
using TCJ.Messaging.Envelopes;

namespace TCJ.Messaging.RabbitMQ.Publishing;

internal sealed class RabbitMqMessageMapper
{
    private readonly MessagingHeaderPolicy _headerPolicy;

    internal RabbitMqMessageMapper(MessagingHeaderPolicy headerPolicy) => _headerPolicy = headerPolicy ?? throw new ArgumentNullException(nameof(headerPolicy));

    internal BasicProperties ToProperties(TransportMessageEnvelope message, TimeSpan? timeToLive, IReadOnlyDictionary<string, string>? additionalHeaders = null)
    {
        var headers = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in message.Headers)
            headers[key] = Encoding.UTF8.GetBytes(value);
        if (message.CausationId is not null) headers["tcj-causation-id"] = Encoding.UTF8.GetBytes(message.CausationId);
        headers["tcj-message-version"] = Encoding.UTF8.GetBytes(message.MessageVersion.ToString(CultureInfo.InvariantCulture));
        if (additionalHeaders is not null)
            foreach ((string key, string value) in additionalHeaders) headers[key] = Encoding.UTF8.GetBytes(value);

        return new BasicProperties
        {
            MessageId = message.MessageId,
            Type = message.MessageType,
            ContentType = message.ContentType,
            CorrelationId = message.CorrelationId,
            Timestamp = new AmqpTimestamp(message.CreatedAtUtc.ToUnixTimeSeconds()),
            Headers = headers,
            Expiration = timeToLive is null ? null : checked((long)timeToLive.Value.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            Persistent = true
        };
    }


    internal IDictionary<string, object?> ToSafeHeaders(IReadOnlyBasicProperties properties)
    {
        var safe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (properties.Headers is not null)
        {
            foreach ((string key, object? value) in properties.Headers)
            {
                if (!TryReadHeaderString(value, out string? text)) continue;
                try
                {
                    IReadOnlyDictionary<string, string> single = _headerPolicy.Filter(
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = text });
                    if (!single.TryGetValue(key, out string? filtered)) continue;
                    var candidate = new Dictionary<string, string>(safe, StringComparer.OrdinalIgnoreCase) { [key] = filtered };
                    _ = _headerPolicy.Filter(candidate);
                    safe[key] = filtered;
                }
                catch (ArgumentException)
                {
                    // Malformed or oversized external headers are intentionally discarded from terminal dead-letter metadata.
                }
            }
        }
        return safe.ToDictionary(static pair => pair.Key, static pair => (object?)Encoding.UTF8.GetBytes(pair.Value), StringComparer.OrdinalIgnoreCase);
    }

    internal TransportMessageEnvelope FromDelivery(IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body)
    {
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (properties.Headers is not null)
        {
            foreach ((string key, object? value) in properties.Headers)
            {
                if (TryReadHeaderString(value, out string? text)) raw[key] = text;
            }
        }
        IReadOnlyDictionary<string, string> headers = _headerPolicy.Filter(raw);
        string messageId = properties.MessageId ?? headers.GetValueOrDefault("tcj-message-id")
            ?? throw new InvalidOperationException("RabbitMQ delivery is missing a stable message ID.");
        string messageType = properties.Type ?? headers.GetValueOrDefault("tcj-message-type")
            ?? throw new InvalidOperationException("RabbitMQ delivery is missing a logical message type.");
        if (!int.TryParse(headers.GetValueOrDefault("tcj-message-version"), NumberStyles.None, CultureInfo.InvariantCulture, out int version) || version <= 0)
            throw new InvalidOperationException("RabbitMQ delivery contains an invalid or missing message version.");
        string contentType = properties.ContentType ?? headers.GetValueOrDefault("content-type")
            ?? throw new InvalidOperationException("RabbitMQ delivery is missing a content type.");
        DateTimeOffset created = properties.Timestamp.UnixTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(properties.Timestamp.UnixTime)
            : ParseCreatedAt(headers.GetValueOrDefault("tcj-created-at"));
        string? correlation = properties.CorrelationId ?? headers.GetValueOrDefault("tcj-correlation-id");
        string? causation = headers.GetValueOrDefault("tcj-causation-id");
        return new TransportMessageEnvelope(messageId, messageType, version, body, contentType, created, correlation, causation, headers: headers);
    }

    internal static int GetDeliveryAttempt(IReadOnlyBasicProperties properties, int maximum)
    {
        if (properties.Headers is null || !properties.Headers.TryGetValue("x-death", out object? value) || value is null) return 1;
        long maximumCount = 0;
        if (value is IEnumerable<object?> deaths)
        {
            foreach (object? item in deaths)
            {
                if (item is IDictionary<string, object?> typed && typed.TryGetValue("count", out object? count))
                    maximumCount = Math.Max(maximumCount, ConvertPositiveInt64(count));
                else if (item is System.Collections.IDictionary dictionary && dictionary.Contains("count"))
                    maximumCount = Math.Max(maximumCount, ConvertPositiveInt64(dictionary["count"]));
            }
        }
        long attempt = Math.Min((long)maximum + 1L, maximumCount + 1L);
        return checked((int)Math.Max(1L, attempt));
    }

    private static long ConvertPositiveInt64(object? value)
    {
        try { long result = Convert.ToInt64(value, CultureInfo.InvariantCulture); return result > 0 ? result : 0; }
        catch (Exception) when (value is not null) { return 0; }
    }

    private static bool TryReadHeaderString(object? value, out string? text)
    {
        text = value switch
        {
            string s => s,
            byte[] bytes => Decode(bytes),
            ReadOnlyMemory<byte> memory => Decode(memory.Span),
            _ => null
        };
        return text is not null;
    }

    private static string? Decode(ReadOnlySpan<byte> bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    private static DateTimeOffset ParseCreatedAt(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)
            ? parsed : DateTimeOffset.UnixEpoch;
}
