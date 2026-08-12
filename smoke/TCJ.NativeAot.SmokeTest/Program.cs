using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TCJ.AspNetCore.Extensions;
using TCJ.AspNetCore.Results;
using TCJ.Core.DomainEvents;
using TCJ.Core.Results;
using TCJ.DependencyInjection.Extensions;
using AspNetCoreServiceCollectionExtensions = TCJ.AspNetCore.Extensions.ServiceCollectionExtensions;
using DependencyInjectionServiceCollectionExtensions = TCJ.DependencyInjection.Extensions.ServiceCollectionExtensions;

var expectedVersion = ReadAssemblyMetadata(typeof(Program).Assembly, "ExpectedTcjPackageVersion");
AssertPackageVersion("TCJ.Core", typeof(Result).Assembly, expectedVersion);
AssertPackageVersion("TCJ.DependencyInjection", typeof(DependencyInjectionServiceCollectionExtensions).Assembly, expectedVersion);
AssertPackageVersion("TCJ.AspNetCore", typeof(AspNetCoreServiceCollectionExtensions).Assembly, expectedVersion);

var dispatchProbe = new DispatchProbe();
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, NativeAotSmokeJsonContext.Default));
builder.Services.AddTcjDependencyInjection();
builder.Services.AddTcjDomainEvent<SmokeDomainEvent>();
builder.Services.AddSingleton(dispatchProbe);
builder.Services.AddTransient<IDomainEventHandler<SmokeDomainEvent>, SmokeDomainEventHandler>();
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
app.MapPost("/domain-event", async (IDomainEventDispatcher dispatcher, CancellationToken cancellationToken) =>
{
    await dispatcher.DispatchAsync([new SmokeDomainEvent(42, DateTimeOffset.UnixEpoch)], cancellationToken);
    return Results.Ok(new DispatchResponse(dispatchProbe.Count, dispatchProbe.LastSequence));
});

await app.StartAsync();
IServer server = app.Services.GetRequiredService<IServer>();
IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
    ?? throw new InvalidOperationException("Server addresses feature is unavailable.");
string address = addresses.Addresses.Single();

using var client = new HttpClient { BaseAddress = new Uri(address) };
await AssertResponseAsync(client, HttpMethod.Get, "/success", HttpStatusCode.OK, "\"value\":\"ok\"");
await AssertResponseAsync(client, HttpMethod.Get, "/validation", HttpStatusCode.BadRequest, "Name is required.", "VALIDATION_FAILED");
await AssertResponseAsync(client, HttpMethod.Get, "/not-found", HttpStatusCode.NotFound, "NOT_FOUND");
await AssertResponseAsync(client, HttpMethod.Get, "/conflict", HttpStatusCode.Conflict, "CONFLICT");
string unhandledBody = await AssertResponseAsync(client, HttpMethod.Get, "/unhandled", HttpStatusCode.InternalServerError, "UNEXPECTED_ERROR");
if (unhandledBody.Contains("native-aot-sensitive-detail", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Unhandled exception details leaked from the packaged Native AOT host.");
}
await AssertResponseAsync(client, HttpMethod.Post, "/domain-event", HttpStatusCode.OK, "\"count\":1", "\"lastSequence\":42");

if (dispatchProbe.Count != 1 || dispatchProbe.LastSequence != 42)
{
    throw new InvalidOperationException("The explicit AOT-safe domain-event route did not invoke exactly one handler.");
}

await app.StopAsync();
Console.WriteLine("TCJ Native AOT packed-package smoke passed");

static Microsoft.AspNetCore.Http.IResult ThrowUnhandled() =>
    throw new InvalidOperationException("native-aot-sensitive-detail");

static string ReadAssemblyMetadata(Assembly assembly, string key)
{
    string? value = assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
        ?.Value;

    return string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"Assembly '{assembly.GetName().Name}' does not contain metadata '{key}'.")
        : value;
}

static void AssertPackageVersion(string packageId, Assembly assembly, string expectedVersion)
{
    string actualVersion = ReadAssemblyMetadata(assembly, "PackageVersion");
    if (!string.Equals(actualVersion, expectedVersion, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Loaded package '{packageId}' reports version '{actualVersion}', expected '{expectedVersion}'.");
    }

    Console.WriteLine($"TCJ_PACKAGE_VERSION {packageId} {actualVersion}");
}

static async Task<string> AssertResponseAsync(
    HttpClient client,
    HttpMethod method,
    string path,
    HttpStatusCode expectedStatus,
    params string[] requiredFragments)
{
    using var request = new HttpRequestMessage(method, path);
    using HttpResponseMessage response = await client.SendAsync(request);
    string body = await response.Content.ReadAsStringAsync();
    if (response.StatusCode != expectedStatus)
    {
        throw new InvalidOperationException(
            $"{path} returned {(int)response.StatusCode}; expected {(int)expectedStatus}. Body: {body}");
    }

    foreach (string fragment in requiredFragments)
    {
        if (!body.Contains(fragment, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{path} response did not contain required fragment {fragment}. Body: {body}");
        }
    }

    return body;
}

internal sealed record SmokeResponse(string Value);
internal sealed record DispatchResponse(int Count, int LastSequence);
internal sealed record SmokeDomainEvent(int Sequence, DateTimeOffset OccurredOn) : IDomainEvent;

internal sealed class DispatchProbe
{
    public int Count { get; private set; }
    public int LastSequence { get; private set; }

    public void Record(int sequence)
    {
        Count++;
        LastSequence = sequence;
    }
}

internal sealed class SmokeDomainEventHandler(DispatchProbe probe) : IDomainEventHandler<SmokeDomainEvent>
{
    public Task HandleAsync(SmokeDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        probe.Record(domainEvent.Sequence);
        return Task.CompletedTask;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SmokeResponse))]
[JsonSerializable(typeof(DispatchResponse))]
internal sealed partial class NativeAotSmokeJsonContext : JsonSerializerContext
{
}
