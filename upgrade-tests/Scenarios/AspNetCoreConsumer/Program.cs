using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using HttpResults = Microsoft.AspNetCore.Http.Results;
using TCJ.AspNetCore.Extensions;
using TCJ.Core.Security;
using TCJ.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddTcjDependencyInjection(typeof(Program).Assembly);
builder.Services.AddTcjAspNetCore();

await using WebApplication app = builder.Build();
app.UseTcjAspNetCore();
app.MapGet("/ok", (ICurrentUserProvider currentUser) => HttpResults.Ok(new { ok = true, authenticated = currentUser.UserId is not null }));
app.MapGet("/handled-error", ThrowHandledError);

await app.StartAsync();
IServer server = app.Services.GetRequiredService<IServer>();
IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
    ?? throw new InvalidOperationException("Server addresses feature is unavailable.");
string address = addresses.Addresses.Single();
using var client = new HttpClient { BaseAddress = new Uri(address) };
using HttpResponseMessage ok = await client.GetAsync("/ok");
string okBody = await ok.Content.ReadAsStringAsync();
using HttpResponseMessage error = await client.GetAsync("/handled-error");
string errorBody = await error.Content.ReadAsStringAsync();

var behavior = new
{
    schemaVersion = 1,
    scenario = "AspNetCoreConsumer",
    checks = new
    {
        startupSucceeded = true,
        successEndpoint = ok.IsSuccessStatusCode && okBody.Contains("\"ok\":true", StringComparison.Ordinal),
        errorMapped = (int)error.StatusCode == StatusCodes.Status500InternalServerError,
        productionSafeError = !errorBody.Contains("upgrade-secret", StringComparison.Ordinal),
        currentUserResolved = okBody.Contains("\"authenticated\":false", StringComparison.Ordinal),
    },
};

await WriteBehaviorAsync(behavior);
await app.StopAsync();
Console.WriteLine("TCJ.AspNetCore upgrade scenario passed");

static Microsoft.AspNetCore.Http.IResult ThrowHandledError() => throw new InvalidOperationException("upgrade-secret");

static async Task WriteBehaviorAsync<T>(T value)
{
    string path = Environment.GetEnvironmentVariable("TCJ_UPGRADE_BEHAVIOR_PATH")
        ?? throw new InvalidOperationException("TCJ_UPGRADE_BEHAVIOR_PATH is required.");
    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Behavior path has no directory."));
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
