using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCJ.Messaging.AsyncApi;

/// <summary>AOT-friendly JSON serialization for messaging catalog input.</summary>
public static class MessagingCatalogJson
{
    public static string Serialize(MessagingCatalog catalog, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var options = new JsonSerializerOptions(MessagingCatalogJsonSerializerContext.Default.Options)
        {
            WriteIndented = indented
        };
        var context = new MessagingCatalogJsonSerializerContext(options);
        return JsonSerializer.Serialize(catalog, context.MessagingCatalog);
    }

    public static MessagingCatalog Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, MessagingCatalogJsonSerializerContext.Default.MessagingCatalog)
            ?? throw new JsonException("Messaging catalog JSON must contain an object.");
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(MessagingCatalog))]
internal sealed partial class MessagingCatalogJsonSerializerContext : JsonSerializerContext;
