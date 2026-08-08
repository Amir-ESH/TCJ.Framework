using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TCJ.AspNetCore.Extensions;
using TCJ.Core.Identifiers;
using TCJ.Core.Security;
using TCJ.DependencyInjection.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.UnitOfWork;
using TcjUpgrade.FullStackConsumer;

const string connectionString = "Server=127.0.0.1,1433;Database=TcjUpgradeFullStack;Integrated Security=True;Encrypt=False;TrustServerCertificate=True";
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddTcjDependencyInjection(typeof(Program).Assembly);
builder.Services.AddTcjSqlServer<FullStackDbContext>(connectionString, options => options.EnableRetryOnFailure = false);
builder.Services.AddTcjAspNetCore();

await using WebApplication app = builder.Build();
app.UseTcjAspNetCore();
app.MapGet("/framework", (IUnitOfWork unitOfWork, ICurrentUserProvider currentUser, IGuidGenerator guidGenerator, FullStackDbContext dbContext) =>
    Results.Ok(new
    {
        unitOfWork = unitOfWork.GetType().Name,
        authenticated = currentUser.UserId is not null,
        guidAvailable = guidGenerator.CreateVersion7() != Guid.Empty,
        provider = dbContext.Database.ProviderName,
    }));

await app.StartAsync();
IServer server = app.Services.GetRequiredService<IServer>();
IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
    ?? throw new InvalidOperationException("Server addresses feature is unavailable.");
string address = addresses.Addresses.Single();
using var client = new HttpClient { BaseAddress = new Uri(address) };
using HttpResponseMessage response = await client.GetAsync("/framework");
string body = await response.Content.ReadAsStringAsync();

var behavior = new
{
    schemaVersion = 1,
    scenario = "FullStackConsumer",
    checks = new
    {
        startupSucceeded = true,
        endpointSucceeded = response.IsSuccessStatusCode,
        sqlServerProvider = body.Contains("Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal),
        unitOfWorkResolved = body.Contains("EfUnitOfWork", StringComparison.Ordinal),
        currentUserResolved = body.Contains("\"authenticated\":false", StringComparison.Ordinal),
        guidGeneratorResolved = body.Contains("\"guidAvailable\":true", StringComparison.Ordinal),
    },
};

await WriteBehaviorAsync(behavior);
await app.StopAsync();
Console.WriteLine("TCJ full-stack upgrade scenario passed");

static async Task WriteBehaviorAsync<T>(T value)
{
    string path = Environment.GetEnvironmentVariable("TCJ_UPGRADE_BEHAVIOR_PATH")
        ?? throw new InvalidOperationException("TCJ_UPGRADE_BEHAVIOR_PATH is required.");
    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Behavior path has no directory."));
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
