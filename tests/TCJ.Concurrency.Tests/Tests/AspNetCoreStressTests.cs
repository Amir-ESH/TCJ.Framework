using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TCJ.Concurrency.Tests.Fixtures;
using TCJ.Concurrency.Tests.Infrastructure;

namespace TCJ.Concurrency.Tests.Tests;

[Trait("Category", "Concurrency")]
[Trait("Category", "Stress")]
[Trait("Category", "AspNetCore")]
[Trait("Category", "RequestScope")]
public sealed class AspNetCoreStressTests
{
    [Fact]
    [Trait("Category", "CurrentUser")]
    public async Task ConcurrentRequestsKeepCurrentUsersIsolated()
    {
        await using var host = new ConcurrencyWebHost();
        await host.StartAsync();
        using HttpClient client = host.CreateClient();
        await StressRunner.RunAsync(nameof(ConcurrentRequestsKeepCurrentUsersIsolated), "aspnetcore", async context =>
        {
            long expectedUser = 1_000_000L + (context.Worker * 100_000L) + context.Iteration;
            using var request = new HttpRequestMessage(HttpMethod.Get, "/identity");
            request.Headers.Add("X-Stress-UserId", expectedUser.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add("X-Stress-Correlation", context.OperationId);
            using HttpResponseMessage response = await client.SendAsync(request, context.CancellationToken);
            response.EnsureSuccessStatusCode();
            IdentityResponse? payload = await response.Content.ReadFromJsonAsync<IdentityResponse>(cancellationToken: context.CancellationToken);
            Assert.NotNull(payload);
            if (payload.UserId != expectedUser || payload.Correlation != context.OperationId)
            {
                context.ReportViolation(StressViolationKind.IdentityLeakage);
            }
            Assert.Equal(expectedUser, payload.UserId);
            Assert.Equal(context.OperationId, payload.Correlation);
        });
    }

    [Fact]
    [Trait("Category", "CurrentUser")]
    public async Task ConcurrentRequestsKeepRolesAndScopedServicesIsolated()
    {
        await using var host = new ConcurrencyWebHost();
        await host.StartAsync();
        using HttpClient client = host.CreateClient();
        var owners = new ConcurrentDictionary<Guid, string>();
        await StressRunner.RunAsync(nameof(ConcurrentRequestsKeepRolesAndScopedServicesIsolated), "aspnetcore", async context =>
        {
            string role = $"role-{context.Worker}-{context.Iteration}";
            using var request = new HttpRequestMessage(HttpMethod.Get, "/identity");
            request.Headers.Add("X-Stress-UserId", "42");
            request.Headers.Add("X-Stress-Role", role);
            request.Headers.Add("X-Stress-Correlation", context.OperationId);
            using HttpResponseMessage response = await client.SendAsync(request, context.CancellationToken);
            IdentityResponse? payload = await response.Content.ReadFromJsonAsync<IdentityResponse>(cancellationToken: context.CancellationToken);
            Assert.NotNull(payload);
            Assert.Equal(payload.FirstScope, payload.SecondScope);
            Assert.Equal([role], payload.Roles);
            if (!owners.TryAdd(payload.FirstScope, context.OperationId))
            {
                context.ReportViolation(StressViolationKind.ScopeLeakage);
            }
            Assert.Equal(context.OperationId, owners[payload.FirstScope]);
        });
    }

    [Fact]
    [Trait("Category", "Cancellation")]
    public async Task CanceledRequestDoesNotCancelOtherRequests()
    {
        await using var host = new ConcurrencyWebHost();
        await host.StartAsync();
        using HttpClient client = host.CreateClient();
        await StressRunner.RunAsync(nameof(CanceledRequestDoesNotCancelOtherRequests), "aspnetcore", async context =>
        {
            if ((context.Worker + context.Iteration) % 5 != 0)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/identity");
                request.Headers.Add("X-Stress-UserId", "55");
                request.Headers.Add("X-Stress-Correlation", context.OperationId);
                using HttpResponseMessage response = await client.SendAsync(request, context.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return;
            }

            string id = context.OperationId;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            Task<HttpResponseMessage> requestTask = client.GetAsync($"/cancel/{id}", cancellation.Token);
            await host.CancellationProbe.WaitStartedAsync(id).WaitAsync(context.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await requestTask.ConfigureAwait(false));
        });
    }

    [Fact]
    public async Task FailedRequestDoesNotLeakExceptionState()
    {
        await using var host = new ConcurrencyWebHost();
        await host.StartAsync();
        using HttpClient client = host.CreateClient();
        await StressRunner.RunAsync(nameof(FailedRequestDoesNotLeakExceptionState), "aspnetcore", async context =>
        {
            if ((context.Worker + context.Iteration) % 4 == 0)
            {
                using HttpResponseMessage failure = await client.GetAsync("/fail", context.CancellationToken);
                Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "/identity");
            request.Headers.Add("X-Stress-UserId", "77");
            request.Headers.Add("X-Stress-Correlation", context.OperationId);
            using HttpResponseMessage success = await client.SendAsync(request, context.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        });
    }


    [Fact]
    [Trait("Category", "HealthChecks")]
    public async Task ConcurrentReadinessRequestsUseSingleFlightCache()
    {
        await using var host = new ConcurrencyWebHost();
        await host.StartAsync();
        using HttpClient client = host.CreateClient();
        await StressRunner.RunAsync(nameof(ConcurrentReadinessRequestsUseSingleFlightCache), "aspnetcore", async context =>
        {
            using HttpResponseMessage response = await client.GetAsync("/health/ready", context.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });
        Assert.Equal(1, host.ReadinessProbe.MaxConcurrentExecutions);
        Assert.True(host.ReadinessProbe.ExecutionCount > 0);
    }

    [Fact]
    [Trait("Category", "HealthChecks")]
    [Trait("Category", "Cancellation")]
    public async Task CanceledReadinessRequestDoesNotCorruptSharedCache()
    {
        await using var host = new ConcurrencyWebHost();
        await host.StartAsync();
        using HttpClient client = host.CreateClient();
        await StressRunner.RunAsync(nameof(CanceledReadinessRequestDoesNotCorruptSharedCache), "aspnetcore", async context =>
        {
            if ((context.Worker + context.Iteration) % 7 == 0)
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(1));
                try
                {
                    using HttpResponseMessage _ = await client.GetAsync("/health/ready", cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                return;
            }

            using HttpResponseMessage response = await client.GetAsync("/health/ready", context.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });
        using HttpResponseMessage final = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, final.StatusCode);
        Assert.Equal(1, host.ReadinessProbe.MaxConcurrentExecutions);
    }

    private sealed record IdentityResponse(long? UserId, string[] Roles, Guid FirstScope, Guid SecondScope, string Correlation);
}
