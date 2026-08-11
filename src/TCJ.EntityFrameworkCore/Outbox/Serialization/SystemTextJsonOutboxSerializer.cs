using System.Text.Json;
using TCJ.Core.DomainEvents;
using TCJ.Core.Outbox;

namespace TCJ.EntityFrameworkCore.Outbox.Serialization;

/// <summary>
/// Default safe transactional-outbox serializer based on <see cref="System.Text.Json.JsonSerializer"/>.
/// </summary>
public sealed class SystemTextJsonOutboxSerializer : IOutboxSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>Creates the serializer from the configured outbox JSON options.</summary>
    /// <param name="options">Validated outbox options containing the JSON serializer settings.</param>
    public SystemTextJsonOutboxSerializer(TcjOutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = new JsonSerializerOptions(options.JsonSerializerOptions);
    }

    /// <inheritdoc />
    public string Serialize(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), _options);
    }

    /// <inheritdoc />
    public IDomainEvent Deserialize(Type eventType, string payload)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(payload);

        if (!typeof(IDomainEvent).IsAssignableFrom(eventType))
        {
            throw new InvalidOperationException($"Resolved outbox type '{eventType.FullName}' does not implement IDomainEvent.");
        }

        object? value = JsonSerializer.Deserialize(payload, eventType, _options);
        return value as IDomainEvent
            ?? throw new JsonException($"The outbox payload could not be deserialized as '{eventType.FullName}'.");
    }
}
