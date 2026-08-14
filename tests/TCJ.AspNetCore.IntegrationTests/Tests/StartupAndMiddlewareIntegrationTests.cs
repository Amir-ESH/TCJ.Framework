using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TCJ.AspNetCore.IntegrationTests.Fixtures;
using TCJ.AspNetCore.Options;
using TCJ.AspNetCore.Security;
using TCJ.Core.Security;

namespace TCJ.AspNetCore.IntegrationTests.Tests;

[Collection(AspNetCoreIntegrationCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "AspNetCore")]
[Trait("Category", "Startup")]
[Trait("Category", "Middleware")]
public sealed class StartupAndMiddlewareIntegrationTests(TcjWebApplicationFactory factory)
{
    [Fact]
    public async Task Application_starts_and_health_endpoint_responds_over_test_server()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Public_registration_api_resolves_framework_logging_and_exception_services()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/services/framework");
        using var json = await TestHttp.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(nameof(HttpContextCurrentUserProvider), json.RootElement.GetProperty("currentUser").GetString(), StringComparison.Ordinal);
        Assert.True(json.RootElement.GetProperty("loggerAvailable").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("currentUserProviderCount").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("problemDetailsServiceCount").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("exceptionHandlerCount").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("problemDetails").GetString()));
    }

    [Fact]
    public async Task Invalid_framework_options_fail_startup_with_actionable_validation_error()
    {
        OptionsValidationException error = await Assert.ThrowsAsync<OptionsValidationException>(async () =>
        {
            await using TcjWebApplicationFactory invalid = await TcjWebApplicationFactory.StartAsync(
                Environments.Production,
                configureTcj: options => options.UserIdClaimType = string.Empty);
        });

        Assert.Contains(nameof(TcjAspNetCoreOptions.UserIdClaimType), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_framework_registration_keeps_single_default_exception_handler()
    {
        await using TcjWebApplicationFactory duplicate = await TcjWebApplicationFactory.StartAsync(
            Environments.Production,
            duplicateRegistration: true);
        using HttpClient client = duplicate.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/services/framework");
        using var json = await TestHttp.ReadJsonAsync(response);

        Assert.Equal(1, json.RootElement.GetProperty("currentUserProviderCount").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("problemDetailsServiceCount").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("exceptionHandlerCount").GetInt32());
    }

    [Fact]
    public async Task Tcj_middleware_converts_empty_error_status_to_problem_details()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/empty-not-found");
        using var json = await TestHttp.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(404, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("/empty-not-found", json.RootElement.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task Missing_tcj_middleware_leaves_unhandled_exception_outside_framework_handler()
    {
        await using TcjWebApplicationFactory withoutMiddleware = await TcjWebApplicationFactory.StartAsync(
            Environments.Production,
            includeTcjMiddleware: false);
        using HttpClient client = withoutMiddleware.CreateClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/errors/unhandled"));
    }
}
