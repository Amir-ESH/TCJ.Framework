using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TCJ.AspNetCore.Diagnostics;
using TCJ.AspNetCore.Extensions;
using TCJ.AspNetCore.Options;
using TCJ.AspNetCore.Security;
using TCJ.AspNetCore.IntegrationTests.TestHost;
using TCJ.Core.Results;
using TCJ.Core.Security;
using TCJ.AspNetCore.Results;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace TCJ.AspNetCore.IntegrationTests.Fixtures;

public sealed class TcjWebApplicationFactory : IAsyncLifetime
{
    private readonly string _environmentName;
    private readonly bool _includeTcjMiddleware;
    private readonly bool _duplicateRegistration;
    private readonly Action<TcjAspNetCoreOptions>? _configureTcj;
    private WebApplication? _application;

    public TcjWebApplicationFactory()
        : this(Environments.Production)
    {
    }

    private TcjWebApplicationFactory(string environmentName,
                                     bool includeTcjMiddleware = true,
                                     bool duplicateRegistration = false,
                                     Action<TcjAspNetCoreOptions>? configureTcj = null)
    {
        _environmentName = environmentName;
        _includeTcjMiddleware = includeTcjMiddleware;
        _duplicateRegistration = duplicateRegistration;
        _configureTcj = configureTcj;
        Diagnostics = new IntegrationDiagnostics(environmentName);
    }

    internal IntegrationDiagnostics Diagnostics { get; }

    internal IServiceProvider Services => _application?.Services
                                          ?? throw new InvalidOperationException("The test host has not started.");

    internal string EnvironmentName => _environmentName;

