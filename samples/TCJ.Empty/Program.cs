using TCJ.AspNetCore.Extensions;
using TCJ.DependencyInjection.Extensions;
using TCJ.Empty.Data;
using TCJ.Empty.Products;
using TCJ.EntityFrameworkCore.Seeding;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString(name: "Default")
    ?? throw new InvalidOperationException("Connection string 'Default' was not found.");

builder.Services.AddOpenApi();
builder.Services.AddTcjDependencyInjection(typeof(Program).Assembly);
builder.Services.AddTcjAspNetCore();

builder.Services.AddTcjSqlServer<AppDbContext>(connectionString, configureTcjSqlServer: options =>
                                               {
                                                   // The sample data seeder owns an explicit transaction. Retry is disabled in
                                                   // // this local sample until transaction execution-strategy orchestration is added.
                                                   options.EnableRetryOnFailure = false;
                                                   options.CommandTimeout = 30;
                                               });

builder.Services.AddTcjDataSeedContributor<ProductSeedContributor>();

WebApplication app = builder.Build();

app.UseTcjAspNetCore();
app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    await EnsureDatabaseCreatedAsync(app.Services);
    await app.Services.SeedTcjDataAsync();
}

app.MapProductEndpoints();

app.Run();

static async Task EnsureDatabaseCreatedAsync(IServiceProvider serviceProvider)
{
    await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();

    AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    await dbContext.Database.EnsureCreatedAsync();
}
