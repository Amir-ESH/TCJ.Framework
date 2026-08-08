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
app.MapGet("/ok", (ICurrentUserProvider currentUser) => HttpResults.Ok(new { ok = true, currentUser.UserId }));
app.MapGet("/handled-error", ThrowHandledError);

await app.StartAsync();
IServer server = app.Services.GetRequiredService<IServer>();
IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
    ?? throw new InvalidOperationException("Server addresses feature is unavailable.");
string address = addresses.Addresses.Single();

using var client = new HttpClient { BaseAddress = new Uri(address) };
using HttpResponseMessage ok = await client.GetAsync("/ok");
string okBody = await ok.Content.ReadAsStringAsync();
if (!ok.IsSuccessStatusCode || !okBody.Contains("\"ok\":true", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Successful minimal API endpoint failed.");
}

using HttpResponseMessage error = await client.GetAsync("/handled-error");
string errorBody = await error.Content.ReadAsStringAsync();
if ((int)error.StatusCode != StatusCodes.Status500InternalServerError || errorBody.Contains("compatibility-secret", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Handled error endpoint failed.");
}

await app.StopAsync();
Console.WriteLine("TCJ.AspNetCore consumer passed");

static Microsoft.AspNetCore.Http.IResult ThrowHandledError() =>
    throw new InvalidOperationException("compatibility-secret");
