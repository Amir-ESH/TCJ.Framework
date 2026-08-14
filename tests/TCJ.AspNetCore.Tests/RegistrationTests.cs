using Microsoft.AspNetCore.Diagnostics;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TCJ.AspNetCore.Diagnostics;
using TCJ.AspNetCore.Extensions;
using TCJ.Core.Security;

namespace TCJ.AspNetCore.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void AddTcjAspNetCore_registers_current_user_and_exception_handler_once()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddTcjAspNetCore();
        services.AddTcjAspNetCore();

        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ICurrentUserProvider>());

        IExceptionHandler[] handlers = scope.ServiceProvider.GetServices<IExceptionHandler>().ToArray();

        Assert.Single(handlers.Where(handler => handler is TcjExceptionHandler));

        HttpJsonOptions jsonOptions = provider.GetRequiredService<IOptions<HttpJsonOptions>>().Value;
        Assert.NotNull(jsonOptions.SerializerOptions.GetTypeInfo(typeof(ProblemDetails)));
        Assert.Single(jsonOptions.SerializerOptions.TypeInfoResolverChain.Where(
            resolver => resolver.GetType().Assembly == typeof(AspNetCoreServiceCollectionExtensions).Assembly));
    }
}
