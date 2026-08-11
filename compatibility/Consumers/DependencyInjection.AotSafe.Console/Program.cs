using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.DomainEvents;
using TCJ.Core.Identifiers;
using TCJ.DependencyInjection.Extensions;

namespace TcjCompatibility.DependencyInjectionAotSafeConsumer;

public static class Program
{
    public static void Main()
    {
        var services = new ServiceCollection();

        services.AddTcjDependencyInjection();
        services.AddTcjDependencyInjection();

        EnsureSingleRegistration<TimeProvider>(services);
        EnsureSingleRegistration<IGuidGenerator>(services);
        EnsureSingleRegistration<IDomainEventDispatcher>(services);

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        if (provider.GetRequiredService<TimeProvider>() != TimeProvider.System ||
            provider.GetRequiredService<IGuidGenerator>() is null)
        {
            throw new InvalidOperationException("Reflection-free framework registration is invalid.");
        }

        using IServiceScope scope = provider.CreateScope();
        if (scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>() is null)
        {
            throw new InvalidOperationException("Domain-event dispatcher registration is invalid.");
        }

        Console.WriteLine("TCJ.DependencyInjection AOT-safe bootstrap consumer passed");
    }

    private static void EnsureSingleRegistration<TService>(IServiceCollection services)
    {
        if (services.Count(descriptor => descriptor.ServiceType == typeof(TService)) != 1)
        {
            throw new InvalidOperationException($"Expected exactly one {typeof(TService).Name} registration.");
        }
    }
}
