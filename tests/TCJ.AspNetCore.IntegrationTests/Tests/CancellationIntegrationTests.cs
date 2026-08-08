using Microsoft.Extensions.DependencyInjection;
using TCJ.AspNetCore.IntegrationTests.Fixtures;
using TCJ.AspNetCore.IntegrationTests.TestHost;

namespace TCJ.AspNetCore.IntegrationTests.Tests;

[Collection(AspNetCoreIntegrationCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "AspNetCore")]
[Trait("Category", "RequestScope")]
[Trait("Category", "Cancellation")]
public sealed class CancellationIntegrationTests(TcjWebApplicationFactory factory)
{
    [Fact]
    public async Task Client_cancellation_reaches_request_aborted_without_framework_500()
    {
        CancellationObserver observer = factory.Services.GetRequiredService<CancellationObserver>();
        observer.Reset();
        using HttpClient client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource();

        Task<HttpResponseMessage> request = client.GetAsync("/errors/canceled", cancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(await observer.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
        Assert.DoesNotContain(factory.Diagnostics.Entries, entry =>
            entry.Category.Contains("TcjExceptionHandler", StringComparison.Ordinal)
            && entry.Level >= Microsoft.Extensions.Logging.LogLevel.Error
            && entry.Message.Contains("/errors/canceled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Canceled_authenticated_request_does_not_leak_identity_to_next_request()
    {
        CancellationObserver observer = factory.Services.GetRequiredService<CancellationObserver>();
        observer.Reset();
        using HttpClient client = factory.CreateClient();
        using var request = TestHttp.AuthenticatedGet("/errors/canceled", userId: "999");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, cancellation.Token));
        Assert.True(await observer.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));

        using HttpResponseMessage next = await client.GetAsync("/current-user");
        using var json = await TestHttp.ReadJsonAsync(next);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("userId").ValueKind);
    }
}
