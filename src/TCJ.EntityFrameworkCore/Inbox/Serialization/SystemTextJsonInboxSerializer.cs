using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TCJ.Core.Inbox;

namespace TCJ.EntityFrameworkCore.Inbox.Serialization;

/// <summary>Default Inbox serializer that only deserializes CLR types selected by the explicit message registry.</summary>
public sealed class SystemTextJsonInboxSerializer : IInboxSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>Creates the serializer from validated Inbox JSON settings.</summary>
    /// <param name="options">Inbox options.</param>
    public SystemTextJsonInboxSerializer(TcjInboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = new JsonSerializerOptions(options.JsonSerializerOptions);
        if (_options.TypeInfoResolver is null && JsonSerializer.IsReflectionEnabledByDefault)
        {
            _options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        }
    }

    /// <inheritdoc />
    public object Deserialize(Type messageType, string payload)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(payload);
        JsonTypeInfo typeInfo = _options.GetTypeInfo(messageType);
        return JsonSerializer.Deserialize(payload, typeInfo)
            ?? throw new JsonException($"Inbox payload could not be deserialized as registered type '{messageType.FullName}'.");
    }
}
