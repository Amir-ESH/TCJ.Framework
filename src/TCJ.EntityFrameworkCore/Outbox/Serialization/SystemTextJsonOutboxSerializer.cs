using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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

        // GetTypeInfo(Type) requires an explicit resolver. Preserve the historical JIT behavior
        // without rooting reflection in Native AOT applications where reflection serialization is disabled.
        if (_options.TypeInfoResolver is null && JsonSerializer.IsReflectionEnabledByDefault)
        {
            _options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        }
    }

    /// <inheritdoc />
    public string Serialize(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        JsonTypeInfo typeInfo = _options.GetTypeInfo(domainEvent.GetType());
        return JsonSerializer.Serialize(domainEvent, typeInfo);
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

        JsonTypeInfo typeInfo = _options.GetTypeInfo(eventType);
        object? value = JsonSerializer.Deserialize(payload, typeInfo);
        return value as IDomainEvent
            ?? throw new JsonException($"The outbox payload could not be deserialized as '{eventType.FullName}'.");
    }
}
