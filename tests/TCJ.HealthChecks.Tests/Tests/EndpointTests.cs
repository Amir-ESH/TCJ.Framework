using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TCJ.AspNetCore.Extensions;
using TCJ.AspNetCore.HealthChecks;
using TCJ.HealthChecks.Tests.Infrastructure;

namespace TCJ.HealthChecks.Tests.Tests;

[Trait("Category", "Integration")]
[Trait("Category", "HealthChecks")]
[Trait("Category", "AspNetCore")]
public sealed class EndpointTests
{
    [Fact]
    public async Task Default_liveness_endpoint_returns_healthy_json()
    {
        var (app, client) = await HealthWebHost.StartAsync();
        await using (app)
        using (client)
        using (HttpResponseMessage response = await client.GetAsync(TcjHealthEndpointDefaults.LivenessPath))
        {
            string body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Healthy", body, StringComparison.Ordinal);
            Assert.DoesNotContain("exception", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Readiness_endpoint_returns_503_for_required_unhealthy_dependency_while_liveness_stays_200()
    {
        var (app, client) = await HealthWebHost.StartAsync(
            services => services.AddHealthChecks().AddCheck("app.required", () => HealthCheckResult.Unhealthy("secret=TCJ_TEST_SECRET"), tags: ["ready"]));
        await using (app)
        using (client)
        {
            using HttpResponseMessage ready = await client.GetAsync(TcjHealthEndpointDefaults.ReadinessPath);
            using HttpResponseMessage live = await client.GetAsync(TcjHealthEndpointDefaults.LivenessPath);
            string body = await ready.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.DoesNotContain("TCJ_TEST_SECRET", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Public_response_hides_exception_message_and_check_descriptions_in_production()
    {
        var (app, client) = await HealthWebHost.StartAsync(
            services => services.AddHealthChecks().AddCheck("app.secret", () => HealthCheckResult.Unhealthy("server=TCJ_TEST_SERVER;password=TCJ_TEST_PASSWORD", new InvalidOperationException("TCJ_TEST_EXCEPTION")), tags: ["ready"]));
        await using (app)
        using (client)
        using (HttpResponseMessage response = await client.GetAsync(TcjHealthEndpointDefaults.ReadinessPath))
        {
            string body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("TCJ_TEST_SERVER", body, StringComparison.Ordinal);
            Assert.DoesNotContain("TCJ_TEST_PASSWORD", body, StringComparison.Ordinal);
            Assert.DoesNotContain("TCJ_TEST_EXCEPTION", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Public_response_hides_exception_details_in_development_too()
    {
        var (app, client) = await HealthWebHost.StartAsync(
            services => services.AddHealthChecks().AddCheck("app.secret", () => HealthCheckResult.Unhealthy("TCJ_TEST_DB_NAME"), tags: ["ready"]),
            environment: "Development");
        await using (app)
        using (client)
        using (HttpResponseMessage response = await client.GetAsync(TcjHealthEndpointDefaults.ReadinessPath))
        {
            Assert.DoesNotContain("TCJ_TEST_DB_NAME", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Custom_response_writer_is_used_without_duplicate_framework_output()
    {
        var (app, client) = await HealthWebHost.StartAsync(
            mapEndpoints: app => app.MapTcjReadinessChecks("/custom-ready", options => options.ResponseWriter = async (context, _) => await context.Response.WriteAsync("custom-health")));
        await using (app)
        using (client)
        using (HttpResponseMessage response = await client.GetAsync("/custom-ready"))
        {
            Assert.Equal("custom-health", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Custom_path_does_not_map_default_path_unexpectedly()
    {
        var (app, client) = await HealthWebHost.StartAsync(mapEndpoints: app => app.MapTcjReadinessChecks("/probe/ready"));
        await using (app)
        using (client)
        {
            using HttpResponseMessage custom = await client.GetAsync("/probe/ready");
            using HttpResponseMessage original = await client.GetAsync(TcjHealthEndpointDefaults.ReadinessPath);
            Assert.Equal(HttpStatusCode.OK, custom.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, original.StatusCode);
        }
    }

    [Fact]
    public async Task Duplicate_endpoint_mapping_is_idempotent()
    {
        var (app, client) = await HealthWebHost.StartAsync(mapEndpoints: app =>
        {
            app.MapTcjLivenessChecks();
            app.MapTcjLivenessChecks();
        });
        await using (app)
        using (client)
        using (HttpResponseMessage response = await client.GetAsync(TcjHealthEndpointDefaults.LivenessPath))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Detailed_endpoint_requires_authorization_by_default()
    {
        var (app, client) = await HealthWebHost.StartAsync(mapEndpoints: app => app.MapTcjHealthDetails());
        await using (app)
        using (client)
        using (HttpResponseMessage response = await client.GetAsync(TcjHealthEndpointDefaults.DetailsPath))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }


    [Fact]
    public async Task Detailed_endpoint_supports_consumer_authorization_policy_and_stays_sanitized()
    {
        var (app, client) = await HealthWebHost.StartAsync(
            services =>
            {
                services.AddAuthorization(options =>
                    options.AddPolicy("health-details", policy => policy.RequireClaim("scope", "health.details")));
                services.AddHealthChecks().AddCheck(
                    "server=TCJ_TEST_SERVER",
                    () => HealthCheckResult.Unhealthy("TCJ_TEST_SECRET", new InvalidOperationException("TCJ_TEST_EXCEPTION")),
                    tags: ["tcj", "password=TCJ_TEST_PASSWORD"]);
            },
            mapEndpoints: app => app.MapTcjHealthDetails().RequireAuthorization("health-details"));
        await using (app)
        using (client)
        {
            client.DefaultRequestHeaders.Add("X-Health-Test-Auth", "true");
            using HttpResponseMessage response = await client.GetAsync(TcjHealthEndpointDefaults.DetailsPath);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("custom", body, StringComparison.Ordinal);
            Assert.DoesNotContain("TCJ_TEST_SERVER", body, StringComparison.Ordinal);
            Assert.DoesNotContain("TCJ_TEST_PASSWORD", body, StringComparison.Ordinal);
            Assert.DoesNotContain("TCJ_TEST_SECRET", body, StringComparison.Ordinal);
            Assert.DoesNotContain("TCJ_TEST_EXCEPTION", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Degraded_readiness_maps_to_http_200()
    {
        var (app, client) = await HealthWebHost.StartAsync(
            services => services.AddHealthChecks().AddCheck("app.optional", () => HealthCheckResult.Degraded(), tags: ["ready"]));
        await using (app)
        using (client)
        using (HttpResponseMessage response = await client.GetAsync(TcjHealthEndpointDefaults.ReadinessPath))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Combined_endpoint_contains_only_safe_aggregate_response_by_default()
    {
        var (app, client) = await HealthWebHost.StartAsync(mapEndpoints: app => app.MapTcjHealthChecks());
        await using (app)
        using (client)
        using (HttpResponseMessage response = await client.GetAsync("/health"))
        {
            string body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("status", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tcj.domain_events", body, StringComparison.Ordinal);
        }
    }
}
