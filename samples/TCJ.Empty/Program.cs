using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TCJ.Core.Diagnostics;
using TCJ.AspNetCore.Extensions;
using TCJ.DependencyInjection.Extensions;
using TCJ.DependencyInjection.HealthChecks;
using TCJ.Empty.Data;
using TCJ.Empty.Products;
using TCJ.EntityFrameworkCore.Seeding;
using TCJ.EntityFrameworkCore.HealthChecks;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.HealthChecks;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString(name: "Default")
    ?? throw new InvalidOperationException("Connection string 'Default' was not found.");

builder.Services.AddOpenApi();
builder.Services.AddTcjDependencyInjection(typeof(Program).Assembly);
builder.Services.AddTcjAspNetCore();
builder.Services.AddTcjTelemetry();
builder.Services
    .AddTcjHealthChecks()
    .AddTcjDependencyInjection()
    .AddTcjDomainEvents()
    .AddTcjEntityFrameworkCore<AppDbContext>()
    .AddTcjSqlServer<AppDbContext>();

builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService("TCJ.Empty")
        .AddAttributes(
            [new KeyValuePair<string, object>(
                TcjDiagnosticNames.Tags.FrameworkVersion,
                TcjTelemetry.FrameworkVersion)]))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddSource(
            TcjDiagnosticNames.Sources.Core,
            TcjDiagnosticNames.Sources.DependencyInjection,
            TcjDiagnosticNames.Sources.EntityFrameworkCore,
            TcjDiagnosticNames.Sources.EntityFrameworkCoreSqlServer,
            TcjDiagnosticNames.Sources.AspNetCore)
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter(
            TcjDiagnosticNames.Sources.Core,
            TcjDiagnosticNames.Sources.DependencyInjection,
            TcjDiagnosticNames.Sources.EntityFrameworkCore,
            TcjDiagnosticNames.Sources.EntityFrameworkCoreSqlServer,
            TcjDiagnosticNames.Sources.AspNetCore)
        .AddOtlpExporter());

builder.Services.AddTcjSqlServer<AppDbContext>(connectionString, configureTcjSqlServer: options =>
                                               {
                                                   // The sample data seeder owns an explicit transaction. Retry is disabled in
                                                   // this local sample until transaction execution-strategy orchestration is added.
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

app.MapTcjLivenessChecks();
app.MapTcjReadinessChecks();
app.MapProductEndpoints();

app.Run();

static async Task EnsureDatabaseCreatedAsync(IServiceProvider serviceProvider)
{
    await using AsyncServiceScope scope = serviceProvider.CreateAsyncScope();

    AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    await dbContext.Database.EnsureCreatedAsync();
}
