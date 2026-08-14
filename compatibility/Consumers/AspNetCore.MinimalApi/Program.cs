using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TCJ.AspNetCore.Extensions;
using TCJ.AspNetCore.Results;
using TCJ.Core.Results;
using TCJ.Core.Security;
using TCJ.DependencyInjection.Extensions;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AspNetCoreCompatibilityJsonContext.Default));
builder.Services.AddTcjDependencyInjection();
builder.Services.AddTcjAspNetCore();

await using WebApplication app = builder.Build();
app.UseTcjAspNetCore();
app.MapGet("/ok", (ICurrentUserProvider currentUser) =>
    Result.Success(new CompatibilityResponse(true, currentUser.UserId)).ToHttpResult());
app.MapGet("/validation", () =>
    Result.Failure(CommonErrors.ValidationForField("Name", "Name is required.")).ToHttpResult());
app.MapGet("/not-found", () =>
    Result.Failure(CommonErrors.NotFound("Widget", 42)).ToHttpResult());
app.MapGet("/conflict", () =>
    Result.Failure(CommonErrors.Conflict("The widget already exists.")).ToHttpResult());
app.MapGet("/handled-error", ThrowHandledError);

await app.StartAsync();
IServer server = app.Services.GetRequiredService<IServer>();
IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
    ?? throw new InvalidOperationException("Server addresses feature is unavailable.");
string address = addresses.Addresses.Single();

using var client = new HttpClient { BaseAddress = new Uri(address) };
await AssertResponseAsync(client, "/ok", HttpStatusCode.OK, "\"ok\":true");
await AssertResponseAsync(client, "/validation", HttpStatusCode.BadRequest, "Name is required.", "VALIDATION_FAILED");
await AssertResponseAsync(client, "/not-found", HttpStatusCode.NotFound, "NOT_FOUND");
await AssertResponseAsync(client, "/conflict", HttpStatusCode.Conflict, "CONFLICT");
string unhandledBody = await AssertResponseAsync(client, "/handled-error", HttpStatusCode.InternalServerError, "UNEXPECTED_ERROR");
if (unhandledBody.Contains("compatibility-secret", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Unhandled exception details leaked to the response.");
}

await app.StopAsync();
Console.WriteLine("TCJ.AspNetCore consumer passed");

static Microsoft.AspNetCore.Http.IResult ThrowHandledError() =>
    throw new InvalidOperationException("compatibility-secret");

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

internal sealed record CompatibilityResponse(bool Ok, long? UserId);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(CompatibilityResponse))]
internal sealed partial class AspNetCoreCompatibilityJsonContext : JsonSerializerContext
{
}
