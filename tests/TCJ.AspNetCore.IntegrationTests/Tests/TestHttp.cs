using System.Net.Http.Json;
using System.Text.Json;
using TCJ.AspNetCore.IntegrationTests.TestHost;

namespace TCJ.AspNetCore.IntegrationTests.Tests;

internal static class TestHttp
{
    internal static HttpRequestMessage AuthenticatedGet(string path,
                                                        string? userId = "42",
                                                        string? roles = null,
                                                        string? claims = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestAuthenticationHandler.AuthenticateHeader, "true");
        if (userId is not null)
        {
            request.Headers.Add(TestAuthenticationHandler.UserIdHeader, userId);
        }
        if (!string.IsNullOrWhiteSpace(roles))
        {
            request.Headers.Add(TestAuthenticationHandler.RoleHeader, roles);
        }
        if (!string.IsNullOrWhiteSpace(claims))
        {
            request.Headers.Add(TestAuthenticationHandler.ClaimHeader, claims);
        }
        return request;
    }

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
    }

    internal static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
}