    public async ValueTask InitializeAsync()
    {
        _application = await BuildApplicationAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is not null)
        {
            await _application.DisposeAsync().ConfigureAwait(false);
        }
        Diagnostics.Dispose();
    }

    internal HttpClient CreateClient()
    {
        WebApplication app = _application ?? throw new InvalidOperationException("The test host has not started.");
        return Diagnostics.CreateClient(app.GetTestServer().CreateHandler());
    }

    internal static async Task<TcjWebApplicationFactory> StartAsync(
        string environmentName,
        bool includeTcjMiddleware = true,
        bool duplicateRegistration = false,
        Action<TcjAspNetCoreOptions>? configureTcj = null)
    {
        var factory = new TcjWebApplicationFactory(environmentName, includeTcjMiddleware, duplicateRegistration, configureTcj);
        try
        {
            await factory.InitializeAsync().ConfigureAwait(false);
            return factory;
        }
        catch
        {
            await factory.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<WebApplication> BuildApplicationAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(TcjWebApplicationFactory).Assembly.FullName,
            EnvironmentName = _environmentName,
        });

        builder.WebHost.UseTestServer();
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        builder.Logging.AddProvider(Diagnostics);
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
            options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
            options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
        }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        builder.Services.AddSingleton<ScopedDisposalTracker>();
        builder.Services.AddSingleton<CancellationObserver>();
        builder.Services.AddScoped<ScopedMarker>();
        builder.Services.AddTransient<TransientMarker>();
        builder.Services.AddSingleton<SingletonMarker>();

        Action<TcjAspNetCoreOptions> configuration = options =>
        {
            options.IncludeExceptionDetails = builder.Environment.IsDevelopment();
            _configureTcj?.Invoke(options);
        };

        builder.Services.AddTcjAspNetCore(configuration);
        if (_duplicateRegistration)
        {
            builder.Services.AddTcjAspNetCore(configuration);
        }

        WebApplication app = builder.Build();

        if (_includeTcjMiddleware)
        {
            app.UseTcjAspNetCore();
        }

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            Diagnostics.RecordEndpoint(context.GetEndpoint()?.DisplayName);
            await next(context).ConfigureAwait(false);
        });

        MapEndpoints(app);
        await app.StartAsync().ConfigureAwait(false);
        return app;
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/health", () => HttpResults.Ok(new { status = "ok" })).WithDisplayName("Health");

        app.MapGet("/current-user", (ICurrentUserProvider currentUser, HttpContext context) =>
            HttpResults.Ok(new
            {
                userId = currentUser.UserId,
                authenticated = context.User.Identity?.IsAuthenticated == true,
            })).WithDisplayName("CurrentUser");

        app.MapGet("/current-user/claims", (HttpContext context) =>
            HttpResults.Ok(new
            {
                claims = context.User.Claims.Select(claim => new { claim.Type, claim.Value }).ToArray(),
                roles = context.User.Claims.Where(claim => claim.Type == System.Security.Claims.ClaimTypes.Role)
                                    .Select(claim => claim.Value)
                                    .ToArray(),
            })).WithDisplayName("CurrentUserClaims");

        app.MapGet("/services/framework", (IServiceProvider requestServices) =>
        {
            var handlers = requestServices.GetServices<IExceptionHandler>().ToArray();
            var currentUsers = requestServices.GetServices<ICurrentUserProvider>().ToArray();
            var problemDetailsServices = requestServices.GetServices<IProblemDetailsService>().ToArray();
            return HttpResults.Ok(new
            {
                currentUser = requestServices.GetRequiredService<ICurrentUserProvider>().GetType().FullName,
                problemDetails = requestServices.GetRequiredService<IProblemDetailsService>().GetType().FullName,
                currentUserProviderCount = currentUsers.Length,
                problemDetailsServiceCount = problemDetailsServices.Length,
                exceptionHandlerCount = handlers.Count(handler => handler is TcjExceptionHandler),
                loggerAvailable = requestServices.GetService<ILogger<TcjExceptionHandler>>() is not null,
            });
        }).WithDisplayName("FrameworkServices");

        app.MapGet("/services/scoped", (IServiceProvider requestServices) =>
        {
            ScopedMarker first = requestServices.GetRequiredService<ScopedMarker>();
            ScopedMarker second = requestServices.GetRequiredService<ScopedMarker>();
            return HttpResults.Ok(new { first = first.Id, second = second.Id });
        }).WithDisplayName("ScopedServices");

        app.MapGet("/services/transient", (IServiceProvider requestServices) =>
        {
            TransientMarker first = requestServices.GetRequiredService<TransientMarker>();
            TransientMarker second = requestServices.GetRequiredService<TransientMarker>();
            return HttpResults.Ok(new { first = first.Id, second = second.Id });
        }).WithDisplayName("TransientServices");

        app.MapGet("/services/singleton", (SingletonMarker marker) => HttpResults.Ok(new { marker.Id }))
           .WithDisplayName("SingletonService");

        app.MapGet("/services/disposed/{id:guid}", (Guid id, ScopedDisposalTracker tracker) =>
            HttpResults.Ok(new { disposed = tracker.WasDisposed(id) })).WithDisplayName("ScopedDisposal");

        app.MapGet("/errors/validation", () =>
            Result.Failure([CommonErrors.ValidationForField("Name", "Name is required.")]).ToHttpResult())
           .WithDisplayName("ValidationError");

        app.MapGet("/errors/not-found", () =>
            Result.Failure(CommonErrors.NotFound("Widget", 42)).ToHttpResult())
           .WithDisplayName("NotFoundError");

        app.MapGet("/errors/conflict", () =>
            Result.Failure(CommonErrors.Conflict("The widget already exists.")).ToHttpResult())
           .WithDisplayName("ConflictError");

        app.MapGet("/errors/unauthorized", () =>
            Result.Failure(CommonErrors.Unauthorized()).ToHttpResult())
           .WithDisplayName("UnauthorizedResult");

        app.MapGet("/errors/forbidden", () =>
            Result.Failure(CommonErrors.Forbidden()).ToHttpResult())
           .WithDisplayName("ForbiddenResult");

        app.MapGet("/errors/unhandled", ThrowUnhandled).WithDisplayName("UnhandledError");

        app.MapGet("/errors/canceled", async (HttpContext context, CancellationObserver observer) =>
        {
            observer.Reset();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted).ConfigureAwait(false);
                return HttpResults.NoContent();
            }
            finally
            {
                observer.Signal(context.RequestAborted.IsCancellationRequested);
            }
        }).WithDisplayName("CanceledRequest");

        app.MapGet("/empty-not-found", () => HttpResults.StatusCode(StatusCodes.Status404NotFound))
           .WithDisplayName("EmptyNotFound");

        app.MapGet("/auth/required", () => HttpResults.Ok(new { authorized = true }))
           .RequireAuthorization()
           .WithDisplayName("AuthenticatedEndpoint");

        app.MapGet("/auth/admin", () => HttpResults.Ok(new { authorized = true }))
           .RequireAuthorization(policy => policy.RequireRole("admin"))
           .WithDisplayName("AdminEndpoint");

        app.MapPost("/echo", (EchoPayload payload) => HttpResults.Ok(payload)).WithDisplayName("Echo");
    }

    private static IResult ThrowUnhandled()
        => throw new InvalidOperationException("sensitive-internal-diagnostic-message");
}
