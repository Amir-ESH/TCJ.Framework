using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TCJ.AspNetCore.Extensions;
using TCJ.AspNetCore.Results;
using TCJ.Core.Results;

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

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SmokeResponse))]
internal sealed partial class NativeAotSmokeJsonContext : JsonSerializerContext
{
}
