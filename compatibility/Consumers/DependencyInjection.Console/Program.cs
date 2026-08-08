using Microsoft.Extensions.DependencyInjection;
using TCJ.Core.Identifiers;
using TCJ.DependencyInjection.Extensions;
using TCJ.DependencyInjection.Lifetimes;

namespace TcjCompatibility.DependencyInjectionConsumer;

public static class Program
{
    public static void Main()
    {
        var services = new ServiceCollection();
        services.AddTcjDependencyInjection(typeof(Program).Assembly);
        services.AddTcjDependencyInjection(typeof(Program).Assembly);

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        ITransientService transient1 = provider.GetRequiredService<ITransientService>();
        ITransientService transient2 = provider.GetRequiredService<ITransientService>();
        if (ReferenceEquals(transient1, transient2))
        {
            throw new InvalidOperationException("Transient registration is invalid.");
        }

        using IServiceScope scope1 = provider.CreateScope();
        IScopedService scoped1a = scope1.ServiceProvider.GetRequiredService<IScopedService>();
        IScopedService scoped1b = scope1.ServiceProvider.GetRequiredService<IScopedService>();
        if (!ReferenceEquals(scoped1a, scoped1b))
        {
            throw new InvalidOperationException("Scoped registration is invalid.");
        }

        using IServiceScope scope2 = provider.CreateScope();
        if (ReferenceEquals(scoped1a, scope2.ServiceProvider.GetRequiredService<IScopedService>()))
        {
            throw new InvalidOperationException("Scope isolation is invalid.");
        }

        ISingletonService singleton1 = provider.GetRequiredService<ISingletonService>();
        ISingletonService singleton2 = provider.GetRequiredService<ISingletonService>();
        if (!ReferenceEquals(singleton1, singleton2))
        {
            throw new InvalidOperationException("Singleton registration is invalid.");
        }

        if (provider.GetRequiredService<SelfTransientService>() is null ||
            provider.GetRequiredService<IGuidGenerator>() is null)
        {
            throw new InvalidOperationException("Self/framework registration is invalid.");
        }

        Console.WriteLine("TCJ.DependencyInjection consumer passed");
    }
}

public interface ITransientService { Guid InstanceId { get; } }
public sealed class TransientService : ITransientService, ITransientDependency { public Guid InstanceId { get; } = Guid.NewGuid(); }
public interface IScopedService { Guid InstanceId { get; } }
public sealed class ScopedService : IScopedService, IScopedDependency { public Guid InstanceId { get; } = Guid.NewGuid(); }
public interface ISingletonService { Guid InstanceId { get; } }
public sealed class SingletonService : ISingletonService, ISingletonDependency { public Guid InstanceId { get; } = Guid.NewGuid(); }
public sealed class SelfTransientService : ISelfTransientDependency { }
