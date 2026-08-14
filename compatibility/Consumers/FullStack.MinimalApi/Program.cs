using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using TCJ.AspNetCore.Extensions;
using TCJ.Core.Identifiers;
using TCJ.Core.Security;
using TCJ.DependencyInjection.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.UnitOfWork;
using TcjCompatibility.FullStackConsumer;

const string connectionString = "Server=127.0.0.1,1433;Database=TcjFullStackCompatibility;Integrated Security=True;Encrypt=False;TrustServerCertificate=True";

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
        currentUser = currentUser.UserId,
        guid = guidGenerator.CreateVersion7(),
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
if (!response.IsSuccessStatusCode ||
    !body.Contains("Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal) ||
    !body.Contains("EfUnitOfWork", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Full-stack package combination failed.");
}

await app.StopAsync();
Console.WriteLine("TCJ full-stack consumer passed");

