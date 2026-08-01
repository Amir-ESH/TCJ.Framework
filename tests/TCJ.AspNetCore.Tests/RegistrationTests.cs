using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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
    }
}
