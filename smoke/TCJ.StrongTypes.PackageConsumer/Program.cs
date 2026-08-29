using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TCJ.AspNetCore.Extensions;
using TCJ.Core.Results;
using TCJ.Core.StrongTypes;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.StrongTypes;

var orderId = new OrderId(Guid.Parse("7a29be31-268d-4f2b-babc-fce0ce1cb46c"));
Result<EmailAddress> emailResult = EmailAddress.Create("  Customer@Example.com  ");
if (emailResult.IsFailure || emailResult.Value.Value != "customer@example.com")
{
    throw new InvalidOperationException("Generated Value Object normalization or validation failed.");
}

EmailAddress email = emailResult.Value;
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
jsonOptions.Converters.Add(new OrderId.StrongIdJsonConverter());
jsonOptions.Converters.Add(new EmailAddress.ValueObjectJsonConverter());
var jsonContext = new StrongTypesPackageJsonContext(jsonOptions);

string orderJson = JsonSerializer.Serialize(orderId, jsonContext.OrderId);
string emailJson = JsonSerializer.Serialize(email, jsonContext.EmailAddress);
OrderId jsonOrderId = JsonSerializer.Deserialize(orderJson, jsonContext.OrderId);
EmailAddress jsonEmail = JsonSerializer.Deserialize(emailJson, jsonContext.EmailAddress);
if (jsonOrderId != orderId || jsonEmail != email || orderJson != $"\"{orderId}\"" || emailJson != "\"customer@example.com\"")
{
    throw new InvalidOperationException("Generated Strong Type JSON converters did not preserve the scalar contract.");
}

var dbOptions = new DbContextOptionsBuilder<StrongTypesDbContext>()
    .UseInMemoryDatabase("strong-types-packed-consumer")
    .Options;
await using (var dbContext = new StrongTypesDbContext(dbOptions))
{
    dbContext.Records.Add(new StrongTypesRecord { Id = orderId, Email = email });
    await dbContext.SaveChangesAsync();
    dbContext.ChangeTracker.Clear();

    StrongTypesRecord stored = await dbContext.Records.SingleAsync();
    if (stored.Id != orderId || stored.Email != email)
    {
        throw new InvalidOperationException("Packed EF Core Strong Type round-trip did not preserve generated values.");
    }

    var idProperty = dbContext.Model.FindEntityType(typeof(StrongTypesRecord))?.FindProperty(nameof(StrongTypesRecord.Id));
    var emailProperty = dbContext.Model.FindEntityType(typeof(StrongTypesRecord))?.FindProperty(nameof(StrongTypesRecord.Email));
    if (idProperty?.GetValueConverter()?.ProviderClrType != typeof(Guid)
        || emailProperty?.GetValueConverter()?.ProviderClrType != typeof(string))
    {
        throw new InvalidOperationException("Packed EF Core Strong Type conversions were not applied as primitive provider shapes.");
    }
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddTcjAspNetCore();

await using WebApplication app = builder.Build();
app.UseTcjAspNetCore();
app.MapGet("/orders/{id}/{email}", ParseStrongTypes);

await app.StartAsync();
IServer server = app.Services.GetRequiredService<IServer>();
IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
    ?? throw new InvalidOperationException("Server addresses feature is unavailable.");
string address = addresses.Addresses.Single();

using var client = new HttpClient { BaseAddress = new Uri(address) };
using HttpResponseMessage response = await client.GetAsync($"/orders/{orderId}/{Uri.EscapeDataString(email.Value)}");
string body = await response.Content.ReadAsStringAsync();
if (response.StatusCode != HttpStatusCode.OK
    || !body.Contains(orderId.ToString(), StringComparison.Ordinal)
    || !body.Contains(email.Value, StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Minimal API generated Strong Type binding failed. Body: {body}");
}

await app.StopAsync();
Console.WriteLine("TCJ strong-types packed consumer passed");

static IResult ParseStrongTypes(OrderId id, EmailAddress email)
    => Results.Text($"{id}|{email.Value}");

[StronglyTypedId<Guid>]
public readonly partial record struct OrderId;

[ValueObject<string>]
public readonly partial record struct EmailAddress
{
    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static Result Validate(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Contains('@')
            ? Result.Success()
            : Result.Failure(new ResultError("email.invalid", "Email must contain an '@' character."));
}

public sealed class StrongTypesRecord
{
    public OrderId Id { get; set; }

    public EmailAddress Email { get; set; }
}

public sealed class StrongTypesDbContext(DbContextOptions<StrongTypesDbContext> options) : DbContext(options)
{
    public DbSet<StrongTypesRecord> Records => Set<StrongTypesRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StrongTypesRecord>(entity =>
        {
            entity.HasKey(static record => record.Id);
            entity.Property(static record => record.Email).HasMaxLength(320);
        });

        var strongIds = new StrongIdConversionRegistry()
            .Register<OrderId, Guid>(
                OrderId.StrongIdConversion.ToBackingValue,
                OrderId.StrongIdConversion.FromBackingValue);
        var valueObjects = new ValueObjectConversionRegistry()
            .Register<EmailAddress, string>(
                EmailAddress.ValueObjectConversion.ToBackingValue,
                EmailAddress.ValueObjectConversion.FromBackingValue);

        modelBuilder.ApplyStrongIdConversions(strongIds);
        modelBuilder.ApplyValueObjectConversions(valueObjects);
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
[System.Text.Json.Serialization.JsonSerializable(typeof(OrderId))]
[System.Text.Json.Serialization.JsonSerializable(typeof(EmailAddress))]
internal sealed partial class StrongTypesPackageJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
