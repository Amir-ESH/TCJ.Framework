using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TCJ.AspNetCore.Extensions;
using TCJ.Core.Security;

namespace TCJ.Concurrency.Tests.Fixtures;

internal sealed class ConcurrencyWebHost : IAsyncDisposable
{
    private WebApplication? _application;
    public CancellationProbe CancellationProbe { get; } = new();

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = StressAuthenticationHandler.Scheme;
            options.DefaultChallengeScheme = StressAuthenticationHandler.Scheme;
        }).AddScheme<AuthenticationSchemeOptions, StressAuthenticationHandler>(StressAuthenticationHandler.Scheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<RequestMarker>();
        builder.Services.AddSingleton(CancellationProbe);
        builder.Services.AddTcjAspNetCore();

        WebApplication app = builder.Build();
        app.UseTcjAspNetCore();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/identity", (ICurrentUserProvider currentUser, HttpContext context, RequestMarker first, IServiceProvider services) =>
        {
            RequestMarker second = services.GetRequiredService<RequestMarker>();
            string[] roles = context.User.Claims.Where(claim => claim.Type == ClaimTypes.Role).Select(claim => claim.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            return Results.Ok(new
            {
                userId = currentUser.UserId,
                roles,
                firstScope = first.Id,
                secondScope = second.Id,
                correlation = context.Request.Headers["X-Stress-Correlation"].ToString()
            });
        });

        app.MapGet("/cancel/{id}", async (string id, CancellationProbe probe, HttpContext context) =>
        {
            probe.SignalStarted(id);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted).ConfigureAwait(false);
            return Results.Ok();
        });

        app.MapGet("/fail", (HttpContext _) => throw new InvalidOperationException("stress-endpoint-failure"));

        await app.StartAsync().ConfigureAwait(false);
        _application = app;
    }

    public HttpClient CreateClient()
    {
        WebApplication app = _application ?? throw new InvalidOperationException("The concurrency test host is not started.");
        return app.GetTestServer().CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is not null)
        {
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class RequestMarker
{
    public Guid Id { get; } = Guid.NewGuid();
}

internal sealed class CancellationProbe
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _started = new(StringComparer.Ordinal);

    public void SignalStarted(string id) => Get(id).TrySetResult(true);
    public Task WaitStartedAsync(string id) => Get(id).Task;

    private TaskCompletionSource<bool> Get(string id) =>
        _started.GetOrAdd(id, static _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
}

internal sealed class StressAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Stress";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Stress-UserId", out var userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (Request.Headers.TryGetValue("X-Stress-Role", out var role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
        }

        var identity = new ClaimsIdentity(claims, Scheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}
