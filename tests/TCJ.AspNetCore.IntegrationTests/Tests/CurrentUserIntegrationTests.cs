using System.Net;
using System.Security.Claims;
using TCJ.AspNetCore.IntegrationTests.Fixtures;
using TCJ.AspNetCore.IntegrationTests.TestHost;

namespace TCJ.AspNetCore.IntegrationTests.Tests;

[Collection(AspNetCoreIntegrationCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "AspNetCore")]
[Trait("Category", "CurrentUser")]
public sealed class CurrentUserIntegrationTests(TcjWebApplicationFactory factory)
{
    [Fact]
    public async Task Anonymous_request_has_no_current_user_identifier()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/current-user");
        using var json = await TestHttp.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(json.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("userId").ValueKind);
    }

    [Fact]
    public async Task Authenticated_request_exposes_expected_numeric_user_identifier()
    {
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestHttp.AuthenticatedGet("/current-user", userId: "7001");
        using HttpResponseMessage response = await client.SendAsync(request);
        using var json = await TestHttp.ReadJsonAsync(response);

        Assert.True(json.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(7001L, json.RootElement.GetProperty("userId").GetInt64());
    }

    [Fact]
    public async Task Missing_user_id_claim_keeps_authenticated_user_without_framework_identifier()
    {
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestHttp.AuthenticatedGet("/current-user", userId: null);
        using HttpResponseMessage response = await client.SendAsync(request);
        using var json = await TestHttp.ReadJsonAsync(response);

        Assert.True(json.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("userId").ValueKind);
    }

    [Fact]
    public async Task Claims_and_roles_are_available_to_endpoint_logic()
    {
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestHttp.AuthenticatedGet(
            "/current-user/claims",
            userId: "42",
            roles: "admin,auditor",
            claims: "tenant=alpha,permission=widgets.read");
        using HttpResponseMessage response = await client.SendAsync(request);
        using var json = await TestHttp.ReadJsonAsync(response);

        string[] roles = json.RootElement.GetProperty("roles").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Contains("admin", roles);
        Assert.Contains("auditor", roles);
        Assert.Contains(json.RootElement.GetProperty("claims").EnumerateArray(), claim =>
            claim.GetProperty("type").GetString() == "tenant" && claim.GetProperty("value").GetString() == "alpha");
    }

    [Fact]
    public async Task Duplicate_user_identifier_claim_uses_first_configured_claim()
    {
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestHttp.AuthenticatedGet("/current-user", userId: "11");
        request.Headers.Add(TestAuthenticationHandler.DuplicateUserIdHeader, "22");
        using HttpResponseMessage response = await client.SendAsync(request);
        using var json = await TestHttp.ReadJsonAsync(response);

        Assert.Equal(11L, json.RootElement.GetProperty("userId").GetInt64());
    }

    [Fact]
    public async Task Current_user_identity_does_not_leak_between_requests()
    {
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage firstRequest = TestHttp.AuthenticatedGet("/current-user", userId: "101");
        using HttpResponseMessage firstResponse = await client.SendAsync(firstRequest);
        using var first = await TestHttp.ReadJsonAsync(firstResponse);

        using HttpRequestMessage secondRequest = TestHttp.AuthenticatedGet("/current-user", userId: "202");
        using HttpResponseMessage secondResponse = await client.SendAsync(secondRequest);
        using var second = await TestHttp.ReadJsonAsync(secondResponse);

        using HttpResponseMessage anonymousResponse = await client.GetAsync("/current-user");
        using var anonymous = await TestHttp.ReadJsonAsync(anonymousResponse);

        Assert.Equal(101L, first.RootElement.GetProperty("userId").GetInt64());
        Assert.Equal(202L, second.RootElement.GetProperty("userId").GetInt64());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, anonymous.RootElement.GetProperty("userId").ValueKind);
    }

    [Fact]
    public async Task Authorization_challenge_and_role_forbid_keep_expected_status_codes()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage anonymous = await client.GetAsync("/auth/required");

        using HttpRequestMessage nonAdminRequest = TestHttp.AuthenticatedGet("/auth/admin", userId: "42", roles: "reader");
        using HttpResponseMessage forbidden = await client.SendAsync(nonAdminRequest);
        using HttpRequestMessage adminRequest = TestHttp.AuthenticatedGet("/auth/admin", userId: "43", roles: "admin");
        using HttpResponseMessage admin = await client.SendAsync(adminRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal("application/problem+json", anonymous.Content.Headers.ContentType?.MediaType);
        Assert.Equal("application/problem+json", forbidden.Content.Headers.ContentType?.MediaType);
    }
    [Fact]
    public async Task Deterministic_authentication_failure_returns_unauthorized_problem_details()
    {
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/required");
        request.Headers.Add(TestAuthenticationHandler.AuthenticationFailureHeader, "true");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Authorization_headers_are_not_written_to_host_diagnostics()
    {
        const string secret = "tcj-test-secret-never-log-this";
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(factory.Diagnostics.Entries, entry => entry.Message.Contains(secret, StringComparison.Ordinal));
    }

}
