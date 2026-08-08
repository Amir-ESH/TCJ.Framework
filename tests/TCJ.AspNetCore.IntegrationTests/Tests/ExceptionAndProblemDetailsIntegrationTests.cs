using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using TCJ.AspNetCore.Diagnostics;
using TCJ.AspNetCore.IntegrationTests.Fixtures;

namespace TCJ.AspNetCore.IntegrationTests.Tests;

[Collection(AspNetCoreIntegrationCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "AspNetCore")]
[Trait("Category", "ExceptionHandling")]
[Trait("Category", "ProblemDetails")]
public sealed class ExceptionAndProblemDetailsIntegrationTests(TcjWebApplicationFactory factory)
{
    [Theory]
    [InlineData("/errors/validation", 400)]
    [InlineData("/errors/not-found", 404)]
    [InlineData("/errors/conflict", 409)]
    [InlineData("/errors/unauthorized", 401)]
    [InlineData("/errors/forbidden", 403)]
    public async Task Known_framework_failures_map_to_problem_details(string path, int expectedStatus)
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(path);
        using var json = await TestHttp.ReadJsonAsync(response);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedStatus, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("title").GetString()));
        Assert.True(json.RootElement.TryGetProperty("type", out _));
        Assert.True(json.RootElement.TryGetProperty("traceId", out var traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId.GetString()));
    }

    [Fact]
    public async Task Validation_problem_contains_field_errors_and_error_codes()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/errors/validation");
        using var json = await TestHttp.ReadJsonAsync(response);

        Assert.Equal("Name is required.", json.RootElement.GetProperty("errors").GetProperty("Name")[0].GetString());
        Assert.Equal("VALIDATION_FAILED", json.RootElement.GetProperty("errorCodes").GetProperty("Name")[0].GetString());
    }

    [Fact]
    public async Task Unknown_exception_in_Production_is_safe_and_logged_by_tcj_handler()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/errors/unhandled");
        string body = await response.Content.ReadAsStringAsync();
        using var json = System.Text.Json.JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("sensitive-internal-diagnostic-message", body, StringComparison.Ordinal);
        Assert.DoesNotContain("System.InvalidOperationException", body, StringComparison.Ordinal);
        Assert.Equal("UNEXPECTED_ERROR", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("/errors/unhandled", json.RootElement.GetProperty("instance").GetString());
        Assert.Contains(factory.Diagnostics.Entries, entry =>
            entry.Level == LogLevel.Error && entry.Category.Contains(nameof(TcjExceptionHandler), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_exception_in_Development_follows_opt_in_diagnostic_policy()
    {
        await using TcjWebApplicationFactory development = await TcjWebApplicationFactory.StartAsync(Environments.Development);
        using HttpClient client = development.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/errors/unhandled");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("sensitive-internal-diagnostic-message", body, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Echo_endpoint_round_trips_json_through_real_http_pipeline()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/echo", new { value = "tcj" });
        using var json = await TestHttp.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("tcj", json.RootElement.GetProperty("value").GetString());
    }
}
