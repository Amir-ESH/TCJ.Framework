using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TCJ.AspNetCore.Extensions;
using TCJ.DependencyInjection.Extensions;
using TCJ.DependencyInjection.HealthChecks;

namespace TCJ.HealthChecks.Tests.Infrastructure;

internal static class HealthWebHost
{
    internal static async Task<(WebApplication App, HttpClient Client)> StartAsync(
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? mapEndpoints = null,
        string environment = "Production")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ApplicationName = typeof(HealthWebHost).Assembly.FullName
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(TestAuthenticationHandler.Scheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.Scheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddTcjDependencyInjection();
        builder.Services.AddTcjHealthChecks()
            .AddTcjDependencyInjection()
            .AddTcjDomainEvents();
        configureServices?.Invoke(builder.Services);

        WebApplication app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        if (mapEndpoints is null)
        {
            app.MapTcjLivenessChecks();
            app.MapTcjReadinessChecks();
        }
        else
        {
            mapEndpoints(app);
        }
        await app.StartAsync().ConfigureAwait(false);
        return (app, app.GetTestClient());
    }
}

internal sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string Scheme = "HealthTests";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Health-Test-Auth", out var value) || !string.Equals(value.ToString(), "true", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "health-test"), new Claim("scope", "health.details")],
            Scheme);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        return Task.CompletedTask;
    }
}

internal sealed class FixedHealthCheck(HealthCheckResult result) : IHealthCheck
{
    internal int Executions;
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Executions);
        return Task.FromResult(result);
    }
}
