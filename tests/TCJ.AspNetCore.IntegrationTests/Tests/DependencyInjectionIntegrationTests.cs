using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TCJ.AspNetCore.IntegrationTests.Fixtures;
using TCJ.AspNetCore.IntegrationTests.TestHost;

namespace TCJ.AspNetCore.IntegrationTests.Tests;

[Collection(AspNetCoreIntegrationCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "AspNetCore")]
[Trait("Category", "DependencyInjection")]
[Trait("Category", "RequestScope")]
public sealed class DependencyInjectionIntegrationTests(TcjWebApplicationFactory factory)
{
    [Fact]
    public async Task Scoped_dependency_is_stable_within_one_request()
    {
        GuidPair pair = await GetPairAsync("/services/scoped");
        Assert.Equal(pair.First, pair.Second);
    }

    [Fact]
    public async Task Scoped_dependency_differs_between_requests_and_is_disposed()
    {
        GuidPair first = await GetPairAsync("/services/scoped");
        GuidPair second = await GetPairAsync("/services/scoped");

        Assert.NotEqual(first.First, second.First);

        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync($"/services/disposed/{first.First}");
        using var json = await TestHttp.ReadJsonAsync(response);
        Assert.True(json.RootElement.GetProperty("disposed").GetBoolean());
    }

    [Fact]
    public async Task Transient_dependency_differs_within_one_request()
    {
        GuidPair pair = await GetPairAsync("/services/transient");
        Assert.NotEqual(pair.First, pair.Second);
    }

    [Fact]
    public async Task Singleton_dependency_is_stable_across_requests()
    {
        Guid first = await GetSingletonAsync();
        Guid second = await GetSingletonAsync();
        Assert.Equal(first, second);
    }

    [Fact]
    public void Resolving_scoped_marker_from_root_provider_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => factory.Services.GetRequiredService<ScopedMarker>());
    }

    private async Task<GuidPair> GetPairAsync(string path)
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await TestHttp.ReadJsonAsync(response);
        return new GuidPair(
            json.RootElement.GetProperty("first").GetGuid(),
            json.RootElement.GetProperty("second").GetGuid());
    }

    private async Task<Guid> GetSingletonAsync()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/services/singleton");
        using var json = await TestHttp.ReadJsonAsync(response);
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private sealed record GuidPair(Guid First, Guid Second);
}
