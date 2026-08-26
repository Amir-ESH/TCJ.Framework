using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TCJ.AspNetCore.Extensions;
using TCJ.AspNetCore.Results;
using TCJ.Core.Results;
using TCJ.Core.StrongTypes;

VerifyStrongIdJson();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, NativeAotSmokeJsonContext.Default));
builder.Services.AddTcjAspNetCore();

await using WebApplication app = builder.Build();
app.UseTcjAspNetCore();
app.MapGet("/success", () => Result.Success(new SmokeResponse("ok")).ToHttpResult());
app.MapGet("/validation", () =>
    Result.Failure(CommonErrors.ValidationForField("Name", "Name is required.")).ToHttpResult());
app.MapGet("/not-found", () =>
    Result.Failure(CommonErrors.NotFound("Widget", 42)).ToHttpResult());
app.MapGet("/conflict", () =>
    Result.Failure(CommonErrors.Conflict("The widget already exists.")).ToHttpResult());
app.MapGet("/unhandled", ThrowUnhandled);

await app.StartAsync();
IServer server = app.Services.GetRequiredService<IServer>();
IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
    ?? throw new InvalidOperationException("Server addresses feature is unavailable.");
string address = addresses.Addresses.Single();

using var client = new HttpClient { BaseAddress = new Uri(address) };
await AssertResponseAsync(client, "/success", HttpStatusCode.OK, "\"value\":\"ok\"");
await AssertResponseAsync(client, "/validation", HttpStatusCode.BadRequest, "Name is required.", "VALIDATION_FAILED");
await AssertResponseAsync(client, "/not-found", HttpStatusCode.NotFound, "NOT_FOUND");
await AssertResponseAsync(client, "/conflict", HttpStatusCode.Conflict, "CONFLICT");
string unhandledBody = await AssertResponseAsync(client, "/unhandled", HttpStatusCode.InternalServerError, "UNEXPECTED_ERROR");
if (unhandledBody.Contains("native-aot-sensitive-detail", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Unhandled exception details leaked from the Native AOT host.");
}

await app.StopAsync();
Console.WriteLine("TCJ.AspNetCore Native AOT smoke passed");

static void VerifyStrongIdJson()
{
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    // TCJ and System.Text.Json generators share the same input compilation, so register the concrete generated converters explicitly.
    options.Converters.Add(new NativeAotGuidId.StrongIdJsonConverter());
    options.Converters.Add(new NativeAotIntId.StrongIdJsonConverter());
    options.Converters.Add(new NativeAotLongId.StrongIdJsonConverter());
    var jsonContext = new NativeAotSmokeJsonContext(options);

    var guidValue = Guid.Parse("7a29be31-268d-4f2b-babc-fce0ce1cb46c");
    var guidId = new NativeAotGuidId(guidValue);
    var intId = new NativeAotIntId(-42);
    var longId = new NativeAotLongId(long.MaxValue);

    string guidJson = JsonSerializer.Serialize(guidId, jsonContext.NativeAotGuidId);
    string intJson = JsonSerializer.Serialize(intId, jsonContext.NativeAotIntId);
    string longJson = JsonSerializer.Serialize(longId, jsonContext.NativeAotLongId);

    if (!string.Equals(guidJson, "\"7a29be31-268d-4f2b-babc-fce0ce1cb46c\"", StringComparison.Ordinal) ||
        !string.Equals(intJson, "-42", StringComparison.Ordinal) ||
        !string.Equals(longJson, "9223372036854775807", StringComparison.Ordinal) ||
        JsonSerializer.Deserialize(guidJson, jsonContext.NativeAotGuidId) != guidId ||
        JsonSerializer.Deserialize(intJson, jsonContext.NativeAotIntId) != intId ||
        JsonSerializer.Deserialize(longJson, jsonContext.NativeAotLongId) != longId)
    {
        throw new InvalidOperationException("Generated Strong ID JSON converters did not preserve the scalar round-trip contract.");
    }
}

static Microsoft.AspNetCore.Http.IResult ThrowUnhandled() =>
    throw new InvalidOperationException("native-aot-sensitive-detail");

static async Task<string> AssertResponseAsync(HttpClient client, string path, HttpStatusCode expectedStatus, params string[] requiredFragments)
{
    using HttpResponseMessage response = await client.GetAsync(path);
    string body = await response.Content.ReadAsStringAsync();
    if (response.StatusCode != expectedStatus)
    {
        throw new InvalidOperationException($"{path} returned {(int)response.StatusCode}; expected {(int)expectedStatus}. Body: {body}");
    }

    foreach (string fragment in requiredFragments)
    {
        if (!body.Contains(fragment, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{path} response did not contain required fragment {fragment}. Body: {body}");
        }
    }

    return body;
}

internal sealed record SmokeResponse(string Value);

[StronglyTypedId<Guid>]
internal readonly partial record struct NativeAotGuidId;

[StronglyTypedId<int>]
internal readonly partial record struct NativeAotIntId;

[StronglyTypedId<long>]
internal readonly partial record struct NativeAotLongId;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SmokeResponse))]
[JsonSerializable(typeof(NativeAotGuidId))]
[JsonSerializable(typeof(NativeAotIntId))]
[JsonSerializable(typeof(NativeAotLongId))]
internal sealed partial class NativeAotSmokeJsonContext : JsonSerializerContext
{
}
